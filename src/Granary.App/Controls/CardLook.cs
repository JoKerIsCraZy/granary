using System.ComponentModel;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Granary.App.Controls;

/// <summary>
/// The profile's poster size and caption (<see cref="CardPresentationSettings"/>) at the window's breakpoint, for a row or grid
/// of cards. The owner takes them with <see cref="Update"/> as it measures, so its cards are laid out in them from the first
/// pass; a new choice, or the window crossing a breakpoint, measures it again. A resize within a breakpoint costs nothing.
/// </summary>
internal sealed class CardLook
{
    private static CardPresentationSettings Settings => field ??= App.Services.GetRequiredService<CardPresentationSettings>();

    private readonly FrameworkElement _owner;
    private XamlRoot? _root;
    private bool _following;
    private bool _taken;

    public CardLook(FrameworkElement owner)
    {
        _owner = owner;
        owner.Loaded += OnLoaded;
        owner.Unloaded += OnUnloaded;
    }

    public CardPresentation Presentation { get; private set; } = CardPresentation.Default;

    /// <summary>The web's breakpoint of the window's width: the web's columns and widths follow the viewport, not the page.</summary>
    public WindowBreakpoint Breakpoint { get; private set; }

    /// <summary>Takes the choice and the window's width as they are now: true the first time, and when either changes the cards.</summary>
    public bool Update()
    {
        var presentation = Settings.Shown;
        var breakpoint = _owner.XamlRoot is { } root ? CardPresentation.BreakpointOf(root.Size.Width) : Breakpoint;
        if (_taken && presentation == Presentation && breakpoint == Breakpoint)
        {
            return false;
        }
        _taken = true;
        Presentation = presentation;
        Breakpoint = breakpoint;
        return true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_following)
        {
            return;
        }
        _following = true;
        Settings.PropertyChanged += OnSettingsChanged;
        _root = _owner.XamlRoot;
        if (_root is not null)
        {
            _root.Changed += OnRootChanged;
        }
        // Changed while away (a page kept in the back stack).
        RemeasureIfChanged();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_following)
        {
            return;
        }
        _following = false;
        Settings.PropertyChanged -= OnSettingsChanged;
        if (_root is not null)
        {
            _root.Changed -= OnRootChanged;
            _root = null;
        }
    }

    // A row the window's resize does not widen (a page at its widest) is measured again only when a breakpoint is crossed.
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RemeasureIfChanged();

    // From any thread the setting changes on, here on the owner's.
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CardPresentationSettings.Shown))
        {
            return;
        }
        if (_owner.DispatcherQueue.HasThreadAccess)
        {
            RemeasureIfChanged();
        }
        else
        {
            _owner.DispatcherQueue.TryEnqueue(RemeasureIfChanged);
        }
    }

    private void RemeasureIfChanged()
    {
        var breakpoint = _owner.XamlRoot is { } root ? CardPresentation.BreakpointOf(root.Size.Width) : Breakpoint;
        if (!_taken || Settings.Shown != Presentation || breakpoint != Breakpoint)
        {
            _owner.InvalidateMeasure();
        }
    }
}
