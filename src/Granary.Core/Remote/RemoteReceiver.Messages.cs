using Microsoft.Extensions.Logging;

namespace Granary.Core.Remote;

// What a phone's messages do, and the status every authorized phone gets, as silo-roku's receiver.bs frame and hello and
// AppSceneControl.bs controlApply have it. Messages of one phone are handled one at a time; the player is only touched
// on the UI thread.
public sealed partial class RemoteReceiver
{
    /// <summary>How often the status goes out while a player is open.</summary>
    public static readonly TimeSpan StatusEvery = TimeSpan.FromMilliseconds(500);

    // The open player and the status tick: the UI thread's only.
    private IRemotePlayer? _player;
    private ITimer? _tick;

    // The newest status frame, made on the UI thread and read when a phone's turn to be sent it comes.
    private byte[] _stateFrame = SiloCastMessages.State(SiloCastState.Idle);

    /// <summary>The player the phones see and control now, or null; read on the UI thread.</summary>
    internal IRemotePlayer? AttachedPlayer => _player;

    /// <summary>
    /// The player page opened <paramref name="player"/>: its status goes to every authorized phone now, every
    /// <see cref="StatusEvery"/> and whenever it changes, and phones' commands go to it. Called on the UI thread.
    /// </summary>
    public void Attach(IRemotePlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (ReferenceEquals(_player, player))
        {
            return;
        }
        if (_player is { } previous)
        {
            previous.StateChanged -= OnPlayerChanged;
        }
        _player = player;
        player.StateChanged += OnPlayerChanged;
        _tick ??= _time.CreateTimer(static self => ((RemoteReceiver)self!).OnTick(), this, StatusEvery, StatusEvery);
        PublishState();
    }

