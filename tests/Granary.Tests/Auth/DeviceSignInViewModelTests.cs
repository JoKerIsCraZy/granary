using System.Globalization;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Auth.DeviceSignInFakeServer;

namespace Granary.Tests.Auth;

public sealed class DeviceSignInViewModelTests : IAsyncDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly ManualTimeProvider _time = new();
    private readonly DeviceSignInFakeServer _server;
    private readonly DeviceSignInFlow _flow;
    private readonly DeviceSignInViewModel _viewModel;

    public DeviceSignInViewModelTests()
    {
        _server = new DeviceSignInFakeServer(_time);
        _flow = _server.CreateFlow();
        _viewModel = new DeviceSignInViewModel(_flow, new ImmediateDispatcher(), _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _viewModel.Dispose();
        await _flow.DisposeAsync();
    }

    [Fact]
    public async Task Start_ShowsTheCodeWhileWaiting()
    {
        _viewModel.Start(Server);
        await _time.WaitForTimerAsync(Ct);

        Assert.True(_viewModel.IsActive);
        Assert.True(_viewModel.IsWaiting);
        Assert.True(_viewModel.HasCode);
        Assert.False(_viewModel.IsOpened);
        Assert.Equal(DeviceSignInStatus.WaitingForApproval, _viewModel.Status);
        Assert.Equal("4821 7730", _viewModel.UserCode);
        Assert.Equal("silo.example.org/activate", _viewModel.VerificationText);
        Assert.Equal("https://silo.example.org/activate?code=48217730", _viewModel.QrPayload);
        Assert.Equal("Waiting for approval…", _viewModel.StatusText);
        string expiry = ManualTimeProvider.DefaultStart.AddSeconds(900).ToString("t", CultureInfo.CurrentCulture);
        Assert.Equal($"Valid until {expiry}", _viewModel.ExpiryText);
        Assert.False(_viewModel.CanRetry);
    }

    [Fact]
    public async Task Start_TellsTheUserToContinueOnThePhone_WhenTheCodeIsOpened()
    {
        _server.Script(PollPath, Pending(opened: true));

        _viewModel.Start(Server);
        await _time.WaitForTimerAsync(Ct);

        Assert.True(_viewModel.IsOpened);
        Assert.True(_viewModel.HasCode);
        Assert.Contains("Continue on your phone", _viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_EndsSignedIn_WhenApproved()
    {
        _server.Script(PollPath, Pending(), Approved());

        _viewModel.Start(Server);
        await _time.FireNextTimerAsync(Ct);
        await _viewModel.Running.WaitAsync(HangGuard, Ct);

        Assert.Equal(DeviceSignInStatus.Approved, _viewModel.Status);
        Assert.Equal("Signed in.", _viewModel.StatusText);
        Assert.False(_viewModel.IsActive);
        Assert.False(_viewModel.IsWaiting);
        Assert.False(_viewModel.HasCode);
        Assert.Equal("", _viewModel.QrPayload);
        Assert.Equal(["tokens:srv1:elena"], _server.Session.Calls);
    }

    [Fact]
    public async Task Stop_ClearsThePanelAndWithdrawsTheCode()
    {
        _viewModel.Start(Server);
        await _time.WaitForTimerAsync(Ct);

        _viewModel.Stop();
        await _viewModel.Running.WaitAsync(HangGuard, Ct);

        Assert.False(_viewModel.IsActive);
        Assert.False(_viewModel.IsWaiting);
        Assert.False(_viewModel.HasCode);
        Assert.Equal("", _viewModel.UserCode);
        Assert.Equal("", _viewModel.StatusText);
        Assert.Equal(DeviceSignInStatus.Idle, _viewModel.Status);
        Assert.Equal(["device-1"], _server.DeviceCodes(CancelPath));
    }

    [Fact]
    public async Task Retry_GetsANewCode_AfterTheSignInWasDeclined()
    {
        _server.Script(StartPath, _server.StartAnswer("device-1", "1111-1111"), _server.StartAnswer("device-2", "2222-2222"));
        _server.Script(PollPath, Status("denied"), Pending());

        _viewModel.Start(Server);
        await _viewModel.Running.WaitAsync(HangGuard, Ct);

        Assert.Equal(DeviceSignInStatus.Denied, _viewModel.Status);
        Assert.Equal("The sign-in was declined.", _viewModel.StatusText);
        Assert.True(_viewModel.CanRetry);
        Assert.True(_viewModel.RetryCommand.CanExecute(null));

        _viewModel.RetryCommand.Execute(null);
        await _time.WaitForTimerAsync(Ct);

        Assert.True(_viewModel.IsActive);
        Assert.False(_viewModel.CanRetry);
        Assert.Equal("2222 2222", _viewModel.UserCode);
    }

    [Fact]
    public async Task Start_ExplainsThatOnlyAPasswordWorks_WhenTheServerHasNoDeviceSignIn()
    {
        _server.Script(CapabilityPath, Json(CapabilityJson(state: "disabled")));

        _viewModel.Start(Server);
        await _viewModel.Running.WaitAsync(HangGuard, Ct);

        Assert.Equal(DeviceSignInStatus.Unavailable, _viewModel.Status);
        Assert.Contains("password", _viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(_viewModel.IsActive);
        Assert.False(_viewModel.CanRetry);
    }

    [Fact]
    public async Task Start_ShowsUnreachable_ButKeepsTheCode()
    {
        _server.Script(PollPath, Pending(), NetworkError());

        _viewModel.Start(Server);
        await _time.WaitForTimerAsync(Ct);
        while (_viewModel.Status != DeviceSignInStatus.Unreachable && _time.Elapsed < TimeSpan.FromMinutes(2))
        {
            await _time.FireNextTimerAsync(Ct);
            await _time.WaitForTimerAsync(Ct);
        }

        Assert.Equal(DeviceSignInStatus.Unreachable, _viewModel.Status);
        Assert.Equal("Cannot reach the server. Still trying…", _viewModel.StatusText);
        Assert.True(_viewModel.HasCode);
        Assert.True(_viewModel.IsWaiting);
    }

    [Fact]
    public async Task Dispose_StopsListeningToTheFlow()
    {
        _server.Script(StartPath, _server.StartAnswer("device-1", "1111-1111"), _server.StartAnswer("device-2", "2222-2222"));
        _viewModel.Start(Server);
        await _time.WaitForTimerAsync(Ct);

        _viewModel.Dispose();
        var run = _flow.StartAsync(Server, Ct);
        await _time.WaitUntilAsync(() => _flow.Current.Code?.UserCode == "2222 2222", Ct);

        Assert.Equal("1111 1111", _viewModel.UserCode);

        _flow.Stop();
        await run.WaitAsync(HangGuard, Ct);
    }

    [Fact]
    public async Task Stop_IgnoresStateThatArrivesAfterwards()
    {
        _viewModel.Start(Server);
        await _time.WaitForTimerAsync(Ct);

        _viewModel.Stop();
        await _viewModel.Running.WaitAsync(HangGuard, Ct);
        _server.Script(PollPath, Pending(opened: true));
        var run = _flow.StartAsync(Server, Ct);
        await _time.WaitUntilAsync(() => _flow.Current.Status == DeviceSignInStatus.Opened, Ct);

        Assert.Equal(DeviceSignInStatus.Idle, _viewModel.Status);
        Assert.False(_viewModel.HasCode);

        _flow.Stop();
        await run.WaitAsync(HangGuard, Ct);
    }
}
