using Granary.Core.Abstractions;

namespace Granary.Core.Playback;

/// <summary>Where a change to mpv.conf made in Granary stands.</summary>
public enum MpvConfigApply
{
    /// <summary>The player uses mpv.conf as it is, or Granary changed nothing.</summary>
    None,

    /// <summary>mpv runs with the file as it was: it applies after a restart of Granary, or with Apply now.</summary>
    Pending,

    /// <summary>
    /// It applies to the next title: Apply now waits while a title plays, and before the player first starts mpv reads
    /// the file anyway.
    /// </summary>
    NextTitle,
}

/// <summary>
/// A change to mpv.conf made in Granary (a save in its editor, a preset loaded or unloaded) and Apply now. mpv reads the
/// file only when it starts, so Apply now starts the player's mpv again (<see cref="IMpvRestart"/>): at once when nothing
/// plays, else with the next title or when the player closes. Settings → Playback → This PC shows where it stands.
/// </summary>
public sealed class MpvConfigChanges
{
    private readonly IMpvRestart _player;
    private readonly IUiDispatcher _ui;

    // Each time mpv started again: an Apply now that was told to wait does not say so over a start that came meanwhile.
    private int _restarts;

    // Each save: a start that is said on the UI thread after a newer save does not clear that one.
    private int _saves;

    public MpvConfigChanges(IMpvRestart player, IUiDispatcher ui)
    {
        _player = player;
        _ui = ui;
        // Both live for the run.
        _player.Restarted += OnRestarted;
    }

    /// <summary>On the UI thread.</summary>
    public MpvConfigApply State { get; private set; }

    /// <summary>A change is still to come.</summary>
    public bool IsPending => State != MpvConfigApply.None;

    /// <summary>Raised on the UI thread when <see cref="State"/> changed or mpv started again, which Settings reads anew.</summary>
    public event EventHandler? Changed;

    /// <summary>Granary wrote mpv.conf. On the UI thread.</summary>
    public void Saved()
    {
        Interlocked.Increment(ref _saves);
        Set(!_player.IsRunning || State == MpvConfigApply.NextTitle ? MpvConfigApply.NextTitle : MpvConfigApply.Pending);
    }

    /// <summary>Starts the player's mpv again with mpv.conf as it is now, or has it wait for the next title. On the UI thread.</summary>
    public async Task ApplyNowAsync()
    {
        if (State != MpvConfigApply.Pending)
        {
            return;
        }
        int before = Volatile.Read(ref _restarts);
        // Off the UI thread: mpv takes a moment to start.
        bool now = await Task.Run(_player.Restart);
        if (!now && Volatile.Read(ref _restarts) == before)
        {
            Set(MpvConfigApply.NextTitle);
        }
    }

    // mpv runs with the file as it is now; also its first start.
    private void OnRestarted(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _restarts);
        int saves = Volatile.Read(ref _saves);
        if (_ui.HasThreadAccess)
        {
            Applied(saves);
        }
        else
        {
            _ui.Post(() => Applied(saves));
        }
    }

    // A save after mpv started again is still to be applied, by Apply now again; Settings reads the new start's state
    // either way.
    private void Applied(int saves)
    {
        State = Volatile.Read(ref _saves) == saves ? MpvConfigApply.None
            : _player.IsRunning ? MpvConfigApply.Pending
            : MpvConfigApply.NextTitle;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Set(MpvConfigApply state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
