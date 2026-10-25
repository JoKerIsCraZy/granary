using System.Globalization;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;

namespace Granary.Core.Playback;

/// <summary>Why a title cannot be played, worded for the viewer.</summary>
public sealed class PlaybackUnavailableException(string message, bool canRetry) : Exception(message)
{
    public bool CanRetry { get; } = canRetry;
}

/// <summary>What every start and replan of one playback needs, gathered before the first start.</summary>
/// <param name="InstallationId">From the server's playback capabilities; replaced when the server was reinstalled.</param>
public sealed record PlaybackContext(
    ServerEntry Server,
    string ProfileId,
    WatchDetail Watch,
    WatchFileVersion Version,
    PlaybackPreferences Preferences,
    string InstallationId,
    PlaybackClientReport Report);

/// <summary>
/// The part of a playback before the plan (docs/api-contract.md, 7.8, 8.3, 8.4, 10.5): the watch detail, the file
/// to play and where to start, the profile's settings, the server's capabilities and this device's report; then
/// the bodies of start and replan, which repeat the same report and preferences.
/// </summary>
public sealed class PlaybackStarter(
    WatchApi watch,
    PlaybackApi playback,
    PlaybackPreferencesReader preferences,
    IPlaybackDeviceProbe device,
    ClientIdentity identity,
    ISessionManager session)
{
    // A resume point at least this far in rewinds by ResumeRewindSeconds, so the viewer finds the scene again
    // (Android's player.resume_rewind_seconds, a client-local setting).
    private const double RewindFromSeconds = 30;
    private const double ResumeRewindSeconds = 7;

    /// <exception cref="PlaybackUnavailableException">The title, the profile or the server cannot play.</exception>
    public async Task<PlaybackContext> PrepareAsync(string contentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        var server = session.ActiveServer ?? throw new PlaybackUnavailableException("Choose a server to play from.", canRetry: false);
        string profileId = session.ProfileId ?? throw new PlaybackUnavailableException("Choose a profile to play from this server.", canRetry: false);
        var settings = preferences.ReadAsync(server.BaseUri, cancellationToken);
        // Read alongside the watch detail; when that fails first, nobody waits for the settings any more.
        _ = settings.ContinueWith(static read => _ = read.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        WatchDetail detail;
        try
        {
            detail = await watch.GetWatchAsync(server.BaseUri, contentId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            throw Describe(ex);
        }
        var version = ChooseVersion(detail) ?? throw new PlaybackUnavailableException("This title has no file to play.", canRetry: false);
        var capabilities = await CapabilitiesAsync(server, cancellationToken).ConfigureAwait(false);
        return new PlaybackContext(server, profileId, detail, version, await settings.ConfigureAwait(false), capabilities.InstallationId!, Report(capabilities));
    }

    /// <summary>The same context with the installation id the server has now (after 409 <c>installation_changed</c>).</summary>
    public async Task<PlaybackContext> WithFreshCapabilitiesAsync(PlaybackContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var capabilities = await CapabilitiesAsync(context.Server, cancellationToken).ConfigureAwait(false);
        return context with { InstallationId = capabilities.InstallationId!, Report = Report(capabilities) };
    }

    // Proxy-node media only where the server offers it, as Apple's client asks (docs/api-contract.md, 9.1).
    private PlaybackClientReport Report(PlaybackCapabilities capabilities) =>
        PlaybackReportBuilder.Build(device.Facts, identity.ClientVersion, identity.ClientChannel,
            capabilities.HasFeature(PlaybackFeatures.AuthorizedMediaOriginsV1));

    /// <summary>Starts an attempt of <paramref name="context"/>'s file at <paramref name="startPosition"/> (source seconds; null: the server's resume point).</summary>
    public Task<PlaybackDecision> StartAsync(PlaybackContext context, string attemptId, double? startPosition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = new PlaybackStartRequest
        {
            InstallationId = context.InstallationId,
            ClientFeatures = context.Report.ClientFeatures,
            FileId = context.Version.FileId,
            ProfileId = context.ProfileId,
            PlaybackAttemptId = attemptId,
            QualityPreference = context.Preferences.Quality,
            StartPosition = startPosition,
            // Always explicit: an omitted index is resolved from another device header (docs/api-contract.md, 8.8.1).
            AudioTrackIndex = context.Version.EffectiveAudioTrackIndex,
            BandwidthCapKbps = context.Preferences.MaxBitrateKbps,
            ClientCapabilities = context.Report.ClientCapabilities,
            ClientPlaybackContext = context.Report.ClientPlaybackContext,
        };
        return playback.StartAsync(context.Server.BaseUri, request, cancellationToken);
    }

    /// <summary>
    /// A replan of <paramref name="plan"/> with <paramref name="tracks"/>, by default the plan's own; the chain's keys and
    /// count come from the caller.
    /// </summary>
    public static PlaybackReplanRequest Replan(
        PlaybackContext context,
        string attemptId,
        PlaybackPlan plan,
        string planAttemptId,
        string operation,
        double sourcePosition,
        IReadOnlyList<string> attemptedPlanKeys,
        PlaybackFailure? failure = null,
        SelectedTracks? tracks = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        return new PlaybackReplanRequest
        {
            InstallationId = context.InstallationId,
            ClientFeatures = context.Report.ClientFeatures,
            Operation = operation,
            PlaybackAttemptId = attemptId,
            ReplanRequestId = Guid.NewGuid().ToString(),
            FailedPlanId = plan.PlanId,
            PlanAttemptId = planAttemptId,
            PlanAttemptKey = plan.PlanAttemptKey,
            AttemptedPlanKeys = attemptedPlanKeys,
            AttemptCount = Math.Max(1, attemptedPlanKeys.Count),
            QualityPreference = context.Preferences.Quality,
            PositionSeconds = Math.Max(0, sourcePosition),
            BandwidthCapKbps = context.Preferences.MaxBitrateKbps,
            SelectedTracks = tracks ?? plan.SelectedTracks,
            Failure = failure,
            ClientCapabilities = context.Report.ClientCapabilities,
            ClientPlaybackContext = context.Report.ClientPlaybackContext,
        };
    }

    /// <summary>The file played last, else the first whole file or first part, else any (Android's order without a picked file).</summary>
    public static WatchFileVersion? ChooseVersion(WatchDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var playable = detail.Versions.Where(v => v.FileId.Length > 0).ToList();
        return playable.FirstOrDefault(v => v.FileId == detail.UserData?.LastFileId)
            ?? playable.FirstOrDefault(v => v.PresentationPartIndex == 0)
            ?? playable.FirstOrDefault();
    }

    /// <summary>
    /// The start's <c>start_position</c>: 0 to start over, the resume point (rewound) to resume, null without one.
    /// It is sent explicitly because the server starts a watched title at 0 even during a rewatch (docs/api-contract.md, 10.5).
    /// </summary>
    public static double? StartPosition(WatchDetail detail, bool fromBeginning)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (fromBeginning)
        {
            return 0;
        }
        double position = detail.UserData?.PositionSeconds ?? 0;
        if (position <= 0 || !double.IsFinite(position))
        {
            return null;
        }
        return position >= RewindFromSeconds ? position - ResumeRewindSeconds : position;
    }

    /// <summary>
    /// The player's title line and the line under it, as Silo's web player writes them: the movie and its year, or
    /// the series and "S1 · E5 — Episode".
    /// </summary>
    public static (string Title, string Subtitle) Titles(WatchDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.Type != WatchItemTypes.Episode)
        {
            return (detail.Title, detail.Year?.ToString(CultureInfo.InvariantCulture) ?? "");
        }
        string code = detail.SeasonNumber is { } season && detail.EpisodeNumber is { } episode
            ? string.Create(CultureInfo.InvariantCulture, $"S{season} · E{episode}")
            : "";
        if (string.IsNullOrWhiteSpace(detail.SeriesTitle))
        {
            return (detail.Title, code);
        }
        return (detail.SeriesTitle, string.Join(" — ", new[] { code, detail.Title }.Where(p => p.Length > 0)));
    }

    /// <summary>A failed call worded for the viewer, and whether trying again can help.</summary>
    public static PlaybackUnavailableException Describe(Exception ex) => ex switch
    {
        PlaybackUnavailableException unavailable => unavailable,
        SiloApiException { Status: 404 } => new("This title is no longer available.", canRetry: false),
        SiloApiException { Status: 429 } api => new("The server is receiving too many requests. " + ApiFailureText.TryAgainIn(api.RetryAfter), canRetry: true),
        SiloApiException { Status: 503 } => new(ApiFailureText.Busy, canRetry: true),
        SiloApiException api => new($"The server could not start playback (error {api.Status}).", canRetry: true),
        _ => new(ApiFailureText.ForTransport(ex), canRetry: true),
    };

    private async Task<PlaybackCapabilities> CapabilitiesAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        PlaybackCapabilities capabilities;
        try
        {
            capabilities = await playback.GetCapabilitiesAsync(server.BaseUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            throw Describe(ex);
        }
        return capabilities.IsUsable
            ? capabilities
            : throw new PlaybackUnavailableException("This server does not offer playback to this app.", canRetry: false);
    }
}
