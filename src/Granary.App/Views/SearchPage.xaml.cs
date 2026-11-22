using System.ComponentModel;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Granary.App.Views;

/// <summary>Search; the parameter is an optional query text that is searched at once.</summary>
public sealed partial class SearchPage : Page, IPadSections
{
    private bool _focusBoxWhenLoaded;
    private SearchView _shownView;
    private bool _isEmptySearch = true;
    // A box had the keyboard when the layouts switched; a box that went out of sight keeps this.
    private bool _typingInBox;

    public SearchPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();
        // The page and its view model live as long as each other (a cached page, a transient view model).
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        PlacePeople();
    }

    public SearchViewModel ViewModel { get; }

    /// <summary>The skeleton grid's cards while the first titles are on their way, as the web's twelve.</summary>
    public IReadOnlyList<Granary.Core.ViewModels.Cards.MediaCard> Skeletons { get; } =
        [.. Enumerable.Repeat(Granary.Core.ViewModels.Cards.MediaCard.Placeholder, 12)];

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.NavigationMode != NavigationMode.Back)
        {
            // Back shows the backdrop as it was left; another visit starts without one, so the last search's does not flash.
            ViewModel.ForgetBackdrop();
        }
        Backdrop.Resume();
        ViewModel.DisplayScale = XamlRoot?.RasterizationScale ?? 1;
        // The page is cached: Back keeps the results where they were; Ctrl+K and the sidebar lead to the box.
        if (e.NavigationMode != NavigationMode.Back)
        {
            if (IsLoaded)
            {
                FocusSearchBox();
            }
            else
            {
                _focusBoxWhenLoaded = true;
            }
        }
        try
        {
            await ViewModel.ActivateAsync(e.Parameter as string);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the view model.
            App.Services.GetRequiredService<ILogger<SearchPage>>().LogError(ex, "Search could not be shown");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        // The page stays cached: its backdrop lets go of its pictures until it shows again.
        Backdrop.Suspend();
        base.OnNavigatedFrom(e);
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public bool Negate(bool value) => !value;

    public Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    // The last search's titles while the next one loads, as the web dims them.
    public double Dim(bool stale) => stale ? 0.6 : 1;

    public string SortName(string label) => AppText.Get("granary.library.sortName", ("sort", label));

    public string OrderName(string label) => AppText.Get("granary.library.orderName", ("order", label));

    // Relevance first, then the sorts of the scope; the list follows the scope and the server's rating sources.
    private void OnSortMenuOpening(object sender, object e)
    {
        SortMenu.Items.Clear();
        AddSort(null, AppText.Get("web.catalog.sort.relevance"));
        foreach (var option in ViewModel.SortOptions)
        {
            AddSort(option, option.Label);
        }
    }

    private void AddSort(LibrarySortOption? option, string label) =>
        SortMenu.Items.Add(new RadioMenuFlyoutItem
        {
            Text = label,
            GroupName = "SearchSort",
            Command = ViewModel.SetSortCommand,
            CommandParameter = option,
            IsChecked = option == ViewModel.Sort,
        });

    // A view's pill; pressing the one shown again clears a request view that fell back to the overview, as the web's.
    private void OnViewTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<SearchView>(tag, out var view))
        {
            ViewModel.ShowViewCommand.Execute(view);
        }
    }

    // People follow the titles to request under Relevance, and the more rows under another sort, as the web's.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.IsSortedOtherwise))
        {
            PlacePeople();
        }
        else if (e.PropertyName == nameof(SearchViewModel.IsEmptySearch) && _isEmptySearch != ViewModel.IsEmptySearch)
        {
            // The empty page's box and the results' field are one box to the user: the focus and the caret move along.
            _isEmptySearch = ViewModel.IsEmptySearch;
            var to = _isEmptySearch ? EmptySearchBox : SearchBox;
            var from = _isEmptySearch ? SearchBox : EmptySearchBox;
            if (_typingInBox)
            {
                // Where the caret was, and what was selected: a move made before the box switched is kept.
                int start = from.SelectionStart;
                int length = from.SelectionLength;
                DispatcherQueue.TryEnqueue(() =>
                {
                    to.Focus(FocusState.Programmatic);
                    int at = Math.Min(start, to.Text.Length);
                    to.Select(at, Math.Min(length, to.Text.Length - at));
                });
            }
        }
        else if (e.PropertyName == nameof(SearchViewModel.ShownView) && _shownView != ViewModel.ShownView)
        {
            // Another view starts at its top, as the web scrolls to it.
            _shownView = ViewModel.ShownView;
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    private void PlacePeople()
    {
        UIElement after = ViewModel.IsSortedOtherwise ? MoreSection : RequestRowBand;
        if (Body.Children.IndexOf(PeopleSection) == Body.Children.IndexOf(after) + 1)
        {
            return;
        }
        Body.Children.Remove(PeopleSection);
        Body.Children.Insert(Body.Children.IndexOf(after) + 1, PeopleSection);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_focusBoxWhenLoaded)
        {
            _focusBoxWhenLoaded = false;
            FocusSearchBox();
        }
    }

    private void FocusSearchBox()
    {
        var box = ViewModel.IsEmptySearch ? EmptySearchBox : SearchBox;
        box.Focus(FocusState.Programmatic);
        box.SelectAll();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearCommand.Execute(null);
        FocusSearchBox();
    }

    // The hero's hairline takes the accent while the field has focus (focus-within:border-ring).
    private void OnHeroFocusChanged(object sender, RoutedEventArgs e)
    {
        HeroLine.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[SearchBox.FocusState == FocusState.Unfocused ? "SiloBorderBrush" : "SiloPrimaryBrush"];
        OnBoxFocusChanged(sender, e);
    }

    // Losing the focus because its layout went out of sight is not leaving the box.
    private void OnBoxFocusChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            bool shown = ReferenceEquals(box, SearchBox) ? !ViewModel.IsEmptySearch : ViewModel.IsEmptySearch;
            _typingInBox = box.FocusState != FocusState.Unfocused || (_typingInBox && !shown);
        }
    }

    // The hero's text follows the window as the web's breakpoints do: 30 px under 640, 36 under 1024, else 48.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = XamlRoot?.Size.Width ?? e.NewSize.Width;
        SearchBox.FontSize = width < 640 ? 30 : width < 1024 ? 36 : 48;
        SearchBox.Padding = width < 640 ? new Thickness(40, 12, 48, 12) : new Thickness(48, 12, 48, 12);
        HeroIcon.FontSize = width < 640 ? 24 : 28;
    }

    private void OnFocusSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FocusSearchBox();
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                ViewModel.SearchNowCommand.Execute(null);
                break;
            case VirtualKey.Escape when ViewModel.Query.Length > 0:
                e.Handled = true;
                ViewModel.ClearCommand.Execute(null);
                break;
        }
    }

    private void OnScopeMenuOpening(object sender, object e)
    {
        foreach (var item in ScopeMenu.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = item.Tag is string tag && Enum.TryParse<SearchScope>(tag, out var scope) && scope == ViewModel.Scope;
        }
    }

    private void OnScopeClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag } && Enum.TryParse<SearchScope>(tag, out var scope))
        {
            ViewModel.Scope = scope;
        }
    }

    // A game controller's bumpers: Movies & Series, Movies, Series.
    void IPadSections.StepSection(int by)
    {
        int index = (int)ViewModel.Scope + by;
        if (Enum.IsDefined((SearchScope)index))
        {
            ViewModel.Scope = (SearchScope)index;
            ScopeButton.Focus(FocusState.Keyboard);
        }
    }

    private void OnPersonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchPerson person } && ViewModel.OpenPersonCommand.CanExecute(person))
        {
            ViewModel.OpenPersonCommand.Execute(person);
        }
    }

}
