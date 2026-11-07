using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>The Discord status as a list of what the player sent it.</summary>
internal sealed class FakeRichPresence : IRichPresence
{
    /// <summary>Every state shown, in order.</summary>
    public List<RichPresenceState> Shown { get; } = [];

    public int Clears { get; private set; }

    /// <summary>What shows now; null before anything showed and after a clear.</summary>
    public RichPresenceState? Current { get; private set; }

    public void Show(RichPresenceState state)
    {
        Shown.Add(state);
        Current = state;
    }

    public void Clear()
    {
        Clears++;
        Current = null;
    }
}
