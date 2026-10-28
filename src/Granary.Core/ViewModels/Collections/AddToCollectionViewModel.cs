using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Collections;

/// <summary>
/// "Add to a collection" from a title's page, as the web's <c>AddToCollectionDialog</c>: the profile's own manual
/// collections with a tick where the title is in them, found by name, and a new one made with the title in it. Ticking
/// saves at once (at the front of the collection) and goes back when it fails.
/// </summary>
public sealed partial class AddToCollectionViewModel : ObservableObject
{
    /// <summary>How long a row says "Added" or "Removed".</summary>
    public static readonly TimeSpan FlashTime = TimeSpan.FromSeconds(2);

    private readonly ISessionManager _session;
    private readonly CollectionsApi _collections;
    private readonly TimeProvider _time;
    private readonly ILogger<AddToCollectionViewModel> _logger;
    private readonly List<CollectionChoiceRow> _all = [];
    private string _contentId = "";

    public AddToCollectionViewModel(ISessionManager session, CollectionsApi collections, TimeProvider time, ILogger<AddToCollectionViewModel> logger)
    {
        _session = session;
        _collections = collections;
        _time = time;
        _logger = logger;
    }

    /// <summary>The collections the search keeps, in the profile's order.</summary>
    public ObservableCollection<CollectionChoiceRow> Shown { get; } = [];

    /// <summary>The title being added, shown as a chip under the heading.</summary>
    [ObservableProperty]
    public partial string ItemTitle { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(HasError), nameof(IsReady), nameof(HasCollections), nameof(HasNoCollections))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>"Find one of your collections".</summary>
    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>The new collection's name ("e.g. Ghibli Night").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsCreating { get; private set; }

    /// <summary>What failed: "Failed to add to collection", "Couldn't make the collection", ….</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = "";

    [ObservableProperty]
    public partial string FooterText { get; private set; } = "Not in a collection yet";

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State is LibraryLoadState.Ready or LibraryLoadState.Empty;

    /// <summary>The profile has manual collections to tick.</summary>
    public bool HasCollections => State == LibraryLoadState.Ready;

    /// <summary>None yet: "Start your first collection".</summary>
    public bool HasNoCollections => State == LibraryLoadState.Empty;

    public bool HasNotice => Notice.Length > 0;

    /// <summary>"No collections match "x"." when the search keeps none.</summary>
    public string NoMatchText => $"No collections match “{Search.Trim()}”.";

    public bool HasNoMatch => HasCollections && Shown.Count == 0;

    /// <summary>"Name it and Dark goes straight in. …" for the first collection.</summary>
    public string FirstCollectionText => $"Name it and {ItemTitle} goes straight in. You can add more titles and change the order later.";

