namespace Granary.Core.Api.Playback;

/// <summary>The three pieces of a start or replan body that describe the client; send the same report on every replan of an attempt.</summary>
public sealed record PlaybackClientReport(
    IReadOnlyList<string> ClientFeatures,
    ClientCapabilities ClientCapabilities,
    ClientPlaybackContext ClientPlaybackContext);

/// <summary>
/// The capability report of the mpv engine (docs/api-contract.md, 13.2): FFmpeg decodes, in software or through
/// D3D11VA, every audio codec and the video codecs the device facts name, from the containers mpv demuxes. It is
/// an honest, conservative first report: <c>declared</c> evidence, SDR output, no passthrough, direct play and HLS.
/// </summary>
public static class PlaybackReportBuilder
{
    /// <summary>
    /// What the client promises the server, and nothing it does not implement:
    /// <list type="bullet">
    /// <item><c>playback_plan_v3</c>: the client speaks protocol v3 (the contract's minimal start names it, as Apple does).</item>
    /// <item><c>header_authenticated_media_v1</c>: every media, manifest, segment, subtitle and font request carries the
    /// bearer and profile headers, so plan URLs carry no credential (docs/api-contract.md, 9.1). Sticky for the attempt.</item>
    /// <item><c>embedded_subtitles_v1</c>: mpv selects an embedded subtitle stream of the original file by its FFmpeg
    /// stream index, as <see cref="NativeEmbeddedSubtitles"/> declares, and reports a failed mount as
    /// <c>subtitle_embedded_failed</c>.</item>
    /// </list>
    /// The server needs no other client feature for a valid start. <see cref="Build"/> adds
    /// <c>authorized_media_origins_v1</c> where the server offers it: the stream may then come from a proxy node,
    /// whose absolute URL <see cref="PlaybackUrl.ResolveStream"/> checks. Left out on purpose:
    /// <c>software_video_decode_v1</c> (no effect at <c>declared</c> evidence), <c>plan_invalidated_v1</c> (needs the
    /// control socket), <c>buffering_quality_v1</c>, <c>subrip_sidecar_v1</c>, <c>hls_subtitle_renditions_v1</c>,
    /// <c>client_video_transformations_v1</c> and <c>device_quirks_v1</c>.
    /// </summary>
    public static IReadOnlyList<string> ClientFeatures { get; } =
        [PlaybackFeatures.PlaybackPlanV3, PlaybackFeatures.HeaderAuthenticatedMediaV1, PlaybackFeatures.EmbeddedSubtitlesV1];

    /// <summary>
    /// The audio codecs FFmpeg decodes, under the names the server's scanner records (ffprobe's), plus the aliases
    /// Apple's FFmpeg client lists because scanners do not all spell PCM and DTS alike.
    /// </summary>
    public static IReadOnlyList<string> AudioCodecs { get; } =
    [
        "aac", "ac3", "eac3", "truehd", "mlp", "dts", "dca", "mp3", "mp2", "flac", "alac", "opus", "vorbis",
        "pcm", "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be", "pcm_s32le", "pcm_f32le", "pcm_u8", "pcm_bluray", "pcm_dvd",
        "wmav1", "wmav2", "wmapro", "wmalossless", "amr_nb", "amr_wb",
    ];

    /// <summary>
    /// The containers mpv demuxes, as the scanner names them: <c>mkv</c> covers WebM and <c>mp4</c> covers mov and m4v;
    /// <c>m2ts</c> is the Blu-ray transport stream and <c>mpeg</c> the program stream of .mpg and .vob files.
    /// </summary>
    public static IReadOnlyList<string> Containers { get; } = ["mkv", "mp4", "avi", "ts", "m2ts", "ogg", "flv", "wmv", "mpeg"];

    /// <summary>
    /// Embedded subtitles mpv shows straight from the original file, addressed by FFmpeg stream index
    /// (<c>track-list/N/ff-index</c>), as Apple's FFmpeg engine declares them. Text tracks are rendered with libass,
    /// bitmap tracks by FFmpeg's decoders.
    /// </summary>
    public static IReadOnlyList<NativeEmbeddedSubtitleCapability> NativeEmbeddedSubtitles { get; } =
    [
        Native("mkv", "subrip", "ass", "ssa", "webvtt", "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle"),
        Native("mp4", "mov_text"),
    ];

    private const int MaxListLength = 64;
    private const int MaxPlatformLength = 32;
    private const int MaxDeviceValueLength = 128;

