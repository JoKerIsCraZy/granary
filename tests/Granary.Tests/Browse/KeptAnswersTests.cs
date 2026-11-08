using Granary.Core.ViewModels.Detail;
using Granary.Tests.Fakes;

namespace Granary.Tests.Browse;

/// <summary>Answers kept for a while, one fetch per key shared by every caller, as the season grid's calendar reads use them.</summary>
public sealed class KeptAnswersTests
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    private readonly ManualTimeProvider _clock = new();
    private int _fetches;

    private static CancellationToken Test => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OneCallerGivingUp_DoesNotCancelTheFetch_TheOthersWaitFor()
    {
        var answers = Create();
        var answer = Answer();
        using var leaving = new CancellationTokenSource();

        var first = answers.GetAsync("week", Held(answer), leaving.Token);
        var second = answers.GetAsync("week", Held(answer), Test);
        await leaving.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        answer.SetResult(42);
        Assert.Equal(42, await second);
        Assert.Equal(42, await answers.GetAsync("week", Answers(7), Test));
        Assert.Equal(1, _fetches);
    }

    [Fact]
    public async Task ACallerWhoLeft_RejoinsTheFetchStillOnItsWay()
    {
        var answers = Create();
        var answer = Answer();
        using var leaving = new CancellationTokenSource();
        var first = answers.GetAsync("week", Held(answer), leaving.Token);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var again = answers.GetAsync("week", Held(answer), Test);
        answer.SetResult(3);

        Assert.Equal(3, await again);
        Assert.Equal(1, _fetches);
    }

    [Fact]
    public async Task AFailure_IsNotKept_SoTheNextCallerFetchesAgain()
    {
        var answers = Create();

        await Assert.ThrowsAsync<HttpRequestException>(() => answers.GetAsync("week", Fails(), Test));

        Assert.Equal(5, await answers.GetAsync("week", Answers(5), Test));
        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task AnAnswer_IsKeptUntilExactlyKeepFor()
    {
        var answers = Create();
        await answers.GetAsync("week", Answers(1), Test);

        _clock.Advance(KeepFor - TimeSpan.FromTicks(1));
        Assert.Equal(1, await answers.GetAsync("week", Answers(2), Test));
        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(2, await answers.GetAsync("week", Answers(2), Test));

        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task AFetchThatNeverAnswers_IsGivenUpAfter30Seconds_AndAskedAgainNextTime()
    {
        var answers = Create();
        var waiting = answers.GetAsync("week", Held(Answer()), Test);

        _clock.Advance(GiveUpAfter);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(9, await answers.GetAsync("week", Answers(9), Test));
        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task Clearing_ForgetsTheAnswers_AndKeepsNoneThatWasOnItsWay()
    {
        var answers = Create();
        await answers.GetAsync("week", Answers(1), Test);
        var answer = Answer();
        var onItsWay = answers.GetAsync("next week", Held(answer), Test);

        answers.Clear();
        answer.SetResult(2);

        Assert.Equal(2, await onItsWay);
        Assert.Equal(10, await answers.GetAsync("week", Answers(10), Test));
        Assert.Equal(20, await answers.GetAsync("next week", Answers(20), Test));
        Assert.Equal(4, _fetches);
    }

    [Fact]
    public async Task AnAnswerForAKeyNoLongerCurrent_IsNotKept()
    {
        // The session's profile changed while the answer was on its way: it may be the other profile's.
        bool current = true;
        var answers = Create(_ => current);
        var answer = Answer();
        var waiting = answers.GetAsync("week", Held(answer), Test);

        current = false;
        answer.SetResult(1);
        Assert.Equal(1, await waiting);
        current = true;

        Assert.Equal(2, await answers.GetAsync("week", Answers(2), Test));
        Assert.Equal(2, _fetches);
    }

    private KeptAnswers<string, int> Create(Func<string, bool>? mayKeep = null) => new(_clock, KeepFor, GiveUpAfter, mayKeep);

    private static TaskCompletionSource<int> Answer() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // A fetch the test answers when it wants; it gives up when the fetch's own token does.
    private Func<CancellationToken, Task<int>> Held(TaskCompletionSource<int> answer) => async cancellationToken =>
    {
        Interlocked.Increment(ref _fetches);
        return await answer.Task.WaitAsync(cancellationToken);
    };

    private Func<CancellationToken, Task<int>> Answers(int value) => _ =>
    {
        Interlocked.Increment(ref _fetches);
        return Task.FromResult(value);
    };

    private Func<CancellationToken, Task<int>> Fails() => _ =>
    {
        Interlocked.Increment(ref _fetches);
        return Task.FromException<int>(new HttpRequestException("The server did not answer."));
    };
}
