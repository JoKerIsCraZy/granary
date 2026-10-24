namespace Granary.MpvSpike;

internal enum SurfaceMode
{
    Composition,
    RenderApi,
    Hwnd,
}

/// <summary>
/// Command line: --mode=composition|render|hwnd --source=PATH_OR_URL [--header="Name: value"]
/// [--sub=URL] [--selftest] [--log=PATH] [--shots=DIR] [--hwnd-zorder=top|bottom] [--hdr-output]
/// [--rotate-token-file=PATH] [--hwdec=auto-safe] [--small-cache].
/// </summary>
internal sealed record SpikeOptions(
    SurfaceMode Mode,
    string? Source,
    string? Header,
    string? SubtitleUrl,
    bool SelfTest,
    string LogPath,
    string ShotsDir,
    bool HwndBottom,
    bool HdrOutput,
    string? RotateTokenFile,
    string Hwdec,
    bool SmallCache)
{
    public static SpikeOptions Parse(string[] args)
    {
        string Get(string name, string fallback) =>
            args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..] ?? fallback;
        bool Has(string name) => args.Contains($"--{name}");

        SurfaceMode mode = Get("mode", "composition") switch
        {
            "render" => SurfaceMode.RenderApi,
            "hwnd" => SurfaceMode.Hwnd,
            _ => SurfaceMode.Composition,
        };
        string? source = Get("source", "");
        string? header = Get("header", "");
        string? sub = Get("sub", "");
        string? rotate = Get("rotate-token-file", "");
        return new SpikeOptions(
            mode,
            source.Length > 0 ? source : null,
            header.Length > 0 ? header : null,
            sub.Length > 0 ? sub : null,
            Has("selftest"),
            Get("log", Path.Combine(AppContext.BaseDirectory, "spike.log")),
            Get("shots", Path.Combine(AppContext.BaseDirectory, "shots")),
            Get("hwnd-zorder", "top") == "bottom",
            Has("hdr-output"),
            rotate.Length > 0 ? rotate : null,
            Get("hwdec", "auto-safe"),
            Has("small-cache"));
    }
}
