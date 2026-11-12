using Granary.Core.Texts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// A sentence with boxes in it, in the order its message has them, as the web fills a message's empty tags with
/// controls ("<c>&lt;w&gt;Show&lt;/w&gt; &lt;scope&gt;&lt;/scope&gt; &lt;w&gt;from&lt;/w&gt; …</c>"): each child whose
/// <see cref="FrameworkElement.Tag"/> names a tag stands where that tag is, and the words between become text blocks in
/// <see cref="WordStyle"/>. A child the sentence does not name is left out. So a language puts the boxes where its words
/// need them.
/// </summary>
public partial class SentencePanel : StackPanel
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.Register(nameof(Runs), typeof(object), typeof(SentencePanel),
        new PropertyMetadata(null, (d, _) => ((SentencePanel)d).Lay()));

    public static readonly DependencyProperty WordStyleProperty = DependencyProperty.Register(nameof(WordStyle), typeof(Style), typeof(SentencePanel),
        new PropertyMetadata(null, (d, _) => ((SentencePanel)d).Lay()));

    // The boxes XAML put in the panel: the panel's children are laid out again from them.
    private readonly List<FrameworkElement> _slots = [];

    public SentencePanel()
    {
        Orientation = Orientation.Horizontal;
    }

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

    private void Lay()
    {
        if (Runs is not IReadOnlyList<TextRun> runs)
        {
            return;
        }
        // XAML's boxes are among the children until the first layout takes them out of the words.
        _slots.AddRange(Children.OfType<FrameworkElement>().Where(child => child.Tag is string && !_slots.Contains(child)));
        Children.Clear();
        var placed = new HashSet<FrameworkElement>();
        foreach (var run in runs)
        {
            if (_slots.FirstOrDefault(slot => run.Tags.Contains((string)slot.Tag)) is { } box)
            {
                if (placed.Add(box))
                {
                    Children.Add(box);
                }
                continue;
            }
            string words = run.Text.Trim();
            if (words.Length > 0)
            {
                Children.Add(new TextBlock { Text = words, Style = WordStyle, VerticalAlignment = VerticalAlignment.Center });
            }
        }
    }
}
