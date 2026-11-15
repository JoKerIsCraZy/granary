using System.Text.Json.Serialization;

namespace Granary.Core.Remote;

/// <summary>
/// What the phone's remote shows: the body of <c>state</c>, in camelCase. The members the Android phone decodes as
/// required are <c>required</c> here, so none can be left out: without one its decoder fails and the session drops
/// (silo-apple's <c>SiloControlWireCompatibilityTests</c>). It has no session id, poster or overview, as Silo's Roku app
/// sends none (silo-roku's <c>state.bs</c>): image addresses can carry signed query strings, and the phones look artwork
/// up themselves. Its <see cref="ToString"/> shows none of it.
/// </summary>
public sealed record SiloCastState
{
    /// <summary>The title of the server that plays; absent when idle. The phone waits for it after a launch.</summary>
    public string? ContentId { get; init; }

    public required string Title { get; init; }

    /// <summary>The episode line.</summary>
    public string? Subtitle { get; init; }

    public required bool IsPlaying { get; init; }

    public required bool IsLoading { get; init; }

    public required bool IsBuffering { get; init; }

    /// <summary>Seconds in the title's own time.</summary>
    public required double CurrentTime { get; init; }

    public required double Duration { get; init; }

    public required IReadOnlyList<SiloCastTrack> AudioTracks { get; init; }

    public required IReadOnlyList<SiloCastTrack> SubtitleTracks { get; init; }

    public long? SelectedAudioTrackId { get; init; }

    /// <summary>The subtitle track shown, or absent for none.</summary>
    public long? SelectedSubtitleTrackId { get; init; }

    public required IReadOnlyList<SiloCastQuality> QualityOptions { get; init; }

    public required string ActiveQualityId { get; init; }

    public required bool IsQualitySwitching { get; init; }

    public required double PlaybackSpeed { get; init; }

    /// <summary><c>fit</c> or <c>fill</c>.</summary>
    public required string VideoGravity { get; init; }

    /// <summary>The phones' HDR switch, which Granary does not offer.</summary>
    public bool HdrEnabled { get; init; }

    public required bool SupportsVideoGravity { get; init; }

    /// <summary>Always false: no phone shows an HDR switch without it, and the Android phone still requires the member.</summary>
    [JsonPropertyName("supportsHDRToggle")]
    public bool SupportsHdrToggle => false;

    public int? SubtitleSyncMs { get; init; }

    public required bool SupportsSubtitleDelay { get; init; }

    /// <summary>Always false: Granary does not move subtitles on the phone's word.</summary>
    public bool SupportsSubtitlePosition => false;

    /// <summary>0 to 1.</summary>
    public required double Volume { get; init; }

    public required bool IsMuted { get; init; }

    public required bool HasNextEpisode { get; init; }

    public string? NextEpisodeTitle { get; init; }

    /// <summary>A message the phone shows for a player that failed.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// The state with no player, as Silo's receivers send it: "Ready", no title, empty lists, quality <c>auto</c>, fit,
    /// full volume.
    /// </summary>
    public static SiloCastState Idle { get; } = new()
    {
        Title = "Ready",
        IsPlaying = false,
        IsLoading = false,
        IsBuffering = false,
        CurrentTime = 0,
        Duration = 0,
        AudioTracks = [],
        SubtitleTracks = [],
        QualityOptions = [],
        ActiveQualityId = "auto",
        IsQualitySwitching = false,
        PlaybackSpeed = 1,
        VideoGravity = "fit",
        SupportsVideoGravity = false,
        SupportsSubtitleDelay = false,
        Volume = 1,
        IsMuted = false,
        HasNextEpisode = false,
    };

    public override string ToString() => nameof(SiloCastState);
}

/// <summary>
/// An audio or subtitle track as the phone lists it. <paramref name="TrackId"/> is the receiver's own number, which the
/// phone only sends back.
/// </summary>
/// <param name="Kind"><c>audio</c> or <c>subtitle</c>.</param>
/// <param name="TrackId">An integer on the wire: the phones decode it as one.</param>
/// <param name="Title">The track's name.</param>
/// <param name="Detail">A second line, such as the codec.</param>
public sealed record SiloCastTrack(string Kind, long TrackId, string Title, string? Detail);

/// <summary>A quality the phone offers; <paramref name="Id"/> comes back in <c>set_quality</c>.</summary>
/// <param name="Id">What <c>set_quality</c> sends back.</param>
/// <param name="Label">The name shown.</param>
/// <param name="Detail">A second line.</param>
public sealed record SiloCastQuality(string Id, string Label, string? Detail);
