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
}
