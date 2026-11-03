using Granary.Player;

namespace Granary.Tests.Player;

/// <summary>The mpv options Granary sets, and keeps over the viewer's own mpv.conf.</summary>
public sealed class MpvOwnOptionsTests
{
    [Fact]
    public void WithoutAFile_GranarySetsWhatItAlwaysSet_InTheSameOrder()
    {
        var core = MpvOwnOptions.Core(VideoOutputKind.D3D11Composition, (1920, 1080));

        Assert.Equal(
        [
            new("keep-open", "yes"), new("idle", "yes"), new("input-default-bindings", "no"), new("input-vo-keyboard", "no"),
            new("osc", "no"), new("ytdl", "no"), new("sub-auto", "no"), new("rebase-start-time", "yes"), new("media-controls", "no"),
            new("resume-playback", "no"), new("save-position-on-quit", "no"), new("vo", "gpu-next"), new("gpu-api", "d3d11"),
            new("gpu-context", "d3d11"), new("d3d11-output-mode", "composition"), new("d3d11-composition-size", "1920x1080"),
            new("force-window", "yes"),
        ], core);
        Assert.Equal([new("vo", "null"), new("ao", "null")], MpvOwnOptions.Core(VideoOutputKind.None, (1280, 720)).TakeLast(2));
    }

    [Fact]
    public void WithAFile_TheStreamsAddressStaysInGranary_AndNothingOfTheFilesRuns()
    {
        var guarded = MpvOwnOptions.Guarded(VideoOutputKind.D3D11Composition).ToDictionary();

        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["save-watch-history"] = "no", ["watch-later-dir"] = "", ["write-filename-in-watch-later-config"] = "no", ["log-file"] = "",
            ["dump-stats"] = "", ["stream-record"] = "", ["stream-dump"] = "", ["http-proxy"] = "", ["stream-lavf-o"] = "",
            ["demuxer-lavf-o"] = "", ["input-ipc-server"] = "", ["input-conf"] = "memory://", ["input-commands"] = "",
            ["load-scripts"] = "no", ["scripts"] = "", ["register"] = "no", ["unregister"] = "no",
        })
        {
            Assert.Equal(value, guarded[name]);
        }
        Assert.DoesNotContain("force-window", guarded.Keys);
        // Headless, the pseudo-GUI profile's own force-window=yes would make a window.
        Assert.Equal("no", MpvOwnOptions.Guarded(VideoOutputKind.None).ToDictionary()["force-window"]);
        // None of them is set without a file.
        Assert.Empty(MpvOwnOptions.Core(VideoOutputKind.D3D11Composition, (1280, 720)).Select(o => o.Key).Intersect(guarded.Keys));
    }

    [Fact]
    public void TheProfile_IsMpvsPseudoGuiProfile_WithGranarysOptions()
    {
        string profile = MpvOwnOptions.Profile([new("vo", "gpu-next"), new("log-file", ""), new("input-commands", "")]);

        // Set to "", input-commands would hold one empty command: it is cleared instead.
        Assert.Equal("[pseudo-gui]\nvo=gpu-next\nlog-file=\ninput-commands-clr=\n", profile);
    }

    [Fact]
    public void EachTitle_GetsGranarysOptions_QuotedByLength_ButTheSizeGranaryChangesItself()
    {
        var perFile = MpvOwnOptions.PerFile([new("vo", "gpu-next"), new("d3d11-composition-size", "1280x720"), new("log-file", ""),
            new("input-conf", "memory://"), new("input-commands", "")]);

        Assert.Equal(["vo=%8%gpu-next", "log-file=%0%", "input-conf=%9%memory://", "input-commands-clr=%0%"], perFile);
    }

    [Fact]
    public void PerFileValues_AreQuotedByUtf8Bytes_AndCannotSplitAtCommas()
    {
        var perFile = MpvOwnOptions.PerFile([new("example", "é,=x"), new("input-commands", "quit")]);

        Assert.Equal(["example=%5%é,=x", "input-commands=%4%quit"], perFile);
    }

    [Theory]
    [InlineData("yes", "yes", true)]
    [InlineData(" Composition ", "composition", true)]
    [InlineData("gpu", "gpu-next", false)]
    [InlineData(null, "", false)]
    public void MpvsValue_IsGranarysWhateverItsCase(string? value, string own, bool same) =>
        Assert.Equal(same, MpvOwnOptions.Same(value, own));
}
