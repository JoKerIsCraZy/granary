using Granary.Core.Controller;
using Granary.Tests.Fakes;

namespace Granary.Tests.Controller;

/// <summary>A pad read once a frame on a clock the test moves; the pad starts at rest.</summary>
internal sealed class PadBench
{
    public static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    private readonly ManualTimeProvider _time = new();

    public PadBench(PadContext? context = null)
    {
        Pad = new PadInterpreter(_time);
        Context = context ?? PadContext.Browsing;
        Read(default);
    }

    public PadInterpreter Pad { get; }

    public PadContext Context { get; set; }

    /// <summary>One reading, a frame (or <paramref name="after"/>) after the one before.</summary>
    public List<PadAction> Read(PadReading reading, TimeSpan? after = null)
    {
        _time.Advance(after ?? Frame);
        return [.. Pad.Update(reading, Context)];
    }

    public List<PadAction> Read(PadButtons buttons) => Read(new PadReading(buttons));

    /// <summary>
    /// A reading at rest the window does not hand over while the pad is idle (MainWindow.ReadPad), <paramref name="after"/>
    /// the one before.
    /// </summary>
    public void Skip(TimeSpan after)
    {
        _time.Advance(after);
        Pad.Skip();
    }

    /// <summary>The same reading frame after frame for <paramref name="duration"/>.</summary>
    public List<PadAction> Hold(PadReading reading, TimeSpan duration)
    {
        var actions = new List<PadAction>();
        for (var held = TimeSpan.Zero; held < duration; held += Frame)
        {
            actions.AddRange(Read(reading));
        }
        return actions;
    }

    public List<PadAction> Hold(PadButtons buttons, TimeSpan duration) => Hold(new PadReading(buttons), duration);

    /// <summary>A short press: down for a few frames, then let go.</summary>
    public List<PadAction> Tap(PadButtons buttons) =>
        [.. Hold(buttons, TimeSpan.FromMilliseconds(64)), .. Read(default)];

    public List<PadAction> Rest(TimeSpan duration) => Hold(default(PadReading), duration);
}
