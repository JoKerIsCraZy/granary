using System.ComponentModel;
using Granary.App.Controls;
using Granary.Core.Settings.Search;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Granary.App.Views;

// The search at the top of the settings' sidebar (docs/ui-design.md, Settings), as the web's settings sidebar has it: the box
// narrows the sidebar to the pages whose texts match, and a page opened from it shows the first of its settings that matched.
// What the pages show is read from their markup (SettingsMarkup), so the search has no list of its own to keep in step.
public sealed partial class SettingsPage
{
    private const string MarkupPrefix = "Granary.App.SettingsMarkup.";
    // Room left above a setting brought to the top, so the line over its row shows too.
    private const double MatchRoom = 32;

    // The sidebar's and the pages' markup, read once for the app's run; the index is built from it in the language on screen.
    private static readonly Lazy<SearchMarkup> Markup = new(ReadMarkup);

    // The sidebar's entries as the page was made, list by list: the search takes them out and puts them back.
    private readonly Dictionary<ListView, ListViewItem[]> _entries = [];

    // A press on an entry while searching, waiting for its page to open so focus goes to the page's setting.
    private readonly SettingsSearchPress _press = new();

    // Counted up by each reveal asked for and by each query: a reveal that waited for its page to be laid out goes when a later
    // one was asked meanwhile, and gives no focus once the query has changed, so an old press cannot take the focus.
    private int _reveals;
    private int _queries;

    /// <summary>The search box at the top of the sidebar.</summary>
    public SettingsSearchViewModel Search { get; }

    /// <summary>The box's room on the right: for the clear button while it shows, else for a longer hint, as on the web.</summary>
    public Thickness SearchPadding(bool hasQuery) => new(36, 11, hasQuery ? 44 : 12, 0);

    private void SetUpSearch()
    {
        // Read off the UI thread while the page opens, not at the first key typed. Reading never fails: a markup that cannot be
        // read leaves its page found by its name alone.
        _ = Task.Run(() => Markup.Value);
        foreach (var list in Lists())
        {
            _entries[list] = [.. list.Items.OfType<ListViewItem>()];
            // A press on an entry, the open one too, goes on to its setting; the list keeps Enter and taps for itself.
            list.AddHandler(TappedEvent, new TappedEventHandler(OnEntryTapped), handledEventsToo: true);
            list.AddHandler(KeyDownEvent, new KeyEventHandler(OnEntryKeyDown), handledEventsToo: true);
        }
        Search.PropertyChanged += OnSearchChanged;
        // The sidebar's profile decides whether Profiles is offered: the results follow it while the page shows.
        Loaded += (_, _) => Sidebar.PropertyChanged += OnSidebarChanged;
        Unloaded += (_, _) => Sidebar.PropertyChanged -= OnSidebarChanged;
    }

