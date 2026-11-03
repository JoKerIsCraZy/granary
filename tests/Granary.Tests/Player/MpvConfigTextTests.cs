using Granary.Player;

namespace Granary.Tests.Player;

/// <summary>What a viewer's mpv.conf sets of Granary's options, read as mpv reads it, and when a file is refused.</summary>
public sealed class MpvConfigTextTests
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> Own =
        [.. MpvOwnOptions.Core(VideoOutputKind.D3D11Composition, (1280, 720)), .. MpvOwnOptions.Guarded(VideoOutputKind.D3D11Composition)];

    private static readonly IReadOnlySet<string> Names = Own.Select(o => o.Key).Append(MpvOwnOptions.OperationMode).ToHashSet();

    private static readonly IReadOnlyDictionary<string, string> Kept =
        Own.Where(o => o.Key != MpvOwnOptions.CompositionSize).ToDictionary();

    [Fact]
    public void AFilesOptions_AreReadAsMpvReadsThem()
    {
        const string text = """
            # vo=gpu is a comment
            deband=yes
              --vo = gpu   # an option, spaced out
            no-osc
            keep-open
            glsl-shaders-append=~~/shaders/FSRCNNX.glsl
            script=~~/scripts/thing.lua
            scale=ewa_lanczossharp # keep-open in a comment
            sub-font="keep-open"
            log-file="C:\logs\mpv # not a comment.log"
            http-proxy=%21%http://127.0.0.1:8080
            [default]
            idle=no
            [anime]
            profile-cond=p["video-params/primaries"] == "bt.709"
            keep-open=no
            """;

        var found = MpvConfigText.Entries(text, Names);

        Assert.Equal(
        [
            new("vo", null, "gpu"), new("osc", null, "no"), new("keep-open", null, "yes"), new("scripts", null, "~~/scripts/thing.lua"),
            new("log-file", null, @"C:\logs\mpv # not a comment.log"), new("http-proxy", null, "http://127.0.0.1:8080"),
            new("idle", null, "no"), new("keep-open", "anime", "no"),
        ], found);
    }

    [Theory]
    [InlineData("deband=%-1%x\n")]
    [InlineData("deband=%+3%abc\n")]
    [InlineData("deband=%99999999999%x\n")]
    [InlineData("vo=%-2%gpu\n")]
    public void ASignedOrHugeLength_NeverThrows(string text)
    {
        var thrown = Record.Exception(() => MpvConfigText.Entries(text, Names));

        Assert.Null(thrown);
    }

    [Fact]
    public void AFileWithoutGranarysOptions_NamesNone() =>
        Assert.Empty(MpvConfigText.Entries("deband=yes\r\nscale=ewa_lanczossharp\r\n[hq]\r\ninterpolation=yes\r\n", Names));

    [Fact]
    public void AFileThatSetsGranarysOptions_AtTheTopOrInProfiles_IsPlayed()
    {
        var entries = MpvConfigText.Entries("vo=gpu\nkeep-open=no\nidle=no\n[protocol.https]\nlog-file=x.log\n[pseudo-gui]\nkeep-open=yes\n", Names);

        Assert.Null(MpvConfigText.Refusal(entries, Kept));
    }

    [Theory]
    [InlineData("player-operation-mode=cplayer\n", "sets player-operation-mode")]
    [InlineData("[some]\nplayer-operation-mode=cplayer\n", "sets player-operation-mode")]
    [InlineData("[pseudo-gui]\nvo=gpu\n", "sets vo in its [pseudo-gui] profile")]
    [InlineData("[pseudo-gui]\nno-keep-open\n", "sets keep-open in its [pseudo-gui] profile")]
    [InlineData("[protocol.http]\ninput-commands=quit\n", "sets input-commands in its profile protocol.http")]
    [InlineData("[later]\ninput-commands-append=quit\n", "sets input-commands in its profile later")]
    public void AFileThatWouldUndoGranarysOptions_IsRefused(string text, string reason) =>
        Assert.StartsWith(reason, MpvConfigText.Refusal(MpvConfigText.Entries(text, Names), Kept), StringComparison.Ordinal);

    [Fact]
    public void ItsOwnPseudoGuiProfile_MaySetWhatGranarySetsAnyway_AndOtherOptions() =>
        Assert.Null(MpvConfigText.Refusal(
            MpvConfigText.Entries("player-operation-mode=pseudo-gui\n[pseudo-gui]\nfullscreen=yes\nforce-window=yes\nidle=YES\n", Names), Kept));

    [Fact]
    public void WhatMpvRead_IsTheFilesPartOfItsProfiles()
    {
        var own = new List<KeyValuePair<string, string>> { new("vo", "gpu-next"), new("keep-open", "yes") };
        const string profiles = """
            [
              { "name": "later", "profile-cond": "true", "options": [ { "key": "keep-open", "value": "no" }, { "key": "deband", "value": "yes" } ] },
              { "name": "pseudo-gui", "options": [ { "key": "player-operation-mode", "value": "pseudo-gui" },
                { "key": "vo", "value": "gpu-next" }, { "key": "keep-open", "value": "yes" }, { "key": "no-osc", "value": "" },
                { "key": "vo", "value": "gpu" } ] },
              { "name": "default", "options": [] }
            ]
            """;

        var found = MpvConfigText.FromProfiles(profiles, own, Names);

        // Granary's own entries in the pseudo-GUI profile are not the file's.
        Assert.Equal([new("keep-open", "later", "no"), new("osc", "pseudo-gui", "no"), new("vo", "pseudo-gui", "gpu")], found);
        Assert.StartsWith("sets vo in its [pseudo-gui] profile", MpvConfigText.Refusal(found, Kept), StringComparison.Ordinal);
    }

    // A config folder of files in memory, by full path; a missing one reads as none, as in mpv.
    private static readonly string Folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "granary-scan", "mpv"));

    private static MpvConfigScan Scan(params (string Name, string Text)[] files)
    {
        var all = files.ToDictionary(f => Path.GetFullPath(Path.Combine(Folder, f.Name)), f => f.Text, StringComparer.OrdinalIgnoreCase);
        return MpvConfigText.Scan(Path.Combine(Folder, "mpv.conf"), Folder, Names, path => all.GetValueOrDefault(path));
    }

    [Fact]
    public void AnInclude_IsReadWhereItStands_InItsProfile()
    {
        var scan = Scan(
            ("mpv.conf", "include=~~/top.conf\n[pseudo-gui]\ninclude=\"~~/sub/gui.conf\"\n[later]\ninclude=~~home/later.conf\n"),
            ("top.conf", "vo=gpu\n[own]\nidle=no\n"),
            ("sub/gui.conf", "keep-open=no\n"),
            ("later.conf", "osc=yes\n"));

        Assert.Null(scan.Problem);
        Assert.Equal([new("vo", null, "gpu"), new("idle", "own", "no"), new("keep-open", "pseudo-gui", "no"), new("osc", "later", "yes")],
            scan.Entries);
        Assert.StartsWith("sets keep-open in its [pseudo-gui] profile", MpvConfigText.Refusal(scan.Entries, Kept), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncludeByItsFullPath_IsRead()
    {
        string other = Path.Combine(Folder, "elsewhere", "x.conf");

        var scan = Scan(("mpv.conf", $"include=\"{other}\"\n"), ("elsewhere/x.conf", "player-operation-mode=cplayer\n"));

        Assert.StartsWith("sets player-operation-mode", MpvConfigText.Refusal(scan.Entries, Kept), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingInclude_IsPassedOver_AsMpvDoes()
    {
        var scan = Scan(("mpv.conf", "include=~~/gone.conf\nvo=gpu\n"));

        Assert.Null(scan.Problem);
        Assert.Equal([new("vo", null, "gpu")], scan.Entries);
    }

    [Fact]
    public void IncludesThatIncludeEachOther_AreTheProblem()
    {
        // mpv 0.41 reads them without end, and never starts.
        var scan = Scan(("mpv.conf", "include=~~/a.conf\n"), ("a.conf", "vo=gpu\ninclude=~~/b.conf\n"), ("b.conf", "include=~~/A.conf\nidle=no\n"));

        Assert.Equal("has includes that include each other, which mpv reads without end", scan.Problem);
    }

    [Fact]
    public void AFileIncludedTwice_OneAfterTheOther_IsNoCycle()
    {
        var scan = Scan(("mpv.conf", "include=~~/common.conf\n[later]\ninclude=~~/common.conf\n"), ("common.conf", "idle=no\n"));

        Assert.Null(scan.Problem);
        Assert.Equal([new("idle", null, "no"), new("idle", "later", "no")], scan.Entries);
    }

    [Fact]
    public void IncludesNestedDeeperThanEight_AreTheProblem()
    {
        var files = Enumerable.Range(1, 9).Select(n => ($"{n}.conf", n == 9 ? "vo=gpu\n" : $"include=~~/{n + 1}.conf\n")).ToList();
        files.Add(("mpv.conf", "include=~~/1.conf\n"));

        Assert.Equal("has includes nested deeper than 8", Scan([.. files]).Problem);
        files[7] = ("8.conf", "idle=no\n");
        Assert.Equal([new("idle", null, "no")], Scan([.. files]).Entries);
    }

    [Theory]
    [InlineData("include=memory://vo=gpu\n", "has an include Granary cannot follow")]
    [InlineData("include=~~cache/x.conf\n", "has an include Granary cannot follow")]
    public void AnIncludeGranaryCannotFollow_IsTheProblem(string text, string problem) =>
        Assert.Equal(problem, Scan(("mpv.conf", text)).Problem);

    [Fact]
    public void AnIncludeGranaryCannotRead_IsTheProblem()
    {
        var scan = MpvConfigText.Scan(Path.Combine(Folder, "mpv.conf"), Folder, Names, path =>
            path.EndsWith("locked.conf", StringComparison.Ordinal) ? throw new UnauthorizedAccessException() : "include=~~/locked.conf\n");

        Assert.Equal("includes locked.conf, which Granary cannot read", scan.Problem);
    }
}
