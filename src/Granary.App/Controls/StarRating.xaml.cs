using System.Windows.Input;
using Granary.Core.Api.Catalog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Granary.App.Controls;

/// <summary>
/// The web's StarRating: <see cref="Value"/> of five stars filled, 0 for none. A click on a star, or OK on it, runs
/// <see cref="RateCommand"/> with the star's number, which sets that rating or, on the current star, clears it. To screen
/// readers it is the group "Rating" of radio buttons "1 star" to "5 stars", the rating's one selected, as on the web.
/// </summary>
/// <remarks>
/// The arrows move between the stars without rating, unlike the web's, where each arrow sets the rating: a remote crosses
/// the action bar with them, and passing the stars must not rate the title.
/// </remarks>
public sealed partial class StarRating : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(int), typeof(StarRating),
            new PropertyMetadata(0, (sender, _) => ((StarRating)sender).EndPreview()));

    public static readonly DependencyProperty RateCommandProperty =
        DependencyProperty.Register(nameof(RateCommand), typeof(ICommand), typeof(StarRating), new PropertyMetadata(null));

    // Segoe Fluent Icons: FavoriteStar, FavoriteStarFill.
    private const string EmptyGlyph = "";
    private const string FilledGlyph = "";

    private readonly List<RatingStar> _stars = [];
    private readonly Brush _filledBrush;
    private readonly Brush _emptyBrush;

    // The star under the pointer and the star the keyboard moved to, 0 for none: each previews its value.
    private int _hovered;
    private int _focused;

    public StarRating()
    {
        InitializeComponent();
        _filledBrush = (Brush)Resources["StarFilledBrush"];
        _emptyBrush = (Brush)Resources["StarEmptyBrush"];
        var style = (Style)Resources["StarButtonStyle"];
        for (int number = RatingSet.Min; number <= RatingSet.Max; number++)
        {
            AddStar(number, style);
        }
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => SizeStars());
        // Hidden, the stars see no pointer leave: a preview must not outlast them.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Collapsed)
            {
                EndPreview();
            }
        });
        SizeStars();
        ShowStars();
    }

    /// <summary>The rating's stars, 1 to 5; 0 when there is none.</summary>
    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Runs with the number of the star clicked.</summary>
    public ICommand? RateCommand
    {
        get => (ICommand?)GetValue(RateCommandProperty);
        set => SetValue(RateCommandProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StarRatingAutomationPeer(this);

    private void AddStar(int number, Style style)
    {
        var star = new RatingStar(number) { Style = style, Content = new FontIcon { Glyph = EmptyGlyph } };
        AutomationProperties.SetName(star, number == 1 ? "1 star" : $"{number} stars");
        // "3 of 5".
        AutomationProperties.SetPositionInSet(star, number);
        AutomationProperties.SetSizeOfSet(star, RatingSet.Max);
        star.Click += OnStarClick;
        // Also when the button marked them handled itself.
        star.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => Hover(number)), handledEventsToo: true);
        star.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => Unhover(number)), handledEventsToo: true);
        star.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => Unhover(number)), handledEventsToo: true);
        star.GotFocus += (_, _) => Focused(star);
        star.LostFocus += (_, _) => Unfocused(number);
        Stars.Children.Add(star);
        _stars.Add(star);
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RatingStar star)
        {
            return;
        }
        // The rating the click made shows, not a preview: until the pointer or the keyboard moves to another star.
        if (RateCommand is { } command && command.CanExecute(star.Number))
        {
            command.Execute(star.Number);
        }
        EndPreview();
    }

    // A new rating, or stars hidden: no preview survives it.
    private void EndPreview()
    {
        _hovered = 0;
        _focused = 0;
        ShowStars();
    }

    private void Hover(int number)
    {
        _hovered = number;
        ShowStars();
    }

    private void Unhover(int number)
    {
        if (_hovered == number)
        {
            _hovered = 0;
            ShowStars();
        }
    }

    // Only a star the keyboard or a remote moved to previews; one focused by a click or in code shows the rating.
    private void Focused(RatingStar star)
    {
        _focused = star.FocusState == FocusState.Keyboard ? star.Number : 0;
        ShowStars();
    }

    private void Unfocused(int number)
    {
        if (_focused == number)
        {
            _focused = 0;
            ShowStars();
        }
    }

    // Tab into the stars lands on the rating's star, the first when there is none, as the web's roving tab stop does.
    private void OnStarsGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (args.Direction is not (FocusNavigationDirection.Next or FocusNavigationDirection.Previous)
            || args.NewFocusedElement is not RatingStar || (args.OldFocusedElement is RatingStar old && _stars.Contains(old)))
        {
            return;
        }
        var target = _stars[Math.Clamp(Value, RatingSet.Min, RatingSet.Max) - 1];
        if (!ReferenceEquals(target, args.NewFocusedElement))
        {
            args.TrySetNewFocusedElement(target);
        }
    }

    // A held OK (Enter, Space, a gamepad's A) rates once: its repeats would set and clear the rating in turn.
    private void OnStarsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is (VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA) && e.KeyStatus.WasKeyDown)
        {
            e.Handled = true;
        }
    }

    private void SizeStars()
    {
        foreach (var star in _stars)
        {
            if (star.Content is FontIcon icon)
            {
                icon.FontSize = FontSize;
            }
        }
    }

    private void ShowStars()
    {
        int rating = Math.Clamp(Value, 0, RatingSet.Max);
        int shown = _hovered > 0 ? _hovered : _focused > 0 ? _focused : rating;
        foreach (var star in _stars)
        {
            bool filled = star.Number <= shown;
            if (star.Content is FontIcon icon)
            {
                icon.Glyph = filled ? FilledGlyph : EmptyGlyph;
                icon.Foreground = filled ? _filledBrush : _emptyBrush;
            }
            star.SetSelected(star.Number == rating);
        }
    }

    private sealed partial class StarRatingAutomationPeer(StarRating owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(StarRating);
    }
}
