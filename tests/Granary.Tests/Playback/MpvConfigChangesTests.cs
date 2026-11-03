using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>A change to mpv.conf made in Granary, and Apply now: the player's mpv started again, never during a title.</summary>
public sealed class MpvConfigChangesTests
{
    private readonly FakeMpvRestart _player = new();

    private MpvConfigChanges Changes(IUiDispatcher? ui = null) => new(_player, ui ?? new ImmediateDispatcher());

    [Fact]
    public void ASave_WhileThePlayerRuns_IsPending()
    {
        var changes = Changes();
        Assert.Equal(MpvConfigApply.None, changes.State);

        changes.Saved();

        Assert.Equal(MpvConfigApply.Pending, changes.State);
        Assert.True(changes.IsPending);
    }

    [Fact]
    public async Task ApplyNow_WithNothingPlaying_StartsThePlayerAgainAtOnce()
    {
        var changes = Changes();
        changes.Saved();
        int raised = 0;
        changes.Changed += (_, _) => raised++;

        await changes.ApplyNowAsync();

        Assert.Equal(1, _player.Restarts);
        Assert.Equal(MpvConfigApply.None, changes.State);
        Assert.False(changes.IsPending);
        Assert.True(raised > 0);
    }

    [Fact]
    public async Task ApplyNow_WhileATitlePlays_WaitsForTheNextTitle()
    {
        var changes = Changes();
        changes.Saved();
        _player.IsPlaying = true;

        await changes.ApplyNowAsync();

        Assert.Equal(0, _player.Restarts);
        Assert.Equal(MpvConfigApply.NextTitle, changes.State);

        // A save meanwhile is read by the same start.
        changes.Saved();
        Assert.Equal(MpvConfigApply.NextTitle, changes.State);

        _player.TitleEnds();

        Assert.Equal(1, _player.Restarts);
        Assert.Equal(MpvConfigApply.None, changes.State);
    }

    [Fact]
    public async Task ASave_BeforeThePlayerFirstStarted_AppliesToTheNextTitle_WithoutARestart()
    {
        _player.IsRunning = false;
        var changes = Changes();

        changes.Saved();
        await changes.ApplyNowAsync();

        Assert.Equal(MpvConfigApply.NextTitle, changes.State);
        Assert.Equal(0, _player.Restarts);

        _player.Starts();

        Assert.Equal(MpvConfigApply.None, changes.State);
    }

    [Fact]
    public async Task ApplyNow_WithNothingToApply_DoesNothing()
    {
        var changes = Changes();

        await changes.ApplyNowAsync();

        Assert.Equal(0, _player.Restarts);
        Assert.Equal(MpvConfigApply.None, changes.State);
    }

    [Fact]
    public void ASaveAfterMpvStartedAgain_ButBeforeTheUiHeardOfIt_IsStillToApply()
    {
        var ui = new HeldDispatcher();
        var changes = Changes(ui);
        changes.Saved();

        // mpv starts again (the engine's own thread); the UI hears of it only after a newer save.
        _player.Restart();
        changes.Saved();
        ui.RunAll();

        Assert.Equal(MpvConfigApply.Pending, changes.State);
    }

    [Fact]
    public async Task ASaveWhileWaiting_ThatTheStartMissed_IsOfferedAgain()
    {
        var ui = new HeldDispatcher();
        var changes = Changes(ui);
        changes.Saved();
        _player.IsPlaying = true;
        await changes.ApplyNowAsync();
        ui.RunAll();
        Assert.Equal(MpvConfigApply.NextTitle, changes.State);

        // The waiting start happens, then a save comes before the UI hears of it.
        _player.TitleEnds();
        changes.Saved();
        ui.RunAll();

        Assert.Equal(MpvConfigApply.Pending, changes.State);
    }

    [Fact]
    public async Task ARestartOnAnotherThread_IsSaidOnTheUiThread()
    {
        using var ui = new SerialDispatcher();
        var changes = await ui.InvokeAsync(() => Changes(ui));
        await ui.InvokeAsync(() =>
        {
            changes.Saved();
            return 0;
        });
        _player.IsPlaying = true;
        await ui.RunAsync(changes.ApplyNowAsync);
        var said = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        changes.Changed += (_, _) => said.TrySetResult(ui.HasThreadAccess);

        // The engine starts mpv again on its own thread once the title ends.
        await Task.Run(_player.TitleEnds, TestContext.Current.CancellationToken);

        Assert.True(await said.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(MpvConfigApply.None, ui.Read(() => changes.State));
    }

    // A UI thread that runs what was posted only when told: the order of a start and a save can be set.
    private sealed class HeldDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _work = new();

        public bool HasThreadAccess => false;

        public void Post(Action action) => _work.Enqueue(action);

        public void RunAll()
        {
            while (_work.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
