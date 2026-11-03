namespace Granary.Core.Playback;

/// <summary>How the viewer's own mpv.conf fared in this run.</summary>
public enum MpvConfigUse
{
    /// <summary>mpv has not started yet, or started without a file.</summary>
    None,

    /// <summary>mpv plays with the file.</summary>
    Used,

    /// <summary>mpv started without the file: it would have turned Granary's options off, or mpv refused it.</summary>
    NotUsed,

    /// <summary>A profile of the file kept changing one of Granary's options; Granary stopped the title and dropped the file.</summary>
    StoppedDuringPlayback,
}

/// <summary>
/// The file's state with a short reason in the log's own words, which names options and the files mpv.conf includes but
/// never a stream's address or a folder outside the mpv folder; <see cref="KeptOptions"/> counts the options of Granary's
/// the file sets, which Granary kept as its own.
/// </summary>
public sealed record MpvConfigState(MpvConfigUse Use, string Reason = "", int KeptOptions = 0)
{
    public static MpvConfigState None { get; } = new(MpvConfigUse.None);
}

/// <summary>What the player reports of the viewer's mpv.conf for this run, for Settings → Playback → This PC to show.</summary>
public interface IMpvConfigStatus
{
    MpvConfigState Current { get; }

    void Report(MpvConfigState state);
}

/// <summary><see cref="IMpvConfigStatus"/> for the run, held in memory.</summary>
public sealed class MpvConfigStatus : IMpvConfigStatus
{
    private volatile MpvConfigState _current = MpvConfigState.None;

    public MpvConfigState Current => _current;

    public void Report(MpvConfigState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _current = state;
    }
}
