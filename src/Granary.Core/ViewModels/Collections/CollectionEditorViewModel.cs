using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Collections;

/// <summary>
/// A profile's own manual collection made or changed, as the web's collection editor
/// (<c>components/collections/editor/CollectionEditor.tsx</c>): name, description and Show only, where it shows, and its
/// titles. Everything but the titles is a draft the save bar saves (or, for a new one, Create collection makes); titles
/// save as they are added, removed or moved. A save is sent at the version the collection was read at: when it changed
/// elsewhere meanwhile, the two are merged field by field, and fields changed on both sides wait for the viewer's pick.
/// </summary>
public sealed partial class CollectionEditorViewModel : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly CollectionsApi _collections;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly ProfilesApi _profiles;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly ILogger<CollectionEditorViewModel> _logger;
    // The collection as last read or saved: what the draft is compared with, and the base of a merge.
    private CollectionDraft _saved = CollectionDraft.Empty;
    private CollectionDraft? _theirs;
    private string? _version;
    private bool _showing;
    private bool _creating;
    private bool _deleted;
    private CollectionCapabilities _capabilities = new();

    public CollectionEditorViewModel(ISessionManager session, CollectionsApi collections, CatalogApi catalog, ImageParameterCache images,
        ProfilesApi profiles, INavigationService navigation, TimeProvider time, ILogger<CollectionEditorViewModel> logger)
    {
        _session = session;
        _collections = collections;
        _catalog = catalog;
        _images = images;
        _profiles = profiles;
        _navigation = navigation;
        _time = time;
        _logger = logger;
        Watch = WatchChoices[0];
        Media = MediaChoices[0];
    }

    public static IReadOnlyList<SettingChoice> WatchChoices { get; } =
        [new(CollectionShowOnly.All, "All"), new(CollectionShowOnly.Unwatched, "Unwatched"), new(CollectionShowOnly.Watched, "Watched")];

    public static IReadOnlyList<SettingChoice> MediaChoices { get; } =
        [new(CollectionShowOnly.All, "All"), new(CollectionShowOnly.Movies, "Movies"), new(CollectionShowOnly.Shows, "Shows")];

    /// <summary>The collection once it exists; null while a new one is made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew), nameof(IsCreated))]
    public partial string? CollectionId { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(HasLoadError), nameof(IsReady))]
    public partial LibraryLoadState State { get; private set; }

    [ObservableProperty]
    public partial string LoadError { get; private set; } = "";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    /// <summary>Show only: All, Unwatched or Watched.</summary>
    [ObservableProperty]
    public partial SettingChoice Watch { get; set; }

    /// <summary>Show only: All, Movies or Shows.</summary>
    [ObservableProperty]
    public partial SettingChoice Media { get; set; }

    [ObservableProperty]
    public partial bool IsShared { get; set; }

    [ObservableProperty]
    public partial bool OnCollectionsTab { get; set; }

    /// <summary>The login has other profiles: Show to other profiles is offered.</summary>
    [ObservableProperty]
    public partial bool CanShare { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    public partial bool IsSaving { get; private set; }

    /// <summary>"Couldn't save · …"; the save bar stays until a save goes through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError), nameof(SaveLabel))]
    public partial string SaveError { get; private set; } = "";

    /// <summary>The fields changed here and elsewhere since the collection was read: Keep mine or Use theirs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflict), nameof(ConflictText))]
    public partial IReadOnlyList<string> Conflicts { get; private set; } = [];

    /// <summary>A delete that failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeleteError))]
    public partial string DeleteError { get; private set; } = "";

    public bool IsNew => CollectionId is null;

    public bool IsCreated => !IsNew;

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool HasLoadError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    public bool HasSaveError => SaveError.Length > 0;

    public bool HasConflict => Conflicts.Count > 0;

    public bool HasDeleteError => DeleteError.Length > 0;

    public string ConflictText => CollectionEditorText.ConflictFields(Conflicts);

    /// <summary>The heading: the name as typed, or "New collection".</summary>
    public string HeaderTitle => Name.Trim() is { Length: > 0 } name ? name : IsNew ? CollectionEditorText.NewCollection : _saved.Name;

    /// <summary>The "Shared" tag: the collection as saved is shared.</summary>
    public bool IsSavedShared => !IsNew && _saved.IsShared;

    /// <summary>Sharing turned off on a shared collection: who loses it once saved.</summary>
    public bool ShowsUnshareWarning => IsSavedShared && !IsShared;

    public string UnshareWarning => CollectionEditorText.UnshareWarning(OtherProfiles);

    /// <summary>The draft as it would be saved.</summary>
    public CollectionDraft Draft => new(Name.Trim(), Description.Trim(), new CollectionShowOnly(Watch.Value, Media.Value), IsShared, OnCollectionsTab);

    public IReadOnlyList<string> ChangedFields => IsNew ? [] : Draft.ChangedFrom(_saved);

    /// <summary>Leaving now would lose something: a changed field, or for a new collection anything at all.</summary>
    public bool HasUnsavedChanges => !_deleted && (IsNew ? Draft != CollectionDraft.Empty || Titles.Count > 0 : ChangedFields.Count > 0);

    /// <summary>A new collection's bar is always there; a saved one's while something changed or a save failed.</summary>
    public bool ShowsSaveBar => IsReady && (IsNew || ChangedFields.Count > 0 || HasSaveError);

    /// <summary>"Not created yet", or "Name and description not saved".</summary>
    public string SaveBarMessage => IsNew ? CollectionEditorText.NotCreatedYet : CollectionEditorText.NotSaved(ChangedFields);

    public string SaveBarHint => !IsNew ? CollectionEditorText.TitlesAlreadySaved
        : Name.Trim().Length == 0 ? CollectionEditorText.NameItThenCreate
        : Titles.Count > 0 ? CollectionEditorText.TitlesReadyToAdd(Titles.Count)
        : "";

    public string SaveLabel => IsSaving ? (IsNew ? "Creating..." : "Saving...") : HasSaveError ? "Try again" : IsNew ? "Create collection" : "Save";

    public IReadOnlyList<string> OtherProfiles { get; private set; } = [];

    /// <summary>Opens the collection to edit it, or a new one when <paramref name="collectionId"/> is null.</summary>
    public async Task ActivateAsync(string? collectionId)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var profiles = ReadOtherProfilesAsync(server.BaseUri);
        var capabilities = ReadCapabilitiesAsync(server.BaseUri);
        if (collectionId is null)
        {
            CollectionId = null;
            Show(CollectionDraft.Empty);
            _saved = CollectionDraft.Empty;
            State = LibraryLoadState.Ready;
            TitlesStatus = CollectionEditorText.NotCreatedYet;
        }
        else
        {
            await LoadAsync(collectionId, server.BaseUri);
        }
        await profiles;
        await capabilities;
        if (IsCreated && IsReady)
        {
            await LoadTitlesAsync();
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (CollectionId is { } id && _session is { State: SessionState.Ready, ActiveServer: { } server })
        {
            await LoadAsync(id, server.BaseUri);
            if (IsReady)
            {
                await LoadTitlesAsync();
            }
        }
    }

    /// <summary>Makes the new collection, or saves the changed one.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => IsNew ? CreateAsync() : UpdateAsync(retried: false);

    private bool CanSave() => !IsSaving && !HasConflict && Name.Trim().Length > 0 && (IsNew || ChangedFields.Count > 0 || HasSaveError);

    /// <summary>Puts the fields back as saved.</summary>
    [RelayCommand]
    private void Discard()
    {
        Show(_saved);
        Conflicts = [];
        _theirs = null;
        SaveError = "";
    }

    /// <summary>Changed in both places: takes the other side's values for those fields.</summary>
    [RelayCommand]
    private void UseTheirs()
    {
        if (_theirs is { } theirs)
        {
            var draft = Draft;
            Show(new CollectionDraft(
                Conflicts.Contains("Name") ? theirs.Name : draft.Name,
                Conflicts.Contains("Description") ? theirs.Description : draft.Description,
                Conflicts.Contains("Show only") ? theirs.ShowOnly : draft.ShowOnly,
                Conflicts.Contains("Show to other profiles") ? theirs.IsShared : draft.IsShared,
                Conflicts.Contains("Show on the Collections tab") ? theirs.OnCollectionsTab : draft.OnCollectionsTab));
        }
        Conflicts = [];
        _theirs = null;
    }

    /// <summary>Changed in both places: keeps this side's values; Save then sends them.</summary>
    [RelayCommand]
    private void KeepMine()
    {
        Conflicts = [];
        _theirs = null;
    }

    /// <summary>Opens the collection's page.</summary>
    [RelayCommand]
    private void Open()
    {
        if (CollectionId is { } id)
        {
            _navigation.NavigateTo(AppRoute.Collection, new CollectionRouteParameter(CatalogSources.UserCollection, id, _saved.Name));
        }
    }

    /// <summary>Deletes the collection (after the page asked) and goes to Collections.</summary>
    public async Task DeleteAsync()
    {
        if (CollectionId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        DeleteError = "";
        try
        {
            _version ??= (await _collections.GetAsync(server.BaseUri, id)).Version;
            await _collections.DeleteAsync(server.BaseUri, id, _version);
            _deleted = true;
            _navigation.ShowSection(AppRoute.Collections);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not delete a collection");
            if (ex is SiloApiException { Status: 412 })
            {
                // Changed meanwhile: the next try goes at the version it has now.
                _version = null;
                DeleteError = CollectionEditorText.Stale;
            }
            else
            {
                DeleteError = LibraryErrors.Describe(ex) is { Length: > 0 } why ? why : "Failed to delete";
            }
        }
    }

    partial void OnNameChanged(string value) => DraftChanged();

    partial void OnDescriptionChanged(string value) => DraftChanged();

    partial void OnWatchChanged(SettingChoice value) => DraftChanged();

    partial void OnMediaChanged(SettingChoice value) => DraftChanged();

    partial void OnIsSharedChanged(bool value) => DraftChanged();

    partial void OnOnCollectionsTabChanged(bool value) => DraftChanged();

    partial void OnStateChanged(LibraryLoadState value) => OnPropertyChanged(nameof(ShowsSaveBar));

    partial void OnSaveErrorChanged(string value) => DraftChanged();

    partial void OnIsSavingChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnConflictsChanged(IReadOnlyList<string> value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnCollectionIdChanged(string? value)
    {
        OnPropertyChanged(nameof(TitlesCaption));
        DraftChanged();
    }

    private async Task LoadAsync(string id, Uri server)
    {
        State = LibraryLoadState.Loading;
        try
        {
            var read = await _collections.GetAsync(server, id);
            if (read.Value.CreatorProfileId != _session.ProfileId || !read.Value.IsManual)
            {
                LoadError = "Only the profile that made this collection can change it here.";
                State = LibraryLoadState.Error;
                return;
            }
            CollectionId = id;
            _version = read.Version;
            _saved = CollectionDraft.Of(read.Value);
            _itemCount = read.Value.ItemCount;
            Show(_saved);
            State = LibraryLoadState.Ready;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not load a collection to edit");
            LoadError = ex is SiloApiException { Status: 404 }
                ? "This collection isn't available. It may have been deleted, or you may not have access to it."
                : "Couldn't load this collection. " + LibraryErrors.Describe(ex);
            State = LibraryLoadState.Error;
        }
    }

    private async Task CreateAsync()
    {
        if (_creating || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        // Not repeatable: a second Create while the first is out would make a second collection.
        _creating = true;
        IsSaving = true;
        SaveError = "";
        try
        {
            var draft = Draft;
            var made = await _collections.CreateAsync(server.BaseUri, draft.ToCreate());
            var staged = Titles.ToList();
            CollectionId = made.Id;
            _saved = CollectionDraft.Of(made);
            var failed = new List<CollectionTitleRow>();
            for (int i = 0; i < staged.Count; i++)
            {
                try
                {
                    await _collections.AddItemAsync(server.BaseUri, made.Id, staged[i].Id, i);
                }
                catch (Exception ex) when (LibraryErrors.IsExpected(ex))
                {
                    _logger.LogWarning(ex, "Could not add a title to a new collection");
                    failed.Add(staged[i]);
                }
            }
            await RefreshVersionAsync(server.BaseUri);
            Show(Draft);
            await LoadTitlesAsync();
            ShowFailedAdds(failed);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not make a collection");
            SaveError = Failure(ex);
            // Not made: Create may be pressed again.
            _creating = CollectionId is not null;
        }
        finally
        {
            IsSaving = false;
        }
    }

    // The draft at the version it was read at; a 412 merges with the collection as it is now and tries once more.
    private async Task UpdateAsync(bool retried)
    {
        if (CollectionId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        IsSaving = true;
        SaveError = "";
        var sent = Draft;
        bool stale = false;
        try
        {
            _version ??= (await _collections.GetAsync(server.BaseUri, id)).Version;
            var saved = await _collections.UpdateAsync(server.BaseUri, id, _version, sent.ToUpdate());
            _version = saved.Version;
            _saved = CollectionDraft.Of(saved.Value);
            DraftChanged();
        }
        catch (SiloApiException ex) when (ex.Status == 412 && !retried)
        {
            _logger.LogInformation("A collection changed elsewhere while it was edited; merging");
            stale = true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not save a collection");
            SaveError = Failure(ex);
        }
        finally
        {
            IsSaving = false;
        }
        if (stale)
        {
            await MergeAndSaveAsync(server.BaseUri, id, sent);
        }
    }

    // Merges what was sent with the collection as it is now; saves again unless a field changed on both sides.
    private async Task MergeAndSaveAsync(Uri server, string id, CollectionDraft sent)
    {
        try
        {
            var now = await _collections.GetAsync(server, id);
            var theirs = CollectionDraft.Of(now.Value);
            var (merged, conflicts) = CollectionDraft.Merge(_saved, sent, theirs);
            _version = now.Version;
            _saved = theirs;
            Show(merged);
            if (conflicts.Count > 0)
            {
                _theirs = theirs;
                Conflicts = conflicts;
                return;
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not read a changed collection to merge");
            SaveError = CollectionEditorText.Stale;
            return;
        }
        await UpdateAsync(retried: true);
    }

    private static string Failure(Exception ex) =>
        ex is SiloApiException { Status: 412 } ? CollectionEditorText.Stale : "Couldn't save · " + LibraryErrors.Describe(ex);

    // After a title changed, the collection's version did too: reads it again. Fields not touched here take what the
    // server has; touched ones stay as typed.
    private async Task RefreshVersionAsync(Uri server)
    {
        if (CollectionId is not { } id)
        {
            return;
        }
        try
        {
            var read = await _collections.GetAsync(server, id);
            var draft = Draft;
            var theirs = CollectionDraft.Of(read.Value);
            var (merged, _) = CollectionDraft.Merge(_saved, draft, theirs);
            _version = read.Version;
            _itemCount = read.Value.ItemCount;
            _saved = theirs;
            Show(merged);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            // The next save reads the version first.
            _logger.LogWarning(ex, "Could not read a collection's version again");
            _version = null;
        }
    }

    private async Task ReadOtherProfilesAsync(Uri server)
    {
        try
        {
            var profiles = await _profiles.ListAsync(server);
            OtherProfiles = [.. profiles.Items.Where(p => p.Id != _session.ProfileId).Select(p => p.Name)];
            CanShare = OtherProfiles.Count > 0;
            OnPropertyChanged(nameof(UnshareWarning));
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not read the login's profiles");
        }
    }

    private async Task ReadCapabilitiesAsync(Uri server)
    {
        try
        {
            _capabilities = await _collections.GetCapabilitiesAsync(server);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not read what collections can do");
        }
    }

    private void Show(CollectionDraft draft)
    {
        _showing = true;
        try
        {
            Name = draft.Name;
            Description = draft.Description;
            Watch = WatchChoices.FirstOrDefault(c => c.Value == draft.ShowOnly.Watch) ?? WatchChoices[0];
            Media = MediaChoices.FirstOrDefault(c => c.Value == draft.ShowOnly.Media) ?? MediaChoices[0];
            IsShared = draft.IsShared;
            OnCollectionsTab = draft.OnCollectionsTab;
        }
        finally
        {
            _showing = false;
        }
        DraftChanged();
    }

    private void DraftChanged()
    {
        if (_showing)
        {
            return;
        }
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(IsSavedShared));
        OnPropertyChanged(nameof(ShowsUnshareWarning));
        OnPropertyChanged(nameof(ChangedFields));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(ShowsSaveBar));
        OnPropertyChanged(nameof(SaveBarMessage));
        OnPropertyChanged(nameof(SaveBarHint));
        OnPropertyChanged(nameof(SaveLabel));
        SaveCommand.NotifyCanExecuteChanged();
    }
}
