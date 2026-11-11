using System.ComponentModel;
using Granary.Core.ViewModels.WatchParty;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Granary.App.Controls;

/// <summary>The start sheet's content; <see cref="DetailHero"/> shows it in a dialog for <see cref="ViewModel"/>.</summary>
public sealed partial class StartPartyPanel : UserControl
{
    public StartPartyPanel(StartPartyViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    public StartPartyViewModel ViewModel { get; }

    /// <summary>
    /// The dialog closed: the panel stops following the view model, which lives with the title's page. Not on Unloaded,
    /// which a dialog's content can get after its Loaded.
    /// </summary>
    public void Detach() => ViewModel.PropertyChanged -= OnViewModelChanged;

    // Static because x:Bind function bindings in the templates call them on the type.
    public static ImageSource? Blur(string hash) => Thumbhashes.Get(hash);

    /// <summary>The Change button says what it changes.</summary>
    public static string ChangeName(string label) => label == "Choose" ? "Choose an episode" : "Change the episode";

    /// <summary>The web's season pills: the chosen one white on dark text, the others raised with muted text.</summary>
    public static Brush PillBackground(bool selected) => Resource(selected ? "SiloForegroundBrush" : "SiloSurfaceRaisedBrush");

    public static Brush PillForeground(bool selected) => Resource(selected ? "SiloBackgroundBrush" : "SiloMutedBrush");

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private void OnSeasonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StartPartySeason season })
        {
            ViewModel.PickSeasonCommand.Execute(season);
        }
    }

    private void OnEpisodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StartPartyEpisode episode })
        {
            ViewModel.PickEpisodeCommand.Execute(episode);
        }
    }

    // What MainWindow.Remote.cs does for a page's radio buttons, whose keys reach the window; a dialog's do not. A radio
    // button takes every arrow itself, so a remote could not leave the two cards for the switch and Create: an arrow
    // towards the other card still moves to it, any other goes where it points. OK checks the card, as Space does.
    private void OnModeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not RadioButton radio || e.Handled)
        {
            return;
        }
        if (e.Key == VirtualKey.Enter)
        {
            if (!e.KeyStatus.WasKeyDown)
            {
                radio.IsChecked = true;
            }
            e.Handled = true;
            return;
        }
        FocusNavigationDirection? direction = e.Key switch
        {
            VirtualKey.Up => FocusNavigationDirection.Up,
            VirtualKey.Down => FocusNavigationDirection.Down,
            VirtualKey.Left => FocusNavigationDirection.Left,
            VirtualKey.Right => FocusNavigationDirection.Right,
            _ => null,
        };
        if (direction is not { } way)
        {
            return;
        }
        var next = FocusManager.FindNextElement(way, new FindNextElementOptions { SearchRoot = Sheet });
        if (next is RadioButton other && other.GroupName == radio.GroupName)
        {
            return;
        }
        if (next is Control target)
        {
            target.Focus(FocusState.Keyboard);
        }
        e.Handled = true;
    }

    // Focus follows the chooser, so a keyboard or a remote never loses its place: into it as it opens (the chosen season
    // once the seasons are in), and back to Change as it closes, where the button that opened it was.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartPartyViewModel.IsChoosing))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ViewModel.IsChoosing)
                {
                    FocusChosenSeason();
                }
                else
                {
                    ChangeButton.Focus(FocusState.Programmatic);
                }
            });
        }
        else if (e.PropertyName == nameof(StartPartyViewModel.IsLoadingEpisodes) && !ViewModel.IsLoadingEpisodes && ViewModel.IsChoosing
            && FocusManager.GetFocusedElement(XamlRoot) is not Button { DataContext: StartPartySeason })
        {
            DispatcherQueue.TryEnqueue(FocusChosenSeason);
        }
    }

    private void FocusChosenSeason()
    {
        int index = ViewModel.Seasons.ToList().FindIndex(s => s.IsSelected);
        SeasonPills.UpdateLayout();
        if (index >= 0 && SeasonPills.TryGetElement(index) is Control pill)
        {
            pill.Focus(FocusState.Programmatic);
        }
        else
        {
            KeepButton.Focus(FocusState.Programmatic);
        }
    }
}
