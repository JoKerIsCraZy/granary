using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>Granary's own updates: looking for one, the notice once it is ready, and handing it over as the app exits.</summary>
public sealed class AppUpdatesViewModelTests
{
    private readonly FakeUpdater _updater = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly LogSink _logs = new();

    private AppUpdatesViewModel Create() => new(_updater, _clock, _logs.For<AppUpdatesViewModel>());

    [Fact]
    public async Task ACopyThatCannotUpdate_NeverLooks()
    {
        _updater.CanUpdate = false;
        var updates = Create();

        await updates.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _updater.Checks);
        Assert.False(updates.IsNoticeShown);
    }

    [Fact]
    public async Task AReleaseOnTheFirstLook_IsOfferedAndAppliedWhenGranaryCloses()
    {
        _updater.Answers.Enqueue(() => "0.2.0");
        var updates = Create();

        await updates.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(updates.IsNoticeShown);
        Assert.Equal("Granary 0.2.0 is ready to install", updates.NoticeText);
        updates.LaterCommand.Execute(null);
        Assert.False(updates.IsNoticeShown);
        updates.OnExiting();
        Assert.Equal([false], _updater.Applied);
    }

    [Fact]
    public async Task NothingNew_LooksAgainSixHoursLater_AndAFailureToo()
    {
        _updater.Answers.Enqueue(() => null);
        _updater.Answers.Enqueue(() => throw new HttpRequestException("offline"));
        _updater.Answers.Enqueue(() => "0.3.0");
        var updates = Create();
        var ct = TestContext.Current.CancellationToken;

        var run = updates.RunAsync(ct);
        await _clock.WaitForTimerAsync(ct);
        Assert.Equal(1, _updater.Checks);
        Assert.Equal(AppUpdatesViewModel.CheckEvery, await _clock.FireNextTimerAsync(ct));
        await _clock.WaitUntilAsync(() => _updater.Checks == 2 && _clock.NextDue is not null, ct);
        Assert.Contains("Could not look for a Granary update", _logs.All, StringComparison.Ordinal);
        await _clock.FireNextTimerAsync(ct);
        await run;

        Assert.Equal(3, _updater.Checks);
        Assert.Equal("0.3.0", updates.ReadyVersion);
        Assert.True(updates.IsNoticeShown);
    }

    [Fact]
    public async Task RestartNow_ClosesTheWindow_AndTheUpdateRestartsGranary()
    {
        _updater.Answers.Enqueue(() => "0.2.0");
        var updates = Create();
        int closes = 0;
        updates.CloseRequested += (_, _) => closes++;
        await updates.RunAsync(TestContext.Current.CancellationToken);

        updates.RestartNowCommand.Execute(null);
        updates.OnExiting();

        Assert.Equal(1, closes);
        Assert.False(updates.IsNoticeShown);
        Assert.Equal([true], _updater.Applied);
    }

    [Fact]
    public async Task WithoutAnUpdate_ClosingAppliesNothing_AndRestartDoesNothing()
    {
        _updater.Answers.Enqueue(() => null);
        var updates = Create();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int closes = 0;
        updates.CloseRequested += (_, _) => closes++;

        var run = updates.RunAsync(stop.Token);
        await _clock.WaitForTimerAsync(TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await run;
        updates.RestartNowCommand.Execute(null);
        updates.OnExiting();

        Assert.Equal(0, closes);
        Assert.Empty(_updater.Applied);
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public bool CanUpdate { get; set; } = true;

        public Queue<Func<string?>> Answers { get; } = new();

        public int Checks { get; private set; }

        public List<bool> Applied { get; } = [];

        public Task<string?> CheckAndDownloadAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Task.FromResult(Answers.Dequeue()());
        }

        public void ApplyWhenExited(bool restart) => Applied.Add(restart);
    }
}
