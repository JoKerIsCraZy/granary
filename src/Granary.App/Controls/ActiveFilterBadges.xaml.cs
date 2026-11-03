using Granary.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Granary.App.Controls;

/// <summary>The badges of a <see cref="LibraryFiltersViewModel"/>'s applied filters, each removed with its ✕.</summary>
public sealed partial class ActiveFilterBadges : UserControl
{
    public static readonly DependencyProperty FiltersProperty =
        DependencyProperty.Register(nameof(Filters), typeof(LibraryFiltersViewModel), typeof(ActiveFilterBadges),
            new PropertyMetadata(null, (d, _) => ((ActiveFilterBadges)d).Bindings.Update()));

    public static readonly DependencyProperty FiltersButtonProperty =
        DependencyProperty.Register(nameof(FiltersButton), typeof(CatalogFiltersButton), typeof(ActiveFilterBadges), new PropertyMetadata(null));

    // The second click of a double click, or a held key's repeat, would land on the ✕ that took the removed one's place and
    // take that filter too. Within the system's double-click time after a removal by a click, another click is ignored.
    private readonly long _doubleClickMilliseconds = new UISettings().DoubleClickTime;
    private long? _clickRemovedAt;

    public ActiveFilterBadges()
    {
        InitializeComponent();
    }

    public LibraryFiltersViewModel? Filters
    {
        get => (LibraryFiltersViewModel?)GetValue(FiltersProperty);
        set => SetValue(FiltersProperty, value);
    }

    /// <summary>Where focus goes once the last badge is gone.</summary>
    public CatalogFiltersButton? FiltersButton
    {
        get => (CatalogFiltersButton?)GetValue(FiltersButtonProperty);
        set => SetValue(FiltersButtonProperty, value);
    }

    // The command takes the filter out and restarts the grid; the badges change at once. Focus then goes to the ✕ that
    // took the removed one's place, else the one before, else the Filters button, so it is never left nowhere.
    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryFilterBadge badge } button || Filters is not { } filters)
        {
            return;
        }
        bool byKey = button.FocusState == FocusState.Keyboard;
        long now = Environment.TickCount64;
        if (!byKey && _clickRemovedAt is { } at && now - at < _doubleClickMilliseconds)
        {
            return;
        }
        _clickRemovedAt = byKey ? null : now;
        int index = IndexOf(filters.Badges, badge);
        var state = byKey ? FocusState.Keyboard : FocusState.Programmatic;
        filters.RemoveBadgeCommand.Execute(badge);
        DispatcherQueue.TryEnqueue(() => FocusAfterRemoval(filters, index, state));
    }

    // A held Enter repeats its key-down on the ✕ focus moved to; only a fresh press removes.
    private void OnRemovePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown && e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA)
        {
            e.Handled = true;
        }
    }

    private void FocusAfterRemoval(LibraryFiltersViewModel filters, int removedAt, FocusState state)
    {
        int count = filters.Badges.Count;
        if (count == 0)
        {
            FiltersButton?.FocusPill(state);
            return;
        }
        Repeater.UpdateLayout();
        int target = Math.Clamp(removedAt, 0, count - 1);
        if (Repeater.TryGetElement(target) is Border { Child: Panel panel } && panel.Children.OfType<Button>().FirstOrDefault() is { } remove)
        {
            remove.Focus(state);
        }
        else
        {
            FiltersButton?.FocusPill(state);
        }
    }

    private static int IndexOf(IReadOnlyList<LibraryFilterBadge> badges, LibraryFilterBadge badge)
    {
        for (int i = 0; i < badges.Count; i++)
        {
            if (badges[i] == badge)
            {
                return i;
            }
        }
        return 0;
    }
}
