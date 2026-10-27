using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Navigation &amp; Cards → Sidebar: which entries Granary's sidebar shows and in which order, section by section
/// (<see cref="SidebarLayout"/>). Each change shows in the sidebar at once.
/// </summary>
public sealed partial class SidebarSettingsViewModel : ObservableObject, IDisposable
{
    public const string ServerOfferNote = "Shows while your server offers it.";

    private readonly SidebarLayout _layout;
    private readonly SidebarViewModel _sidebar;
    private readonly Dictionary<string, SidebarRowViewModel> _rows = new(StringComparer.Ordinal);

    public SidebarSettingsViewModel(SidebarLayout layout, SidebarViewModel sidebar)
    {
        _layout = layout;
        _sidebar = sidebar;
        Groups =
        [
            new SidebarGroupViewModel("Libraries", SidebarSection.Libraries),
            new SidebarGroupViewModel("Discover", SidebarSection.Discover),
            new SidebarGroupViewModel("Your Stuff", SidebarSection.YourStuff),
        ];
        _layout.PropertyChanged += OnLayoutChanged;
        _sidebar.Libraries.CollectionChanged += OnLibrariesChanged;
        Show();
    }

    public IReadOnlyList<SidebarGroupViewModel> Groups { get; }

    /// <summary>Something is hidden or moved; Reset shows the sidebar as it comes.</summary>
    public bool IsCustomized => _layout.IsCustomized;

    [RelayCommand]
    private Task ResetAsync() => _layout.ResetAsync();

    internal Task MoveAsync(SidebarSection section, string tag, int delta) => _layout.MoveAsync(section, TagsOf(section), tag, delta);

    internal Task SetShownAsync(string tag, bool shown)
    {
        int revision = _layout.Revision;
        var saving = _layout.SetHiddenAsync(tag, !shown);
        if (_layout.Revision == revision)
        {
            // Nothing changed (no profile acting): the switch goes back to what the sidebar shows.
            Show();
        }
        return saving;
    }

    public void Dispose()
    {
        _layout.PropertyChanged -= OnLayoutChanged;
        _sidebar.Libraries.CollectionChanged -= OnLibrariesChanged;
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarLayout.Revision))
        {
            Show();
        }
        else if (e.PropertyName == nameof(SidebarLayout.IsCustomized))
        {
            OnPropertyChanged(nameof(IsCustomized));
        }
    }

    private void OnLibrariesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Show();

    // A section's entries as they come: the libraries in the server's order, the others in the sidebar's.
    private IReadOnlyList<string> TagsOf(SidebarSection section) => [.. EntriesOf(section).Select(entry => entry.Tag)];

    // A library the server lists twice is one entry.
    private IEnumerable<SidebarEntry> EntriesOf(SidebarSection section) => section == SidebarSection.Libraries
        ? _sidebar.Libraries.Select(library => new SidebarEntry(SidebarLayout.TagOf(library), library.Name, section, library.Type))
            .DistinctBy(entry => entry.Tag, StringComparer.Ordinal)
        : SidebarLayout.Entries.Where(entry => entry.Section == section);

    // The rows stay the same objects while they move, so a row keeps its switch as it goes.
    private void Show()
    {
        foreach (var group in Groups)
        {
            var entries = EntriesOf(group.Section).ToDictionary(entry => entry.Tag, StringComparer.Ordinal);
            var arranged = _layout.Arrange(group.Section, TagsOf(group.Section));
            var rows = arranged.Select((tag, index) =>
            {
                var entry = entries[tag];
                if (!_rows.TryGetValue(tag, out var row) || row.Entry != entry)
                {
                    _rows[tag] = row = new SidebarRowViewModel(this, entry, entry.Tag is "Requests" or "Notifications" ? ServerOfferNote : null);
                }
                row.Show(!_layout.IsHidden(tag), canMoveUp: index > 0, canMoveDown: index < arranged.Count - 1);
                return row;
            }).ToList();
            group.Sync(rows);
        }
        foreach (string gone in _rows.Keys.Except(Groups.SelectMany(group => group.Rows).Select(row => row.Tag)).ToList())
        {
            _rows.Remove(gone);
        }
        OnPropertyChanged(nameof(IsCustomized));
    }
}

/// <summary>A section of the sidebar and its entries in the order shown.</summary>
public sealed partial class SidebarGroupViewModel(string title, SidebarSection section) : ObservableObject
{
    public string Title { get; } = title;

    public SidebarSection Section { get; } = section;

    public ObservableCollection<SidebarRowViewModel> Rows { get; } = [];

    /// <summary>The section has entries (a profile may have no libraries).</summary>
    [ObservableProperty]
    public partial bool HasRows { get; private set; }

    // Moves rows rather than making the list anew.
    internal void Sync(IReadOnlyList<SidebarRowViewModel> rows)
    {
        for (int index = Rows.Count - 1; index >= 0; index--)
        {
            if (!rows.Contains(Rows[index]))
            {
                Rows.RemoveAt(index);
            }
        }
        for (int index = 0; index < rows.Count; index++)
        {
            int at = Rows.IndexOf(rows[index]);
            if (at < 0)
            {
                Rows.Insert(index, rows[index]);
            }
            else if (at != index)
            {
                Rows.Move(at, index);
            }
        }
        HasRows = Rows.Count > 0;
    }
}

/// <summary>An entry of the sidebar: shown or not, and up or down within its section.</summary>
public sealed partial class SidebarRowViewModel : ObservableObject
{
    private readonly SidebarSettingsViewModel _owner;
    private readonly SidebarEntry _entry;
    // Set while the row shows the saved layout, so showing it changes nothing.
    private bool _showing;

    internal SidebarRowViewModel(SidebarSettingsViewModel owner, SidebarEntry entry, string? note)
    {
        _owner = owner;
        _entry = entry;
        Note = note;
    }

    internal SidebarEntry Entry => _entry;

    public string Tag => _entry.Tag;

    public string Label => _entry.Label;

    /// <summary>What the icon shows: the entry, or a library's type.</summary>
    public string Kind => _entry.Kind;

    public string? Note { get; }

    [ObservableProperty]
    public partial bool IsShown { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool CanMoveUp { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool CanMoveDown { get; private set; }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private Task MoveUpAsync() => _owner.MoveAsync(_entry.Section, Tag, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private Task MoveDownAsync() => _owner.MoveAsync(_entry.Section, Tag, 1);

    internal void Show(bool shown, bool canMoveUp, bool canMoveDown)
    {
        _showing = true;
        try
        {
            IsShown = shown;
        }
        finally
        {
            _showing = false;
        }
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
    }

    partial void OnIsShownChanged(bool value)
    {
        if (!_showing)
        {
            _ = _owner.SetShownAsync(Tag, value);
        }
    }
}
