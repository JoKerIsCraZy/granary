namespace Granary.App.Services;

/// <summary>
/// Windows' words on one registration of <see cref="DnssdServiceAdvertiser"/>: which is the registration's own and which
/// its removal's. Apart from Windows and the memory it reads, so that tests reach it. Windows calls back on a thread of its
/// own, with nothing that says which of the two a word is about, and may do so before <c>DnsServiceDeRegister</c> has
/// returned. A word that comes while a removal is being asked for is therefore held until Windows says whether it took
/// the removal: taken, it is the removal's; not taken, no word on a removal comes, and it is the registration's own.
/// Once a removal is taken every word counts as the removal's, so for a registration not answered before, its own word
/// may never be seen and <see cref="Registered"/> may never complete: nothing frees a registration on that word alone.
/// </summary>
internal sealed class DnssdAnswers
{
    private readonly TaskCompletionSource<uint> _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<uint> _removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private Removal _removal;
    private uint? _held;

    private enum Removal
    {
        None,
        Asked,
        Taken,
    }

    /// <summary>Windows' word on the registration.</summary>
    public Task<uint> Registered => _registered.Task;

    /// <summary>Windows' word on the removal, once it took one.</summary>
    public Task<uint> Removed => _removed.Task;

    /// <summary>Just before <c>DnsServiceDeRegister</c> is called.</summary>
    public void RemovalAsked()
    {
        lock (_gate)
        {
            _removal = Removal.Asked;
            _held = null;
        }
    }

    /// <summary>Once <c>DnsServiceDeRegister</c> returned: whether Windows took the removal.</summary>
    public void RemovalAnswered(bool taken)
    {
        uint? held;
        lock (_gate)
        {
            _removal = taken ? Removal.Taken : Removal.None;
            held = _held;
            _held = null;
        }
        if (held is { } word)
        {
            (taken ? _removed : _registered).TrySetResult(word);
        }
    }

    /// <summary>On Windows' thread: its word on the registration, or on its removal once Windows took that.</summary>
    public void Answer(uint status)
    {
        TaskCompletionSource<uint> to;
        uint word = status;
        lock (_gate)
        {
            switch (_removal)
            {
                case Removal.Asked when _held is { } earlier:
                    // A second word while it is asked: the first was the registration's, which comes before its removal's.
                    _held = status;
                    word = earlier;
                    to = _registered;
                    break;
                case Removal.Asked:
                    _held = status;
                    return;
                case Removal.Taken:
                    to = _removed;
                    break;
                default:
                    to = _registered;
                    break;
            }
        }
        to.TrySetResult(word);
    }
}
