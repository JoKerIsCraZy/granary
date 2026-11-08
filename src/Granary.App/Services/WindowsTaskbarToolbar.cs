using Granary.App.Interop;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels.Player;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// Previous episode, Play/Pause and Next episode in the main window's taskbar preview: the window's thumbnail toolbar
/// (ITaskbarList3). A toolbar takes buttons only once Explorer has made the window's taskbar button, and a new taskbar
/// button (Explorer restarted) comes without them, so all three are added whenever Explorer says it made one
/// (<see cref="WindowMessageHook"/>). Buttons can be neither added to a toolbar later nor taken off it: each is shown or
/// hidden (THBF_HIDDEN) and greyed (THBF_DISABLED). All on the UI thread.
/// </summary>
/// <remarks>
/// Explorer's side cannot be unit-tested; <c>PlayerTaskbarToolbar</c> holds the decisions of what shows, and
/// <see cref="TaskbarToolbarSender"/> those of sending it again after a failure and of giving up; both are. This was built
/// to the Windows SDK's headers (shobjidl_core.h, winuser.h) and the documented message flow; the buttons themselves are
/// checked in the running app: hovering over Granary's taskbar button while a movie and an episode play, paused, load,
/// and after the player is left.
/// </remarks>
internal sealed class WindowsTaskbarToolbar : ITaskbarToolbar, IDisposable
{
    // The buttons' ids in the toolbar's WM_COMMAND, in their order there.
    private const uint PreviousId = 1;
    private const uint PlayPauseId = 2;
    private const uint NextId = 3;

    // The player's own words on its buttons (PlayerPage.xaml, PlayerPage.PlayPauseName).
    private const string PreviousTip = "Previous episode";
    private const string PlayTip = "Play";
    private const string PauseTip = "Pause";
    private const string NextTip = "Next episode";

    private readonly MainWindow _window;
    private readonly ILogger<WindowsTaskbarToolbar> _logger;
    private readonly TaskbarToolbarSender _sender;
    private TaskbarList? _taskbar;
    private TaskbarIcons? _icons;
    // The toolbar of the taskbar button Explorer made last has the buttons.
    private bool _added;
    private bool _disposed;

    public WindowsTaskbarToolbar(MainWindow window, IUiDispatcher dispatcher, ILogger<WindowsTaskbarToolbar> logger)
    {
        _window = window;
        _logger = logger;
        _sender = new TaskbarToolbarSender(Send, dispatcher, TimeProvider.System, logger);
        WindowMessageHook.TaskbarButtonCreated += OnTaskbarButtonCreated;
        WindowMessageHook.ThumbButtonClicked += OnThumbButtonClicked;
        WindowMessageHook.DpiChanged += OnDpiChanged;
        window.Closed += (_, _) => Dispose();
    }

    public event EventHandler<TaskbarButton>? Pressed;

    // Called from the player's changes and its page's unload: the sender keeps a failure from ending the app or keeping the
    // page from letting go of the player.
    public void Show(TaskbarToolbarState state) => _sender.Send(state);

    public void Hide() => _sender.Send(null);

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
        _sender.Dispose();
        _taskbar?.Dispose();
        _taskbar = null;
        _icons?.Dispose();
        _icons = null;
    }

    private nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(_window);

    // Added once there is a taskbar button and something to show, changed after; throws when Explorer did not take it.
    private void Send(TaskbarToolbarState? state)
    {
        if (_disposed || !WindowMessageHook.HasTaskbarButton || (!_added && state is null))
        {
            return;
        }
        try
        {
            _taskbar ??= TaskbarList.Create();
            _icons ??= TaskbarIcons.ForWindow(Hwnd);
            var buttons = ButtonsFor(state, _icons);
            if (_added)
            {
                _taskbar.UpdateButtons(Hwnd, buttons);
            }
            else
            {
                _taskbar.AddButtons(Hwnd, buttons);
                _added = true;
            }
        }
        catch
        {
            // Made again for the next try: the one that failed may belong to an Explorer that is gone.
            _taskbar?.Dispose();
            _taskbar = null;
            throw;
        }
    }

    // In their order in the toolbar. Play while paused, else Pause, as the player's own button.
    private static ThumbButton[] ButtonsFor(TaskbarToolbarState? state, TaskbarIcons icons)
    {
        bool paused = state?.IsPaused == true;
        return
        [
            Button(PreviousId, icons.Previous, PreviousTip, state?.Previous),
            Button(PlayPauseId, paused ? icons.Play : icons.Pause, paused ? PlayTip : PauseTip, state?.PlayPause),
            Button(NextId, icons.Next, NextTip, state?.Next),
        ];
    }

    // Hidden, a button keeps an icon and a tip all the same.
    private static ThumbButton Button(uint id, nint icon, string tip, TaskbarButtonState? state) =>
        new(id, icon, tip, IsHidden: state is null or TaskbarButtonState.Hidden, IsEnabled: state == TaskbarButtonState.Enabled);

    // A new taskbar button has an empty toolbar, and may come with a new Explorer: the buttons are added again, once the
    // message has returned. Failures while the old Explorer went away do not count against the new one.
    private void OnTaskbarButtonCreated() => InMessage(() =>
    {
        _added = false;
        _taskbar?.Dispose();
        _taskbar = null;
        _window.DispatcherQueue.TryEnqueue(_sender.Restart);
    });

    private void OnThumbButtonClicked(int id) => InMessage(() =>
    {
        TaskbarButton? button = id switch
        {
            (int)PreviousId => TaskbarButton.Previous,
            (int)PlayPauseId => TaskbarButton.PlayPause,
            (int)NextId => TaskbarButton.Next,
            _ => null,
        };
        if (button is { } pressed)
        {
            Pressed?.Invoke(this, pressed);
        }
    });

    // The icons are made again at the new scale. The taskbar keeps copies of the icons it was given (THUMBBUTTON's
    // remarks), so the old ones can go even if showing the new ones failed.
    private void OnDpiChanged() => InMessage(() => _window.DispatcherQueue.TryEnqueue(() =>
    {
        var old = _icons;
        _icons = null;
        _sender.Resend();
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
