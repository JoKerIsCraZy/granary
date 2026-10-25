using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Player.Interop;

namespace Granary.Player;

/// <summary>
/// What mpv on this machine decodes (docs/api-contract.md, 13.2), probed once: the GPU's D3D11VA codecs, plus the
/// codecs FFmpeg decodes in software at any size a desktop CPU keeps up with. HEVC, VP9 and AV1 count only with a
/// hardware decoder: at <c>declared</c> evidence the server bounds neither size nor bit depth, and 4K HEVC in
/// software would stutter on many machines.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MpvDeviceProbe : IPlaybackDeviceProbe
{
    private static readonly string[] LightSoftwareCodecs = ["h264", "mpeg2video", "mpeg4", "msmpeg4v3", "vc1", "wmv3", "vp8", "mjpeg"];

    private readonly Lazy<PlaybackDeviceFacts> _facts = new(Probe);

    public PlaybackDeviceFacts Facts => _facts.Value;

    private static PlaybackDeviceFacts Probe() => new()
    {
        Platform = "windows",
        HardwareVideoCodecs = HardwareCodecs(),
        SoftwareVideoCodecs = LightSoftwareCodecs,
        OsVersion = Environment.OSVersion.Version.ToString(),
        FormFactor = PlaybackDeviceFacts.Desktop,
    };

    // Lazy keeps what the probe threw for good: a machine whose driver will not answer plays in software instead.
    private static IReadOnlyList<string> HardwareCodecs()
    {
        try
        {
            return VideoDecoderProbe.HardwareCodecs();
        }
        catch (Exception ex) when (ex is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }
}
