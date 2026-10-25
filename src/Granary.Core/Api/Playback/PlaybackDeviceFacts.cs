namespace Granary.Core.Api.Playback;

/// <summary>
/// What the playing device can do, as the platform layer probes it, for <see cref="PlaybackReportBuilder"/>. Plain
/// facts: codec names are ffprobe's (<c>h264</c>, <c>hevc</c>, <c>av1</c>, <c>vp9</c>, …), in any case.
/// </summary>
public sealed record PlaybackDeviceFacts
{
    /// <summary>Codecs with a hardware decoder on this machine (on Windows, a D3D11VA decoder profile).</summary>
    public IReadOnlyList<string> HardwareVideoCodecs { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Codecs the software decoder plays smoothly on this machine. At <c>declared</c> evidence the server bounds
    /// neither size nor bit depth, so leave out a codec the CPU cannot keep up with (HEVC on a weak CPU).
    /// </summary>
    public IReadOnlyList<string> SoftwareVideoCodecs { get; init => field = value ?? []; } = [];

    /// <summary>The operating system family, for example <c>windows</c>.</summary>
    public required string Platform { get; init; }

    /// <summary>For example <c>10.0.26100</c>.</summary>
    public string? OsVersion { get; init; }

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    /// <summary><c>desktop</c>, <c>tv</c> or <c>mobile</c>.</summary>
    public string FormFactor { get; init => field = value ?? Desktop; } = Desktop;

    public const string Desktop = "desktop";
}
