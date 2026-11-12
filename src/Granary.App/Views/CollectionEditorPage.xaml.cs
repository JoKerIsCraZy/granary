using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Granary.App.Controls;
using Granary.Core.Abstractions;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;

namespace Granary.App.Views;

/// <summary>A profile's own collection made or changed.</summary>
public sealed partial class CollectionEditorPage : Page, ILeaveGuard
{
    private const int PosterWidth = 32;
    private const int PreviewPosterWidth = 96;
    private const double ColumnWidth = 768;

    private bool _released;
    private bool _asking;

    public CollectionEditorPage()
    {
        ViewModel = App.Services.GetRequiredService<CollectionEditorViewModel>();
        BackToCollectionsCommand = new RelayCommand(() => App.Services.GetRequiredService<INavigationService>().ShowSection(AppRoute.Collections));
        InitializeComponent();
        FollowLook();
        ShowDescriptionLabel();
    }

    public CollectionEditorViewModel ViewModel { get; }

    /// <summary>"‹ Collections" and Cancel: the Collections section, as the web's link to the list.</summary>
    public ICommand BackToCollectionsCommand { get; }

    public static BitmapImage? PreviewPoster(Uri? url) => Artwork.Load(url, PreviewPosterWidth);

    public double Dimmed(bool refreshing) => refreshing ? 0.6 : 1;

    bool ILeaveGuard.AsksBeforeLeaving => !_released && ViewModel.HasUnsavedChanges;

    // Static because x:Bind function bindings in the row templates call them on the type.
    public static BitmapImage? Poster(Uri? url) => Artwork.Load(url, PosterWidth);

    public string OrderHint(bool canMove) => canMove
        ? AppText.Get("granary.collections.editor.orderHintMovable", ("up", "Alt+Up"), ("down", "Alt+Down"))
        : AppText.Get("granary.collections.editor.orderHint");

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            var route = e.Parameter as CollectionEditorRouteParameter;
            await ViewModel.ActivateAsync(route?.CollectionId, route?.Kind);
            await ShowPosterPreviewAsync();
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

    // The collage wait, a poster read and the smart preview end with the page.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Deactivate();
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
                Title = AppText.Get("web.shell.unsavedChanges.title"),
                Content = new TextBlock { Text = AppText.Get("web.shell.unsavedChanges.description"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = AppText.Get("web.shell.unsavedChanges.discard"),
                CloseButtonText = AppText.Get("common.cancel"),
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

    void ILeaveGuard.Rearm() => _released = false;

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
                Title = AppText.Get("web.collections.pageActions.deleteTitle", ("name", ViewModel.HeaderTitle)),
                Content = new TextBlock { Text = CollectionEditorText.DeleteDescription(ViewModel.IsSavedShared), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = AppText.Get("web.collections.deleteDialog.delete"),
                CloseButtonText = AppText.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            // A deleted collection has nothing left to lose: the editor lets go of it by itself.
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteAsync();
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

    // The libraries the rules take: all of them, or the ones ticked.
    private void OnLibrariesMenuOpening(object? sender, object e)
    {
        LibrariesMenu.Items.Clear();
        var all = new ToggleMenuFlyoutItem { Text = AppText.Get("web.collections.copy.allMyLibraries"), IsChecked = ViewModel.Rules.Libraries.All(l => !l.IsPicked) };
        all.Click += (_, _) =>
        {
            foreach (var library in ViewModel.Rules.Libraries)
            {
                library.IsPicked = false;
            }
        };
        LibrariesMenu.Items.Add(all);
        if (ViewModel.Rules.Libraries.Count > 0)
        {
            LibrariesMenu.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var library in ViewModel.Rules.Libraries)
        {
            var item = new ToggleMenuFlyoutItem { Text = library.Name, IsChecked = library.IsPicked };
            item.Click += (_, _) => library.IsPicked = item.IsChecked;
            LibrariesMenu.Items.Add(item);
        }
    }

    // A facet or person rule suggests as the viewer types, and when it takes focus.
    private async void OnRuleTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            await SuggestAsync(sender);
        }
    }

    private async void OnRuleSuggestFocus(object sender, RoutedEventArgs e) => await SuggestAsync((AutoSuggestBox)sender);

    private async Task SuggestAsync(AutoSuggestBox box)
    {
        try
        {
            if (box.Tag is SmartRuleViewModel rule)
            {
                await ViewModel.Rules.SuggestAsync(rule);
                box.IsSuggestionListOpen = rule.Suggestions.Count > 0 && box.FocusState != FocusState.Unfocused;
            }
        }
        catch (Exception ex)
        {
            // async void callers: nothing may escape. A failed people search just suggests nothing.
            Log(ex, "A rule's suggestions could not be read");
        }
    }

    // "Description Optional": the web's label, its "Optional" muted, built from the message so each language orders it.
    private void ShowDescriptionLabel()
    {
        var muted = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SiloMutedBrush"];
        var regular = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SiloFontRegular"];
        DescriptionLabel.Inlines.Clear();
        foreach (var run in AppText.Runs("web.collections.details.descriptionLabel", ("field", AppText.Get("web.collections.copy.fieldDescription"))))
        {
            var inline = new Run { Text = run.Text };
            if (run.Tags.Contains("muted"))
            {
                inline.Foreground = muted;
                inline.FontFamily = regular;
                inline.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
            DescriptionLabel.Inlines.Add(inline);
        }
    }

    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<CollectionEditorPage>>().LogError(ex, "{Message}", message);
}
