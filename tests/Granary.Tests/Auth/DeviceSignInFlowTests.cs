using System.Collections.Concurrent;
using Granary.Core.Auth;
using Granary.Tests.Fakes;
using static Granary.Tests.Auth.DeviceSignInFakeServer;

namespace Granary.Tests.Auth;

public sealed class DeviceSignInFlowTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly ManualTimeProvider _time = new();
    private readonly DeviceSignInFakeServer _server;

    public DeviceSignInFlowTests() => _server = new DeviceSignInFakeServer(_time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TimeSpan[] Seconds(params int[] values) => [.. values.Select(v => TimeSpan.FromSeconds(v))];

    /// <summary>Lets the next wait run out and returns once the flow waits again.</summary>
    private async Task StepAsync()
    {
        await _time.FireNextTimerAsync(Ct);
        await _time.WaitForTimerAsync(Ct);
    }

    private static Task FinishAsync(Task run) => run.WaitAsync(HangGuard, Ct);

    private static ConcurrentQueue<DeviceSignInStatus> Record(DeviceSignInFlow flow)
    {
        var statuses = new ConcurrentQueue<DeviceSignInStatus>();
        flow.StateChanged += (_, state) => statuses.Enqueue(state.Status);
        return statuses;
    }

    [Fact]
    public async Task StartAsync_ShowsTheCodeAndSignsIn_WhenApproved()
    {
        _server.Script(PollPath, Pending(), Pending(opened: true), Approved());
        await using var flow = _server.CreateFlow();
        var statuses = Record(flow);

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);

        var waiting = flow.Current;
        Assert.Equal(DeviceSignInStatus.WaitingForApproval, waiting.Status);
        Assert.Equal(new DeviceSignInCode("4821 7730", "silo.example.org/activate",
            "https://silo.example.org/activate?code=48217730", ManualTimeProvider.DefaultStart.AddSeconds(900)), waiting.Code);

        await StepAsync();
        Assert.Equal(DeviceSignInStatus.Opened, flow.Current.Status);
        Assert.NotNull(flow.Current.Code);

        await _time.FireNextTimerAsync(Ct);
        await FinishAsync(run);

        Assert.Equal(new DeviceSignInState(DeviceSignInStatus.Approved), flow.Current);
        Assert.Equal(["tokens:srv1:elena"], _server.Session.Calls);
        Assert.Equal(
            [DeviceSignInStatus.GettingCode, DeviceSignInStatus.WaitingForApproval, DeviceSignInStatus.Opened, DeviceSignInStatus.Approved],
            statuses);
        Assert.Equal(Seconds(0, 3, 6), _server.Times(PollPath));
        Assert.Equal(["device-1", "device-1", "device-1"], _server.DeviceCodes(PollPath));
        var start = Assert.Single(_server.Requests(StartPath));
        Assert.Equal("TEST-PC", start.Field("device_name"));
        Assert.Equal("windows", start.Field("device_platform"));
        Assert.Empty(_server.Requests(CancelPath));
    }

    [Fact]
    public async Task StartAsync_StopsWithoutSigningIn_WhenDenied()
    {
        _server.Script(PollPath, Pending(), Status("denied"));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.FireNextTimerAsync(Ct);
        await FinishAsync(run);

        Assert.Equal(new DeviceSignInState(DeviceSignInStatus.Denied), flow.Current);
        Assert.Empty(_server.Session.Calls);
        Assert.Single(_server.Requests(StartPath));
        Assert.Empty(_server.Requests(CancelPath));
    }

    [Theory]
    [InlineData("no tokens")]
    [InlineData("blank access token")]
    public async Task StartAsync_Fails_WhenApprovedWithoutUsableTokens(string answer)
    {
        _server.Script(PollPath, answer == "no tokens" ? Approved(withTokens: false) : Approved(accessToken: ""));
        await using var flow = _server.CreateFlow();

        await FinishAsync(flow.StartAsync(Server, Ct));

        Assert.Equal(DeviceSignInStatus.Failed, flow.Current.Status);
        Assert.Empty(_server.Session.Calls);
    }

    [Fact]
    public async Task StartAsync_Fails_WhenTheSessionRejectsTheTokens()
    {
        _server.Script(PollPath, Approved());
        _server.Session.FailWith = new InvalidOperationException("secret store unavailable");
        await using var flow = _server.CreateFlow();

        await FinishAsync(flow.StartAsync(Server, Ct));

        Assert.Equal(DeviceSignInStatus.Failed, flow.Current.Status);
        Assert.Single(_server.Session.Calls);
        Assert.Empty(_server.Requests(CancelPath));
    }

    [Fact]
    public async Task StartAsync_Fails_OnAnUnknownStatus()
    {
        _server.Script(PollPath, Status("teleported"));
        await using var flow = _server.CreateFlow();

        await FinishAsync(flow.StartAsync(Server, Ct));

        Assert.Equal(DeviceSignInStatus.Failed, flow.Current.Status);
        Assert.Equal(["device-1"], _server.DeviceCodes(CancelPath));
    }

    [Fact]
    public async Task StartAsync_Fails_WhenAPollIsRejected()
    {
        _server.Script(PollPath, Problem(400, "malformed_request"));
        await using var flow = _server.CreateFlow();

        await FinishAsync(flow.StartAsync(Server, Ct));

        Assert.Equal(DeviceSignInStatus.Failed, flow.Current.Status);
        Assert.Single(_server.Requests(StartPath));
        Assert.Equal(["device-1"], _server.DeviceCodes(CancelPath));
    }

    [Theory]
    [InlineData(400, "malformed_request", DeviceSignInStatus.Failed)]
    [InlineData(409, "capability_disabled", DeviceSignInStatus.Unavailable)]
    [InlineData(409, "capability_not_configured", DeviceSignInStatus.Unavailable)]
    [InlineData(501, "capability_unsupported", DeviceSignInStatus.Unavailable)]
    public async Task StartAsync_Ends_WhenTheServerRefusesACode(int status, string code, DeviceSignInStatus expected)
    {
        _server.Script(StartPath, Problem(status, code));
        await using var flow = _server.CreateFlow();

        await FinishAsync(flow.StartAsync(Server, Ct));

        Assert.Equal(new DeviceSignInState(expected), flow.Current);
        Assert.Single(_server.Requests(StartPath));
        Assert.Empty(_server.Requests(PollPath));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("consumed")]
    [InlineData("canceled")]
    [InlineData("404")]
    public async Task StartAsync_ReplacesTheCodeInPlace_WhenTheServerDropsIt(string gone)
    {
        _server.Script(StartPath, _server.StartAnswer("device-1", "1111-1111"), _server.StartAnswer("device-2", "2222-2222"));
        _server.Script(PollPath, Pending(), gone == "404" ? Problem(404, "not_found") : Status(gone), Pending());
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        await StepAsync();

        // The code lived 3 s: a new one follows after a short pause.
        Assert.Equal(TimeSpan.FromSeconds(1), _time.NextDue);
        await StepAsync();

        Assert.Equal(DeviceSignInStatus.WaitingForApproval, flow.Current.Status);
        Assert.Equal("2222 2222", flow.Current.Code?.UserCode);
        Assert.Equal(Seconds(0, 4), _server.Times(StartPath));
        Assert.Equal(["device-1", "device-1", "device-2"], _server.DeviceCodes(PollPath));
        Assert.Empty(_server.Requests(CancelPath));

        flow.Stop();
        await FinishAsync(run);
        Assert.Equal(["device-2"], _server.DeviceCodes(CancelPath));
    }

    [Fact]
    public async Task StartAsync_PacesNewCodes_WhenCodesKeepVanishingAtOnce()
    {
        _server.Script(PollPath, Status("expired"));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        var waits = new List<TimeSpan> { _time.NextDue!.Value };
        for (int i = 0; i < 3; i++)
        {
            await StepAsync();
            waits.Add(_time.NextDue!.Value);
        }

        Assert.Equal(Seconds(1, 2, 4, 8), waits);
        Assert.Equal(4, _server.Requests(StartPath).Count);

        flow.Stop();
        await FinishAsync(run);
    }

    [Fact]
    public async Task StartAsync_HonoursRetryAfter_On429()
    {
        _server.Script(PollPath, Pending(), Problem(429, "rate_limited", TimeSpan.FromSeconds(7)), Pending());
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        await StepAsync();

        Assert.Equal(DeviceSignInStatus.TooManyRequests, flow.Current.Status);
        Assert.NotNull(flow.Current.Code);
        Assert.Equal(TimeSpan.FromSeconds(7), _time.NextDue);

        await StepAsync();
        Assert.Equal(DeviceSignInStatus.WaitingForApproval, flow.Current.Status);
        Assert.Equal(Seconds(0, 3, 10), _server.Times(PollPath));

        flow.Stop();
        await FinishAsync(run);
    }

    [Fact]
    public async Task StartAsync_HonoursRetryAfter_WhenGettingACodeIsRateLimited()
    {
        _server.Script(StartPath, Problem(429, "rate_limited", TimeSpan.FromSeconds(20)), _server.StartAnswer("device-1", "4821-7730"));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);

        Assert.Equal(new DeviceSignInState(DeviceSignInStatus.TooManyRequests), flow.Current);
        Assert.Equal(TimeSpan.FromSeconds(20), _time.NextDue);

        await StepAsync();
        Assert.Equal(DeviceSignInStatus.WaitingForApproval, flow.Current.Status);
        Assert.Equal(Seconds(0, 20), _server.Times(StartPath));

        flow.Stop();
        await FinishAsync(run);
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(10, 10)]
    public async Task StartAsync_PollsEvery_MaxOfPollAfterAndInterval(int pollAfter, int expectedWait)
    {
        _server.Script(StartPath, _server.StartAnswer("device-1", "4821-7730", interval: 5));
        _server.Script(PollPath, Pending(pollAfter: pollAfter));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);

        Assert.Equal(TimeSpan.FromSeconds(expectedWait), _time.NextDue);

        flow.Stop();
        await FinishAsync(run);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("503")]
    public async Task StartAsync_BacksOffOnTransientErrors_DoublingUpTo30Seconds(string error)
    {
        _server.Script(PollPath, Pending(), error == "network" ? NetworkError() : Problem(503, "dependency_unavailable"));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        var waits = new List<TimeSpan>();
        var statuses = new List<DeviceSignInStatus>();
        for (int i = 0; i < 6; i++)
        {
            await StepAsync();
            waits.Add(_time.NextDue!.Value);
            statuses.Add(flow.Current.Status);
        }

        // Failed polls at 3, 6, 12, 24, 48 and 78 s: "can't reach" once they have failed for 30 s.
        Assert.Equal(Seconds(3, 6, 12, 24, 30, 30), waits);
        Assert.Equal(
            [DeviceSignInStatus.WaitingForApproval, DeviceSignInStatus.WaitingForApproval, DeviceSignInStatus.WaitingForApproval,
             DeviceSignInStatus.WaitingForApproval, DeviceSignInStatus.Unreachable, DeviceSignInStatus.Unreachable],
            statuses);
        Assert.NotNull(flow.Current.Code);

        _server.Script(PollPath, Pending());
        await StepAsync();
        Assert.Equal(DeviceSignInStatus.WaitingForApproval, flow.Current.Status);
        Assert.Equal(TimeSpan.FromSeconds(3), _time.NextDue);

        flow.Stop();
        await FinishAsync(run);
    }

    [Fact]
    public async Task StartAsync_TimesOutHungPolls_AndReportsUnreachable()
    {
        _server.Script(PollPath, Pending(), Hang());
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        for (int i = 0; i < 10 && flow.Current.Status != DeviceSignInStatus.Unreachable; i++)
        {
            await StepAsync();
        }

        // Polls at 3, 21 and 42 s each time out after 15 s; the third failure is 39 s after the first.
        Assert.Equal(DeviceSignInStatus.Unreachable, flow.Current.Status);
        Assert.Equal(TimeSpan.FromSeconds(57), _time.Elapsed);
        Assert.Equal(Seconds(0, 3, 21, 42), _server.Times(PollPath));

        flow.Stop();
        await FinishAsync(run);
    }

    [Fact]
    public async Task StartAsync_BacksOffAndReportsUnreachable_WhenNoCodeCanBeHad()
    {
        _server.Script(StartPath, NetworkError());
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        var waits = new List<TimeSpan> { _time.NextDue!.Value };
        for (int i = 0; i < 4; i++)
        {
            await StepAsync();
            waits.Add(_time.NextDue!.Value);
        }

        Assert.Equal(Seconds(1, 2, 4, 8, 16), waits);
        Assert.Equal(new DeviceSignInState(DeviceSignInStatus.Unreachable), flow.Current);

        flow.Stop();
        await FinishAsync(run);
        Assert.Empty(_server.Requests(CancelPath));
    }

    [Fact]
    public async Task StartAsync_WithdrawsAndReplacesTheCode_AtTheLocalDeadline()
    {
        _server.Script(StartPath,
            _server.StartAnswer("device-1", "1111-1111", expiresIn: 60, interval: 10),
            _server.StartAnswer("device-2", "2222-2222"));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        for (int i = 0; i < 6; i++)
        {
            await StepAsync();
        }

        Assert.Equal(Seconds(0, 10, 20, 30, 40, 50, 60, 60), _server.Times(PollPath));
        var cancel = Assert.Single(_server.Requests(CancelPath));
        Assert.Equal("device-1", cancel.Field("device_code"));
        Assert.Equal(TimeSpan.FromSeconds(60), cancel.At);
        Assert.Equal(Seconds(0, 60), _server.Times(StartPath));
        Assert.Equal("2222 2222", flow.Current.Code?.UserCode);

        flow.Stop();
        await FinishAsync(run);
    }

    [Fact]
    public async Task StartAsync_MovesTheDeadline_ToThePendingExpiry_DespiteClockSkew()
    {
        var skew = TimeSpan.FromHours(1);
        var serverStart = ManualTimeProvider.DefaultStart + skew;
        _server.Script(StartPath,
            _server.StartAnswer("device-1", "1111-1111", expiresIn: 60, interval: 10, serverSkew: skew),
            _server.StartAnswer("device-2", "2222-2222", serverSkew: skew));
        // An approver's lookup extended the request from 60 to 120 seconds (server time).
        _server.Script(PollPath, Pending(pollAfter: 10, expiresAt: serverStart.AddSeconds(120)));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        Assert.Equal(ManualTimeProvider.DefaultStart.AddSeconds(120), flow.Current.Code?.ExpiresAt);

        for (int i = 0; i < 20 && _server.Requests(CancelPath).Count == 0; i++)
        {
            await StepAsync();
        }

        var cancel = Assert.Single(_server.Requests(CancelPath));
        Assert.Equal(TimeSpan.FromSeconds(120), cancel.At);
        Assert.Equal(Seconds(0, 120), _server.Times(StartPath));

        flow.Stop();
        await FinishAsync(run);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_WithdrawsTheCode_OnlyWhenTheServerSupportsCancel(bool supportsCancel)
    {
        _server.Script(CapabilityPath, Json(CapabilityJson(cancel: supportsCancel)));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        flow.Stop();
        await FinishAsync(run);

        string?[] expected = supportsCancel ? ["device-1"] : [];
        Assert.Equal(expected, _server.DeviceCodes(CancelPath));
        Assert.Equal(DeviceSignInState.Idle, flow.Current);
        Assert.Null(_time.NextDue);
    }

    [Fact]
    public async Task Stop_StillWithdrawsTheCode_WhenTheCapabilityCannotBeRead()
    {
        _server.Script(CapabilityPath, NetworkError());
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        flow.Stop();
        await FinishAsync(run);

        Assert.Equal(["device-1"], _server.DeviceCodes(CancelPath));
    }

    [Fact]
    public async Task Stop_IgnoresAFailingCancel()
    {
        _server.Script(CancelPath, Problem(500, "internal_error"));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        flow.Stop();
        await FinishAsync(run);

        Assert.Single(_server.Requests(CancelPath));
        Assert.Equal(DeviceSignInState.Idle, flow.Current);
    }

    [Fact]
    public async Task Stop_GivesUpOnAHungCancel_After5Seconds()
    {
        _server.Script(CancelPath, Hang());
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        flow.Stop();
        await _time.WaitUntilAsync(() => _time.NextDue == TimeSpan.FromSeconds(5), Ct);
        _time.Advance(TimeSpan.FromSeconds(5));
        await FinishAsync(run);

        Assert.Single(_server.Requests(CancelPath));
        Assert.Equal(DeviceSignInState.Idle, flow.Current);
    }

    [Fact]
    public async Task Stop_InterruptsAHungRequestForACode()
    {
        _server.Script(StartPath, Hang());
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        flow.Stop();
        await FinishAsync(run);

        Assert.Equal(TimeSpan.Zero, _time.Elapsed);
        Assert.Equal(DeviceSignInState.Idle, flow.Current);
        Assert.Empty(_server.Requests(CancelPath));
    }

    [Fact]
    public async Task Stop_DropsAnApprovalThatArrivesAfterwards()
    {
        var approval = new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Script(PollPath, Pending(), Held(approval));
        await using var flow = _server.CreateFlow();

        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        await StepAsync(); // the second poll is in flight; its call timeout is the timer
        flow.Stop();
        approval.SetResult(Approved());
        await FinishAsync(run);

        Assert.Empty(_server.Session.Calls);
        Assert.Equal(DeviceSignInState.Idle, flow.Current);
        Assert.Equal(["device-1"], _server.DeviceCodes(CancelPath));
    }

    [Fact]
    public async Task DisposeAsync_StopsTheRunAndWithdrawsTheCode()
    {
        var flow = _server.CreateFlow();
        var run = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);

        await flow.DisposeAsync();
        await FinishAsync(run);

        Assert.Equal(["device-1"], _server.DeviceCodes(CancelPath));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => flow.StartAsync(Server, Ct));
    }

    [Fact]
    public async Task StartAsync_SupersedesTheRunningSignIn()
    {
        _server.Script(StartPath, _server.StartAnswer("device-1", "1111-1111"), _server.StartAnswer("device-2", "2222-2222"));
        await using var flow = _server.CreateFlow();

        var first = flow.StartAsync(Server, Ct);
        await _time.WaitForTimerAsync(Ct);
        var second = flow.StartAsync(Server, Ct);
        await FinishAsync(first);
        await _time.WaitUntilAsync(() => flow.Current.Code?.UserCode == "2222 2222", Ct);

        Assert.Equal(["device-1"], _server.DeviceCodes(CancelPath));
        Assert.Equal(DeviceSignInStatus.WaitingForApproval, flow.Current.Status);

        flow.Stop();
        await FinishAsync(second);
    }

    [Fact]
    public async Task StartAsync_ReportsUnavailable_WithoutAskingForACode()
    {
        _server.Script(CapabilityPath, Json(CapabilityJson(state: "disabled")));
        await using var flow = _server.CreateFlow();

        await FinishAsync(flow.StartAsync(Server, Ct));

        Assert.Equal(new DeviceSignInState(DeviceSignInStatus.Unavailable), flow.Current);
        Assert.Empty(_server.Requests(StartPath));
    }

    [Fact]
    public async Task StartAsync_ReportsUnavailable_WhenTheServerHasNoDeviceSignIn()
    {
        _server.Script(CapabilityPath, Problem(404, "not_found"));
        _server.Script(StartPath, Problem(404, "not_found"));
        await using var flow = _server.CreateFlow();

        await FinishAsync(flow.StartAsync(Server, Ct));

        Assert.Equal(DeviceSignInStatus.Unavailable, flow.Current.Status);
        Assert.Single(_server.Requests(StartPath));
    }

    [Fact]
    public async Task StartAsync_Throws_WhenTheCallerCancels()
    {
        await using var flow = _server.CreateFlow();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = flow.StartAsync(Server, cts.Token);
        await _time.WaitForTimerAsync(Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FinishAsync(run));
        Assert.Equal(["device-1"], _server.DeviceCodes(CancelPath));
    }

    [Theory]
    [InlineData("4821-7730", "4821 7730")]
    [InlineData("48217730", "4821 7730")]
    [InlineData(" 4821 7730 ", "4821 7730")]
    [InlineData("ABCD-EFGH", "ABCD-EFGH")]
    [InlineData("12345", "12345")]
    public void FormatUserCode_GroupsEightDigits(string userCode, string expected) =>
        Assert.Equal(expected, DeviceSignInCode.FormatUserCode(userCode));

    [Theory]
    [InlineData("https://silo.example.org/activate", "", "silo.example.org/activate")]
    [InlineData("http://192.168.1.10:8090/activate", "", "192.168.1.10:8090/activate")]
    [InlineData("https://nas.example/silo/activate/", "", "nas.example/silo/activate")]
    [InlineData("", "https://nas.example/activate?code=48217730", "nas.example/activate")]
    public void FormatVerificationText_DropsTheScheme(string uri, string complete, string expected) =>
        Assert.Equal(expected, DeviceSignInCode.FormatVerificationText(uri, complete));
}
