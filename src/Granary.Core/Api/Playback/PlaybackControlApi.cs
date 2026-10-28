namespace Granary.Core.Api.Playback;

/// <summary>
/// The owner-bound playback control handshake (silo-server <c>docs/realtime-api.md</c>, "Owner-bound playback control
/// handshake (v2)"): a single-use ticket for one playback session, then a WebSocket on which the server sends the
/// session's commands (an administrator's pause, stop or message) and the player answers each.
/// </summary>
public sealed class PlaybackControlApi(SiloHttp http)
{
    /// <summary>The socket's protocol, offered first; the ticket goes second as <c>silo.ticket.&lt;ticket&gt;</c>.</summary>
    public const string Protocol = "silo.playback-control.v2";

    /// <summary>
    /// A ticket for <paramref name="sessionId"/>, which must be this account's and profile's (403 otherwise) and still
    /// running (404 once it ended). It expires within 30 seconds and opens one connection.
    /// </summary>
    public Task<PlaybackControlTicket> MintTicketAsync(Uri server, string sessionId, string installationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return http.PostAsync(server, $"api/v2/playback/sessions/{Uri.EscapeDataString(sessionId)}/control/ws-ticket", SiloAuth.Profile,
            new PlaybackControlTicketRequest(string.IsNullOrEmpty(installationId) ? null : installationId),
            PlaybackJsonContext.Default.PlaybackControlTicketRequest, PlaybackJsonContext.Default.PlaybackControlTicket, cancellationToken);
    }

    /// <summary>
    /// The socket's address on <paramref name="server"/>: <c>wss://</c> for https. No credential belongs in it; the ticket
    /// travels as a protocol.
    /// </summary>
    public static Uri SocketUri(Uri server, string sessionId) =>
        WebSocketAddress.For(server, $"api/v2/playback/sessions/{Uri.EscapeDataString(sessionId)}/control/ws");
}

/// <param name="InstallationId">The value playback capabilities named; null for a session this server did not start through v2.</param>
public sealed record PlaybackControlTicketRequest(string? InstallationId);

/// <param name="ExpiresIn">Seconds the ticket stays valid, at most 30.</param>
/// <param name="MaxConnectionSeconds">How long the server keeps the connection, at most four hours.</param>
public sealed record PlaybackControlTicket(string Ticket, int ExpiresIn, int MaxConnectionSeconds, string Protocol)
{
    /// <summary>A ticket of this protocol, of token characters only (it travels in a header), within the contract's bounds.</summary>
    public bool IsValid =>
        Protocol == PlaybackControlApi.Protocol && ExpiresIn is >= 1 and <= 30 && MaxConnectionSeconds is >= 1 and <= 14_400
        && !string.IsNullOrEmpty(Ticket) && Ticket.All(c => c < 128 && (char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)));
}
