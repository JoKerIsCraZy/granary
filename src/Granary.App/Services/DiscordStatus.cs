using DiscordRPC;
using DiscordRPC.Message;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// Discord's status, through the Discord app on this PC (the DiscordRichPresence library, over Discord's local pipe). It
/// connects with the first status, so nothing reaches Discord while the setting is off or nothing plays. Without Discord
/// nothing happens: the library keeps looking for it in the background, and the log says so once.
/// </summary>
/// <remarks>
/// Shown and cleared on the UI thread; the library raises its events on a thread of its own, and they take the same lock.
/// A client is let go under the lock but disposed outside it, so a dispose never waits on a thread that waits for the
/// lock (the library's own dispose only flags its thread to say goodbye and end, without waiting for it). A status is
/// sent only while Discord is connected, and the latest one again when it connects, also after Discord restarted.
/// </remarks>
internal sealed class DiscordStatus(ILogger<DiscordStatus> logger) : IRichPresence, IDisposable
{
    /// <summary>Granary's application on Discord, whose name and pictures the status shows. It is public, not a secret.</summary>
    public const string ApplicationId = "1568672515423740005";

    // The application's own picture, among its Rich Presence assets on Discord.
    private const string AppAsset = "granary";
    private const string AppName = "Granary";
    private const string LinkLabel = "View on TMDB";

    private readonly Lock _gate = new();
    private DiscordRpcClient? _client;
    private RichPresenceState? _wanted;
    private bool _ready;
    private bool _saidUnavailable;
    private bool _disposed;

    public void Show(RichPresenceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        DiscordRpcClient? failed = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _wanted = state;
            if (_client is null)
            {
                failed = Connect();
            }
            else if (_ready)
            {
                Send();
            }
        }
        Close(failed);
    }

    public void Clear()
    {
        DiscordRpcClient? closing;
        lock (_gate)
        {
            _wanted = null;
            if (_ready)
            {
                Send();
                return;
            }
            // Discord was not found yet: stop looking for it until something plays again.
            closing = LetGo();
        }
        Close(closing);
    }

    /// <summary>At the app's exit: the status goes with the connection.</summary>
    public void Dispose()
    {
        DiscordRpcClient? closing;
        lock (_gate)
        {
            _disposed = true;
            _wanted = null;
            closing = LetGo();
        }
        Close(closing);
    }

    // Under the lock. The client to close when it could not start, else null.
    private DiscordRpcClient? Connect()
    {
        // Granary sends a status only when it changed, so the library compares nothing; that also lets the status go out
        // again whole when Discord restarted.
        var client = new DiscordRpcClient(ApplicationId) { SkipIdenticalPresence = false };
        client.OnReady += OnReady;
        client.OnClose += OnClose;
        client.OnConnectionFailed += OnConnectionFailed;
        client.OnError += OnError;
        _client = client;
        _ready = false;
        try
        {
            client.Initialize();
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Discord's status could not start: {Failure}", ex.GetType().Name);
            return LetGo();
        }
    }

    // Under the lock: the client is no longer this one's, and its events from now on find it so (sender != _client).
    private DiscordRpcClient? LetGo()
    {
        if (_client is not { } client)
        {
            return null;
        }
        _client = null;
        _ready = false;
        client.OnReady -= OnReady;
        client.OnClose -= OnClose;
        client.OnConnectionFailed -= OnConnectionFailed;
        client.OnError -= OnError;
        return client;
    }

    // Outside the lock.
    private void Close(DiscordRpcClient? client)
    {
        if (client is null)
        {
            return;
        }
        try
        {
            // Says goodbye on the library's thread, which also takes the status away.
            client.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug("Discord's connection did not close cleanly: {Failure}", ex.GetType().Name);
        }
    }

    // Under the lock, with a client that is connected.
    private void Send()
    {
        try
        {
            if (_wanted is { } state)
            {
                _client!.SetPresence(Presence(state));
            }
            else
            {
                _client!.ClearPresence();
            }
        }
        catch (Exception ex)
        {
            // A status the library refuses must not reach the player.
            logger.LogWarning("Discord's status could not be set: {Failure}", ex.GetType().Name);
        }
    }

    // Watching, with the details line in the member list ("Watching Dark", not "Watching Granary"); the poster with
    // Granary's picture in its corner, or Granary's picture alone.
    private static RichPresence Presence(RichPresenceState state)
    {
        bool hasPoster = state.Poster is not null;
        return new RichPresence
        {
            Type = ActivityType.Watching,
            StatusDisplay = StatusDisplayType.Details,
            Details = state.Details,
            State = state.State,
            Timestamps = state.StartedAt is { } start ? new Timestamps { Start = start.UtcDateTime, End = state.EndsAt?.UtcDateTime } : null,
            Assets = new Assets
            {
                LargeImageKey = state.Poster?.AbsoluteUri ?? AppAsset,
                LargeImageText = state.Details,
                SmallImageKey = hasPoster ? AppAsset : null,
                SmallImageText = hasPoster ? AppName : null,
            },
            Buttons = state.Link is { } link ? [new Button { Label = LinkLabel, Url = link.AbsoluteUri }] : null,
        };
    }

    // The library's events come on its own thread.
    private void OnReady(object sender, ReadyMessage args)
    {
        lock (_gate)
        {
            if (sender != _client)
            {
                return;
            }
            _ready = true;
            _saidUnavailable = false;
            logger.LogInformation("Connected to Discord for the status");
            Send();
        }
    }

    private void OnClose(object sender, CloseMessage args)
    {
        lock (_gate)
        {
            if (sender == _client)
            {
                _ready = false;
            }
        }
    }

    // Raised at each try while Discord is not there: said once, until it was found.
    private void OnConnectionFailed(object sender, ConnectionFailedMessage args)
    {
        lock (_gate)
        {
            if (sender != _client || _saidUnavailable)
            {
                return;
            }
            _saidUnavailable = true;
        }
        logger.LogInformation("Discord isn't running on this PC; the status shows once it is");
    }

    private void OnError(object sender, ErrorMessage args) =>
        logger.LogWarning("Discord refused the status: {Code} {Reason}", args.Code, args.Message);
}
