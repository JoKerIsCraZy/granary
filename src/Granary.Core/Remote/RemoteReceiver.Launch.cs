using System.Security.Cryptography;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Remote;

// A phone starts a title on the PC (design 5.7), as silo-roku's AppSceneControl.bs controlOffer, controlReady and
// controlLaunch do it for the profile open on the receiver, without anything about borrowed identities. An offer for the
// profile signed in on the PC is ready at once, on the PC's own sign-in: the PC never changes server, account or profile
// for a phone, never contacts an address an offer names (the decoder drops them), and sends no token of its own. A
// launch after it opens the player as a page's Play does; the server decides, under the PC's profile, what it may play.
public sealed partial class RemoteReceiver
{
    /// <summary>
    /// How long a launch counts as opening while its player does not show the title playing (silo-roku's
    /// <c>CONTROL_LAUNCH_WAIT_MS</c>); the phone gives up after as long without the title in the status.
    /// </summary>
    public static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(30);

    // What a handoff_ready says the session lasts (silo-roku's CONTROL_SESSION_SECONDS); required, though nothing reads it.
    private static readonly TimeSpan SessionLasts = TimeSpan.FromDays(1);

    // Why an offer or a launch was refused, as the log says it: fixed phrases, never a value from a frame.
    private const string NoHello = "no hello";
    private const string NotRunning = "the receiver does not run";
    private const string AnotherServer = "another server";
    private const string AnotherProfile = "another profile";
    private const string InAWatchParty = "a Watch Party";
    private const string NoReadyOffer = "no ready offer";
    private const string Locked = "the PC is locked";
    private const string StillOpening = "a launch is still opening";
    private const string NoTitle = "no title";
    private const string PlayerDidNotOpen = "the player did not open";
    private const string Leaving = "the connection is on its way out";

    // The sign-in the ready offers were made under, and how often it changed; under _gate. Another profile or server, or
    // none, voids every ready offer, also when the same one comes back later.
    private SignIn _offersMadeFor;
    private long _signIns;

    // The title a launch opened, until its player shows it playing, fails or closes, or LaunchWait passes; and how many
    // players attached so far, since only one that attached after the launch is its player. The UI thread's only.
    private Opening? _opening;
    private long _attachments;

    /// <summary>
    /// Raised on the UI thread just before the player opens a title a phone started, so the window can tell the player that
    /// opens it who started it (the "Starting …" notice).
    /// </summary>
    public event EventHandler<RemoteLaunch>? Launched;

    // An offer, answered on the UI thread, where the Watch Party is read.
    private async Task OnOfferAsync(Session session, SiloCastMessage.HandoffOffer offer)
    {
        var controller = session.Controller;
        var answering = OnUiThreadAsync(() => AnswerOffer(session, offer));
        if (await Task.WhenAny(answering, controller.Ended).ConfigureAwait(false) != answering)
        {
            return;
        }
        if (await answering.ConfigureAwait(false) is { } answer)
        {
            await controller.SendAsync(answer).ConfigureAwait(false);
        }
    }

