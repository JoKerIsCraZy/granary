namespace Granary.Core.Playback;

// Seek-bar previews of the playing file (docs/api-contract.md, 11.4), read once it plays and again before their
// addresses expire, as the web player keeps them (TrickplayReads).
public sealed partial class ServerPlayback
{
    private PlayerTrickplay? _trickplay;

    // Disposed without a stop (an app closing fast): a file that starts after it reads none.
    private bool _trickplayEnded;
    private string? _trickplayFile;
    private TrickplayReads? _trickplayReads;

    public event EventHandler? TrickplayChanged;

    public PlayerTrickplay? Trickplay => _trickplay is { } trickplay && trickplay.FileId == PlayingVersion?.FileId ? trickplay : null;

    // After the first frame of a file that has previews; another file reads its own.
    private void ReadTrickplay()
    {
        if (_stopping || _trickplayEnded || _context is null || PlayingVersion is not { TrickplayAvailable: true } version
            || version.FileId == _trickplayFile)
        {
            return;
        }
        string fileId = version.FileId;
        _trickplayFile = fileId;
        _trickplayReads?.Dispose();
        // Each read takes the context as it is then: one renewed meanwhile (409 installation_changed) is the one that answers.
        _trickplayReads = new TrickplayReads(cancellationToken => _context is { } context
            ? _starter.ReadTrickplayAsync(context, fileId, cancellationToken)
            : Task.FromResult<PlayerTrickplay?>(null), trickplay =>
        {
            _trickplay = trickplay;
            TrickplayChanged?.Invoke(this, EventArgs.Empty);
        }, _dispatcher, _time, _logger);
        _trickplayReads.Start();
    }

    private void StopTrickplay()
    {
        _trickplayEnded = true;
        _trickplayReads?.Dispose();
        _trickplayReads = null;
    }
}
