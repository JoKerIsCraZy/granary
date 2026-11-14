using Granary.Core.Abstractions;
using Granary.Core.Remote;

namespace Granary.Tests.Fakes;

/// <summary>
/// An open player as the receiver sees it: a status the test sets, and the commands it was given, each noted with
/// whether it came on the UI thread.
/// </summary>
internal sealed class FakeRemotePlayer(IUiDispatcher dispatcher) : IRemotePlayer
{
    private readonly List<SiloCastMessage.Control> _applied = [];
    private int _offThread;

    /// <summary>A film that plays.</summary>
    public static SiloCastState Playing { get; } = SiloCastState.Idle with
    {
        ContentId = "movie-1",
        Title = "Example Movie",
        IsPlaying = true,
        CurrentTime = 120,
        Duration = 6000,
        SupportsVideoGravity = true,
        SupportsSubtitleDelay = true,
    };

    public SiloCastState State { get; set; } = Playing;

    /// <summary>What <see cref="Apply"/> answers; it may change <see cref="State"/> first, as a player does.</summary>
    public Func<SiloCastMessage.Control, RemoteCommandResult> Answer { get; set; } = _ => RemoteCommandResult.Done;

    public event EventHandler? StateChanged;

    public IReadOnlyList<SiloCastMessage.Control> Applied
    {
        get
        {
            lock (_applied)
            {
                return [.. _applied];
            }
        }
    }

    /// <summary>Commands that came on another thread than the UI thread: none, or the receiver went around the dispatcher.</summary>
    public int OffThread => Volatile.Read(ref _offThread);

    public RemoteCommandResult Apply(SiloCastMessage.Control command)
    {
        if (!dispatcher.HasThreadAccess)
        {
            Interlocked.Increment(ref _offThread);
        }
        lock (_applied)
        {
            _applied.Add(command);
        }
        return Answer(command);
    }

    public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
