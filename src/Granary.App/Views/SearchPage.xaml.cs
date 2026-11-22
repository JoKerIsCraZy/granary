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

    public SearchPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();
        ScopeBar.SelectedItem = ScopeBar.Items[(int)ViewModel.Scope];
        // The page and its view model live as long as each other (a cached page, a transient view model).
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        PlacePeople();
    }

    public SearchViewModel ViewModel { get; }

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
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
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
            case VirtualKey.Escape when SearchBox.Text.Length > 0:
                e.Handled = true;
                ViewModel.ClearCommand.Execute(null);
                break;
        }
    }

    private void OnScopeSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int index = sender.Items.IndexOf(sender.SelectedItem);
        if (index >= 0)
        {
            ViewModel.Scope = (SearchScope)index;
        }
    }

    // A game controller's bumpers: Movies & Series, Movies, Series.
    void IPadSections.StepSection(int by)
    {
        int index = ScopeBar.Items.IndexOf(ScopeBar.SelectedItem) + by;
        if (index >= 0 && index < ScopeBar.Items.Count)
        {
            ScopeBar.SelectedItem = ScopeBar.Items[index];
            ScopeBar.Items[index].Focus(FocusState.Keyboard);
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
