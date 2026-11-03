using Granary.Player;

namespace Granary.Tests.Player;

/// <summary>The mpv options Granary keeps over the viewer's own mpv.conf, and how the log names those a file sets.</summary>
public sealed class MpvOwnOptionsTests
{
    private static readonly IReadOnlySet<string> Names =
        MpvOwnOptions.For(VideoOutputKind.D3D11Composition, (1280, 720)).Select(o => o.Key).ToHashSet();

    [Fact]
    public void TheVideo_StaysInGranarysWindow()
    {
        var own = MpvOwnOptions.For(VideoOutputKind.D3D11Composition, (1920, 1080)).ToDictionary();

        Assert.Equal("gpu-next", own["vo"]);
        Assert.Equal("d3d11", own["gpu-api"]);
        Assert.Equal("d3d11", own["gpu-context"]);
        Assert.Equal("composition", own["d3d11-output-mode"]);
        Assert.Equal("1920x1080", own["d3d11-composition-size"]);
        Assert.Equal("yes", own["force-window"]);
    }

    [Fact]
    public void GranaryDrivesThePlayer_AndTheStreamsAddressStaysInGranary()
    {
        var own = MpvOwnOptions.For(VideoOutputKind.None, (1280, 720)).ToDictionary();

        Assert.Equal("null", own["vo"]);
        Assert.Equal("null", own["ao"]);
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["keep-open"] = "yes", ["idle"] = "yes", ["input-default-bindings"] = "no", ["input-vo-keyboard"] = "no", ["osc"] = "no",
            ["ytdl"] = "no", ["sub-auto"] = "no", ["rebase-start-time"] = "yes", ["media-controls"] = "no", ["resume-playback"] = "no",
            ["save-position-on-quit"] = "no", ["save-watch-history"] = "no", ["log-file"] = "", ["input-ipc-server"] = "",
            ["load-scripts"] = "no", ["scripts"] = "", ["register"] = "no", ["unregister"] = "no",
        })
        {
            Assert.Equal(value, own[name]);
        }
    }

    [Fact]
    public void TheProfile_IsMpvsPseudoGuiProfile_WithGranarysOptions()
    {
        string profile = MpvOwnOptions.Profile([new("vo", "gpu-next"), new("log-file", "")]);

        Assert.Equal("[pseudo-gui]\nvo=gpu-next\nlog-file=\n", profile);
    }

    [Fact]
    public void AFilesOptions_AreNamedAsMpvReadsThem()
    {
        const string text = """
            # vo=gpu is a comment
            deband=yes
              --vo = gpu   # an option, spaced out
            no-osc
            keep-open=no
            keep-open=always
            glsl-shaders-append=~~/shaders/FSRCNNX.glsl
            script=~~/scripts/thing.lua
            scale=ewa_lanczossharp # keep-open in a comment
            sub-font="keep-open"
            [default]
            idle=no
            [anime]
            profile-cond=p["video-params/primaries"] == "bt.709"
            keep-open=no
            """;

        var found = MpvOwnOptions.SetIn(text, Names);

        Assert.Equal(
            [("vo", null), ("osc", null), ("keep-open", null), ("scripts", null), ("idle", null), ("keep-open", "anime")],
            found);
    }

    [Fact]
    public void AFileWithoutGranarysOptions_NamesNone() =>
        Assert.Empty(MpvOwnOptions.SetIn("deband=yes\r\nscale=ewa_lanczossharp\r\n[hq]\r\ninterpolation=yes\r\n", Names));
}
