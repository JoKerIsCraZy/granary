using System.Collections.Concurrent;
using Granary.Core.Texts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// A sentence with boxes in it, in the order its message has them, as the web fills a message's empty tags with
/// controls ("<c>&lt;w&gt;Show&lt;/w&gt; &lt;scope&gt;&lt;/scope&gt; &lt;w&gt;from&lt;/w&gt; …</c>"): each child whose
/// <see cref="FrameworkElement.Tag"/> names a tag stands where that tag is, and the words between become text blocks in
/// <see cref="WordStyle"/>. So a language puts the boxes where its words need them. A box its message leaves out stands at
/// the end (and the log says which, once for each sentence); a child without a tag is left out. No sentence (a null
/// <see cref="Runs"/>) empties the panel.
/// <para>
/// The words and boxes wrap onto further lines like text, <see cref="Spacing"/> apart where the message has space and
/// touching where it has none (Japanese, or punctuation after a box). A new sentence changes only what differs
/// (<see cref="SentenceLayout"/>): the boxes stay in the tree, so one with focus or an open menu keeps it, and the same
/// sentence again changes nothing.
/// </para>
/// <para>
/// A screen reader hears the panel as one group named by its sentence (<see cref="SentencePlan.Spoken"/>, "Show … from …";
/// the panel sets its own name), then each box in the sentence's order, with its own name and value. The words are not
/// in its tree, so it no longer stops at each one.
/// </para>
/// </summary>
public partial class SentencePanel : Panel
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.Register(nameof(Runs), typeof(object), typeof(SentencePanel),
        new PropertyMetadata(null, (d, _) => ((SentencePanel)d).Lay()));

    public static readonly DependencyProperty WordStyleProperty = DependencyProperty.Register(nameof(WordStyle), typeof(Style), typeof(SentencePanel),
        new PropertyMetadata(null, (d, _) => ((SentencePanel)d).Restyle()));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(SentencePanel),
        new PropertyMetadata(8.0, (d, _) => ((SentencePanel)d).InvalidateMeasure()));

    // The boxes left out of a sentence that the log has named, by box and sentence, so each is named once.
    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);

    // The boxes XAML put in the panel, in its order, by their tag.
    private readonly List<string> _slots = [];
    private readonly Dictionary<string, FrameworkElement> _boxes = new(StringComparer.Ordinal);
    // Every child the panel laid, by its key, and the other way round; and which have the message's space before them.
    private readonly Dictionary<string, UIElement> _pieces = new(StringComparer.Ordinal);
    private readonly Dictionary<UIElement, string> _keys = [];
    private readonly HashSet<UIElement> _spaced = [];
    private SentencePlan? _plan;

    /// <summary>The sentence, as <see cref="AppText.Runs"/> gives it (an object, which XAML binds without knowing the list's type).</summary>
    public object? Runs
    {
        get => GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    /// <summary>The style of the sentence's words.</summary>
    public Style? WordStyle
    {
        get => (Style?)GetValue(WordStyleProperty);
        set => SetValue(WordStyleProperty, value);
    }

    /// <summary>The gap where the message has space, and between lines.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private void Lay()
    {
        TakeBoxes();
        // No sentence empties the panel; its boxes stay known by their tags and come back with the next one.
        var plan = Runs is IReadOnlyList<TextRun> runs ? SentenceLayout.Plan(runs, _slots) : null;
        if (plan == _plan)
        {
            return;
        }
        _plan = plan;
        AutomationProperties.SetName(this, plan?.Spoken ?? "");
        IReadOnlyList<SentencePiece> pieces = plan?.Pieces ?? [];
        if (plan is not null)
        {
            Report(plan);
        }
        var wanted = pieces.Select(piece => piece.Key).ToList();
        foreach (var step in SentenceLayout.Steps(CurrentKeys(), wanted))
        {
            Apply(step, pieces);
        }
        _spaced.Clear();
        foreach (var piece in pieces)
        {
            if (piece.SpaceBefore)
            {
                _spaced.Add(_pieces[piece.Key]);
            }
        }
        // Words the sentence no longer has are forgotten.
        foreach (string key in _pieces.Keys.Where(key => !wanted.Contains(key) && !_boxes.ContainsKey(key)).ToList())
        {
            _keys.Remove(_pieces[key]);
            _pieces.Remove(key);
        }
        InvalidateMeasure();
    }

    // XAML's boxes are among the children before the first sentence: each is known by its tag from then on.
    private void TakeBoxes()
    {
        foreach (var child in Children.OfType<FrameworkElement>())
        {
            if (child.Tag is string slot && !_boxes.ContainsKey("box:" + slot))
            {
                _slots.Add(slot);
                _boxes["box:" + slot] = child;
                _pieces["box:" + slot] = child;
                _keys[child] = "box:" + slot;
            }
        }
    }

    // The children as keys; one the panel does not know (a child without a tag) gets a key no sentence has.
    private List<string> CurrentKeys() =>
        [.. Children.Select((child, i) => _keys.TryGetValue(child, out string? key) ? key : $"other:{i}")];

    private void Apply(SentenceStep step, IReadOnlyList<SentencePiece> pieces)
    {
        switch (step)
        {
            case SentenceStep.Remove remove:
                Children.RemoveAt(remove.Index);
                break;
            case SentenceStep.Move move:
                Children.Move((uint)move.From, (uint)move.To);
                break;
            case SentenceStep.Insert insert:
                Children.Insert(insert.Index, Piece(pieces.First(piece => piece.Key == insert.Key)));
                break;
        }
    }

    // A box, or a word's text block (the one it had, when it comes back).
    private UIElement Piece(SentencePiece piece)
    {
        if (_pieces.TryGetValue(piece.Key, out var known))
        {
            return known;
        }
        var word = new TextBlock { Text = piece.Text, Style = WordStyle, VerticalAlignment = VerticalAlignment.Center };
        // The panel's name says the words, all at once.
        AutomationProperties.SetAccessibilityView(word, AccessibilityView.Raw);
        _pieces[piece.Key] = word;
        _keys[word] = piece.Key;
        return word;
    }

    private void Restyle()
    {
        foreach (var word in Children.OfType<TextBlock>().Where(child => _keys.TryGetValue(child, out string? key) && !_boxes.ContainsKey(key)))
        {
            word.Style = WordStyle;
        }
    }

    // A translation that leaves a box out shows it at the end. Each sentence's missing box is reported once, whichever
    // panel shows it; the log names the box and the panel's boxes, never the text.
    private void Report(SentencePlan plan)
    {
        foreach (string slot in plan.Missing)
        {
            if (Reported.TryAdd(slot + "\n" + plan.Spoken, 0))
            {
                App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SentencePanel))
                    .LogWarning("A sentence's message leaves out its box {Slot} of {Boxes}: it stands at the end", slot, string.Join(", ", _slots));
            }
        }
    }

    // Each child as wide as it wants (a box) or on one line (a word); the lines from them.
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }
        return Lines(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Lines(finalSize.Width, arrange: true);
        return finalSize;
    }

    // Children left to right (the framework mirrors them for a language written right to left), a line breaking before a
    // child that no longer fits; a gap only where the message has space, and none at a line's start. Each child is
    // centred in its line's height.
    private Size Lines(double width, bool arrange)
    {
        double gap = Spacing;
        double x = 0;
        double y = 0;
        double lineHeight = 0;
        double widest = 0;
        int lineStart = 0;
        var lefts = new double[Children.Count];
        for (int i = 0; i < Children.Count; i++)
        {
            var size = Children[i].DesiredSize;
            if (Children[i].Visibility == Visibility.Collapsed || (size.Width == 0 && size.Height == 0))
            {
                lefts[i] = x;
                continue;
            }
            double before = x > 0 && _spaced.Contains(Children[i]) ? gap : 0;
            if (x > 0 && x + before + size.Width > width)
            {
                Place(lineStart, i, lefts, y, lineHeight, arrange);
                y += lineHeight + gap;
                x = 0;
                before = 0;
                lineHeight = 0;
                lineStart = i;
            }
            lefts[i] = x + before;
            x += before + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
            widest = Math.Max(widest, x);
        }
        Place(lineStart, Children.Count, lefts, y, lineHeight, arrange);
        return new Size(widest, y + lineHeight);
    }

    private void Place(int from, int to, double[] lefts, double top, double lineHeight, bool arrange)
    {
        if (!arrange)
        {
            return;
        }
        for (int i = from; i < to; i++)
        {
            var size = Children[i].DesiredSize;
            Children[i].Arrange(new Rect(lefts[i], top + (lineHeight - size.Height) / 2, size.Width, size.Height));
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SentencePanelAutomationPeer(this);

    // A group named by the sentence, whose children are its boxes in the sentence's order.
    private sealed partial class SentencePanelAutomationPeer(SentencePanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(SentencePanel);

        protected override bool IsControlElementCore() => true;
    }
}
