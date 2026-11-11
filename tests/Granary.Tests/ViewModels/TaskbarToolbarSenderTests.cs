using System.Runtime.InteropServices;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;
using static Granary.Core.Abstractions.TaskbarButtonState;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The taskbar toolbar's sending to Windows, another process that can be busy for a moment: a failed send is sent again,
/// and only failures that last give up.
/// </summary>
public sealed class TaskbarToolbarSenderTests : IDisposable
{
    private static readonly TaskbarToolbarState Playing = new(Disabled, Enabled, Enabled, IsPaused: false);
    private static readonly TaskbarToolbarState Paused = Playing with { IsPaused = true };

    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    // Every call to Windows, and the ones it took.
    private readonly List<TaskbarToolbarState?> _tried = [];
    private readonly List<TaskbarToolbarState?> _sent = [];
    private readonly TaskbarToolbarSender _sender;

    public TaskbarToolbarSenderTests() =>
        _sender = new TaskbarToolbarSender(Send, new ImmediateDispatcher(), _time, _logs.For<TaskbarToolbarSender>());

    /// <summary>Which calls Windows refuses, as while Explorer is busy or restarting; none at first.</summary>
    private Func<TaskbarToolbarState?, bool> Refuses { get; set; } = _ => false;

    public void Dispose() => _sender.Dispose();

    private void Send(TaskbarToolbarState? state)
    {
        _tried.Add(state);
        if (Refuses(state))
        {
            throw new COMException("Explorer did not answer.", unchecked((int)0x80004005));
        }
        _sent.Add(state);
    }

    // Lets every retry that is due run, until none is left.
    private void RunRetries()
    {
        for (int i = 0; i < 50 && _time.NextDue is { } due; i++)
        {
            _time.Advance(due);
        }
    }

    private void GiveUp()
    {
        Refuses = _ => true;
        _sender.Send(Playing);
        RunRetries();
    }

    [Fact]
    public void AState_IsSentAtOnce()
    {
        _sender.Send(Playing);

        Assert.Equal([Playing], _sent);
    }

    [Fact]
    public void AFailedSend_IsSentAgainAMomentLater_ThoughNothingChanged()
    {
        Refuses = _ => true;
        _sender.Send(Paused);
        Refuses = _ => false;

        _time.Advance(TaskbarToolbarSender.RetryDelay);

        Assert.Equal([Paused], _sent);
    }

    [Fact]
    public void FailuresInQuickSuccession_DoNotGiveUp_TheNextChangeIsSent()
    {
        Refuses = _ => true;
        _sender.Send(Playing);
        _sender.Send(Paused);
        _sender.Send(Playing);
        Refuses = _ => false;

        _sender.Send(Paused);
        RunRetries();

        Assert.Equal([Paused], _sent);
    }

    [Fact]
    public void FailuresLastingTenSeconds_GiveUp_AndTryToHideTheButtons()
    {
        GiveUp();

        Assert.True(_time.Elapsed >= TaskbarToolbarSender.GiveUpAfter);
        Assert.Null(_tried[^1]);
        Assert.Null(_time.NextDue);
        Assert.Contains("goes on without them", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public void GivingUp_HidesTheButtons_WhenWindowsStillTakesThat()
    {
        Refuses = state => state is not null;
        _sender.Send(Playing);

        RunRetries();

        Assert.Equal([null], _sent);
    }

    [Fact]
    public void GivenUp_AChangeWithinAMinute_IsNotTried()
    {
        GiveUp();
        int tried = _tried.Count;

        _time.Advance(TimeSpan.FromSeconds(30));
        _sender.Send(Paused);

        Assert.Equal(tried, _tried.Count);
    }

    [Fact]
    public void GivenUp_AChangeAfterAQuietMinute_TriesAgain_AndWorkingAgainSendsEveryChange()
    {
        GiveUp();
        Refuses = _ => false;
        _time.Advance(TaskbarToolbarSender.QuietTime);

        _sender.Send(Paused);
        _sender.Send(Playing);

        Assert.Equal([Paused, Playing], _sent.Skip(_sent.Count - 2));
    }

    [Fact]
    public void GivenUp_ANewTaskbarButton_SendsWhatShouldShowAtOnce()
    {
        GiveUp();
        _sender.Send(Paused);
        Refuses = _ => false;

        _sender.Restart();

        Assert.Equal(Paused, _sent[^1]);
    }

    [Fact]
    public void Resend_SendsWhatShowsAgain()
    {
        _sender.Send(Playing);

        _sender.Resend();

        Assert.Equal([Playing, Playing], _sent);
    }

    [Fact]
    public void AfterDispose_AFailedSendIsNotSentAgain()
    {
        Refuses = _ => true;
        _sender.Send(Playing);
        Refuses = _ => false;

        _sender.Dispose();
        _time.Advance(TaskbarToolbarSender.RetryDelay);

        Assert.Empty(_sent);
    }
}
