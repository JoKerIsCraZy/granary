using Granary.Core.Overlays;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// The words of Settings → Navigation &amp; Cards → Badges, in the web's words for the same badges, styles, categories and
/// colours (lib/overlays/registry, presets.ts and CardOverlaySettings.tsx), read when shown.
/// </summary>
public static class BadgeSettingsText
{
    /// <summary>A badge's name, as the settings list it.</summary>
    public static string Label(string id) => id switch
    {
        "resolution" => AppText.Get("web.settings.overlays.badge.resolution.label"),
        "hdr" => AppText.Get("web.settings.overlays.badge.hdr.label"),
        "resolution_hdr" => AppText.Get("web.settings.overlays.badge.resolutionHdr.label"),
        "audio" => AppText.Get("web.settings.overlays.badge.audio.label"),
        "audio_channels" => AppText.Get("web.settings.overlays.badge.audioChannels.label"),
        "video_codec" => AppText.Get("web.settings.overlays.badge.videoCodec.label"),
        "container" => AppText.Get("web.settings.overlays.badge.container.label"),
        "aspect_ratio" => AppText.Get("web.settings.overlays.badge.aspectRatio.label"),
        "release_type" => AppText.Get("web.settings.overlays.badge.releaseType.label"),
        "edition" => AppText.Get("web.settings.overlays.badge.edition.label"),
        "multi_audio" => AppText.Get("web.settings.overlays.badge.multiAudio.label"),
        "multi_sub" => AppText.Get("web.settings.overlays.badge.multiSub.label"),
        "rating_imdb" => AppText.Get("web.settings.overlays.badge.ratingImdb.label"),
        "rating_tmdb" => AppText.Get("web.settings.overlays.badge.ratingTmdb.label"),
        "rating_rt" => AppText.Get("web.settings.overlays.badge.ratingRt.label"),
        "rating_rt_audience" => AppText.Get("web.settings.overlays.badge.ratingRtAudience.label"),
        "content_rating" => AppText.Get("web.settings.overlays.badge.contentRating.label"),
        "advisory_age" => AppText.Get("web.settings.overlays.badge.advisoryAge.label"),
        "year" => AppText.Get("web.settings.overlays.badge.year.label"),
        "runtime" => AppText.Get("web.settings.overlays.badge.runtime.label"),
        "original_language" => AppText.Get("web.settings.overlays.badge.originalLanguage.label"),
        "studio" => AppText.Get("web.settings.overlays.badge.studio.label"),
        "network" => AppText.Get("web.settings.overlays.badge.network.label"),
        "show_status" => AppText.Get("web.settings.overlays.badge.showStatus.label"),
        "request_status" => AppText.Get("web.settings.overlays.badge.requestStatus.label"),
        _ => id,
    };

    /// <summary>What a badge shows, under its name.</summary>
    public static string Description(string id) => id switch
    {
        "resolution" => AppText.Get("web.settings.overlays.badge.resolution.description"),
        "hdr" => AppText.Get("web.settings.overlays.badge.hdr.description"),
        // Granary's combined badge has no HDR10+ of its own, so not the web's example of one.
        "resolution_hdr" => AppText.Get("granary.settings.badges.resolutionHdrDescription"),
        "audio" => AppText.Get("web.settings.overlays.badge.audio.description"),
        "audio_channels" => AppText.Get("web.settings.overlays.badge.audioChannels.description"),
        "video_codec" => AppText.Get("web.settings.overlays.badge.videoCodec.description"),
        "container" => AppText.Get("web.settings.overlays.badge.container.description"),
        "aspect_ratio" => AppText.Get("web.settings.overlays.badge.aspectRatio.description"),
        "release_type" => AppText.Get("web.settings.overlays.badge.releaseType.description"),
        "edition" => AppText.Get("web.settings.overlays.badge.edition.description"),
        "multi_audio" => AppText.Get("web.settings.overlays.badge.multiAudio.description"),
        "multi_sub" => AppText.Get("web.settings.overlays.badge.multiSub.description"),
        "rating_imdb" => AppText.Get("web.settings.overlays.badge.ratingImdb.description"),
        "rating_tmdb" => AppText.Get("web.settings.overlays.badge.ratingTmdb.description"),
        "rating_rt" => AppText.Get("web.settings.overlays.badge.ratingRt.description"),
        "rating_rt_audience" => AppText.Get("web.settings.overlays.badge.ratingRtAudience.description"),
        "content_rating" => AppText.Get("web.settings.overlays.badge.contentRating.description"),
        "advisory_age" => AppText.Get("web.settings.overlays.badge.advisoryAge.description"),
        "year" => AppText.Get("web.settings.overlays.badge.year.description"),
        "runtime" => AppText.Get("web.settings.overlays.badge.runtime.description"),
        "original_language" => AppText.Get("web.settings.overlays.badge.originalLanguage.description"),
        "studio" => AppText.Get("web.settings.overlays.badge.studio.description"),
        "network" => AppText.Get("web.settings.overlays.badge.network.description"),
        "show_status" => AppText.Get("web.settings.overlays.badge.showStatus.description"),
        "request_status" => AppText.Get("web.settings.overlays.badge.requestStatus.description"),
        _ => "",
    };

