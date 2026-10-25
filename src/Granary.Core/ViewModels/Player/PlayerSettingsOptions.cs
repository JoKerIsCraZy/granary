using System.Globalization;
using Granary.Core.Api.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>A row of QUALITY in the player's settings menu, as Silo's web player builds it (qualityOptionsFromPlanV3).</summary>
/// <param name="Preference">What a pick sends as <c>quality_preference</c>: <c>auto</c> or the rung's label.</param>
/// <param name="Bitrate">"6 Mbps", right-aligned and muted; empty for Auto.</param>
public sealed record QualityOption(string Preference, string Label, string Bitrate, bool IsSelected)
{
    public const string Auto = "auto";

    public string AccessibleName => Bitrate.Length > 0 ? $"{Label}, {Bitrate}" : Label;

    /// <summary>
    /// Auto, then the plan's rungs; none when the plan offers none to choose. Auto is lit for an auto preference, else the
    /// rung the plan delivers (<c>active_quality</c>), else none, so Auto stays to pick.
    /// </summary>
    public static IReadOnlyList<QualityOption> For(IReadOnlyList<AvailableQuality> qualities, string preference, string? active, bool isLimited)
    {
        ArgumentNullException.ThrowIfNull(qualities);
        if (qualities.Count == 0)
        {
            return [];
        }
        bool isAuto = string.Equals(preference, Auto, StringComparison.OrdinalIgnoreCase) || preference.Length == 0;
        return
        [
            new QualityOption(Auto, isLimited ? "Auto · limited after buffering" : "Auto", "", isAuto),
            .. qualities.Select(q => new QualityOption(q.Label, string.IsNullOrWhiteSpace(q.DisplayName) ? q.Label : q.DisplayName,
                FormatBitrate(q.BitrateKbps), !isAuto && q.Label == active)),
        ];
    }

    // "6 Mbps", "4.5 Mbps", "300 kbps".
    public static string FormatBitrate(int? kbps) => kbps switch
    {
        null or <= 0 => "",
        >= 1000 when kbps % 1000 == 0 => string.Create(CultureInfo.InvariantCulture, $"{kbps / 1000} Mbps"),
        >= 1000 => string.Create(CultureInfo.InvariantCulture, $"{kbps / 1000.0:0.0} Mbps"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{kbps} kbps"),
    };
}

/// <summary>A row of VERSION in the settings menu: one file of the title, as the web player names it ("1080p HEVC HDR").</summary>
/// <param name="Status">"Playing", "Requested", or both when the server fell back to another file; empty otherwise.</param>
public sealed record VersionOption(string FileId, string Label, string Status, bool IsPlaying)
{
    public string AccessibleName => Status.Length > 0 ? $"{Label}, {Status}" : Label;

    /// <summary>The files to switch between; none for a title with only one.</summary>
    public static IReadOnlyList<VersionOption> For(IReadOnlyList<WatchFileVersion> versions, string? playing, string? requested)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (versions.Count < 2)
        {
            return [];
        }
        return
        [
            .. versions.Select(v =>
            {
                string label = $"{v.Resolution} {v.CodecVideo.ToUpperInvariant()}{(v.Hdr ? " HDR" : "")}".Trim();
                bool isPlaying = v.FileId == playing;
                bool isRequested = v.FileId == requested;
                string status = isPlaying ? "Playing" : isRequested ? "Requested" : "";
                return new VersionOption(v.FileId, label.Length > 0 ? label : v.FileId, status, isPlaying);
            }),
        ];
    }
}

/// <summary>The speeds of the web, iOS, macOS and Android players.</summary>
public static class PlaybackSpeeds
{
    public static IReadOnlyList<double> Stops { get; } = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3];

    /// <summary>"1.5×".</summary>
    public static string Format(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    /// <summary>The stop nearest <paramref name="speed"/>, for a speed saved by another client.</summary>
    public static int NearestStop(double speed) =>
        Stops.Select((stop, index) => (Distance: Math.Abs(stop - speed), Index: index)).MinBy(s => s.Distance).Index;
}