    private void OnSidebarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.Profile))
        {
            DispatcherQueue.TryEnqueue(ShowResults);
        }
    }

    private void OnSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsSearchViewModel.Results):
                ShowResults();
                break;
            // Read out when its words change, as the web's live region is.
            case nameof(SettingsSearchViewModel.Status):
                Announcer.Announce(SearchBox, Search.Status, "SettingsSearch");
                break;
        }
    }

    // The entries of the pages that match under their groups' headings, every entry again once the box is empty. The open page
    // stays chosen while it is listed.
    private void ShowResults()
    {
        var listed = Search.Results?.SelectMany(group => group.Pages).Select(page => page.Tag).ToHashSet(StringComparer.Ordinal);
        bool isFirst = true;
        _switchingLists = true;
        try
        {
            foreach (var (heading, list, _) in Groups())
            {
                // A page held back for this profile (Profiles, for the household's primary profile only) is not offered either.
                var shown = _entries[list].Where(entry => entry.Visibility == Visibility.Visible
                    && (listed is null || listed.Contains(entry.Tag as string ?? ""))).ToList();
                Keep(list.Items, shown);
                heading.Visibility = list.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (shown.Count > 0)
                {
                    // The first group shown sits close under the box, as the first one always does.
                    heading.Margin = new Thickness(8, isFirst ? 8 : 20, 0, 6);
                    isFirst = false;
                }
                list.SelectedItem = shown.FirstOrDefault(entry => entry.Tag as string == _shownPage);
            }
            // A new query has its own matches, shown once a page opens; a press made for the last one goes.
            LeadInto(null, null);
            _press.Forget();
            _queries++;
        }
        finally
        {
            _switchingLists = false;
        }
    }

    // The list's entries become those shown, in their order; one that stays is not taken out, so focus on it stays too.
    private static void Keep(ItemCollection items, List<ListViewItem> shown)
    {
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is not ListViewItem entry || !shown.Contains(entry))
            {
                items.RemoveAt(i);
            }
        }
        for (int i = 0; i < shown.Count; i++)
        {
            if (i >= items.Count || !ReferenceEquals(items[i], shown[i]))
            {
                items.Insert(i, shown[i]);
            }
        }
    }

    // A page opened while searching shows the first of its settings that matched; focus goes there when a press opened it.
    private void ShowMatch(string page)
    {
        FocusState? focus = _press.TryTake(page, out bool byKeyboard) ? StateOf(byKeyboard) : null;
        LeadInto(page, null);
        RevealWhenShown(page, focus);
    }

    // A press (a click, Enter, a remote's OK) on an entry while searching: focus goes to its page's setting, at once on the page
    // that shows, else once the choice the press makes has opened it (SettingsSearchPress).
    private void Press(ListViewItem? entry, bool byKeyboard)
    {
        if (!Search.HasQuery || entry?.Tag is not string page)
        {
            return;
        }
        bool showsOpenPage = _editor is null && _views.TryGetValue(_shownPage, out var view) && ReferenceEquals(PageHost.Content, view);
        if (_press.Press(page, _shownPage, showsOpenPage, byKeyboard))
        {
            RevealWhenShown(page, StateOf(byKeyboard));
        }
    }

    private static FocusState StateOf(bool byKeyboard) => byKeyboard ? FocusState.Keyboard : FocusState.Programmatic;

    private void OnEntryTapped(object sender, TappedRoutedEventArgs e) =>
        Press(Ancestor<ListViewItem>(e.OriginalSource as DependencyObject), byKeyboard: false);

    private void OnEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !e.KeyStatus.WasKeyDown)
        {
            Press(Ancestor<ListViewItem>(e.OriginalSource as DependencyObject), byKeyboard: true);
        }
    }

    // The page's matches are looked for once it is laid out: a view shown for the first time has no texts before it loads.
    private void RevealWhenShown(string page, FocusState? focus)
    {
        // Every page opened and every press supersedes a reveal still waiting, also where this one has nothing to show.
        int reveal = ++_reveals;
        int query = _queries;
        if (Search.MatchesOn(page).Count == 0 || !_views.TryGetValue(page, out var view) || view is not FrameworkElement shown)
        {
            return;
        }
        void Reveal()
        {
            // A later reveal took over, or the page left before it was laid out and has come back since.
            if (reveal != _reveals || !ReferenceEquals(PageHost.Content, shown))
            {
                return;
            }
            // Read again, as the query may have changed while the page was laid out; then the press was for the old one.
            if (Search.MatchesOn(page) is { Count: > 0 } matches)
            {
                RevealMatch(shown, page, matches, query == _queries ? focus : null);
            }
        }
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            shown.Loaded -= OnLoaded;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Reveal);
        }
        if (shown.IsLoaded)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Reveal);
        }
        else
        {
            shown.Loaded += OnLoaded;
        }
    }

    // The first match the page shows comes to its top. Right from the page's entry goes to it, for a remote and a controller.
    private void RevealMatch(FrameworkElement view, string page, IReadOnlyList<SettingsSearchEntry> matches, FocusState? focus)
    {
        foreach (var match in matches)
        {
            if (FindShown(view, match) is not { } target)
            {
                continue;
            }
            var control = FocusTargetOf(target, view);
            LeadInto(page, control);
            // Focus first: the scroll that comes with it would otherwise undo the one to the top.
            if (focus is { } state)
            {
                control?.Focus(state);
            }
            target.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 0,
                TargetRect = new Rect(0, -MatchRoom, target.ActualWidth, target.ActualHeight + MatchRoom),
            });
            return;
        }
    }

    // Right from the page's entry goes to its match; from any other entry, and without a match, it goes where it would.
    private void LeadInto(string? page, UIElement? match)
    {
        foreach (var entry in _entries.Values.SelectMany(entries => entries))
        {
            entry.XYFocusRight = entry.Tag as string == page ? match : null;
        }
    }

    // The element showing a match, outside anything collapsed: a setting this profile or PC does not have is passed over.
    private static FrameworkElement? FindShown(DependencyObject element, SettingsSearchEntry match)
    {
        if (element is UIElement { Visibility: Visibility.Collapsed })
        {
            return null;
        }
        if (element is FrameworkElement candidate && Shows(candidate, match))
        {
            return candidate;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindShown(VisualTreeHelper.GetChild(element, i), match) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static bool Shows(FrameworkElement element, SettingsSearchEntry match) => match.Kind switch
    {
        SettingsEntryKind.Section => element is SettingsGroup group && group.Title == match.Label,
        SettingsEntryKind.Row => element is SettingsRow row && row.Label == match.Label,
        SettingsEntryKind.Heading => element is TextBlock heading && heading.Text == match.Label
            && AutomationProperties.GetHeadingLevel(heading) != AutomationHeadingLevel.None,
        _ => element is not TextBlock && AutomationProperties.GetName(element) == match.Label,
    };

    // What focus goes to for a match: a control itself, the first control of a row or section, and after a heading the first
    // control that follows it.
    private static UIElement? FocusTargetOf(FrameworkElement target, FrameworkElement view)
    {
        if (Focusable(target) is { } own)
        {
            return own;
        }
        DependencyObject child = target;
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null && !ReferenceEquals(child, view);
            child = parent, parent = VisualTreeHelper.GetParent(parent))
        {
            bool isAfter = false;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var sibling = VisualTreeHelper.GetChild(parent, i);
                if (isAfter && Focusable(sibling) is { } next)
                {
                    return next;
                }
                isAfter |= ReferenceEquals(sibling, child);
            }
        }
        return null;
    }

    private static UIElement? Focusable(DependencyObject element) =>
        element is Control { IsTabStop: true, IsEnabled: true, Visibility: Visibility.Visible } control
            ? control
            : FocusManager.FindFirstFocusableElement(element) as UIElement;

    // Ctrl+F finds a setting: the box, with what it holds chosen to type over.
    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    // Escape empties the box, as its clear button does; with nothing in it, Escape goes back as anywhere else.
    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && Search.Query.Length > 0)
        {
            e.Handled = true;
            Search.ClearCommand.Execute(null);
        }
    }

    private static T? Ancestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = VisualTreeHelper.GetParent(element);
        }
        return element as T;
    }

    // The sidebar's groups with their pages and what each page shows, in the language on screen.
    private IReadOnlyList<SettingsSearchGroup> BuildSearchIndex()
    {
        var markup = Markup.Value;
        return SettingsSearchIndex.Build(
            [.. Groups().Select(group => new SettingsSidebarGroup(group.Pages.Name, group.NameKey, markup.Sidebar.GetValueOrDefault(group.Pages.Name) ?? []))],
            markup.Pages.GetValueOrDefault);
    }

    // Never fails, as the result is kept for the run: a markup that cannot be read leaves its page found by its name alone, and
    // without the sidebar's the search finds nothing.
    private static SearchMarkup ReadMarkup()
    {
        var logger = App.Services.GetService<ILogger<SettingsPage>>() ?? (ILogger)NullLogger.Instance;
        var sidebar = Read(nameof(SettingsPage), SettingsMarkup.ReadSidebar) ?? new Dictionary<string, IReadOnlyList<SettingsSidebarPage>>();
        var pages = new Dictionary<string, SettingsMarkupPage>(StringComparer.Ordinal);
        foreach (var (tag, view) in PageViews)
        {
            if (Read(view.Markup, SettingsMarkup.ReadPage) is { } page)
            {
                pages[tag] = page;
            }
        }
        return new SearchMarkup(sidebar, pages);

        T? Read<T>(string name, Func<string, T> parse)
            where T : class
        {
            try
            {
                using var stream = typeof(SettingsPage).Assembly.GetManifestResourceStream(MarkupPrefix + name + ".xaml");
                if (stream is null)
                {
                    logger.LogWarning("The settings' search has no markup for {View}", name);
                    return null;
                }
                using var reader = new StreamReader(stream);
                return parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                // By its kind only: a message may name a folder of the Windows user's.
                logger.LogWarning("The settings' search could not read the markup of {View} ({Failure})", name, ex.GetType().Name);
                return null;
            }
        }
    }

    private sealed record SearchMarkup(
        IReadOnlyDictionary<string, IReadOnlyList<SettingsSidebarPage>> Sidebar, IReadOnlyDictionary<string, SettingsMarkupPage> Pages);
}
