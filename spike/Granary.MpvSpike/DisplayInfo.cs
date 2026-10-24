using Vortice.DXGI;

namespace Granary.MpvSpike;

/// <summary>Logs every DXGI output with its active color space, so HDR results can be interpreted.</summary>
internal static class DisplayInfo
{
    public static void LogOutputs()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        using (var output6 = output.QueryInterface<IDXGIOutput6>())
                        {
                            var d = output6.Description1;
                            bool hdr = d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                            SpikeLog.Write("display",
                                $"{adapter.Description1.Description} | {d.DeviceName} {d.DesktopCoordinates.Right - d.DesktopCoordinates.Left}x{d.DesktopCoordinates.Bottom - d.DesktopCoordinates.Top} " +
                                $"colorspace={d.ColorSpace} hdrActive={hdr} bits={d.BitsPerColor} maxLum={d.MaxLuminance} maxFullFrame={d.MaxFullFrameLuminance}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SpikeLog.Write("display", $"probe failed: {ex.Message}");
        }
    }
}
