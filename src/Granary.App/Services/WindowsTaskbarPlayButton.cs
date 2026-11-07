using Granary.App.Interop;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// The Play/Pause in the main window's taskbar preview: the one button of the window's thumbnail toolbar (ITaskbarList3).
/// A toolbar takes buttons only once Explorer has made the window's taskbar button, and a new taskbar button (Explorer
/// restarted) comes without them, so the button is added whenever Explorer says it made one (<see cref="WindowMessageHook"/>).
/// Buttons cannot be taken off a toolbar: hiding hides it (THBF_HIDDEN). All on the UI thread.
/// </summary>
/// <remarks>
/// Explorer's side cannot be unit-tested; <c>PlayerTaskbarButton</c> holds the decisions and is. This was built to the
/// Windows SDK's headers (shobjidl_core.h, winuser.h) and the documented message flow; the button itself is checked in the
/// running app: hovering over Granary's taskbar button while a title plays, paused, loads, and after the player is left.
/// </remarks>
internal sealed class WindowsTaskbarPlayButton : ITaskbarPlayButton, IDisposable
{
    // The button's id in the toolbar's WM_COMMAND; it is the toolbar's only button.
    private const uint ButtonId = 1;

    // Failures in a row after which the player goes on without the button.
    private const int FailuresAllowed = 3;

    // The player's own words on its Play/Pause button (PlayerPage.PlayPauseName).
    private const string PlayTip = "Play";
    private const string PauseTip = "Pause";

    private readonly MainWindow _window;
    private readonly ILogger<WindowsTaskbarPlayButton> _logger;
    private TaskbarList? _taskbar;
    private PlayPauseIcons? _icons;
    // What shows; null while hidden.
    private TaskbarPlayButtonState? _state;
    // The toolbar of the taskbar button Explorer made last has the button.
    private bool _added;
    private bool _unavailable;
    private bool _disposed;
    private int _failures;

    public WindowsTaskbarPlayButton(MainWindow window, ILogger<WindowsTaskbarPlayButton> logger)
    {
        _window = window;
        _logger = logger;
        WindowMessageHook.TaskbarButtonCreated += OnTaskbarButtonCreated;
        WindowMessageHook.ThumbButtonClicked += OnThumbButtonClicked;
        WindowMessageHook.DpiChanged += OnDpiChanged;
        window.Closed += (_, _) => Dispose();
    }

    public event EventHandler? Pressed;

    public void Show(TaskbarPlayButtonState state)
    {
        _state = state;
        Apply();
    }

    public void Hide()
    {
        _state = null;
        Apply();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        WindowMessageHook.TaskbarButtonCreated -= OnTaskbarButtonCreated;
        WindowMessageHook.ThumbButtonClicked -= OnThumbButtonClicked;
        WindowMessageHook.DpiChanged -= OnDpiChanged;
        _taskbar?.Dispose();
        _taskbar = null;
        _icons?.Dispose();
        _icons = null;
    }

    private nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(_window);

    // Added once there is a taskbar button and something to show, changed after. Called from the player's changes and its
    // page's unload: a failure must neither end the app nor keep the page from letting go of the player.
    private void Apply()
    {
        if (_disposed || _unavailable || !WindowMessageHook.HasTaskbarButton || (!_added && _state is null))
        {
            return;
        }
        try
        {
            _taskbar ??= TaskbarList.Create();
            _icons ??= PlayPauseIcons.ForWindow(Hwnd);
            var button = ButtonFor(_state, _icons);
            if (_added)
            {
                _taskbar.UpdateButton(Hwnd, button);
            }
            else
            {
                _taskbar.AddButton(Hwnd, button);
                _added = true;
            }
            _failures = 0;
        }
        catch (Exception ex)
        {
            // Made again for the next try: the one that failed may belong to an Explorer that is gone.
            _taskbar?.Dispose();
            _taskbar = null;
            _unavailable = ++_failures >= FailuresAllowed;
            _logger.LogWarning("The taskbar preview's Play/Pause failed: {Failure} {Result:X8}{GivenUp}", ex.GetType().Name, ex.HResult,
                _unavailable ? "; the player goes on without it" : "");
        }
    }

    // Play while paused, else Pause, as the player's own button; hidden, it keeps an icon all the same.
    private static ThumbButton ButtonFor(TaskbarPlayButtonState? state, PlayPauseIcons icons)
    {
        bool paused = state?.IsPaused == true;
        return new ThumbButton(ButtonId, paused ? icons.Play : icons.Pause, paused ? PlayTip : PauseTip,
            IsHidden: state is null, IsEnabled: state?.IsEnabled == true);
    }

    // A new taskbar button has an empty toolbar, and may come with a new Explorer: the button is added again, once the
    // message has returned. Failures while the old Explorer went away do not count against the new one.
    private void OnTaskbarButtonCreated() => InMessage(() =>
    {
        _added = false;
        _unavailable = false;
        _failures = 0;
        _taskbar?.Dispose();
        _taskbar = null;
        _window.DispatcherQueue.TryEnqueue(Apply);
    });

    private void OnThumbButtonClicked(int id) => InMessage(() =>
    {
        if (id == ButtonId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
    });

    // The icons are made again at the new scale. The taskbar keeps copies of the icons it was given (THUMBBUTTON's
    // remarks), so the old ones can go even if showing the new ones failed.
    private void OnDpiChanged() => InMessage(() => _window.DispatcherQueue.TryEnqueue(() =>
    {
        var old = _icons;
        _icons = null;
        Apply();
        old?.Dispose();
    }));

    // Called inside the window's message: nothing may escape.
    private void InMessage(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The taskbar's message could not be handled");
        }
    }
}
