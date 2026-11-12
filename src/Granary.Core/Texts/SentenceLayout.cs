namespace Granary.Core.Texts;

/// <summary>A word of a sentence with boxes in it, or the box a tag of its message names.</summary>
/// <param name="Slot">The tag of the box that stands here; null for a word.</param>
/// <param name="Text">The word, without the space around it; empty for a box.</param>
/// <param name="SpaceBefore">
/// The message has white space before it: a gap there, none where the language writes without spaces or punctuation
/// follows a box.
/// </param>
/// <param name="Key">What a panel knows the piece by: <c>box:scope</c>, or <c>word:from#1</c> for the first "from".</param>
public sealed record SentencePiece(string? Slot, string Text, bool SpaceBefore, string Key);

/// <summary>A sentence's pieces in order, and the boxes its message left out, which stand at its end.</summary>
public sealed record SentencePlan(IReadOnlyList<SentencePiece> Pieces, IReadOnlyList<string> Missing)
{
    public bool Equals(SentencePlan? other) =>
        other is not null && Pieces.SequenceEqual(other.Pieces) && Missing.SequenceEqual(other.Missing, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var piece in Pieces)
        {
            hash.Add(piece);
        }
        return hash.ToHashCode();
    }
}

/// <summary>One change to a panel's children, in order: each step's indices are those after the steps before it.</summary>
public abstract record SentenceStep
{
    private SentenceStep()
    {
    }

    /// <summary>The child at <paramref name="Index"/> goes: the sentence no longer has it.</summary>
    public sealed record Remove(int Index) : SentenceStep;

    /// <summary>The child at <paramref name="From"/> moves to <paramref name="To"/>, staying in the tree.</summary>
    public sealed record Move(int From, int To) : SentenceStep;

    /// <summary>The piece known by <paramref name="Key"/> comes in at <paramref name="Index"/>.</summary>
    public sealed record Insert(int Index, string Key) : SentenceStep;
}

/// <summary>
/// How a sentence with boxes in it is laid out (the web's message with empty tags for its controls, "<c>&lt;w&gt;Show&lt;/w&gt;
/// &lt;scope&gt;&lt;/scope&gt; …</c>"): its words and boxes in the message's order, a gap only where the message has space,
/// and the least a panel must change to show a new sentence, so a box with focus or an open menu is never taken out.
/// </summary>
public static class SentenceLayout
{
    /// <summary>
    /// The pieces of <paramref name="runs"/> for the boxes <paramref name="slots"/> names: each word on its own, so a line
    /// can break between any two, and each box once, where its tag first stands. A box the message leaves out stands at
    /// the end, so a translation that forgets one still shows it.
    /// </summary>
    public static SentencePlan Plan(IReadOnlyList<TextRun> runs, IReadOnlyList<string> slots)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(slots);
        var pieces = new List<SentencePiece>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        bool space = false;
        foreach (var run in runs)
        {
            if (slots.FirstOrDefault(slot => run.Tags.Contains(slot)) is { } box)
            {
                if (placed.Add(box))
                {
                    pieces.Add(new SentencePiece(box, "", space, "box:" + box));
                }
                space = false;
                continue;
            }
            int start = -1;
            for (int i = 0; i <= run.Text.Length; i++)
            {
                bool white = i == run.Text.Length || char.IsWhiteSpace(run.Text[i]);
                if (!white && start < 0)
                {
                    start = i;
                }
                else if (white && start >= 0)
                {
                    string word = run.Text[start..i];
                    int count = seen[word] = seen.GetValueOrDefault(word) + 1;
                    pieces.Add(new SentencePiece(null, word, space, $"word:{word}#{count}"));
                    space = false;
                    start = -1;
                }
                if (white && i < run.Text.Length)
                {
                    space = true;
                }
            }
        }
        var missing = slots.Where(slot => !placed.Contains(slot)).Distinct(StringComparer.Ordinal).ToList();
        pieces.AddRange(missing.Select(slot => new SentencePiece(slot, "", true, "box:" + slot)));
        return new SentencePlan(pieces, missing);
    }

    /// <summary>
    /// The steps that turn children known by <paramref name="current"/> into <paramref name="wanted"/>: what the sentence no
    /// longer has goes, what stays is moved only where the order changed, and the rest comes in. Nothing for the same keys.
    /// </summary>
    public static IReadOnlyList<SentenceStep> Steps(IReadOnlyList<string> current, IReadOnlyList<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(wanted);
        var steps = new List<SentenceStep>();
        var list = current.ToList();
        var kept = wanted.ToHashSet(StringComparer.Ordinal);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(list[i]))
            {
                steps.Add(new SentenceStep.Remove(i));
                list.RemoveAt(i);
            }
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < list.Count && list[i] == wanted[i])
            {
                continue;
            }
            // A word in the way goes, and comes in again where it belongs: only boxes are moved, and only when their order
            // changed.
            while (i < list.Count && list[i] != wanted[i] && !IsBox(list[i]))
            {
                steps.Add(new SentenceStep.Remove(i));
                list.RemoveAt(i);
            }
            if (i < list.Count && list[i] == wanted[i])
            {
                continue;
            }
            int at = list.IndexOf(wanted[i], i);
            if (at >= 0)
            {
                steps.Add(new SentenceStep.Move(at, i));
                list.RemoveAt(at);
                list.Insert(i, wanted[i]);
            }
            else
            {
                steps.Add(new SentenceStep.Insert(i, wanted[i]));
                list.Insert(i, wanted[i]);
            }
        }
        return steps;
    }

    private static bool IsBox(string key) => key.StartsWith("box:", StringComparison.Ordinal);
}
