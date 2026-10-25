namespace Granary.Core.Api.Playback;

/// <summary>
/// The server's view of this device on the connection it uses now (<c>buffering_quality_v1</c>, <c>connection_speed_v1</c>):
/// the limit it learned after buffering, the latest speed reading, and Auto's budget. Read by the playback settings page.
/// </summary>
public sealed class PlaybackConnectionApi(SiloHttp http)
{
    public Task<PlaybackConnectionState> GetAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/playback/connection", SiloAuth.Profile, PlaybackJsonContext.Default.PlaybackConnectionState, cancellationToken);

    /// <summary>Forgets the learned limit on this connection, so Auto may pick higher qualities again.</summary>
    public Task ResetLearnedLimitAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, "api/v2/playback/connection/learned-limit", SiloAuth.Profile, cancellationToken);
}

public sealed record PlaybackConnectionState
{
    /// <summary>What Auto plays without converting, in kilobits per second; null when the server has no reading.</summary>
    public long? AutoBudgetKbps { get; init; }

    public string? Connection { get; init; }

    /// <summary>The limit learned after buffering on this connection; null without one.</summary>
    public LearnedLimit? LearnedLimit { get; init; }

    /// <summary>The best recent playback throughput, a lower bound on the speed.</summary>
    public PlaybackSpeedReading? PlaybackSample { get; init; }

    /// <summary>The latest speed test.</summary>
    public PlaybackSpeedReading? Probe { get; init; }
}

public sealed record LearnedLimit
{
    public long Kbps { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>A speed reading: a test (with whether it only proved a lower bound, and the transport) or a playback sample.</summary>
public sealed record PlaybackSpeedReading
{
    public long Kbps { get; init; }

    public bool LowerBound { get; init; }

    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary><c>wifi</c>, <c>ethernet</c>, <c>cellular</c> or <c>other</c>; tests only.</summary>
    public string? Transport { get; init; }
}
