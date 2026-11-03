using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Player.Interop;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

/// <summary>
/// What mpv on this machine decodes (docs/api-contract.md, 13.2), probed once: the GPU's D3D11VA codecs, plus the
/// codecs FFmpeg decodes in software at any size a desktop CPU keeps up with. HEVC, VP9 and AV1 count only with a
/// hardware decoder: at <c>declared</c> evidence the server bounds neither size nor bit depth, and 4K HEVC in
/// software would stutter on many machines. For the same reason HDR originals come only where those decoders take 10-bit video too.
/// The result is logged once, so a log shows why a title was transcoded on this PC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MpvDeviceProbe : IPlaybackDeviceProbe
{
    private static readonly string[] LightSoftwareCodecs = ["h264", "mpeg2video", "mpeg4", "msmpeg4v3", "vc1", "wmv3", "vp8", "mjpeg"];

    private readonly ILogger<MpvDeviceProbe> _logger;
    private readonly Lazy<PlaybackDeviceFacts> _facts;

    public MpvDeviceProbe(ILogger<MpvDeviceProbe> logger)
    {
        _logger = logger;
        _facts = new(Probe);
    }

    public PlaybackDeviceFacts Facts => _facts.Value;

    private PlaybackDeviceFacts Probe()
    {
        var hardware = HardwareDecoders();
        _logger.LogInformation("The graphics card decodes {Codecs} in hardware, {TenBit} also in 10-bit; the CPU decodes {Software}",
            Listed(hardware.Codecs), Listed(hardware.TenBitCodecs), Listed(LightSoftwareCodecs));
        return new()
        {
            Platform = "windows",
            HardwareVideoCodecs = hardware.Codecs,
            HardwareTenBitVideoCodecs = hardware.TenBitCodecs,
            SoftwareVideoCodecs = LightSoftwareCodecs,
            OsVersion = Environment.OSVersion.Version.ToString(),
            FormFactor = PlaybackDeviceFacts.Desktop,
        };
    }

    private static string Listed(IReadOnlyList<string> codecs) => codecs.Count == 0 ? "nothing" : string.Join(", ", codecs);

    // Lazy keeps what the probe threw for good: a machine whose driver will not answer plays in software instead.
    private HardwareVideoDecoders HardwareDecoders()
    {
        try
        {
            return VideoDecoderProbe.HardwareDecoders();
        }
        catch (Exception ex) when (ex is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "The graphics card's video decoders could not be read; video is decoded in software");
            return HardwareVideoDecoders.None;
        }
    }
}
