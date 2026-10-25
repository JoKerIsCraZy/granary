using System.Globalization;
using System.Windows.Input;
using Granary.Core.Api.Catalog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Granary.App.Controls;

/// <summary>
/// "#" and A–Z for a title-ordered grid, from its letter index (<see cref="Index"/>): letters without titles are
/// greyed, and all of them until the index arrives. <see cref="LetterCommand"/> gets the letter picked ("#", "A" …
/// "Z"); <see cref="ActiveLetter"/> marks the letter the grid's top row is under.
/// </summary>
public sealed partial class AlphabetJumpBar : UserControl
{
    public static readonly DependencyProperty IndexProperty =
        DependencyProperty.Register(nameof(Index), typeof(CatalogLetters), typeof(AlphabetJumpBar),
            new PropertyMetadata(null, (sender, _) => ((AlphabetJumpBar)sender).ShowIndex()));

    public static readonly DependencyProperty ActiveLetterProperty =
        DependencyProperty.Register(nameof(ActiveLetter), typeof(string), typeof(AlphabetJumpBar),
            new PropertyMetadata(null, (sender, _) => ((AlphabetJumpBar)sender).ShowActive()));

    public static readonly DependencyProperty LetterCommandProperty =
        DependencyProperty.Register(nameof(LetterCommand), typeof(ICommand), typeof(AlphabetJumpBar), new PropertyMetadata(null));

    private const string Symbols = "#";

    private readonly Dictionary<string, Button> _entries = [];
    private readonly Style _letterStyle;
    private readonly Style _currentStyle;

    public AlphabetJumpBar()
    {
        InitializeComponent();
        _letterStyle = (Style)Resources["AlphabetLetterButtonStyle"];
        _currentStyle = (Style)Resources["AlphabetCurrentLetterButtonStyle"];
        AddEntry(Symbols);
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            AddEntry(letter.ToString());
        }
        ShowIndex();
    }

    /// <summary>The letter index of the grid's listing; null while it loads.</summary>
    public CatalogLetters? Index
    {
        get => (CatalogLetters?)GetValue(IndexProperty);
        set => SetValue(IndexProperty, value);
    }

    /// <summary>The letter the grid's top row is under, or null.</summary>
    public string? ActiveLetter
    {
        get => (string?)GetValue(ActiveLetterProperty);
        set => SetValue(ActiveLetterProperty, value);
    }

    public ICommand? LetterCommand
    {
        get => (ICommand?)GetValue(LetterCommandProperty);
        set => SetValue(LetterCommandProperty, value);
    }

    private static string Spoken(string letter) => letter == Symbols ? "Numbers and symbols" : letter;

    private void AddEntry(string letter)
    {
        var entry = new Button { Content = letter, Tag = letter, Style = _letterStyle };
        entry.Click += OnEntryClick;
        Letters.Children.Add(entry);
        _entries[letter] = entry;
    }

    private void OnEntryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string letter && LetterCommand is { } command && command.CanExecute(letter))
        {
            command.Execute(letter);
            Announcer.Announce(this, $"Showing titles under {Spoken(letter)}", "LetterJump");
        }
    }

    // Typing a letter goes to it; a digit or symbol goes to "#".
    private void OnLettersKeyDown(object sender, KeyRoutedEventArgs e)
    {
        string? letter = e.Key switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => ((char)('A' + (e.Key - VirtualKey.A))).ToString(),
            (>= VirtualKey.Number0 and <= VirtualKey.Number9) or (>= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9) => Symbols,
            _ => null,
        };
        if (letter is not null && _entries.TryGetValue(letter, out var entry) && entry.IsEnabled)
        {
            entry.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    private void ShowIndex()
    {
        var counts = Index?.Letters.ToDictionary(l => l.Letter, l => l.Count) ?? [];
        foreach (var (letter, entry) in _entries)
        {
            bool has = counts.TryGetValue(letter, out int count);
            entry.IsEnabled = has;
            string name = Index is null ? Spoken(letter)
                : has ? $"{Spoken(letter)}, {(count == 1 ? "1 title" : count.ToString("N0", CultureInfo.InvariantCulture) + " titles")}"
                : $"{Spoken(letter)}, no titles";
            AutomationProperties.SetName(entry, name);
        }
        ShowActive();
    }

    // The current letter is red with dark text: told by more than its colour, it is also announced as current.
    private void ShowActive()
    {
        foreach (var (letter, entry) in _entries)
        {
            bool isActive = letter == ActiveLetter && entry.IsEnabled;
            var style = isActive ? _currentStyle : _letterStyle;
            if (!ReferenceEquals(entry.Style, style))
            {
                entry.Style = style;
            }
            AutomationProperties.SetItemStatus(entry, isActive ? "Current" : "");
        }
    }
}