    // Ready for the profile signed in on the PC, or why not: a phone before its hello as one too old, a phone or an offer
    // on another server with the server this PC plays from, a Watch Party, another profile. A resume token a phone sends
    // back is not even read: every offer is checked afresh. A connection on its way out (a newer one of the same phone
    // took its place while its offer waited here) gets nothing, and readies nothing: it must not take the newer one's
    // readiness away. Null when nothing is to be sent.
    private byte[]? AnswerOffer(Session session, SiloCastMessage.HandoffOffer offer)
    {
        var controller = session.Controller;
        if (!IsStillConnected(session))
        {
            _logger.LogInformation("Connection {Number}: an offer to start a title was dropped ({Why})", controller.Number, Leaving);
            return null;
        }
        var identity = Identity();
        (string Reason, string Message, string Why)? refusal = null;
        if (!controller.SaidHello)
        {
            refusal = (SiloCastCancelReasons.VersionUnsupported, RemoteTexts.VersionUnsupported, NoHello);
        }
        else if (identity is null || !RunsForTheActiveServer(identity))
        {
            refusal = (SiloCastCancelReasons.HandoffFailed, RemoteTexts.PlayerNotReady, NotRunning);
        }
        else if (!controller.IsAuthorized
            || !SiloCastServerId.ServersMatch(offer.ServerId, offer.ServerIdentity, identity.ServerId, identity.ServerIdentity))
        {
            refusal = (SiloCastCancelReasons.HandoffFailed, RemoteTexts.PlaysFrom(identity.ServerName), AnotherServer);
        }
        else if (InWatchParty)
        {
            refusal = (SiloCastCancelReasons.WatchPartyActive, RemoteTexts.LeaveWatchParty, InAWatchParty);
        }
        else if (MakeReady(session, offer.ProfileId, out bool gone) is not { } token)
        {
            if (gone)
            {
                _logger.LogInformation("Connection {Number}: an offer to start a title was dropped ({Why})", controller.Number, Leaving);
                return null;
            }
            refusal = (SiloCastCancelReasons.HandoffFailed, RemoteTexts.AnotherProfile, AnotherProfile);
        }
        else
        {
            _logger.LogInformation("Connection {Number}: an offer to start a title is ready", controller.Number);
            return SiloCastMessages.HandoffReady(offer.RequestId, offer.ServerId, offer.ProfileId, _time.GetUtcNow() + SessionLasts,
                controller.Features.Contains(SiloCastFeatures.ResumeToken) ? token : null);
        }
        _logger.LogInformation("Connection {Number}: an offer to start a title was refused ({Why})", controller.Number, refusal.Value.Why);
        return SiloCastMessages.HandoffCancel(offer.RequestId, refusal.Value.Reason, refusal.Value.Message);
    }

    // The offer is for the profile signed in on the PC: this connection is ready, and no other is (readiness is one
    // phone's alone, as silo-roku's controlReady and Silo's TV apps keep it). Answers the connection's resume token, or
    // null for another profile or none, or, with <paramref name="gone"/>, for a connection that is on its way out by now.
    private string? MakeReady(Session session, string profileId, out bool gone)
    {
        var now = SignedInAs();
        lock (_gate)
        {
            NoteSignInLocked(now);
            gone = session.Leaving || !session.Controller.IsOpen || !_sessions.Contains(session);
            if (gone)
            {
                return null;
            }
            if (_offersMadeFor is not { State: SessionState.Ready, ProfileId: { } signedIn } || !string.Equals(signedIn, profileId, StringComparison.Ordinal))
            {
                return null;
            }
            foreach (var other in _sessions)
            {
                other.Ready = null;
            }
            session.Ready = new ReadyOffer(profileId, _signIns);
            return session.ResumeToken ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        }
    }

    // A launch, carried out on the UI thread; the phone is asked about again there, as for a command.
    private async Task OnLaunchAsync(Session session, SiloCastMessage.Launch launch)
    {
        var controller = session.Controller;
        var launching = OnUiThreadAsync(() => StillControls(session) ? StartTitle(session, launch) : null);
        if (await Task.WhenAny(launching, controller.Ended).ConfigureAwait(false) != launching)
        {
            return;
        }
        if (await launching.ConfigureAwait(false) is { } refusal)
        {
            await controller.SendAsync(refusal).ConfigureAwait(false);
        }
    }

