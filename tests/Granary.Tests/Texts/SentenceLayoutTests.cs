using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// A sentence with boxes in it (the collection editor's "Show [movies] from [all my libraries]"): which pieces it has,
/// where the message puts space, and what changes in the panel when the sentence does.
/// </summary>
public sealed class SentenceLayoutTests
{
    private static readonly string[] Boxes = ["scope", "libraries"];

    // "<w>Show</w> <scope></scope> <w>from</w> <libraries></libraries>", as the formatter gives it.
    private static IReadOnlyList<TextRun> English(string from = "from") =>
    [
        new("Show", ["w"]), new(" ", []), new("", ["scope"]), new(" ", []), new(from, ["w"]), new(" ", []), new("", ["libraries"]),
    ];

    [Fact]
    public void ASentence_IsItsWordsAndBoxes_SpacedWhereTheMessageHasSpace()
    {
        var plan = SentenceLayout.Plan(English("from the"), Boxes);

        Assert.Equal([(null, "Show", false), ("scope", "", true), (null, "from", true), (null, "the", true), ("libraries", "", true)],
            plan.Pieces.Select(p => (p.Slot, p.Text, p.SpaceBefore)));
        Assert.Empty(plan.Missing);
    }

    [Fact]
    public void ALanguageWithoutSpaces_GetsNoGaps()
    {
        using var japanese = AppText.Use(TextLanguage.Load("ja"));

        var plan = SentenceLayout.Plan(AppText.Runs("web.home.addRow.rules.groupSentence"), ["join", "match"]);

        Assert.All(plan.Pieces, piece => Assert.False(piece.SpaceBefore));
        Assert.Equal(["join", null, "match", null], plan.Pieces.Select(p => p.Slot));
    }

    [Fact]
    public void Punctuation_StaysWithTheBoxBeforeIt()
    {
        IReadOnlyList<TextRun> runs = [new("", ["scope"]), new(", then ", ["w"]), new("", ["libraries"]), new(".", [])];

        var plan = SentenceLayout.Plan(runs, Boxes);

        Assert.Equal([("scope", "", false), (null, ",", false), (null, "then", true), ("libraries", "", true), (null, ".", false)],
            plan.Pieces.Select(p => (p.Slot, p.Text, p.SpaceBefore)));
    }

    [Fact]
    public void ABoxATranslationLeavesOut_StandsAtTheEnd_AndIsNamed()
    {
        IReadOnlyList<TextRun> runs = [new("Show", ["w"]), new(" ", []), new("", ["libraries"])];

        var plan = SentenceLayout.Plan(runs, Boxes);

        Assert.Equal(["Show", "box:libraries", "box:scope"], plan.Pieces.Select(p => p.Slot is null ? p.Text : "box:" + p.Slot));
        Assert.True(plan.Pieces[^1].SpaceBefore);
        Assert.Equal(["scope"], plan.Missing);
    }

    [Fact]
    public void ABoxNamedTwice_StandsOnce()
    {
        IReadOnlyList<TextRun> runs = [new("", ["scope"]), new(" or ", []), new("", ["scope"])];

        Assert.Equal(["scope", null, "libraries"], SentenceLayout.Plan(runs, Boxes).Pieces.Select(p => p.Slot));
    }

    [Fact]
    public void TheSameSentenceAgain_IsTheSamePlan()
    {
        Assert.Equal(SentenceLayout.Plan(English(), Boxes), SentenceLayout.Plan(English(), Boxes));
        Assert.NotEqual(SentenceLayout.Plan(English(), Boxes), SentenceLayout.Plan(English("in"), Boxes));
    }

    [Fact]
    public void AWordTwice_IsKnownByWhichOneItIs()
    {
        IReadOnlyList<TextRun> runs = [new("of", []), new("", ["scope"]), new("of", [])];

        Assert.Equal(["word:of#1", "box:scope", "word:of#2", "box:libraries"], SentenceLayout.Plan(runs, Boxes).Pieces.Select(p => p.Key));
    }

    [Fact]
    public void NothingChanges_WhenTheSentenceIsTheSame()
    {
        var keys = SentenceLayout.Plan(English(), Boxes).Pieces.Select(p => p.Key).ToList();

        Assert.Empty(SentenceLayout.Steps(keys, keys));
    }

    [Fact]
    public void NewWords_GoInAroundTheBoxes_WhichNeitherLeaveNorMove()
    {
        var before = SentenceLayout.Plan(English(), Boxes).Pieces.Select(p => p.Key).ToList();
        var after = SentenceLayout.Plan(English("from the"), Boxes).Pieces.Select(p => p.Key).ToList();

        var steps = SentenceLayout.Steps(before, after);

        Assert.DoesNotContain(steps, step => step is SentenceStep.Move);
        Assert.DoesNotContain(steps, step => step is SentenceStep.Remove remove && before[remove.Index].StartsWith("box:", StringComparison.Ordinal));
        Assert.Equal(after, Apply(before, steps));
    }

    [Fact]
    public void BoxesInAnotherOrder_AreMoved_NotTakenOut()
    {
        string[] before = ["box:scope", "word:from#1", "box:libraries"];
        string[] after = ["box:libraries", "word:of#1", "box:scope"];

        var steps = SentenceLayout.Steps(before, after);

        Assert.DoesNotContain(steps, step => step is SentenceStep.Remove remove && before[remove.Index].StartsWith("box:", StringComparison.Ordinal));
        Assert.Contains(steps, step => step is SentenceStep.Move);
        Assert.Equal(after, Apply(before, steps));
    }

    [Fact]
    public void AWordBeforeABox_MovesItself_AndTheBoxStays()
    {
        string[] before = ["word:of#1", "box:scope", "word:from#1"];
        string[] after = ["box:scope", "word:of#1", "word:in#1"];

        var steps = SentenceLayout.Steps(before, after);

        Assert.DoesNotContain(steps, step => step is SentenceStep.Move);
        Assert.Equal(after, Apply(before, steps));
    }

    [Fact]
    public void WhatTheSentenceNoLongerHas_IsTakenOut()
    {
        string[] before = ["other:0", "box:scope", "word:from#1", "word:the#1", "box:libraries"];
        string[] after = ["box:scope", "word:from#1", "box:libraries"];

        Assert.Equal(after, Apply(before, SentenceLayout.Steps(before, after)));
    }

    // Carries the steps out on a list, as the panel does on its children.
    private static List<string> Apply(IReadOnlyList<string> keys, IReadOnlyList<SentenceStep> steps)
    {
        var list = keys.ToList();
        foreach (var step in steps)
        {
            switch (step)
            {
                case SentenceStep.Remove remove:
                    list.RemoveAt(remove.Index);
                    break;
                case SentenceStep.Move move:
                    string moved = list[move.From];
                    list.RemoveAt(move.From);
                    list.Insert(move.To, moved);
                    break;
                case SentenceStep.Insert insert:
                    list.Insert(insert.Index, insert.Key);
                    break;
            }
        }
        return list;
    }
}
