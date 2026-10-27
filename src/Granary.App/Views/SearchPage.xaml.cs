using System.ComponentModel;
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
public sealed partial class SearchPage : Page
{
    // Request to add's grid while another page loads, as the web's opacity-60.
    private const double ChangingPageOpacity = 0.6;

    // The space left above Request to add's heading when a page change brings it into view (the web's scroll-mt-4).
    private const double SectionScrollMargin = 16;

    private bool _focusBoxWhenLoaded;

    public SearchPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();
        ScopeBar.SelectedItem = ScopeBar.Items[(int)ViewModel.Scope];
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.Requests.PropertyChanged += OnRequestsPropertyChanged;
    }

    public SearchViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
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
        base.OnNavigatedFrom(e);
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public double Dimmed(bool isDimmed) => isDimmed ? ChangingPageOpacity : 1;

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

    private void OnPersonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchPerson person } && ViewModel.OpenPersonCommand.CanExecute(person))
        {
            ViewModel.OpenPersonCommand.Execute(person);
        }
    }

    // A person's titles start at the top, and keyboard users land on the way back.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchViewModel.Person))
        {
            return;
        }
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        if (ViewModel.IsPersonOpen)
        {
            DispatcherQueue.TryEnqueue(() => BackToResultsButton.Focus(FocusState.Programmatic));
        }
    }

    // The pager sits under the grid: another page brings Request to add's first row into view when its top is scrolled away.
    private void OnRequestsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A page that arrives while the cached page is off screen has no tree to measure.
        if (e.PropertyName != nameof(RequestToAddViewModel.Page) || !ViewModel.Requests.IsShown || !IsLoaded || XamlRoot is null
            || Scroller.Content is not UIElement content)
        {
            return;
        }
        double top = RequestSection.TransformToVisual(content).TransformPoint(default).Y;
        if (top < Scroller.VerticalOffset)
        {
            Scroller.ChangeView(null, Math.Max(0, top - SectionScrollMargin), null);
        }
    }
}
