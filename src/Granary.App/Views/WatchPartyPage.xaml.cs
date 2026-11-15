using Granary.Core.Texts;
using Granary.Core.ViewModels.WatchParty;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Granary.App.Views;

/// <summary>The Watch Party hub.</summary>
public sealed partial class WatchPartyPage : Page
{
    private const double ColumnWidth = 896;

    // An input method is composing in the code box.
    private bool _composing;

    public WatchPartyPage()
    {
        ViewModel = App.Services.GetRequiredService<WatchPartyHubViewModel>();
        InitializeComponent();
        ShowFromTitleHint();
    }

    public WatchPartyHubViewModel ViewModel { get; }

    /// <summary>"WATCH PARTY" above the headline.</summary>
    public string Eyebrow => AppText.Upper(AppText.Get("watchParty.title"));

    /// <summary>"YOUR RECENT PARTIES".</summary>
    public string RecentHeading => AppText.Upper(AppText.Get("web.watchTogether.hub.recentTitle"));

    public static double Dim(bool dimmed) => dimmed ? 0.6 : 1;

    public static string RejoinName(string title) => AppText.Get("granary.watchParty.rejoinRoom", ("title", title));

    // "Already know what you want to watch? … choose <item>Start a party with this</item> from the menu.": the name in the
    // foreground, where the language puts it.
    private void ShowFromTitleHint()
    {
        var nameBrush = (Brush)Application.Current.Resources["SiloForegroundBrush"];
        foreach (var part in AppText.Runs("web.watchTogether.hub.fromTitleHint"))
        {
            var run = new Run { Text = part.Text };
            if (part.Tags.Contains("item"))
            {
                run.Foreground = nameBrush;
            }
            FromTitleHint.Inlines.Add(run);
        }
    }

    // A mode card's icon: the foreground when chosen, else muted at 60 %, as the web's.
    public static Brush ModeIconBrush(bool chosen) =>
        (Brush)Application.Current.Resources[chosen ? "SiloForegroundBrush" : "SiloMutedBrush"];

    public static double ModeIconOpacity(bool chosen) => chosen ? 1 : 0.6;

    // The page is cached: each visit starts from Host picks and asks again which recent rooms are still on.
    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Arrive();
        try
        {
            await ViewModel.RefreshRecentAsync();
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            App.Services.GetRequiredService<ILogger<WatchPartyPage>>().LogError(ex, "The recent Watch Parties could not be shown");
        }
    }

    private void OnRejoinRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WatchPartyRecentRow row } && ViewModel.RejoinRecentCommand.CanExecute(row))
        {
            ViewModel.RejoinRecentCommand.Execute(row);
        }
    }

    // A column capped by MaxWidth in a ScrollViewer ends up off centre, so its width is set here.
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => Column.Width = Math.Min(ColumnWidth, e.NewSize.Width);

    // A code reads in capitals as it is typed, as in the web's box; the caret stays where it was. An input method's
    // composition is left alone until it ends: rewriting the text under it would break it.
    private void OnCodeTextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        if (!_composing)
        {
            ShowAsTyped(sender);
        }
    }

    private void OnCodeCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _composing = true;

    private void OnCodeCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _composing = false;
        ShowAsTyped(sender);
    }

    private static void ShowAsTyped(TextBox box)
    {
        string shown = WatchPartyHubViewModel.AsTyped(box.Text);
        if (shown != box.Text)
        {
            int caret = box.SelectionStart;
            box.Text = shown;
            box.SelectionStart = caret;
        }
    }

    private void OnCodeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.JoinCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.JoinCommand.Execute(null);
        }
    }

    // Paste takes the clipboard's text as it is: a code, or an invite link the join reads the token from.
    private async void OnPasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                ViewModel.Code = (await content.GetTextAsync()).Trim();
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A refused clipboard leaves the box to typing; the text is never logged.
            App.Services.GetRequiredService<ILogger<WatchPartyPage>>().LogInformation("The clipboard could not be read: {Failure}", ex.GetType().Name);
        }
    }
}
