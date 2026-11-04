using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>A change to mpv.conf made in Granary: it waits until a title has started with it.</summary>
public sealed class MpvConfigChangesTests
{
    private readonly MpvConfigStatus _status = new();

    private MpvConfigChanges Changes(IUiDispatcher? ui = null) => new(_status, ui ?? new ImmediateDispatcher());

    private void ATitleOpens()
    {
        _status.ReportTitleOpening();
        _status.ReportTitleOpened();
    }

    [Fact]
    public void ASave_IsPending_UntilATitleStartedWithIt()
    {
        var changes = Changes();
        Assert.False(changes.IsPending);
        int raised = 0;
        changes.Changed += (_, _) => raised++;

        changes.Saved();

        Assert.True(changes.IsPending);
        Assert.Equal(1, raised);

        ATitleOpens();

        Assert.False(changes.IsPending);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ATitleStillOpening_LeavesASaveThatCameBeforeItOpens_Pending()
    {
        var changes = Changes();
        changes.Saved();

        _status.ReportTitleOpening();

        Assert.True(changes.IsPending);
    }

    [Fact]
    public void ASaveAfterATitleReadTheFile_StaysPending_ForTheNextTitle()
    {
        var changes = Changes();
        changes.Saved();

        _status.ReportTitleOpening();
        // The title read the file before this.
        changes.Saved();
        _status.ReportTitleOpened();

        Assert.True(changes.IsPending);

        ATitleOpens();

        Assert.False(changes.IsPending);
    }

    [Fact]
    public void ASaveBeforeTheUiHeardOfATitle_StaysPending()
    {
        var ui = new HeldDispatcher();
        var changes = Changes(ui);
        changes.Saved();

        // The title opened on the player's thread; the UI hears of it only after a newer save.
        ATitleOpens();
        changes.Saved();
        ui.RunAll();

        Assert.True(changes.IsPending);
    }

    [Fact]
    public async Task ATitleOnAnotherThread_IsSaidOnTheUiThread()
    {
        using var ui = new SerialDispatcher();
        var changes = await ui.InvokeAsync(() => Changes(ui));
        await ui.InvokeAsync(() =>
        {
            changes.Saved();
            return 0;
        });
        var said = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        changes.Changed += (_, _) => said.TrySetResult(ui.HasThreadAccess);

        await Task.Run(ATitleOpens, TestContext.Current.CancellationToken);

        Assert.True(await said.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(ui.Read(() => changes.IsPending));
    }

    // A UI thread that runs what was posted only when told: the order of a title and a save can be set.
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
