using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>
/// "ALL" and A–Z for a title-ordered grid. <see cref="LetterCommand"/> gets the letter ("A" … "Z"), or null
/// for every title; <see cref="SelectedLetter"/> marks the current one. There is no "#": the server matches
/// a name prefix literally and has no bucket for titles starting with a digit or symbol.
/// </summary>
public sealed partial class AlphabetJumpBar : UserControl
{
    public static readonly DependencyProperty SelectedLetterProperty =
        DependencyProperty.Register(nameof(SelectedLetter), typeof(string), typeof(AlphabetJumpBar),
            new PropertyMetadata(null, (sender, _) => ((AlphabetJumpBar)sender).ShowSelection()));

    public static readonly DependencyProperty LetterCommandProperty =
        DependencyProperty.Register(nameof(LetterCommand), typeof(ICommand), typeof(AlphabetJumpBar), new PropertyMetadata(null));

    private readonly List<Button> _entries = [];

    public AlphabetJumpBar()
    {
        InitializeComponent();
        AddEntry(null, "ALL", "All titles");
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string text = letter.ToString();
            AddEntry(text, text, $"Letter {text}");
        }
        ShowSelection();
    }

    /// <summary>The picked letter, or null when every title shows.</summary>
    public string? SelectedLetter
    {
        get => (string?)GetValue(SelectedLetterProperty);
        set => SetValue(SelectedLetterProperty, value);
    }

    public ICommand? LetterCommand
    {
        get => (ICommand?)GetValue(LetterCommandProperty);
        set => SetValue(LetterCommandProperty, value);
    }

    private void AddEntry(string? letter, string text, string name)
    {
        var entry = new Button
        {
            Content = text,
            Tag = letter,
            Style = (Style)Resources["AlphabetLetterButtonStyle"],
        };
        AutomationProperties.SetName(entry, name);
        entry.Click += OnEntryClick;
        Letters.Children.Add(entry);
        _entries.Add(entry);
    }

    private void OnEntryClick(object sender, RoutedEventArgs e)
    {
        string? letter = (sender as FrameworkElement)?.Tag as string;
        if (LetterCommand is { } command && command.CanExecute(letter))
        {
            command.Execute(letter);
        }
    }

    // The picked entry is red on a raised pill: not told by colour alone.
    private void ShowSelection()
    {
        var resources = Application.Current.Resources;
        var selectedText = (Brush)resources["SiloPrimaryBrush"];
        var selectedBackground = (Brush)resources["SiloSurfaceRaisedBrush"];
        var normalText = (Brush)resources["SiloMutedBrush"];
        var normalBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var entry in _entries)
        {
            bool isSelected = string.Equals(entry.Tag as string, SelectedLetter, StringComparison.OrdinalIgnoreCase);
            entry.Foreground = isSelected ? selectedText : normalText;
            entry.Background = isSelected ? selectedBackground : normalBackground;
            AutomationProperties.SetItemStatus(entry, isSelected ? "Selected" : "");
        }
    }
}