    // On the UI thread, in this order: a ready offer on this connection for the profile still signed in, the launch's
    // server, the lock, a Watch Party, a launch still opening (silo-roku's controlLaunch, with the PC's lock where the Roku
    // app checks its shell). Then the player opens the title, replacing what plays without asking. Null once it opens,
    // else the refusal to send.
    private byte[]? StartTitle(Session session, SiloCastMessage.Launch launch)
    {
        int number = session.Controller.Number;
        if (ReadyFor(session) is not var (serverEntryId, profileId))
        {
            return Refused(number, NoReadyOffer, SiloCastErrors.HandoffRequired, RemoteTexts.HandoffRequired);
        }
        if (Identity() is not { } identity || !RunsForTheActiveServer(identity))
        {
            return Refused(number, NotRunning, SiloCastErrors.PlayerNotReady, RemoteTexts.PlayerNotReady);
        }
        // A phone at another address of the PC's server names its own; its hello's identity matched (silo-roku's controlLaunch).
        string? phoneIdentity = string.IsNullOrEmpty(launch.ServerId) ? null : session.ServerIdentity;
        if (!SiloCastServerId.ServersMatch(launch.ServerId, phoneIdentity, identity.ServerId, identity.ServerIdentity))
        {
            return Refused(number, AnotherServer, SiloCastErrors.ServerMismatch, RemoteTexts.ServerMismatch);
        }
        if (_pcLock.IsLocked)
        {
            return Refused(number, Locked, SiloCastErrors.PlayerNotReady, RemoteTexts.PlayerNotReady);
        }
        if (InWatchParty)
        {
            return Refused(number, InAWatchParty, SiloCastErrors.WatchPartyActive, RemoteTexts.LeaveWatchParty);
        }
        if (IsOpening)
        {
            return Refused(number, StillOpening, SiloCastErrors.LaunchPending, RemoteTexts.LaunchPending);
        }
        // Bound to the sign-in the offer was ready for: should the profile switch before the title starts, it plays nothing.
        return RemoteReceiverPolicy.RequestFor(launch.Playback) is { ContentId: { } contentId } request
            ? OpenPlayer(session, request.OnlyFor(serverEntryId, profileId), contentId)
            : Refused(number, NoTitle, SiloCastErrors.LaunchFailed, RemoteTexts.NoSuchTitle);
    }

