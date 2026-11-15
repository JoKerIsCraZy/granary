using Granary.App.Services;

namespace Granary.Tests.Remote;

/// <summary>
/// Which of Windows' words on a DNS-SD registration is the registration's own and which its removal's. Windows calls back
/// on a thread of its own, also before <c>DnsServiceDeRegister</c> has returned; a word in that moment is played here by
/// answering between the removal asked and Windows' reply to it.
/// </summary>
public sealed class DnssdAnswersTests
{
    private const uint Success = 0;
    private const uint NotFound = 1168;

    [Fact]
    public void WithoutARemoval_AWord_IsTheRegistrations()
    {
        var answers = new DnssdAnswers();

        answers.Answer(Success);

        Assert.Equal(Success, Word(answers.Registered));
        Assert.Null(Word(answers.Removed));
    }

    [Fact]
    public void AWordAsARemovalIsAsked_IsTheRegistrations_WhenWindowsDoesNotTakeTheRemoval()
    {
        var answers = new DnssdAnswers();

        answers.RemovalAsked();
        // The registration went through late, just as its removal was asked; Windows then refused the removal.
        answers.Answer(Success);
        answers.RemovalAnswered(taken: false);

        Assert.Equal(Success, Word(answers.Registered));
        Assert.Null(Word(answers.Removed));
    }

    [Fact]
    public void AWordAsARemovalIsAsked_IsTheRemovals_WhenWindowsTakesIt()
    {
        var answers = new DnssdAnswers();
        answers.Answer(Success);

        answers.RemovalAsked();
        // Windows confirmed the removal before DnsServiceDeRegister returned.
        answers.Answer(Success);
        answers.RemovalAnswered(taken: true);

        Assert.Equal(Success, Word(answers.Removed));
    }

    [Fact]
    public void AfterARefusedRemoval_ALateWord_IsTheRegistrations()
    {
        var answers = new DnssdAnswers();

        answers.RemovalAsked();
        answers.RemovalAnswered(taken: false);
        answers.Answer(NotFound);

        Assert.Equal(NotFound, Word(answers.Registered));
        Assert.Null(Word(answers.Removed));
    }

    [Fact]
    public void TwoWordsAsARemovalIsAsked_AreTheRegistrations_ThenTheRemovals()
    {
        var answers = new DnssdAnswers();

        answers.RemovalAsked();
        answers.Answer(Success);
        answers.Answer(NotFound);
        answers.RemovalAnswered(taken: true);

        Assert.Equal(Success, Word(answers.Registered));
        Assert.Equal(NotFound, Word(answers.Removed));
    }

    [Fact]
    public void AfterATakenRemoval_TheNextWord_IsTheRemovals()
    {
        var answers = new DnssdAnswers();
        answers.Answer(Success);

        answers.RemovalAsked();
        answers.RemovalAnswered(taken: true);
        answers.Answer(Success);

        Assert.Equal(Success, Word(answers.Removed));
    }

    // The word, or null while none came; never waits.
    private static uint? Word(Task<uint> answer) => answer.IsCompletedSuccessfully ? answer.Result : null;
}
