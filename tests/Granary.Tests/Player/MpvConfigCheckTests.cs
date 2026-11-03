using Granary.Player;

namespace Granary.Tests.Player;

/// <summary>The editor's check of a mpv.conf: the player's own pre-scan, in plain words.</summary>
public sealed class MpvConfigCheckTests
{
    private static readonly MpvConfigCheck Check = new(new MpvEngineOptions());

    [Fact]
    public void AFileOfOtherOptions_HasNothingToSay() =>
        Assert.Empty(Check.Notes("# a comment\ndeband=yes\nscale=ewa_lanczossharp\n[hq]\ninterpolation=yes\n"));

    [Fact]
    public void GranarysOwnOptions_AreNamedOnce_InTheFilesOrder()
    {
        var notes = Check.Notes("vo=gpu\ndeband=yes\nno-osc\nvo=gpu-next\n[hq]\nkeep-open=no\n[later]\nkeep-open=no\n");

        Assert.Equal(
        [
            "Granary keeps its own vo.",
            "Granary keeps its own osc.",
            "Granary keeps its own keep-open, which the profile hq sets.",
        ], notes);
    }

    [Fact]
    public void AnOptionGranaryGuards_IsNamedToo() =>
        Assert.Equal(["Granary keeps its own scripts."], Check.Notes("script=~~/scripts/thing.lua\n"));

    [Theory]
    [InlineData("player-operation-mode=cplayer\nvo=gpu\n",
        "This file would not be used: it sets player-operation-mode, which Granary needs to keep its own options.")]
    [InlineData("[pseudo-gui]\nkeep-open=no\n",
        "This file would not be used: it sets keep-open in its [pseudo-gui] profile, which mpv applies over Granary's own options.")]
    [InlineData("[protocol.http]\ninput-commands=quit\n",
        "This file would not be used: it sets input-commands in its profile protocol.http, which mpv runs as soon as the profile applies.")]
    public void AFileThePlayerWouldRefuse_SaysSoAlone(string text, string note) =>
        Assert.Equal([note], Check.Notes(text));

    [Fact]
    public void AnInclude_IsCheckedOnlyWhenThePlayerStarts() =>
        Assert.Equal(["Granary checks the files it includes only when the player starts."], Check.Notes("include=~~/more.conf\ndeband=yes\n"));

    [Fact]
    public void LineEndingsOfAnyKind_ReadTheSame() =>
        Assert.Equal(Check.Notes("vo=gpu\nidle=no\n"), Check.Notes("vo=gpu\ridle=no\r"));
}
