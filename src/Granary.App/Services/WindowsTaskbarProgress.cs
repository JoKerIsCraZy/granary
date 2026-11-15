using System.ComponentModel;
using Granary.App.Interop;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// The downloads' overall progress on the main window's taskbar button (ITaskbarList3's progress bar), as
/// <see cref="DownloadActivity.Progress"/> says it: a normal bar while any download is to come, a paused one once every one is
/// paused or waits for a connection that is not metered, none when nothing comes. While the player has its buttons in the
/// preview (<see cref="WindowsTaskbarToolbar.IsInUse"/>), the downloads leave the taskbar button alone: no bar, until the
/// player is left. A bar is sent only when it changes by a thousandth or more, and once Explorer made the taskbar button;
/// a new one (Explorer restarted) gets it again. All on the UI thread.
/// </summary>
/// <remarks>
/// Explorer's side cannot be unit-tested; what the bar shows is <c>DownloadTally</c>'s, which is (<c>DownloadTallyTests</c>).
/// This was built to the Windows SDK's headers (shobjidl_core.h) and is to be checked in the running app.
/// </remarks>
internal sealed class WindowsTaskbarProgress : IDisposable
{
    private const ulong Steps = 1000;

    private readonly MainWindow _window;
    private readonly DownloadActivity _activity;
    private readonly WindowsTaskbarToolbar _toolbar;
    private readonly ILogger<WindowsTaskbarProgress> _logger;
    private TaskbarList? _taskbar;
    // The bar as Explorer last took it; null before, and after a failure or a new taskbar button.
    private (DownloadProgressState State, ulong Step)? _shown;
    private bool _failureLogged;
    private bool _started;
    private bool _disposed;

    public WindowsTaskbarProgress(MainWindow window, DownloadActivity activity, WindowsTaskbarToolbar toolbar,
        ILogger<WindowsTaskbarProgress> logger)
    {
        _window = window;
        _activity = activity;
        _toolbar = toolbar;
        _logger = logger;
    }

    /// <summary>Starts following the downloads and the player's buttons; once. It ends as the window closes.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }
        _started = true;
        _activity.PropertyChanged += OnActivityChanged;
        _toolbar.InUseChanged += OnToolbarInUseChanged;
        WindowMessageHook.TaskbarButtonCreated += OnTaskbarButtonCreated;
        _window.Closed += (_, _) => Dispose();
        Send();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _activity.PropertyChanged -= OnActivityChanged;
        _toolbar.InUseChanged -= OnToolbarInUseChanged;
        WindowMessageHook.TaskbarButtonCreated -= OnTaskbarButtonCreated;
        _taskbar?.Dispose();
        _taskbar = null;
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadActivity.Progress))
        {
            Send();
        }
    }

    private void OnToolbarInUseChanged(object? sender, EventArgs e) => Send();

    // Inside the window's message: the bar is sent again once it has returned, to the new taskbar button and perhaps a new
    // Explorer.
    private void OnTaskbarButtonCreated()
    {
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            _shown = null;
            _taskbar?.Dispose();
            _taskbar = null;
            Send();
        });
    }

    private void Send()
    {
        if (_disposed || !WindowMessageHook.HasTaskbarButton)
        {
            return;
        }
        var progress = _toolbar.IsInUse ? DownloadProgress.None : _activity.Progress;
        ulong step = (ulong)Math.Round(progress.Fraction * Steps);
        if (_shown == (progress.State, step))
        {
            return;
        }
        try
        {
            _taskbar ??= TaskbarList.Create();
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            if (progress.State == DownloadProgressState.None)
            {
                _taskbar.SetProgressState(hwnd, TaskbarProgressFlag.NoProgress);
            }
            else
            {
                // The value first: it turns a bar that showed none into a normal one, and leaves a paused one paused.
                _taskbar.SetProgressValue(hwnd, step, Steps);
                _taskbar.SetProgressState(hwnd, progress.State == DownloadProgressState.Paused ? TaskbarProgressFlag.Paused : TaskbarProgressFlag.Normal);
            }
            _shown = (progress.State, step);
            _failureLogged = false;
        }
        catch (Exception ex)
        {
            // Never the taskbar's to stop the app: the next change tries again, with a COM object made again, since the one
            // that failed may belong to an Explorer that is gone. Said once until a send gets through.
            _taskbar?.Dispose();
            _taskbar = null;
            _shown = null;
            if (!_failureLogged)
            {
                _failureLogged = true;
                _logger.LogWarning("The downloads' progress could not be shown on the taskbar ({Failure})", ex.GetType().Name);
            }
        }
    }
}
