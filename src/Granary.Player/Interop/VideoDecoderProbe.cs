using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Granary.Player.Interop;

/// <summary>
/// Which video codecs the GPU decodes (D3D11VA, which mpv's <c>hwdec=auto-safe</c> uses on Windows), read from
/// the decoder profiles of a D3D11 video device on the default adapter (docs/api-contract.md, 13.2).
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class VideoDecoderProbe
{
    private const int DriverTypeHardware = 1;
    private const uint CreateDeviceVideoSupport = 0x800;
    private const uint SdkVersion = 7;

    private static readonly Guid VideoDeviceIid = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");

    // IUnknown (3) + CreateVideoDecoder … CreateVideoProcessorEnumerator (8).
    private const int GetVideoDecoderProfileCountSlot = 11;
    private const int GetVideoDecoderProfileSlot = 12;

    // The DXVA decoder profiles (d3d11.h, dxva.h) by the codec names ffprobe and the server use.
    private static readonly Dictionary<Guid, string> Profiles = new()
    {
        [new("1b81be68-a0c7-11d3-b984-00c04f2e73c5")] = "h264", // H264_VLD_NOFGT
        [new("1b81be69-a0c7-11d3-b984-00c04f2e73c5")] = "h264", // H264_VLD_FGT
        [new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0")] = "hevc", // HEVC_VLD_MAIN
        [new("107af0e0-ef1a-4d19-aba8-67a163073d13")] = "hevc", // HEVC_VLD_MAIN10
        [new("463707f8-a1d0-4585-876d-83aa6d60b89e")] = "vp9", // VP9_VLD_PROFILE0
        [new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7")] = "vp9", // VP9_VLD_10BIT_PROFILE2
        [new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a")] = "av1", // AV1_VLD_PROFILE0
        [new("ee27417f-5e28-4e65-beea-1d26b508adc9")] = "mpeg2video", // MPEG2_VLD
        [new("86695f12-340e-4f04-9fd3-9253dd327460")] = "mpeg2video", // MPEG2and1_VLD
        [new("1b81bea3-a0c7-11d3-b984-00c04f2e73c5")] = "vc1", // VC1_VLD
        [new("1b81bea4-a0c7-11d3-b984-00c04f2e73c5")] = "vc1", // VC1_D2010
        [new("efd64d74-c9e8-41d7-a5e9-e9b0e39fa319")] = "mpeg4", // MPEG4PT2_VLD_SIMPLE
        [new("ed418a9f-010d-4eda-9ae3-9a65358d8d2e")] = "mpeg4", // MPEG4PT2_VLD_ADVSIMPLE_NOGMC
        [new("90b899ea-3a62-4705-88b3-8df04b2744e7")] = "vp8", // VP8_VLD
    };

    /// <summary>The ffprobe codec name of a DXVA decoder profile; null for profiles Granary does not use.</summary>
    public static string? Codec(Guid profile) => Profiles.GetValueOrDefault(profile);

    /// <summary>
    /// The codecs the default adapter decodes in hardware, each once; empty when there is no D3D11 video
    /// device (no GPU, a remote session, a basic display driver).
    /// </summary>
    public static IReadOnlyList<string> HardwareCodecs()
    {
        int hr = D3D11CreateDevice(0, DriverTypeHardware, 0, CreateDeviceVideoSupport, 0, 0, SdkVersion,
            out nint device, out _, out nint context);
        if (hr < 0 || device == 0)
        {
            return [];
        }
        try
        {
            Guid iid = VideoDeviceIid;
            if (Marshal.QueryInterface(device, in iid, out nint videoDevice) < 0)
            {
                return [];
            }
            try
            {
                return ReadCodecs(videoDevice);
            }
            finally
            {
                Marshal.Release(videoDevice);
            }
        }
        finally
        {
            if (context != 0)
            {
                Marshal.Release(context);
            }
            Marshal.Release(device);
        }
    }

    private static List<string> ReadCodecs(nint videoDevice)
    {
        nint* vtable = *(nint**)videoDevice;
        var getCount = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[GetVideoDecoderProfileCountSlot];
        var getProfile = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, int>)vtable[GetVideoDecoderProfileSlot];
        var codecs = new List<string>();
        uint count = getCount(videoDevice);
        for (uint i = 0; i < count; i++)
        {
            Guid profile;
            if (getProfile(videoDevice, i, &profile) >= 0 && Codec(profile) is { } codec && !codecs.Contains(codec))
            {
                codecs.Add(codec);
            }
        }
        return codecs;
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);
}
