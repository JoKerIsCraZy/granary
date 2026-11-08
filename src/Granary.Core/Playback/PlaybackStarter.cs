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
/// <param name="SubtitlePick">
/// The viewer's subtitle pick during this playback, as a combined ordinal (-1 for off), which a new attempt starts
/// with instead of the automatic choice; null without one.
/// </param>
public sealed record PlaybackContext(
    ServerEntry Server,
    string ProfileId,
    WatchDetail Watch,
    WatchFileVersion Version,
    PlaybackPreferences Preferences,
    string InstallationId,
    PlaybackClientReport Report,
    int? SubtitlePick = null)
{
    /// <summary>The id the title was asked for by, which the watch detail may spell differently in its <c>content_id</c>.</summary>
    public string ContentId { get; init; } = "";

    /// <summary>The file was chosen for this playback (a Watch Party's): the server may not fall back to another.</summary>
    public bool FilePinned { get; init; }

    /// <summary>The viewer picked the file on the detail page: the server may not swap in another version either.</summary>
    public bool VersionPicked { get; init; }

    /// <summary>Every start keeps to <see cref="Version"/>: the server may send none of the title's other files.</summary>
    public bool KeepsVersion => FilePinned || VersionPicked;

    /// <summary>
    /// This PC's software decoding choice (Settings → Playback) when the title started, which <see cref="Report"/> follows
    /// until the title ends, also for another of its files.
    /// </summary>
    public bool DecodesInSoftware { get; init; }
}

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
    IPlaybackDevicePreferences devicePreferences,
    ClientIdentity identity,
    ISessionManager session,
    TrackChoiceWrites? trackChoices = null)
{
    /// <summary>
    /// Gathers what the start of <paramref name="request"/>'s title needs: its <see cref="PlayRequest.PinnedFileId"/> (a
    /// Watch Party's file) or else its <see cref="PlayRequest.Choice"/> (the detail page's picks), or the client's choice.
    /// </summary>
    /// <exception cref="PlaybackUnavailableException">The title, the profile or the server cannot play.</exception>
    public async Task<PlaybackContext> PrepareAsync(PlayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string contentId = request.ContentId ?? throw new ArgumentException("The request plays no title of the server.", nameof(request));
        string? pinnedFileId = request.PinnedFileId;
        // A room plays the room's file, or with none named yet the client's choice, never a page's picks.
        var choice = request.IsWatchParty ? null : request.Choice;
        var server = session.ActiveServer ?? throw new PlaybackUnavailableException("Choose a server to play from.", canRetry: false);
        string profileId = session.ProfileId ?? throw new PlaybackUnavailableException("Choose a profile to play from this server.", canRetry: false);
        var settings = preferences.ReadAsync(server.BaseUri, cancellationToken);
        // Read alongside the watch detail; when that fails first, nobody waits for the settings any more.
        _ = settings.ContinueWith(static read => _ = read.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        WatchDetail detail;
        try
        {
            // A pick or an Auto made on the detail page a moment ago is in the watch detail's preferences (Apple's
            // client settles its pending writes before this read too). A Watch Party's start, with its file or before the
            // room names one, takes none of the page's picks, so it waits for none of their saves.
            if (trackChoices is not null && !request.IsWatchParty)
            {
                await trackChoices.SettleAsync(cancellationToken).ConfigureAwait(false);
            }
            detail = await watch.GetWatchAsync(server.BaseUri, contentId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            throw Describe(ex);
        }
        var picked = pinnedFileId is null ? Picked(detail, choice) : null;
        var version = (pinnedFileId is not null ? detail.Versions.FirstOrDefault(v => v.FileId == pinnedFileId) : picked?.Version ?? ChooseVersion(detail))
            ?? throw new PlaybackUnavailableException(pinnedFileId is null ? "This title has no file to play." : "The room's file is not available here.",
                canRetry: false);
        var capabilities = await CapabilitiesAsync(server, cancellationToken).ConfigureAwait(false);
        bool pinned = pinnedFileId is not null;
        bool versionPicked = picked?.IsVersionPicked == true;
        bool software = devicePreferences.DecodesInSoftware;
        var report = Report(OffersMediaOrigins(capabilities), software, HdrFileCodecs(detail, version, keepsVersion: pinned || versionPicked));
        return new PlaybackContext(server, profileId, detail, version, await settings.ConfigureAwait(false), capabilities.InstallationId!, report,
            picked?.SubtitleTrack)
        {
            ContentId = contentId,
            FilePinned = pinned,
            VersionPicked = versionPicked,
            DecodesInSoftware = software,
        };
    }

    // The detail page's file with its picked audio as the one it starts with, and its picked subtitle; null when the title
    // no longer has that file, which then plays as without a pick (Apple's client falls back the same way). A track the
    // file does not have is left to the automatic choice.
    private static (WatchFileVersion Version, bool IsVersionPicked, int? SubtitleTrack)? Picked(WatchDetail detail, PrePlayChoice? choice)
    {
        if (choice is null || detail.Versions.FirstOrDefault(v => v.FileId.Length > 0 && v.FileId == choice.FileId) is not { } version)
        {
            return null;
        }
        if (choice.AudioTrack is { } audio && audio >= 0 && audio < version.AudioTracks.Count)
        {
            version = version with { EffectiveAudioTrackIndex = audio };
        }
        int? subtitle = choice.SubtitleTrack is { } track && track >= -1 && track < version.SubtitleTracks.Count ? track : null;
        return (version, choice.IsVersionPicked, subtitle);
    }

    /// <summary>
    /// The marked parts of file <paramref name="fileId"/>, read again with the file named so that the server fills the
    /// markers it finds on demand (docs/api-contract.md, 11.1); null when the answer does not list the file.
    /// </summary>
    public async Task<IReadOnlyList<PlaybackMarker>?> ReadMarkersAsync(PlaybackContext context, string fileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        string contentId = context.ContentId.Length > 0 ? context.ContentId : context.Watch.ContentId;
        var detail = await watch.GetWatchAsync(context.Server.BaseUri, contentId, fileId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return detail.Versions.FirstOrDefault(v => v.FileId == fileId) is { } version ? PlaybackMarker.Of(version, context.Server.BaseUri) : null;
    }

    /// <summary>The seek-bar previews of <paramref name="fileId"/>; null when they cannot be shown, 404 when there are none.</summary>
    public async Task<PlayerTrickplay?> ReadTrickplayAsync(PlaybackContext context, string fileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        string contentId = context.ContentId.Length > 0 ? context.ContentId : context.Watch.ContentId;
        var manifest = await watch.GetTrickplayAsync(context.Server.BaseUri, contentId, fileId, cancellationToken).ConfigureAwait(false);
        return PlayerTrickplay.Of(manifest, context.Server.BaseUri);
    }

    /// <summary>The same context with the installation id the server has now (after 409 <c>installation_changed</c>).</summary>
    public async Task<PlaybackContext> WithFreshCapabilitiesAsync(PlaybackContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var capabilities = await CapabilitiesAsync(context.Server, cancellationToken).ConfigureAwait(false);
        var report = Report(OffersMediaOrigins(capabilities), context.DecodesInSoftware, HdrFileCodecs(context.Watch, context.Version, context.KeepsVersion));
        return context with { InstallationId = capabilities.InstallationId!, Report = report };
    }

    /// <summary>
    /// The same context starting <paramref name="version"/>, another file of the title, with the report for the files that
    /// start may get. Media origins and software decoding stay as the title's report has them.
    /// </summary>
    public PlaybackContext WithVersion(PlaybackContext context, WatchFileVersion version)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(version);
        bool mediaOrigins = context.Report.ClientFeatures.Contains(PlaybackFeatures.AuthorizedMediaOriginsV1);
        var report = Report(mediaOrigins, context.DecodesInSoftware, HdrFileCodecs(context.Watch, version, context.KeepsVersion));
        return context with { Version = version, Report = report };
    }

    // Proxy-node media only where the server offers it, as Apple's client asks (docs/api-contract.md, 9.1).
    private static bool OffersMediaOrigins(PlaybackCapabilities capabilities) => capabilities.HasFeature(PlaybackFeatures.AuthorizedMediaOriginsV1);

    // A start's report. This PC's software decoding choice is the one read when the title started, so a change in the
    // settings counts from the next title on; the HDR claim follows the codecs of the HDR files the start may get.
    private PlaybackClientReport Report(bool mediaOrigins, bool softwareDecoding, IReadOnlyList<string> hdrFileCodecs) =>
        PlaybackReportBuilder.Build(device.Facts, identity.ClientVersion, identity.ClientChannel, mediaOrigins, softwareDecoding, hdrFileCodecs);

    // The video codecs of the HDR files the server may send for a start of that version: among its own and, unless the start
    // keeps to it, the title's other files, which the server may fall back to or resume in. A file that does not say whether
    // it is HDR counts as HDR, and one without a codec makes them unknown; an SDR file needs no 10-bit decoder.
    private static IReadOnlyList<string> HdrFileCodecs(WatchDetail watch, WatchFileVersion version, bool keepsVersion)
    {
        IEnumerable<WatchFileVersion> files = keepsVersion ? [version] : [version, .. watch.Versions];
        return [.. files.Where(f => f.MayBeHdr).Select(f => f.CodecVideo)];
    }

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
            // A Watch Party plays one file for everyone, and a version the viewer picked is the one they want: no
            // recovery may pick another.
            AllowAlternateVersions = context.KeepsVersion ? false : null,
            QualityPreference = context.Preferences.Quality,
            StartPosition = startPosition,
            // Always explicit: an omitted index is resolved from another device header (docs/api-contract.md, 8.8.1).
            AudioTrackIndex = context.Version.EffectiveAudioTrackIndex,
            // The start applies no saved subtitle preference, and leaving the track out means none: the client resolves it.
            SubtitleTrackIndex = context.SubtitlePick is { } pick
                ? (pick >= 0 ? pick : null)
                : SubtitleAutoSelection.ForStart(context.Watch, context.Version),
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
        return ChooseVersion(detail.Versions, v => v.FileId, v => v.PresentationPartIndex, detail.UserData?.LastFileId);
    }

    /// <summary>The same order for the files of another listing (the detail page's), which show what Play plays.</summary>
    public static T? ChooseVersion<T>(IEnumerable<T> versions, Func<T, string> fileId, Func<T, int> partIndex, string? lastFileId) where T : class
    {
        ArgumentNullException.ThrowIfNull(versions);
        var playable = versions.Where(v => fileId(v).Length > 0).ToList();
        return playable.FirstOrDefault(v => fileId(v) == lastFileId)
            ?? playable.FirstOrDefault(v => partIndex(v) == 0)
            ?? playable.FirstOrDefault();
    }

    /// <summary>
    /// The start's <c>start_position</c>: 0 to start over, the resume point to resume, rewound by this PC's Rewind on resume
    /// as it is now (<see cref="ResumeRewind"/>), null without one. It is sent explicitly because the server starts a
    /// watched title at 0 even during a rewatch (docs/api-contract.md, 10.5).
    /// </summary>
    public double? StartPosition(WatchDetail detail, bool fromBeginning)
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
        return ResumeRewind.Apply(position, devicePreferences.ResumeRewindSeconds);
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
