namespace Granary.Core.Playback;

/// <summary>
/// Whether mpv.conf changed in this run after mpv read it: a save in Granary's editor or a preset loaded. mpv reads the
/// file only when it starts, so Settings → Playback → This PC says the change is still to come.
/// </summary>
public sealed class MpvConfigChanges
{
    /// <summary>mpv.conf was written since mpv started.</summary>
    public bool IsPending { get; private set; }

    /// <summary>Raised on the thread that wrote the file.</summary>
    public event EventHandler? Changed;

    /// <summary>Granary wrote mpv.conf.</summary>
    public void Saved()
    {
        IsPending = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
