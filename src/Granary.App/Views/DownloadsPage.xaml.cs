using Granary.App.Controls;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// The active profile's downloads on this PC (docs/ui-design.md, "Downloads"). The page is cached; it follows the downloads
/// only while it shows: attached as it is shown, detached as it is left, and as it unloads, so the downloads, which live as
/// long as the app, never hold it (<c>DownloadsViewModelTests</c>).
/// </summary>
public sealed partial class DownloadsPage : Page
{
    // A row's artwork as wide as it shows: a film's poster, an episode's still; and a series' poster beside its title.
    private const int PosterWidth = 60;
    private const int PosterHeight = 90;
    private const int StillWidth = 96;
    private const int StillHeight = 54;
    private const int SeriesPosterWidth = 40;

    public DownloadsPage()
    {
        // Made outside the container: the root provider would keep the disposable view model, and the page with it, for good.
        ViewModel = ActivatorUtilities.CreateInstance<DownloadsViewModel>(App.Services);
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public DownloadsViewModel ViewModel { get; }

    public string EmptyTitle => DownloadsViewModel.EmptyTitle;

    public string EmptyText => DownloadsViewModel.EmptyText;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }

    // Also when the page goes without being navigated from. One shown again before its Unloaded came stays attached.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(Frame?.Content, this))
        {
            ViewModel.Detach();
        }
    }

    // Static because x:Bind function bindings in the templates call them on the type.
    public static ImageSource? Art(string? path, bool isEpisode) => LocalArtwork.Load(path, isEpisode ? StillWidth : PosterWidth);

    public static double ArtWidth(bool isEpisode) => isEpisode ? StillWidth : PosterWidth;

    public static double ArtHeight(bool isEpisode) => isEpisode ? StillHeight : PosterHeight;

    /// <summary>What shows until the artwork is on this PC: a television for an episode, a film for a film.</summary>
    public static string ArtGlyph(bool isEpisode) => isEpisode ? "" : "";

    public static ImageSource? SeriesPoster(string? path) => LocalArtwork.Load(path, SeriesPosterWidth);

    public static Visibility HasPath(string? path) => string.IsNullOrEmpty(path) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The row's one transfer button: Pause, Resume or Retry.</summary>
    public static string ActionGlyph(DownloadRowAction action) => action switch
    {
        DownloadRowAction.Pause => "",
        DownloadRowAction.Resume => "",
        DownloadRowAction.Retry => "",
        _ => "",
    };

    /// <summary>"Example Series, E5 · Chapter Five, Downloading · 42%": what a screen reader says of a row.</summary>
    public static string RowName(string title, string detail, string status) =>
        string.Join(", ", new[] { title, detail, status }.Where(part => !string.IsNullOrEmpty(part)));

    // The transfer button goes only when its download has nothing left to do (done, or only to be deleted). Had it the focus,
    // the focus goes to its row rather than wherever Windows would put it. Once per button; the callback is static, so it
    // holds no page.
    private void OnPrimaryButtonLoaded(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.Loaded -= OnPrimaryButtonLoaded;
        button.RegisterPropertyChangedCallback(VisibilityProperty, OnPrimaryVisibilityChanged);
    }

    private static void OnPrimaryVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (sender is Button { Visibility: Visibility.Collapsed } button && button.FocusState != FocusState.Unfocused
            && button.FindName("OpenButton") is Control row)
        {
            row.Focus(button.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic);
        }
    }

    // Delete asks first. Once the row went, the focus goes to the row now in its place, or to the last one, so a keyboard or a
    // remote carries on down the list.
    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadRowViewModel row } button)
        {
            return;
        }
        var state = button.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
        int place = Rows().IndexOf(row);
        try
        {
            await row.DeleteCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            App.Services.GetRequiredService<ILogger<DownloadsPage>>().LogWarning("A download could not be deleted ({Failure})", ex.GetType().Name);
            return;
        }
        // The page shows the downloads without it from a refresh already posted; this comes after it.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusRowAt(row, place, state));
    }

    private void FocusRowAt(DownloadRowViewModel deleted, int place, FocusState state)
    {
        var rows = Rows();
        // Kept (the question was declined), or nothing left: the focus stays where it is.
        if (place < 0 || rows.Count == 0 || rows.Contains(deleted))
        {
            return;
        }
        var target = rows[Math.Min(place, rows.Count - 1)];
        SectionsRepeater.UpdateLayout();
        if (Find(SectionsRepeater, element => element is Button { Name: "OpenButton" } open && ReferenceEquals(open.DataContext, target)) is Control found)
        {
            found.Focus(state);
        }
    }

    private List<DownloadRowViewModel> Rows() => [.. ViewModel.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Rows)];

    private static DependencyObject? Find(DependencyObject element, Func<DependencyObject, bool> matches)
    {
        if (matches(element))
        {
            return element;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (Find(VisualTreeHelper.GetChild(element, i), matches) is { } inside)
            {
                return inside;
            }
        }
        return null;
    }
}
