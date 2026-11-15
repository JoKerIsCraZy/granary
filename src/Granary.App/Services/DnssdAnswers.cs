namespace Granary.App.Services;

/// <summary>
/// Windows' words on one registration of <see cref="DnssdServiceAdvertiser"/>: which is the registration's own and which
/// its removal's. Apart from Windows and the memory it reads, so that tests reach it. Windows calls back on a thread of its
/// own, with nothing that says which of the two a word is about, may do so before <c>DnsServiceDeRegister</c> has returned,
/// and Microsoft's pages do not say in which order the two words come. So no order of Windows' is relied on:
/// <list type="bullet">
/// <item>Without a removal asked, a word is the registration's.</item>
/// <item>A word that comes while a removal is being asked for is held until Windows says whether it took the removal. Not
/// taken, no word on a removal comes, and it is the registration's own.</item>
/// <item>Taken for a registration already answered, every word is the removal's.</item>
/// <item>Taken for one not answered yet, a word is either. One stays undecided until a second comes, and then the first is
/// the registration's (a registration ends before its removal does) and the second the removal's; when the wait for the
/// removal ends with one alone (<see cref="RemovalWaitEnded"/>), it counts as the removal's.</item>
/// </list>
/// Only the last case guesses, and a registration whose removal Windows took before it answered is never freed, so nothing
/// frees memory on a guess. Its own word may thus never be seen: <see cref="Registered"/> may never complete.
/// </summary>
internal sealed class DnssdAnswers
{
    private readonly TaskCompletionSource<uint> _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<uint> _removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    // Words whose owner is not known yet, oldest first.
    private readonly List<uint> _held = [];
    private Removal _removal;

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
        }
    }

    /// <summary>Once <c>DnsServiceDeRegister</c> returned: whether Windows took the removal.</summary>
    public void RemovalAnswered(bool taken)
    {
        lock (_gate)
        {
            _removal = taken ? Removal.Taken : Removal.None;
            if (taken)
            {
                Settle(waitEnded: false);
            }
            else if (_held.Count > 0)
            {
                _registered.TrySetResult(_held[0]);
                _held.Clear();
            }
        }
    }

    /// <summary>The wait for the removal's word ended: a word still undecided counts as the removal's.</summary>
    public void RemovalWaitEnded()
    {
        lock (_gate)
        {
            if (_removal == Removal.Taken)
            {
                Settle(waitEnded: true);
            }
        }
    }

    /// <summary>On Windows' thread: its word on the registration, or on its removal.</summary>
    public void Answer(uint status)
    {
        lock (_gate)
        {
            if (_removal == Removal.None)
            {
                _registered.TrySetResult(status);
                return;
            }
            _held.Add(status);
            if (_removal == Removal.Taken)
            {
                Settle(waitEnded: false);
            }
        }
    }

    // Under the gate, a removal taken: the words held go to their owners where that is known. The tasks run their
    // continuations elsewhere, so they are completed here.
    private void Settle(bool waitEnded)
    {
        if (_registered.Task.IsCompleted)
        {
            if (_held.Count > 0)
            {
                _removed.TrySetResult(_held[0]);
            }
        }
        else if (_held.Count >= 2)
        {
            _registered.TrySetResult(_held[0]);
            _removed.TrySetResult(_held[1]);
        }
        else if (waitEnded && _held.Count == 1)
        {
            _removed.TrySetResult(_held[0]);
        }
        else
        {
            return;
        }
        _held.Clear();
    }
}
