using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Servers;

namespace Granary.Core.ViewModels;

/// <summary>
/// The QR code panel of the sign-in page: one <see cref="DeviceSignInFlow"/> run per <see cref="Start"/>,
/// shown on the UI thread. On approval the session manager is signed in and the shell moves on.
/// </summary>
public sealed partial class DeviceSignInViewModel : ObservableObject, IDisposable
{
    private readonly DeviceSignInFlow _flow;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ServerEntry? _server;

    // Bumped by every Start and Stop, so the end of an older run cannot touch the current one.
    private int _run;

    public DeviceSignInViewModel(DeviceSignInFlow flow, IUiDispatcher dispatcher, TimeProvider time)
    {
        _flow = flow;
        _dispatcher = dispatcher;
        _time = time;
        _flow.StateChanged += OnStateChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial DeviceSignInStatus Status { get; set; }

    /// <summary>The code grouped for display, for example <c>4821 7730</c>.</summary>
    [ObservableProperty]
    public partial string UserCode { get; set; } = "";

    /// <summary>The address to type, for example <c>silo.example.org/activate</c>.</summary>
    [ObservableProperty]
    public partial string VerificationText { get; set; } = "";

    /// <summary>The text to encode as QR code (the verification URI with the code).</summary>
    [ObservableProperty]
    public partial string QrPayload { get; set; } = "";

    [ObservableProperty]
    public partial string ExpiryText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasCode { get; set; }

    /// <summary>Someone opened the code on another device: "continue on your phone".</summary>
    [ObservableProperty]
    public partial bool IsOpened { get; set; }

    /// <summary>Working or waiting for the approver (for a progress indicator).</summary>
    [ObservableProperty]
    public partial bool IsWaiting { get; set; }

    /// <summary>A sign-in run is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool IsActive { get; set; }

    /// <summary>The run ended in a way a new code may fix (declined or failed).</summary>
    public bool CanRetry => !IsActive && _server is not null && Status is DeviceSignInStatus.Denied or DeviceSignInStatus.Failed;

    /// <summary>The current run, for tests.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Starts device sign-in on <paramref name="server"/>, replacing any running one.</summary>
    public void Start(ServerEntry server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
        int run = ++_run;
        IsActive = true;
        Show(new DeviceSignInState(DeviceSignInStatus.GettingCode));
        Running = RunAsync(server, run);
    }

    /// <summary>Stops the running sign-in, withdraws its code and clears the panel.</summary>
    public void Stop()
    {
        _run++;
        IsActive = false;
        _flow.Stop();
        Show(DeviceSignInState.Idle);
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry()
    {
        if (_server is { } server)
        {
            Start(server);
        }
    }

    private async Task RunAsync(ServerEntry server, int run)
    {
        try
        {
            await _flow.StartAsync(server);
        }
        finally
        {
            _dispatcher.Post(() =>
            {
                if (run == _run)
                {
                    IsActive = false;
                }
            });
        }
    }

    // Shows the flow's latest state rather than the event's, so a late event from an older run cannot win.
    private void OnStateChanged(object? sender, DeviceSignInState state) => _dispatcher.Post(() =>
    {
        if (IsActive)
        {
            Show(_flow.Current);
        }
    });

    private void Show(DeviceSignInState state)
    {
        var code = state.Code;
        Status = state.Status;
        HasCode = code is not null;
        UserCode = code?.UserCode ?? "";
        VerificationText = code?.VerificationText ?? "";
        QrPayload = code?.QrPayload ?? "";
        ExpiryText = code is null ? "" : $"Valid until {FormatTime(code.ExpiresAt)}";
        IsOpened = state.Status == DeviceSignInStatus.Opened;
        IsWaiting = state.Status is DeviceSignInStatus.GettingCode or DeviceSignInStatus.WaitingForApproval
            or DeviceSignInStatus.Opened or DeviceSignInStatus.Unreachable or DeviceSignInStatus.TooManyRequests;
        StatusText = Describe(state.Status);
    }

    private string FormatTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _time.LocalTimeZone).ToString("t", CultureInfo.CurrentCulture);

    private static string Describe(DeviceSignInStatus status) => status switch
    {
        DeviceSignInStatus.GettingCode => "Getting a sign-in code…",
        DeviceSignInStatus.WaitingForApproval => "Waiting for approval…",
        DeviceSignInStatus.Opened => "Code opened. Continue on your phone.",
        DeviceSignInStatus.Unreachable => "Cannot reach the server. Still trying…",
        DeviceSignInStatus.TooManyRequests => "Too many requests. Trying again shortly…",
        DeviceSignInStatus.Approved => "Signed in.",
        DeviceSignInStatus.Denied => "The sign-in was declined.",
        DeviceSignInStatus.Unavailable => "This server does not offer sign-in with a code. Use your password.",
        DeviceSignInStatus.Failed => "Sign-in with a code did not work.",
        _ => "",
    };

    public void Dispose()
    {
        _flow.StateChanged -= OnStateChanged;
        _run++;
        _flow.Stop();
    }
}
