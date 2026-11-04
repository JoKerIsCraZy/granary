using Granary.Core.Abstractions;

namespace Granary.Core.Playback;

/// <summary>
/// A change to mpv.conf made in Granary (a save in its editor, a preset loaded or unloaded). The player compares the file as
/// each title loads, so the change waits for the next title, which Settings → Playback → This PC says until then.
/// </summary>
public sealed class MpvConfigChanges
{
    private readonly IUiDispatcher _ui;

    // Each save, and how many there were when the latest title read the file: a title says nothing of a save after that.
    private int _saves;
    private int _savesRead;

    public MpvConfigChanges(IMpvConfigStatus status, IUiDispatcher ui)
    {
        _ui = ui;
        // Both live for the run.
        status.TitleOpening += OnTitleOpening;
        status.TitleOpened += OnTitleOpened;
    }

    /// <summary>A change no title has started with yet. On the UI thread.</summary>
    public bool IsPending { get; private set; }

    /// <summary>Raised on the UI thread after a save and as a title opens, when Settings reads how the file fared anew.</summary>
    public event EventHandler? Changed;

    /// <summary>Granary wrote mpv.conf. On the UI thread.</summary>
    public void Saved()
    {
        Interlocked.Increment(ref _saves);
        IsPending = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTitleOpening(object? sender, EventArgs e) => Volatile.Write(ref _savesRead, Volatile.Read(ref _saves));

    private void OnTitleOpened(object? sender, EventArgs e)
    {
        int read = Volatile.Read(ref _savesRead);
        _ui.Post(() =>
        {
            if (Volatile.Read(ref _saves) == read)
            {
                IsPending = false;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}