    // On the UI thread: the launch opens, and the player opens the title as a page's Play does. Null once it is open.
    private byte[]? OpenPlayer(Session session, PlayRequest request, string contentId)
    {
        int number = session.Controller.Number;
        var opening = new Opening(session, contentId, _time.GetTimestamp(), _attachments);
        opening.Deadline = _time.CreateTimer(_ => _dispatcher.Post(() => OpeningTimedOut(opening)), null, LaunchWait, Timeout.InfiniteTimeSpan);
        _opening = opening;
        try
        {
            Launched?.Invoke(this, new RemoteLaunch(session.Name, request));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("Connection {Number}: a listener of the launch failed ({Error})", number, RemoteLog.Failure(ex));
        }
        try
        {
            _navigation.NavigateTo(AppRoute.Player, request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("Connection {Number}: the player did not open ({Error})", number, RemoteLog.Failure(ex));
            EndOpening(opening);
            PublishState();
            return Refused(number, PlayerDidNotOpen, SiloCastErrors.LaunchFailed, RemoteTexts.PlayerNotReady);
        }
        _logger.LogInformation("Connection {Number}: a launch opens the player", number);
        // The phone sees its title at once, loading, before the player has it: it gives up after LaunchWait without it.
        PublishState();
        return null;
    }

    private byte[] Refused(int number, string why, string code, string message)
    {
        _logger.LogInformation("Connection {Number}: a launch was refused ({Why})", number, why);
        return SiloCastMessages.Error(code, message);
    }

    // The server entry and the profile of a ready offer on this connection, made under the sign-in that is still the PC's;
    // null without one.
    private (string ServerEntryId, string ProfileId)? ReadyFor(Session session)
    {
        var now = SignedInAs();
        lock (_gate)
        {
            NoteSignInLocked(now);
            return session.Ready is { } ready && ready.SignInNumber == _signIns
                && _offersMadeFor is { State: SessionState.Ready, ServerEntryId: { } serverEntryId, ProfileId: { } signedIn }
                && string.Equals(signedIn, ready.ProfileId, StringComparison.Ordinal)
                    ? (serverEntryId, signedIn)
                    : null;
        }
    }

    // What the receiver says of itself is the session's active server: after a change of server it runs for the one
    // before until its lifetime has caught up, and that one's phones must not be readied for the new one.
    private bool RunsForTheActiveServer(ReceiverIdentity identity) =>
        _session.ActiveServer is { } active && string.Equals(SiloCastServerId.From(active.BaseUri), identity.ServerId, StringComparison.Ordinal);

    // On the UI thread: a room this profile is in. The Watch Party's own state, as the room page and the player read it.
    private bool InWatchParty => _watchParty?.IsInRoom == true;

    // On the UI thread: a launch opens still, within LaunchWait.
    private bool IsOpening
    {
        get
        {
            if (_opening is { } opening && _time.GetElapsedTime(opening.Started) >= LaunchWait)
            {
                EndOpening(opening);
            }
            return _opening is not null;
        }
    }

    // On the UI thread, from PublishState: the status to show. While a launch opens and no player that came after it shows
    // its title yet, that title loading: the player before it may show the same title, failed or playing. The launch ends
    // when its player shows it playing; when it shows a failure, the phone that started it is told with the player's
    // message; and a player that showed it and closed before it loaded ends it too (silo-roku's controlSendState).
    private SiloCastState StateNow()
    {
        var state = _player?.State ?? SiloCastState.Idle;
        if (state.Error is { } error && RemoteTexts.PlayerError(error) is var told && !string.Equals(told, error, StringComparison.Ordinal))
        {
            state = state with { Error = told };
        }
        if (_opening is not { } opening)
        {
            return state;
        }
        if (_player is not null && _attachments > opening.AttachmentsBefore
            && string.Equals(state.ContentId, opening.ContentId, StringComparison.Ordinal))
        {
            opening.Seen = true;
            if (state.Error is { } failure)
            {
                EndOpening(opening);
                _logger.LogInformation("Connection {Number}: the launched title failed to start", opening.Session.Controller.Number);
                var controller = opening.Session.Controller;
                _ = Task.Run(() => controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.LaunchFailed, failure)).AsTask());
            }
            else if (!state.IsLoading)
            {
                EndOpening(opening);
            }
            return state;
        }
        if (_player is null && opening.Seen)
        {
            EndOpening(opening);
            return state;
        }
        return SiloCastState.Loading(opening.ContentId);
    }

    private void OpeningTimedOut(Opening opening)
    {
        if (!ReferenceEquals(_opening, opening))
        {
            return;
        }
        EndOpening(opening);
        _logger.LogInformation("Connection {Number}: the launched title did not show in time", opening.Session.Controller.Number);
        PublishState();
    }

    private void EndOpening(Opening opening)
    {
        opening.Deadline?.Dispose();
        if (ReferenceEquals(_opening, opening))
        {
            _opening = null;
        }
    }

    private SignIn SignedInAs() => new(_session.State, _session.ActiveServer?.Id, _session.ActiveServer?.BaseUri, _session.ProfileId);

    private void OnSignInChanged(object? sender, EventArgs e)
    {
        var now = SignedInAs();
        lock (_gate)
        {
            NoteSignInLocked(now);
        }
    }

    // Another sign-in than the one the ready offers were made under: none of them counts any more.
    private void NoteSignInLocked(SignIn now)
    {
        if (now == _offersMadeFor)
        {
            return;
        }
        _offersMadeFor = now;
        _signIns++;
        foreach (var session in _sessions)
        {
            session.Ready = null;
        }
    }

    // Who the PC is signed in as, as far as an offer is concerned.
    private readonly record struct SignIn(SessionState State, string? ServerEntryId, Uri? ServerAddress, string? ProfileId);

    // An offer answered ready: for which profile, under which sign-in (a count of its changes).
    private sealed record ReadyOffer(string ProfileId, long SignInNumber)
    {
        public override string ToString() => nameof(ReadyOffer);
    }

    // A title a launch opened; the UI thread's only.
    private sealed class Opening(Session session, string contentId, long started, long attachmentsBefore)
    {
        public Session Session { get; } = session;

        public string ContentId { get; } = contentId;

        public long Started { get; } = started;

        /// <summary>The players attached before the launch: none of them is the launch's.</summary>
        public long AttachmentsBefore { get; } = attachmentsBefore;

        public ITimer? Deadline { get; set; }

        /// <summary>A player showed the title.</summary>
        public bool Seen { get; set; }
    }
}
