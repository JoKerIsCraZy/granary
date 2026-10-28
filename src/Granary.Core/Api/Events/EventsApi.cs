namespace Granary.Core.Api.Events;

/// <summary>
/// The realtime events handshake (docs/api-contract.md, 12; silo-server <c>docs/realtime-api.md</c>, "Session-bound socket
/// handshake"): a single-use ticket for the login session and the selected profile, then a WebSocket that carries the
/// catalog's, the profile's state's and its notifications' changes.
/// </summary>
public sealed class EventsApi(SiloHttp http)
{
    /// <summary>The socket's protocol, offered first; the ticket goes second as <c>silo.ticket.&lt;ticket&gt;</c>.</summary>
    public const string Protocol = "silo.events.v2";

    /// <summary>
    /// A ticket bound to the selected profile, so the socket carries its state and notifications. It expires within 30
    /// seconds and opens one connection.
    /// </summary>
    public Task<EventsTicket> MintTicketAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Post, server, "api/v2/events/ws-ticket", SiloAuth.Profile, SiloJsonContext.Default.EventsTicket, cancellationToken);

    /// <summary>The socket's address on <paramref name="server"/>; no credential belongs in it.</summary>
    public static Uri SocketUri(Uri server) => WebSocketAddress.For(server, "api/v2/events/ws");
}

/// <param name="ExpiresIn">Seconds the ticket stays valid, at most 30.</param>
/// <param name="MaxConnectionSeconds">How long the server keeps the connection: five minutes.</param>
public sealed record EventsTicket(string Ticket, int ExpiresIn, int MaxConnectionSeconds, string Protocol)
{
    /// <summary>A ticket of this protocol, of token characters only (it travels in a header), within the contract's bounds.</summary>
    public bool IsValid =>
        Protocol == EventsApi.Protocol && ExpiresIn is >= 1 and <= 30 && MaxConnectionSeconds is >= 1 and <= 3_600
        && !string.IsNullOrEmpty(Ticket) && Ticket.All(c => c < 128 && (char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)));
}
