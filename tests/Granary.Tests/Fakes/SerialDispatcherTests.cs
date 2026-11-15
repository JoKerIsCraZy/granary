namespace Granary.Tests.Fakes;

/// <summary>
/// The tests' UI thread keeps to what the app's DispatcherQueue does: work posted once it is gone is dropped, as
/// <c>TryEnqueue</c> drops it, never thrown back at the thread that posted it. That can be the thread pool, a continuation
/// coming back to the UI thread after its test ended, where a throw ends the whole test run.
/// </summary>
public sealed class SerialDispatcherTests
{
    [Fact]
    public void WorkPostedAfterTheThreadWent_IsDropped()
    {
        var ui = new SerialDispatcher();
        ui.Dispose();
        bool ran = false;

        ui.Post(() => ran = true);

        Assert.False(ran);
        Assert.Empty(ui.Failures);
    }

    [Fact]
    public async Task WorkPostedBeforeTheThreadWent_StillRuns()
    {
        var ui = new SerialDispatcher();
        var ran = ui.InvokeAsync(() => true);

        ui.Dispose();

        Assert.True(await ran.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
