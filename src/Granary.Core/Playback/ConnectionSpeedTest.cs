using Granary.Core.Api.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

public enum SpeedTestStatus
{
    /// <summary>Measured, and the server keeps the result.</summary>
    Measured,

    /// <summary>The link is cellular or metered: such a link is not tested.</summary>
    Metered,

    /// <summary>The server allows another probe after <see cref="SpeedTestOutcome.RetryAfterSeconds"/>.</summary>
    RateLimited,

    /// <summary>Measured, but the server did not take the result.</summary>
    NotSaved,

    /// <summary>It ran and measured nothing.</summary>
    Failed,

    /// <summary>Stopped before it was done.</summary>
    Stopped,
}

public sealed record SpeedTestOutcome(SpeedTestStatus Status, SpeedTestResult? Result = null, int RetryAfterSeconds = 0);

/// <summary>
/// Settings → <b>Test speed</b>, as Silo's web runs it (player/bandwidth-probe.ts, testConnectionSpeed): the accurate test
/// against the advertised <c>bandwidth_probe_url</c>, reported with <c>manual: true</c>, so the server keeps it as this
/// device's reading on this connection, replacing the playback sample and lifting a learned limit it beats. A call while
/// one runs joins it. Only Test speed runs here: no automatic quick tests.
/// </summary>
public sealed class ConnectionSpeedTest(
    PlaybackApi playback, BandwidthProbe probe, BandwidthProbeApi probes, INetworkLink network, ILogger<ConnectionSpeedTest> logger)
{
    private readonly Lock _gate = new();
    private Task<SpeedTestOutcome>? _running;

    /// <summary>Runs the test against <paramref name="server"/>, or joins the one running; the first caller's token stops it.</summary>
    public Task<SpeedTestOutcome> RunAsync(Uri server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_gate)
        {
            if (_running is { IsCompleted: false } running)
            {
                return running;
            }
            return _running = RunCoreAsync(server, cancellationToken);
        }
    }

    /// <summary>The advertised probe resolved against the server, asking for the bytes <paramref name="mode"/> reads at most.</summary>
    public static Uri? ProbeUrl(Uri server, string? advertised, SpeedTestMode mode)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(mode);
        if (string.IsNullOrWhiteSpace(advertised)
            || !Uri.TryCreate(new Uri(server.AbsoluteUri.TrimEnd('/') + "/"), advertised.Trim(), out var resolved)
            || resolved.Scheme is not ("https" or "http"))
        {
            return null;
        }
        var builder = new UriBuilder(resolved);
        var query = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.Split('=')[0].Equals("size", StringComparison.OrdinalIgnoreCase));
        builder.Query = string.Join('&', query.Append(FormattableString.Invariant($"size={mode.RequestBytes}")));
        return builder.Uri;
    }

    private async Task<SpeedTestOutcome> RunCoreAsync(Uri server, CancellationToken cancellationToken)
    {
        var link = network.Current;
        if (link.IsMetered || link.Transport == NetworkTransports.Cellular)
        {
            return new SpeedTestOutcome(SpeedTestStatus.Metered);
        }
        try
        {
            return await MeasureAsync(server, link, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SpeedTestOutcome(SpeedTestStatus.Stopped);
        }
    }

    private async Task<SpeedTestOutcome> MeasureAsync(Uri server, NetworkLink link, CancellationToken cancellationToken)
    {
        PlaybackCapabilities capabilities;
        try
        {
            // Read from the saved address, the probe is named beneath it: it carries the credentials, and still takes the
            // local route as media does.
            capabilities = await playback.GetCapabilitiesFromSavedAddressAsync(server, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogInformation("The speed test could not read the playback capabilities: {Failure}", ApiFailureText.Describe(ex));
            return new SpeedTestOutcome(SpeedTestStatus.Failed);
        }
        if (!capabilities.HasFeature(PlaybackFeatures.ConnectionSpeedV1)
            || ProbeUrl(server, capabilities.BandwidthProbeUrl, SpeedTestMode.Accurate) is not { } url)
        {
            logger.LogInformation("The server offers no speed test probe");
            return new SpeedTestOutcome(SpeedTestStatus.Failed);
        }
        var reading = await probe.MeasureAsync(server, url, SpeedTestMode.Accurate, capabilities.BandwidthProbeTargetKbps, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        switch (reading)
        {
            case { Kind: ProbeReadingKind.RateLimited }:
                return new SpeedTestOutcome(SpeedTestStatus.RateLimited, RetryAfterSeconds: reading.RetryAfterSeconds);
            case { Kind: ProbeReadingKind.Measured, Result: { } result }:
                return await ReportAsync(server, link, result, cancellationToken).ConfigureAwait(false);
            default:
                logger.LogInformation("The speed test measured nothing ({Reading})", reading.Kind);
                return new SpeedTestOutcome(SpeedTestStatus.Failed);
        }
    }

    // The viewer asked for this one: a result above the learned limit lifts the limit, as Reset would.
    private async Task<SpeedTestOutcome> ReportAsync(Uri server, NetworkLink link, SpeedTestResult result, CancellationToken cancellationToken)
    {
        var report = new BandwidthProbeReport
        {
            Kbps = result.Kbps,
            LowerBound = result.LowerBound,
            Bytes = result.Bytes,
            DurationMs = result.DurationMs,
            Transport = link.Transport,
            Manual = true,
        };
        try
        {
            await probes.ReportAsync(server, report, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogInformation("The speed test's result was not kept: {Failure}", ApiFailureText.Describe(ex));
            return new SpeedTestOutcome(SpeedTestStatus.NotSaved, result);
        }
        logger.LogInformation("Speed test: {Kbps} kbps{Bound} on {Transport}", result.Kbps, result.LowerBound ? " or more" : "", link.Transport);
        return new SpeedTestOutcome(SpeedTestStatus.Measured, result);
    }
}
