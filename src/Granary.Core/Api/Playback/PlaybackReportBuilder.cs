namespace Granary.Core.Api.Playback;

/// <summary>The three pieces of a start or replan body that describe the client; send the same report on every replan of an attempt.</summary>
public sealed record PlaybackClientReport(
    IReadOnlyList<string> ClientFeatures,
    ClientCapabilities ClientCapabilities,
    ClientPlaybackContext ClientPlaybackContext);

/// <summary>
/// The capability report of the mpv engine (docs/api-contract.md, 13.2): FFmpeg decodes, in software or through
/// D3D11VA, every audio codec and the video codecs the device facts name, from the containers mpv demuxes. It is
/// an honest, conservative report: video at <c>platform_attested</c> evidence where the server takes software decoders
/// (else <c>declared</c>), audio <c>declared</c>, SDR output (HDR originals tone-mapped by mpv on direct play where the
/// GPU decodes them), no passthrough, direct play and HLS.
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
    /// whose absolute URL <see cref="PlaybackUrl.ResolveStream"/> checks, and <c>software_video_decode_v1</c> with the attested
    /// decoders (<see cref="Decoders"/>). Left out on purpose: <c>plan_invalidated_v1</c> (needs the
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

    // The codecs HDR and Dolby Vision video comes in, as ffprobe names them.
    private static readonly string[] HdrCodecs = ["hevc", "vp9", "av1"];

    private const int MaxListLength = 64;
    private const int MaxPlatformLength = 32;
    private const int MaxDeviceValueLength = 128;

    /// <summary>The report for <paramref name="device"/> and this app.</summary>
    /// <param name="appVersion">The app's version, for example <see cref="Granary.Core.Auth.ClientIdentity.ClientVersion"/>.</param>
    /// <param name="appChannel">The distribution channel (<c>release</c>, <c>dev</c>), when known.</param>
    /// <exception cref="ArgumentException">The app version is blank, or a codec list has more entries than the server accepts.</exception>
    /// <param name="mediaOrigins">The server offers <c>authorized_media_origins_v1</c> (its capabilities list it).</param>
    /// <param name="softwareDecoding">
    /// The viewer chose direct play without full hardware decoding (Settings → Playback): HEVC, VP9 and AV1 are declared
    /// for direct play whatever the GPU does, and HDR is tone-mapped here, decoded on the CPU where the GPU cannot. A PC
    /// that decodes 4K HDR well enough in software then plays the original instead of a server transcode; a slower one
    /// may stutter. HLS keeps the list without them, the codecs the GPU or the CPU decode smoothly, so a transcode the
    /// server still makes stays one this PC decodes without strain (the server encodes HEVC only for an HLS delivery that
    /// lists it).
    /// </param>
    /// <param name="hdrFileCodecs">
    /// The video codecs of the HDR and Dolby Vision files the server may send for this start, as the scanner names them;
    /// empty when none is HDR. HDR then counts only in those codecs, instead of in every codec the report lists. Null, or
    /// with a blank among them, when that is not known.
    /// </param>
    /// <param name="attestsDecoders">
    /// The server offers <c>software_video_decode_v1</c> (its capabilities list it): the video evidence is then
    /// <c>platform_attested</c>, with the decoders of <see cref="Decoders"/>. Without it the server would count the GPU's
    /// decoders only, so the report stays <c>declared</c>.
    /// </param>
    public static PlaybackClientReport Build(PlaybackDeviceFacts device, string appVersion, string? appChannel = null, bool mediaOrigins = false,
        bool softwareDecoding = false, IReadOnlyCollection<string>? hdrFileCodecs = null, bool attestsDecoders = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        var hardware = CodecList(device.HardwareVideoCodecs);
        // A codec the GPU decodes plays even when the software list leaves it out.
        var smooth = CodecList([.. device.SoftwareVideoCodecs, .. device.HardwareVideoCodecs]);
        var video = softwareDecoding ? CodecList([.. smooth, .. HdrCodecs]) : smooth;

        var capabilities = new ClientCapabilities
        {
            VideoEvidence = attestsDecoders ? CapabilityEvidence.PlatformAttested : CapabilityEvidence.Declared,
            // FFmpeg decodes every listed audio codec; only exact evidence would earn passthrough, which Granary does not
            // do. Silo's Apple app keeps audio declared beside its attested video too.
            AudioEvidence = CapabilityEvidence.Declared,
            CodecsVideo = video,
            CodecsVideoHardware = hardware,
            CodecsAudio = AudioCodecs,
            Containers = Containers,
            // SDR output only for now; no max_resolution, which the server would read as a delivery ceiling.
            Hdr = false,
            VideoDecode = attestsDecoders ? Decoders(device, softwareDecoding) : null,
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
                [DeliveryClasses.OriginalHttp] = OriginalHttp(video, softwareDecoding || DecodesHdr(device, video, hdrFileCodecs)),
                [DeliveryClasses.Hls] = Hls(smooth),
            },
        };
        IReadOnlyList<string> features =
        [
            .. ClientFeatures,
            .. attestsDecoders ? [PlaybackFeatures.SoftwareVideoDecodeV1] : Array.Empty<string>(),
            .. mediaOrigins ? [PlaybackFeatures.AuthorizedMediaOriginsV1] : Array.Empty<string>(),
        ];
        return new PlaybackClientReport(features, capabilities, context);
    }

    /// <summary>
    /// The <c>video_decode</c> entries of a <c>platform_attested</c> report: Silo Server's tier for "platform-level
    /// decoder-stack attestation … (for example Apple's VideoToolbox plus a pinned software stack)" (internal/playback/
    /// protocol_v3.go). Granary's stack is that: D3D11VA as the probe reads it, and the libmpv build, with FFmpeg compiled in,
    /// that native.lock.json pins by its hash. The server then walks these entries instead of the flat lists
    /// (<c>videoEligibleV3</c>, capabilities_v3.go): a listed codec no entry names is refused, so every one has an entry.
    /// <list type="bullet">
    /// <item>A GPU decoder per hardware codec, at 8 bits, and at 10 too where the probe found its 10-bit profile. The server
    /// skips profile and level for it at this tier and matches bit depth exactly, so a 10-bit original goes to a CPU entry
    /// or to the server, as <see cref="DecodesHdr"/> already has it for HDR.</item>
    /// <item>An FFmpeg decoder per codec the CPU plays smoothly, and for HEVC, VP9 and AV1 when the viewer chose software
    /// decoding. These count only with <c>software_video_decode_v1</c>, which the report then sends. They carry no bit
    /// depth: FFmpeg's decoder takes every depth its codec has, H.264 High 10 among them.</item>
    /// </list>
    /// No size, frame rate or bitrate bound: none is measured, and an omitted bound is unconstrained, as the flat lists
    /// were (playback-protocol-v3.md, "An omitted bound means unconstrained").
    /// </summary>
    private static IReadOnlyList<VideoDecodeCapability> Decoders(PlaybackDeviceFacts device, bool softwareDecoding)
    {
        var tenBit = CodecList(device.HardwareTenBitVideoCodecs);
        VideoDecodeCapability[] decoders =
        [
            .. CodecList(device.HardwareVideoCodecs).Select(codec => new VideoDecodeCapability
            {
                Codec = codec,
                Hardware = true,
                BitDepths = tenBit.Contains(codec) ? [8, 10] : [8],
            }),
            .. CodecList([.. device.SoftwareVideoCodecs, .. softwareDecoding ? HdrCodecs : []])
                .Select(codec => new VideoDecodeCapability { Codec = codec, Hardware = false }),
        ];
        if (decoders.Length > MaxListLength)
        {
            throw new ArgumentException($"The server accepts at most {MaxListLength} video decoders; {decoders.Length} were given.", nameof(device));
        }
        return decoders;
    }

    /// <summary>
    /// Direct play: mpv opens the original file, so its lists are the top-level ones. It switches audio tracks
    /// itself (<c>client_selected_audio_track_v1</c>; without it a non-default track forces a remux) and selects
    /// embedded subtitles natively. Where the GPU decodes the start's HDR video (<see cref="DecodesHdr"/>) it also tone-maps HDR
    /// and Dolby Vision originals to the SDR output with libplacebo (<c>client_managed_dynamic_range_v1</c>, as Silo's
    /// Apple app claims for its FFmpeg engine and the web for its decode engine on a hardware GPU), so the server sends
    /// a 4K HDR file as it is instead of tone-mapping it in a transcode.
    /// </summary>
    private static DeliveryCapability OriginalHttp(IReadOnlyList<string> video, bool toneMapsHdr) => new()
    {
        Enabled = true,
        SupportedOnDevice = true,
        Containers = Containers,
        VideoCodecs = video,
        AudioDecodeCodecs = AudioCodecs,
        Subtitles = SubtitleCapabilities(NativeEmbeddedSubtitles),
        ValidatedClaims = toneMapsHdr
            ? [DeliveryClaims.ClientSelectedAudioTrackV1, DeliveryClaims.ClientManagedDynamicRangeV1, .. DeliveryClaims.ClientManagedDolbyVisionBaseLayersV1]
            : [DeliveryClaims.ClientSelectedAudioTrackV1],
    };

    /// <summary>
    /// The GPU decodes some HDR video at 10 bits, and every codec HDR may come in for this start plays without stutter:
    /// decoded at 10 bits by the GPU, or on the CPU's list. Those are the codecs of the HDR files the server may send, or
    /// every HDR codec the report lists where one of theirs is not known, since at <c>declared</c> evidence the server
    /// checks codec names, not bit depth (at <c>platform_attested</c> the <see cref="Decoders"/> carry it). So an 8-bit-only decoder, such as VP9 without Profile 2, leaves HDR in that codec
    /// to the server; SDR files need no 10-bit decoder. A codec the report does not list never plays directly. H.264 High
    /// 10 is left out: no D3D11 decoder takes it and HDR in it is rare (docs/api-contract.md, 13.2).
    /// </summary>
    private static bool DecodesHdr(PlaybackDeviceFacts device, IReadOnlyList<string> video, IReadOnlyCollection<string>? hdrFileCodecs)
    {
        var tenBit = CodecList(device.HardwareTenBitVideoCodecs);
        var software = CodecList(device.SoftwareVideoCodecs);
        var files = KnownCodecs(hdrFileCodecs);
        return tenBit.Count > 0 && HdrCodecs.Where(c => video.Contains(c) && (files is null || files.Contains(c)))
            .All(c => tenBit.Contains(c) || software.Contains(c));
    }

    /// <summary>
    /// The files' codecs as Silo Server's planner reads them (its <c>normalizeCodecV3</c>), lower-case with HEVC's other
    /// names as <c>hevc</c>; null when one of them is not known, which leaves the claim to every HDR codec the report lists.
    /// </summary>
    private static HashSet<string>? KnownCodecs(IReadOnlyCollection<string>? codecs)
    {
        if (codecs is null || codecs.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }
        return codecs.Select(c => c.Trim().ToLowerInvariant() switch
        {
            "h265" or "h.265" or "x265" => "hevc",
            var name => name,
        }).ToHashSet(StringComparer.Ordinal);
    }

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