    /// <summary>The report for <paramref name="device"/> and this app.</summary>
    /// <param name="appVersion">The app's version, for example <see cref="Granary.Core.Auth.ClientIdentity.ClientVersion"/>.</param>
    /// <param name="appChannel">The distribution channel (<c>release</c>, <c>dev</c>), when known.</param>
    /// <exception cref="ArgumentException">The app version is blank, or a codec list has more entries than the server accepts.</exception>
    /// <param name="mediaOrigins">The server offers <c>authorized_media_origins_v1</c> (its capabilities list it).</param>
    public static PlaybackClientReport Build(PlaybackDeviceFacts device, string appVersion, string? appChannel = null, bool mediaOrigins = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        var hardware = CodecList(device.HardwareVideoCodecs);
        // A codec the GPU decodes plays even when the software list leaves it out.
        var video = CodecList([.. device.SoftwareVideoCodecs, .. device.HardwareVideoCodecs]);

        var capabilities = new ClientCapabilities
        {
            VideoEvidence = CapabilityEvidence.Declared,
            AudioEvidence = CapabilityEvidence.Declared,
            CodecsVideo = video,
            CodecsVideoHardware = hardware,
            CodecsAudio = AudioCodecs,
            Containers = Containers,
            // SDR output only for now; no max_resolution, which the server would read as a delivery ceiling.
            Hdr = false,
        };
        var context = new ClientPlaybackContext
        {
            FormFactor = Bounded(device.FormFactor, MaxPlatformLength) ?? PlaybackDeviceFacts.Desktop,
            AppVersion = appVersion.Trim(),
            AppChannel = Bounded(appChannel, MaxPlatformLength),
            Device = new DeviceContext
            {
                Platform = Bounded(device.Platform, MaxPlatformLength),
                OsVersion = Bounded(device.OsVersion, MaxDeviceValueLength),
                Manufacturer = Bounded(device.Manufacturer, MaxDeviceValueLength),
                Model = Bounded(device.Model, MaxDeviceValueLength),
            },
            Deliveries = new Dictionary<string, DeliveryCapability>
            {
                [DeliveryClasses.OriginalHttp] = OriginalHttp(video),
                [DeliveryClasses.Hls] = Hls(video),
            },
        };
        IReadOnlyList<string> features = mediaOrigins ? [.. ClientFeatures, PlaybackFeatures.AuthorizedMediaOriginsV1] : ClientFeatures;
        return new PlaybackClientReport(features, capabilities, context);
    }

    /// <summary>
    /// Direct play: mpv opens the original file, so its lists are the top-level ones. It switches audio tracks
    /// itself (<c>client_selected_audio_track_v1</c>; without it a non-default track forces a remux) and selects
    /// embedded subtitles natively.
    /// </summary>
    private static DeliveryCapability OriginalHttp(IReadOnlyList<string> video) => new()
    {
        Enabled = true,
        SupportedOnDevice = true,
        Containers = Containers,
        VideoCodecs = video,
        AudioDecodeCodecs = AudioCodecs,
        Subtitles = SubtitleCapabilities(NativeEmbeddedSubtitles),
        ValidatedClaims = [DeliveryClaims.ClientSelectedAudioTrackV1],
    };

    /// <summary>
    /// HLS remux and transcode. FFmpeg's HLS demuxer reads both segment formats, so the container list stays empty
    /// (unconstrained); the codec lists are the device's, which also lets the server transcode to HEVC only where
    /// the device plays it. Subtitles arrive as sidecars; there is no native selection without the original file.
    /// </summary>
    private static DeliveryCapability Hls(IReadOnlyList<string> video) => new()
    {
        Enabled = true,
        SupportedOnDevice = true,
        VideoCodecs = video,
        AudioDecodeCodecs = AudioCodecs,
        Subtitles = SubtitleCapabilities(null),
    };

    /// <summary>
    /// libass renders text sidecars with their styling and the fonts the server bundles; FFmpeg shows embedded bitmap
    /// tracks, which arrive as <c>.sup</c> sidecars. External bitmap files are not a thing the server serves.
    /// </summary>
    private static DeliverySubtitleCapabilities SubtitleCapabilities(IReadOnlyList<NativeEmbeddedSubtitleCapability>? native) => new()
    {
        NativeEmbedded = native,
        EmbeddedText = true,
        SidecarText = true,
        AssStyling = true,
        EmbeddedBitmap = true,
        SidecarBitmap = false,
        FontAttachments = true,
    };

    private static NativeEmbeddedSubtitleCapability Native(string container, params string[] codecs) => new()
    {
        Container = container,
        Codecs = codecs,
        TrackIdentity = SubtitleTrackIdentities.FfmpegStreamIndex,
        AssStyling = true,
        FontAttachments = true,
    };

    /// <summary>Lower-case, trimmed, without blanks and repeats, in first-seen order; the server takes at most 64 entries.</summary>
    private static IReadOnlyList<string> CodecList(IEnumerable<string?> codecs)
    {
        string[] list = [.. codecs
            .Select(c => c?.Trim().ToLowerInvariant())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)];
        if (list.Length > MaxListLength)
        {
            throw new ArgumentException($"The server accepts at most {MaxListLength} video codecs; {list.Length} were given.", nameof(codecs));
        }
        return list;
    }

    /// <summary>
    /// Trimmed and cut to the server's bound, or null when blank. Longer values are a 422 for device fields; a
    /// truncated model name still identifies the device for the server's quirk matching.
    /// </summary>
    private static string? Bounded(string? value, int maxLength)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
