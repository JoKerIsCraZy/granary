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
/// A profile's own manual or smart collection made or changed, as the web's collection editor
/// (<c>components/collections/editor/CollectionEditor.tsx</c>): name and description, a manual collection's Show only
/// and titles or a smart collection's rules with their live preview, and where it shows. Everything but a manual
/// collection's titles is a draft the save bar saves (or, for a new one, Create collection makes); titles save as they
/// are added, removed or moved. A save is sent at the version the collection was read at: when it changed
/// elsewhere meanwhile, the two are merged field by field, and fields changed on both sides wait for the viewer's pick.
/// </summary>
public sealed partial class CollectionEditorViewModel : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly CollectionsApi _collections;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly ProfilesApi _profiles;
    private readonly VisibleLibraries _visibleLibraries;
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
    // Writes and version reads go one at a time: each starts from the version and positions the last one left.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CollectionEditorViewModel(ISessionManager session, CollectionsApi collections, CatalogApi catalog, ImageParameterCache images,
        ProfilesApi profiles, VisibleLibraries visibleLibraries, INavigationService navigation, TimeProvider time, ILogger<CollectionEditorViewModel> logger)
    {
        _session = session;
        _collections = collections;
        _catalog = catalog;
        _images = images;
        _profiles = profiles;
        _visibleLibraries = visibleLibraries;
        _navigation = navigation;
        _time = time;
        _logger = logger;
        Watch = WatchChoices[0];
        Media = MediaChoices[0];
        Rules.Changed += OnRulesChanged;
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
    public CollectionDraft Draft => IsSmart
        ? new(Name.Trim(), Description.Trim(), CollectionShowOnly.Everything, IsShared, OnCollectionsTab, PersonalCollectionTypes.Smart, Rules.ToQuery(),
            SmartSortConfig.Key(Rules.SortConfig))
        : new(Name.Trim(), Description.Trim(), new CollectionShowOnly(Watch.Value, Media.Value), IsShared, OnCollectionsTab);

    /// <summary>The fields the save bar names: the draft's that changed, then a staged poster.</summary>
    public IReadOnlyList<string> ChangedFields => IsNew ? [] : [.. Draft.ChangedFrom(_saved), .. IsPosterStaged ? ["Poster"] : Array.Empty<string>()];

    /// <summary>Leaving now would lose something: a changed field, or for a new collection anything at all.</summary>
    public bool HasUnsavedChanges => !_deleted && (IsNew ? !Draft.SameAs(_saved) || Titles.Count > 0 || IsPosterStaged : ChangedFields.Count > 0);

    /// <summary>A new collection's bar is always there; a saved one's while something changed or a save failed.</summary>
    public bool ShowsSaveBar => IsReady && (IsNew || ChangedFields.Count > 0 || HasSaveError);

    /// <summary>"Not created yet", or "Name and description not saved".</summary>
    public string SaveBarMessage => IsNew ? CollectionEditorText.NotCreatedYet : CollectionEditorText.NotSaved(ChangedFields);

    public string SaveBarHint => !IsNew ? (IsSmart ? "" : CollectionEditorText.TitlesAlreadySaved)
        : Name.Trim().Length == 0 ? CollectionEditorText.NameItThenCreate
        : IsSmart ? SmartCreateHint()
        : Titles.Count > 0 ? CollectionEditorText.TitlesReadyToAdd(Titles.Count)
        : "";

    public string SaveLabel => IsSaving ? (IsNew ? "Creating..." : "Saving...") : HasSaveError ? "Try again" : IsNew ? "Create collection" : "Save";

    public IReadOnlyList<string> OtherProfiles { get; private set; } = [];

    /// <summary>
    /// Opens the collection to edit it, or a new one of <paramref name="kind"/> (manual unless it says
    /// <see cref="PersonalCollectionTypes.Smart"/>) when <paramref name="collectionId"/> is null.
    /// </summary>
    public async Task ActivateAsync(string? collectionId, string? kind = null)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        ResetSmart();
        ResetLook();
        var profiles = ReadOtherProfilesAsync(server.BaseUri);
        var capabilities = ReadCapabilitiesAsync(server.BaseUri);
        if (collectionId is null)
        {
            CollectionId = null;
            Kind = kind == PersonalCollectionTypes.Smart ? PersonalCollectionTypes.Smart : PersonalCollectionTypes.Manual;
            if (IsSmart)
            {
                await ReadSmartContextAsync(server.BaseUri);
            }
            _saved = IsSmart ? CollectionDraft.EmptySmart : CollectionDraft.Empty;
            Show(_saved);
            State = LibraryLoadState.Ready;
            TitlesStatus = CollectionEditorText.NotCreatedYet;
        }
        else
        {
            await LoadAsync(collectionId, server.BaseUri);
            if (IsSmart && IsReady)
            {
                // The libraries and the fields the server takes arrived after the rules showed: in place, with their names.
                await ReadSmartContextAsync(server.BaseUri);
                Rules.ShowContext(_libraries);
            }
        }
        await profiles;
        await capabilities;
        // Whether the poster card shows, and what a missing poster falls back to, come with the capabilities.
        LookChanged();
        if (IsCreated && IsReady && CollectionId is { } id)
        {
            await ReadListedPosterAsync(server.BaseUri, id, CancellationToken.None);
        }
        if (IsCreated && IsReady && !IsSmart)
        {
            await LoadTitlesAsync();
        }
        SchedulePreview(TimeSpan.Zero);
    }

    [RelayCommand]
    private Task RetryAsync() => SerializedAsync(async () =>
    {
        if (CollectionId is { } id && _session is { State: SessionState.Ready, ActiveServer: { } server })
        {
            await LoadAsync(id, server.BaseUri);
            if (IsReady && !IsSmart)
            {
                await LoadTitlesAsync();
            }
            SchedulePreview(TimeSpan.Zero);
        }
    });

    /// <summary>Makes the new collection, or saves the changed one.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SerializedAsync(IsNew ? CreateAsync : UpdateAsync);

    private bool CanSave() => !IsSaving && !HasConflict && Name.Trim().Length > 0 && (IsNew || ChangedFields.Count > 0 || HasSaveError);

    /// <summary>Puts the fields back as saved.</summary>
    [RelayCommand]
    private void Discard()
    {
        Show(_saved);
        Conflicts = [];
        _theirs = null;
        SaveError = "";
        IsPastingLink = false;
        Stage(null);
    }

    /// <summary>Changed in both places: takes the other side's values for those fields.</summary>
    [RelayCommand]
    private void UseTheirs()
    {
        if (_theirs is { } theirs)
        {
            Show(Draft.Taking(theirs, Conflicts));
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
    public Task DeleteAsync() => SerializedAsync(DeleteNowAsync);

    private async Task DeleteNowAsync()
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

    partial void OnStateChanged(LibraryLoadState value)
    {
        OnPropertyChanged(nameof(ShowsSaveBar));
        OnPropertyChanged(nameof(ShowsLook));
    }

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
            if (read.Value.CreatorProfileId != _session.ProfileId || !(read.Value.IsManual || read.Value.IsSmart))
            {
                LoadError = "Only the profile that made this collection can change it here.";
                State = LibraryLoadState.Error;
                return;
            }
            CollectionId = id;
            Kind = read.Value.IsSmart ? PersonalCollectionTypes.Smart : PersonalCollectionTypes.Manual;
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
            // A link goes in the create; a file is put once the collection exists.
            var made = await _collections.CreateAsync(server.BaseUri, draft.ToCreate() with { PosterSourceUrl = StagedPoster?.Link });
            var staged = Titles.ToList();
            // Titles added from here on wait for these, and go after them.
            _maxPosition = staged.Count - 1;
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
            if (!IsSmart)
            {
                await LoadTitlesAsync();
                ShowFailedAdds(failed);
            }
            await SavePosterAsync(server.BaseUri, made.Id, justCreated: true);
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

    // The draft at the version it was read at. A 412 merges with the collection as it is now and sends the merge once
    // more; a field changed on both sides waits for the viewer's pick instead. Save stays busy throughout.
    private async Task UpdateAsync()
    {
        if (CollectionId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        IsSaving = true;
        SaveError = "";
        try
        {
            // Only the poster changed: the collection is as saved, so only the poster goes, and Retry moves no version.
            if (IsPosterStaged && Draft.SameAs(_saved))
            {
                await SavePosterAsync(server.BaseUri, id, justCreated: false);
                return;
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    _version ??= (await _collections.GetAsync(server.BaseUri, id)).Version;
                    var saved = await _collections.UpdateAsync(server.BaseUri, id, _version, Draft.ToUpdate());
                    _version = saved.Version.Length > 0 ? saved.Version : null;
                    _saved = CollectionDraft.Of(saved.Value);
                    DraftChanged();
                    // The poster after the collection: one that fails stays staged, and the collection is saved.
                    await SavePosterAsync(server.BaseUri, id, justCreated: false);
                    return;
                }
                catch (SiloApiException ex) when (ex.Status == 412 && attempt == 0)
                {
                    _logger.LogInformation("A collection changed elsewhere while it was edited; merging");
                    var now = await _collections.GetAsync(server.BaseUri, id);
                    if (AdoptRemote(CollectionDraft.Of(now.Value), now.Version).Count > 0)
                    {
                        return;
                    }
                }
            }
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
    }

    // The collection as the server has it now becomes the base: fields not touched here take its values, touched ones
    // stay as typed, and fields changed on both sides wait for Use theirs or Keep mine. Answers those fields.
    private IReadOnlyList<string> AdoptRemote(CollectionDraft theirs, string version)
    {
        var (merged, conflicts) = CollectionDraft.Merge(_saved, Draft, theirs);
        _version = version;
        _saved = theirs;
        Show(merged);
        if (conflicts.Count > 0)
        {
            _theirs = theirs;
            Conflicts = conflicts;
        }
        return conflicts;
    }

    private async Task SerializedAsync(Func<Task> work)
    {
        await _gate.WaitAsync();
        try
        {
            await work();
        }
        finally
        {
            _gate.Release();
        }
    }

    // A smart collection's libraries, rules and saved sort, as the save bar names them.
    private static readonly string[] SmartFields = ["Libraries", "Rules", "Order"];

    // Builds the Rules card from a draft, with the libraries to pick from.
    private void ShowRules(CollectionDraft draft)
    {
        Rules.Show(draft.Rules ?? SmartQuery.Empty, ParseSortConfig(draft.SortConfig), _libraries);
        _rulesShown = true;
    }

    private static System.Text.Json.JsonElement ParseSortConfig(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
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
            _itemCount = read.Value.ItemCount;
            AdoptRemote(CollectionDraft.Of(read.Value), read.Version);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            // The last version stays: a save at it answers 412 and merges, rather than overwriting what changed.
            _logger.LogWarning(ex, "Could not read a collection's version again");
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
            // Only what differs: a box being typed in keeps its text (and its trailing space).
            if (Name.Trim() != draft.Name)
            {
                Name = draft.Name;
            }
            if (Description.Trim() != draft.Description)
            {
                Description = draft.Description;
            }
            Watch = WatchChoices.FirstOrDefault(c => c.Value == draft.ShowOnly.Watch) ?? WatchChoices[0];
            Media = MediaChoices.FirstOrDefault(c => c.Value == draft.ShowOnly.Media) ?? MediaChoices[0];
            IsShared = draft.IsShared;
            OnCollectionsTab = draft.OnCollectionsTab;
            // The rules too only when they differ: rebuilding them would take a rule being typed in away.
            if (draft.IsSmart && (!_rulesShown || Draft.ChangedFrom(draft).Intersect(SmartFields).Any()))
            {
                ShowRules(draft);
            }
        }
        finally
        {
            _showing = false;
        }
        DraftChanged();
        // Rules put back (Discard, Use theirs, a merge) are previewed too.
        SchedulePreview(PreviewDelay);
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
