using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Granary.App.Controls;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels.Collections;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;

namespace Granary.App.Views;

/// <summary>A profile's own collection made or changed.</summary>
public sealed partial class CollectionEditorPage : Page, ILeaveGuard
{
    private const int PosterWidth = 32;
    private const double ColumnWidth = 768;

    private bool _released;
    private bool _asking;

    public CollectionEditorPage()
    {
        ViewModel = App.Services.GetRequiredService<CollectionEditorViewModel>();
        BackToCollectionsCommand = new RelayCommand(() => App.Services.GetRequiredService<INavigationService>().ShowSection(AppRoute.Collections));
        InitializeComponent();
    }

    public CollectionEditorViewModel ViewModel { get; }

    /// <summary>"‹ Collections" and Cancel: the Collections section, as the web's link to the list.</summary>
    public ICommand BackToCollectionsCommand { get; }

    public IReadOnlyList<SettingChoice> WatchChoices => CollectionEditorViewModel.WatchChoices;

    public IReadOnlyList<SettingChoice> MediaChoices => CollectionEditorViewModel.MediaChoices;

    bool ILeaveGuard.AsksBeforeLeaving => !_released && ViewModel.HasUnsavedChanges;

    // Static because x:Bind function bindings in the row templates call them on the type.
    public static BitmapImage? Poster(Uri? url) => Artwork.Load(url, PosterWidth);

    public string OrderHint(bool canMove) => canMove
        ? "Your order. Drag titles, or focus one and press Alt+Up or Alt+Down, to change it."
        : "Your order.";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.ActivateAsync((e.Parameter as CollectionEditorRouteParameter)?.CollectionId);
            if (ViewModel.IsNew)
            {
                NameBox.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            Log(ex, "The collection editor could not be shown");
        }
    }

    async Task<bool> ILeaveGuard.ConfirmLeaveAsync()
    {
        if (_asking)
        {
            return false;
        }
        _asking = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Discard unsaved changes?",
                Content = new TextBlock { Text = "This page has edits that were never saved. Leaving now throws them away.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Discard",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            _released = await dialog.ShowAsync() == ContentDialogResult.Primary;
            return _released;
        }
        catch (Exception ex)
        {
            // Another dialog is open: stay.
            Log(ex, "Could not ask before leaving the collection editor");
            return false;
        }
        finally
        {
            _asking = false;
        }
    }

    // A column capped by MaxWidth in a ScrollViewer ends up off centre, so its width is set here.
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => Column.Width = Math.Min(ColumnWidth, e.NewSize.Width);

    // The web's delete question; the collection goes for every profile it is shared with.
    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Delete “{ViewModel.HeaderTitle}”?",
                Content = new TextBlock { Text = CollectionEditorText.DeleteDescription(ViewModel.IsSavedShared), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _released = true;
                await ViewModel.DeleteAsync();
                _released = !ViewModel.HasDeleteError;
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The collection could not be deleted");
        }
    }

    // A drag moved the shown rows: the order saves from them.
    private async void OnTitlesDragged(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            if (args.DropResult == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            {
                await ViewModel.ApplyShownOrderAsync([.. ViewModel.ShownTitles.Select(t => t.Id)]);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "A collection's new order could not be saved");
        }
    }

    // Alt+Up and Alt+Down move the focused title, as the web's handle with Space and the arrows.
    private async void OnTitlesKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down)
            || !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down)
            || (e.OriginalSource as FrameworkElement)?.DataContext is not CollectionTitleRow row)
        {
            return;
        }
        e.Handled = true;
        try
        {
            await (e.Key == VirtualKey.Up ? ViewModel.MoveTitleUpCommand : ViewModel.MoveTitleDownCommand).ExecuteAsync(row);
            TitlesList.UpdateLayout();
            (TitlesList.ContainerFromItem(row) as Control)?.Focus(FocusState.Keyboard);
        }
        catch (Exception ex)
        {
            Log(ex, "A title could not be moved");
        }
    }

    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<CollectionEditorPage>>().LogError(ex, "{Message}", message);
}