    /// <summary>Why a badge may show on few titles yet; null for the badges whose data is all there, as on the web.</summary>
    public static string? AvailabilityNote(string id) =>
        id == "show_status" ? AppText.Get("web.settings.overlays.badge.showStatus.availabilityNote") : null;

    /// <summary>The note on a badge the combined Resolution + HDR badge stands in for.</summary>
    public static string SuppressedHint => AppText.Get("web.settings.cardOverlay.suppressedHint",
        ("badge", AppText.Get("web.settings.overlays.badge.resolutionHdr.shortLabel")));

    public static string CategoryTitle(OverlayCategory category) => category switch
    {
        OverlayCategory.Tech => AppText.Get("web.settings.overlays.category.tech.title"),
        OverlayCategory.Ratings => AppText.Get("web.settings.overlays.category.ratings.title"),
        OverlayCategory.Metadata => AppText.Get("web.settings.overlays.category.metadata.title"),
        _ => AppText.Get("web.settings.overlays.category.ribbons.title"),
    };

    public static string CategoryDescription(OverlayCategory category) => category switch
    {
        OverlayCategory.Tech => AppText.Get("web.settings.overlays.category.tech.description"),
        OverlayCategory.Ratings => AppText.Get("web.settings.overlays.category.ratings.description"),
        OverlayCategory.Metadata => AppText.Get("web.settings.overlays.category.metadata.description"),
        _ => AppText.Get("web.settings.overlays.category.ribbons.description"),
    };

    public static string PresetLabel(OverlayPresetId preset) => preset switch
    {
        OverlayPresetId.Minimal => AppText.Get("web.settings.overlays.preset.minimal.label"),
        OverlayPresetId.Classic => AppText.Get("web.settings.overlays.preset.classic.label"),
        OverlayPresetId.Vibrant => AppText.Get("web.settings.overlays.preset.vibrant.label"),
        OverlayPresetId.Pill => AppText.Get("web.settings.overlays.preset.pill.label"),
        _ => AppText.Get("web.settings.overlays.preset.square.label"),
    };

    public static string PresetDescription(OverlayPresetId preset) => preset switch
    {
        OverlayPresetId.Minimal => AppText.Get("web.settings.overlays.preset.minimal.description"),
        OverlayPresetId.Classic => AppText.Get("web.settings.overlays.preset.classic.description"),
        OverlayPresetId.Vibrant => AppText.Get("web.settings.overlays.preset.vibrant.description"),
        OverlayPresetId.Pill => AppText.Get("web.settings.overlays.preset.pill.description"),
        _ => AppText.Get("web.settings.overlays.preset.square.description"),
    };

    /// <summary>A palette colour's name (<see cref="BadgeRowViewModel.Palette"/>), as the web's swatches are named.</summary>
    public static string ColorName(string hex) => hex switch
    {
        "#f5c518" => AppText.Get("web.settings.overlays.accent.gold"),
        "#fa320a" => AppText.Get("web.settings.overlays.accent.tomato"),
        "#f97316" => AppText.Get("web.settings.overlays.accent.orange"),
        "#f59e0b" => AppText.Get("web.settings.overlays.accent.amber"),
        "#10b981" => AppText.Get("web.settings.overlays.accent.emerald"),
        "#06b6d4" => AppText.Get("web.settings.overlays.accent.cyan"),
        "#3b82f6" => AppText.Get("web.settings.overlays.accent.blue"),
        "#6366f1" => AppText.Get("web.settings.overlays.accent.indigo"),
        "#8b5cf6" => AppText.Get("web.settings.overlays.accent.violet"),
        "#ec4899" => AppText.Get("web.settings.overlays.accent.pink"),
        "#64748b" => AppText.Get("web.settings.overlays.accent.slate"),
        "#ffffff" => AppText.Get("web.settings.overlays.accent.white"),
        _ => hex,
    };
}