    /// <summary>The player closed: the phones are shown the idle status once, and commands get <c>player_not_ready</c>. Called on the UI thread.</summary>
    public void Detach(IRemotePlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!ReferenceEquals(_player, player))
        {
            return;
        }
        player.StateChanged -= OnPlayerChanged;
        _player = null;
        _tick?.Dispose();
        _tick = null;
        PublishState();
    }

    // Every message a phone sends but ping, pong and close (which the controller answers) takes from its budget, since each
    // may post to the UI thread, log or be answered. Over the budget it is dropped, and the phone is told at most once a
    // second; a phone that floods gets nothing more. Unknown types and controls never get here.
    private async ValueTask OnMessageAsync(Session session, SiloCastMessage message)
    {
        if (!session.Budget.TryTake())
        {
            if (session.Budget.MayReport())
            {
                await session.Controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.RateLimited, RemoteTexts.RateLimited)).ConfigureAwait(false);
            }
            return;
        }
        switch (message)
        {
            case SiloCastMessage.PhoneHello hello:
                await OnHelloAsync(session, hello).ConfigureAwait(false);
                return;
            case SiloCastMessage.Presence presence:
                OnPresence(session, presence);
                return;
            case SiloCastMessage.HandoffOffer offer:
                await OnOfferAsync(session, offer).ConfigureAwait(false);
                return;
            case SiloCastMessage.HandoffCancelled:
                // No offer is ever pending yet.
                return;
        }
        var controller = session.Controller;
        if (!controller.IsAuthorized)
        {
            await controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.Unauthorized, RemoteTexts.Unauthorized)).ConfigureAwait(false);
            return;
        }
        switch (message)
        {
            case SiloCastMessage.Launch:
                // A launch needs a ready offer on this connection, and none is given yet.
                await controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.HandoffRequired, RemoteTexts.HandoffRequired)).ConfigureAwait(false);
                return;
            case SiloCastMessage.Control control:
                await OnControlAsync(session, control).ConfigureAwait(false);
                return;
        }
    }

    // A phone's hello, once: refused unless a phone's on version 2 with a server; authorized when the server is Granary's.
    // A phone on another server stays connected without status or control, so its offer can be told why.
    private async Task OnHelloAsync(Session session, SiloCastMessage.PhoneHello hello)
    {
        var controller = session.Controller;
        if (controller.SaidHello)
        {
            return;
        }
        var verdict = Identity() is { } identity ? RemoteReceiverPolicy.Judge(hello, identity) : HelloVerdict.Unauthorized;
        if (verdict == HelloVerdict.Refused)
        {
            controller.Features = hello.Features;
            await controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.VersionUnsupported, RemoteTexts.VersionUnsupported)).ConfigureAwait(false);
            await controller.CloseAsync(SiloCastCloseReasons.Unauthorized, "the hello was not a phone's").ConfigureAwait(false);
            return;
        }

        // The hello is the first frame that proves the key: only now does the connection take a phone's place. The phone's
        // own older connections give theirs up first; then, at the limit, an unauthorized phone, then the oldest passive
        // one makes room, or the newcomer is told there are too many.
        Admission admission;
        List<Session> replaced = [];
        Session? evicted = null;
        lock (_gate)
        {
            var numbers = RemoteReceiverPolicy.ReplacedBy(hello.DeviceId, controller.Number, [.. PhonesLocked(session).Select(Facts)]);
            var phones = PhonesLocked(session).Where(other => !numbers.Contains(other.Controller.Number)).ToList();
            admission = RemoteReceiverPolicy.Admit([.. phones.Select(Facts)]);
            if (admission is not Admission.Refuse)
            {
                replaced = [.. PhonesLocked(session).Where(other => numbers.Contains(other.Controller.Number))];
                if (admission is Admission.Evict(int number))
                {
                    evicted = phones.First(other => other.Controller.Number == number);
                }
                foreach (var other in replaced.Append(evicted).OfType<Session>())
                {
                    other.Leaving = true;
                }
                controller.Features = hello.Features;
                controller.DeviceId = hello.DeviceId;
                controller.DeviceName = hello.DeviceName;
                controller.IsPassive = hello.Passive;
                controller.IsAuthorized = verdict == HelloVerdict.Authorized;
                controller.SaidHello = true;
                session.Name = RemoteTexts.CleanName(hello.DeviceName);
            }
        }
        if (admission is Admission.Refuse)
        {
            await controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.TooManyControllers, RemoteTexts.TooManyControllers)).ConfigureAwait(false);
            // As silo-roku's refusal: the reason goes whatever the phone lists, since every phone ignores what it does not read.
            await controller.CloseAsync(SiloCastCloseReasons.TooManyControllers, RemoteLogReasons.TooManyPhones, evenUnlisted: true).ConfigureAwait(false);
            return;
        }
        foreach (var other in replaced)
        {
            _ = other.Controller.CloseAsync(SiloCastCloseReasons.Replaced, "a newer connection of the same phone");
        }
        if (evicted is not null)
        {
            _ = evicted.Controller.CloseAsync(SiloCastCloseReasons.TooManyControllers, RemoteLogReasons.MadeRoomForPhone);
        }
        _logger.LogInformation("Connection {Number}: a phone {Verdict}", controller.Number,
            verdict == HelloVerdict.Authorized ? "on this server" : "on another server");
        if (controller.IsAuthorized)
        {
            controller.OfferState(_currentState);
            if (!controller.IsPassive)
            {
                Announce(session);
            }
        }
        RaisePhonesChanged();
    }

    private void OnPresence(Session session, SiloCastMessage.Presence presence)
    {
        var controller = session.Controller;
        if (!controller.SaidHello || controller.IsPassive == presence.Passive)
        {
            return;
        }
        controller.IsPassive = presence.Passive;
        if (controller.IsAuthorized && !controller.IsPassive)
        {
            Announce(session);
        }
        RaisePhonesChanged();
    }

    // Every offer is refused until a phone can start titles here: a phone before its hello as one too old, one on another
    // server with the name of the server this PC plays from.
    private async Task OnOfferAsync(Session session, SiloCastMessage.HandoffOffer offer)
    {
        var controller = session.Controller;
        string reason = SiloCastCancelReasons.HandoffFailed;
        string message;
        if (!controller.SaidHello)
        {
            reason = SiloCastCancelReasons.VersionUnsupported;
            message = RemoteTexts.VersionUnsupported;
        }
        else if (Identity() is not { } identity)
        {
            message = RemoteTexts.StartingNotYetAvailable;
        }
        else if (!SiloCastServerId.ServersMatch(offer.ServerId, offer.ServerIdentity, identity.ServerId, identity.ServerIdentity))
        {
            message = RemoteTexts.PlaysFrom(identity.ServerName);
        }
        else
        {
            message = RemoteTexts.StartingNotYetAvailable;
        }
        _logger.LogInformation("Connection {Number}: an offer to start a title was refused", controller.Number);
        await controller.SendAsync(SiloCastMessages.HandoffCancel(offer.RequestId, reason, message)).ConfigureAwait(false);
    }

    // A command (within the phone's budget, OnMessageAsync) goes to the player on the UI thread; after it is done every
    // authorized phone gets the new status.
    private async Task OnControlAsync(Session session, SiloCastMessage.Control control)
    {
        var controller = session.Controller;
        var applying = OnUiThreadAsync(() => Apply(controller.Number, control));
        if (await Task.WhenAny(applying, controller.Ended).ConfigureAwait(false) != applying)
        {
            return;
        }
        switch (await applying.ConfigureAwait(false))
        {
            case RemoteCommandResult.NotReady:
                await controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.PlayerNotReady, RemoteTexts.PlayerNotReady)).ConfigureAwait(false);
                break;
            case RemoteCommandResult.Failed:
                await controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.CommandFailed, FailureText(control))).ConfigureAwait(false);
                break;
        }
    }

    // On the UI thread. Without a player only stop is done: there is nothing to stop.
    private RemoteCommandResult Apply(int number, SiloCastMessage.Control control)
    {
        if (_player is not { } player)
        {
            return control.Name == SiloCastControls.Stop ? RemoteCommandResult.Done : RemoteCommandResult.NotReady;
        }
        RemoteCommandResult result;
        try
        {
            result = player.Apply(control);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("Connection {Number}: the player failed a command ({Error})", number, RemoteLog.Failure(ex));
            result = RemoteCommandResult.Failed;
        }
        if (result == RemoteCommandResult.Done && ReferenceEquals(_player, player))
        {
            PublishState();
        }
        return result;
    }

    private static string FailureText(SiloCastMessage.Control control) => control.Name switch
    {
        SiloCastControls.PlayNext => RemoteTexts.NoNextEpisode,
        SiloCastControls.Play or SiloCastControls.Pause or SiloCastControls.PlayPause or SiloCastControls.Seek => RemoteTexts.NotAllowedInWatchParty,
        _ => RemoteTexts.NotAvailable,
    };

    private void OnTick() => _dispatcher.Post(PublishState);

    private void OnPlayerChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasThreadAccess)
        {
            PublishState();
        }
        else
        {
            _dispatcher.Post(PublishState);
        }
    }

    // On the UI thread: the status as it is now, offered to every authorized phone. A phone that has not taken the last
    // one yet gets this one instead. A title that plays also shows in the advertisement, once it settled.
    private void PublishState()
    {
        var state = _player?.State ?? SiloCastState.Idle;
        NotePlaying(_player is not null && state.ContentId is not null);
        Volatile.Write(ref _stateFrame, SiloCastMessages.State(state));
        foreach (var session in AuthorizedSessions())
        {
            session.Controller.OfferState(_currentState);
        }
    }

    private Task<T> OnUiThreadAsync<T>(Func<T> work)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return Task.FromResult(work());
        }
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }
}
