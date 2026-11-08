using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>What the player asked Windows to keep awake, as a list of its requests; it can refuse some, as Windows can.</summary>
internal sealed class FakeKeepAwake : IKeepAwake
{
    /// <summary>Every request, in order, refused ones too.</summary>
    public List<KeepAwakeState> Requests { get; } = [];

    /// <summary>What is held now: the last request taken.</summary>
    public KeepAwakeState Current { get; private set; }

    /// <summary>How many of the next requests are refused.</summary>
    public int RefuseNext { get; set; }

    public bool Set(KeepAwakeState state)
    {
        Requests.Add(state);
        if (RefuseNext > 0)
        {
            RefuseNext--;
            return false;
        }
        Current = state;
        return true;
    }
}
