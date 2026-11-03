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
    public void AnOptionThatCouldTakeTheStreamOut_MeansTheFileIsNotUsed() =>
        Assert.Equal(["This file would not be used: it sets scripts, which Granary does not let mpv.conf change."],
            Check.Notes("deband=yes\nscript=~~/scripts/thing.lua\n"));

    [Fact]
    public void SuchAnOption_AtTheValueGranaryGivesIt_IsFine() =>
        Assert.Empty(Check.Notes("load-scripts=no\nytdl=no\n"));

    [Fact]
    public void EveryOptionTheFileMayNotSet_IsSaid()
    {
        foreach (string option in MpvOwnOptions.Refused.Keys)
        {
            var notes = Check.Notes(option + "=C:\\elsewhere\n");
            Assert.True(notes.Count == 1 && notes[0].StartsWith($"This file would not be used: it sets {option}", StringComparison.Ordinal),
                $"{option}: {string.Join(" | ", notes)}");
        }
    }

    [Theory]
    [InlineData("player-operation-mode=cplayer\nvo=gpu\n",
        "This file would not be used: it sets player-operation-mode, which Granary needs to keep its own options.")]
    [InlineData("[pseudo-gui]\nkeep-open=no\n",
        "This file would not be used: it sets keep-open in its [pseudo-gui] profile, which mpv applies over Granary's own options.")]
    [InlineData("[protocol.http]\ninput-commands=quit\n",
        "This file would not be used: it sets input-commands, which Granary does not let mpv.conf change.")]
    public void AFileThePlayerWouldRefuse_SaysSoAlone(string text, string note) =>
        Assert.Equal([note], Check.Notes(text));

    [Fact]
    public void AnInclude_IsCheckedOnlyWhenThePlayerStarts() =>
        Assert.Equal(["Granary checks the files it includes only when the player starts."], Check.Notes("include=~~/more.conf\ndeband=yes\n"));

    [Fact]
    public void LineEndingsOfAnyKind_ReadTheSame() =>
        Assert.Equal(Check.Notes("vo=gpu\nidle=no\n"), Check.Notes("vo=gpu\ridle=no\r"));
}