    /// <summary>Reads the profile's own manual collections and which hold <paramref name="contentId"/>.</summary>
    public async Task LoadAsync(string contentId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        _contentId = contentId;
        ItemTitle = title;
        OnPropertyChanged(nameof(FirstCollectionText));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server } || _contentId.Length == 0)
        {
            return;
        }
        State = LibraryLoadState.Loading;
        Notice = "";
        try
        {
            var list = await _collections.ListContainingAsync(server.BaseUri, _contentId);
            _all.Clear();
            foreach (var collection in list.Items.Where(c => c.IsManual && c.CreatorProfileId == _session.ProfileId && c.Id.Length > 0))
            {
                _all.Add(new CollectionChoiceRow(collection, ArtworkUrl.Resolve(server.BaseUri, collection.PosterUrl), collection.Contains == true,
                    ToggleCommand));
            }
            Filter();
            State = _all.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not load the collections to add a title to");
            State = LibraryLoadState.Error;
        }
    }

    /// <summary>Puts the title in the row's collection, or takes it out: at once, and back when the server says no.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleAsync(CollectionChoiceRow? row)
    {
        if (row is null || row.IsBusy || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        bool add = !row.IsIn;
        row.IsBusy = true;
        row.IsIn = add;
        Count();
        Notice = "";
        try
        {
            if (add)
            {
                await _collections.AddItemAsync(server.BaseUri, row.Id, _contentId, 0);
            }
            else
            {
                await _collections.RemoveItemAsync(server.BaseUri, row.Id, _contentId);
            }
            _ = FlashAsync(row, add ? "Added" : "Removed");
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not change a collection from a title's page");
            row.IsIn = !add;
            Count();
            Notice = add ? "Failed to add to collection" : "Failed to remove item";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Makes a manual collection with the title in it.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        string name = NewName.Trim();
        if (name.Length == 0 || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        IsCreating = true;
        Notice = "";
        try
        {
            PersonalCollection made;
            try
            {
                made = await _collections.CreateAsync(server.BaseUri, new PersonalCollectionCreate { Name = name });
            }
            catch (Exception ex) when (LibraryErrors.IsExpected(ex))
            {
                _logger.LogWarning(ex, "Could not make a collection from a title's page");
                Notice = "Couldn't make the collection";
                return;
            }
            // Its tick waits until the title went in.
            var row = new CollectionChoiceRow(made, null, isIn: false, ToggleCommand) { IsBusy = true };
            _all.Add(row);
            NewName = "";
            Search = "";
            State = LibraryLoadState.Ready;
            Filter();
            try
            {
                await _collections.AddItemAsync(server.BaseUri, made.Id, _contentId, 0);
                row.IsIn = true;
                _ = FlashAsync(row, "Added");
            }
            catch (Exception ex) when (LibraryErrors.IsExpected(ex))
            {
                _logger.LogWarning(ex, "Made a collection but could not add the title to it");
                Notice = $"Made {made.Name}, but couldn't add {ItemTitle}";
            }
            finally
            {
                row.IsBusy = false;
                Count();
            }
        }
        finally
        {
            IsCreating = false;
        }
    }

    private bool CanCreate() => !IsCreating && NewName.Trim().Length > 0;

    partial void OnSearchChanged(string value) => Filter();

    private void Filter()
    {
        string text = Search.Trim();
        Shown.Clear();
        foreach (var row in _all.Where(r => text.Length == 0 || r.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
        {
            Shown.Add(row);
        }
        OnPropertyChanged(nameof(NoMatchText));
        OnPropertyChanged(nameof(HasNoMatch));
        Count();
    }

    private void Count()
    {
        int count = _all.Count(r => r.IsIn);
        FooterText = count == 0 ? "Not in a collection yet" : count == 1 ? "In 1 collection" : $"In {count} collections";
    }

    private async Task FlashAsync(CollectionChoiceRow row, string text)
    {
        // Only the latest flash clears it: Add, Remove, Add within the time keeps the last "Added" its full time.
        int flash = ++row.Flashes;
        row.Flash = text;
        await Task.Delay(FlashTime, _time);
        if (row.Flashes == flash)
        {
            row.Flash = "";
        }
    }
}

/// <summary>A collection to tick: its poster, name, "Manual · 4 titles", Shared, and whether the title is in it.</summary>
public sealed partial class CollectionChoiceRow : ObservableObject
{
    internal CollectionChoiceRow(PersonalCollection collection, Uri? posterUrl, bool isIn, System.Windows.Input.ICommand toggleCommand)
    {
        Id = collection.Id;
        Name = collection.Name;
        Meta = "Manual · " + CollectionEditorText.Titles(collection.ItemCount);
        IsShared = collection.IsShared;
        PosterUrl = posterUrl;
        IsIn = isIn;
        ToggleCommand = toggleCommand;
    }

    public string Id { get; }

    public string Name { get; }

    public string Meta { get; }

    public bool IsShared { get; }

    public Uri? PosterUrl { get; }

    /// <summary>Takes this row.</summary>
    public System.Windows.Input.ICommand ToggleCommand { get; }

    [ObservableProperty]
    public partial bool IsIn { get; internal set; }

    /// <summary>A change on its way: the tick waits.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; internal set; }

    /// <summary>"Added" or "Removed" for a moment after a tick.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlash))]
    public partial string Flash { get; internal set; } = "";

    public bool HasFlash => Flash.Length > 0;

    internal int Flashes { get; set; }
}
