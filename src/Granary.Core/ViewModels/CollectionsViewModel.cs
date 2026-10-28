using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>Another profile's shared collections: "by Mara", read-only.</summary>
/// <param name="OpenCommand">Takes the collection's <see cref="MediaCard"/>.</param>
public sealed record SharedCollectionShelf(string OwnerName, IReadOnlyList<MediaCard> Cards, ICommand OpenCommand)
{
    public string Heading => $"by {OwnerName}";

    public string Initial => OwnerName.Length > 0 ? OwnerName[..1].ToUpperInvariant() : "";
}

/// <summary>
/// The Collections page, as Silo's web shows it (<c>pages/Collections.tsx</c>): the profile's own collections in its
/// order, the ones other profiles on the login share with it (by owner, in the login's profile order), and the
/// server's (<see cref="ServerCollectionsViewModel"/>). A collection opens as a list of its titles
/// (<see cref="AppRoute.Collection"/>). Each list fails on its own: the others still show.
/// </summary>
public sealed partial class CollectionsViewModel : ObservableObject
{
    private const string AnotherProfile = "Another profile";

    private readonly ISessionManager _session;
    private readonly CollectionsApi _collections;
    private readonly ProfilesApi _profiles;
    private readonly INavigationService _navigation;
    private readonly IConfirmation _confirmation;
    private readonly ILogger<CollectionsViewModel> _logger;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    private CancellationTokenSource _run = new();

    public CollectionsViewModel(ISessionManager session, CollectionsApi collections, LibraryPageApi pages, ProfilesApi profiles,
        INavigationService navigation, IConfirmation confirmation, ILogger<CollectionsViewModel> logger)
    {
        _session = session;
        _collections = collections;
        _profiles = profiles;
        _navigation = navigation;
        _confirmation = confirmation;
        _logger = logger;
        Server = new ServerCollectionsViewModel(pages, navigation, logger);
    }

    /// <summary>The profile's own collections, in its order.</summary>
    public ObservableCollection<MediaCard> Own { get; } = [];

    public ObservableCollection<SharedCollectionShelf> Shared { get; } = [];

    public ServerCollectionsViewModel Server { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(HasError), nameof(IsReady))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>Why nothing could be shown.</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    /// <summary>Why the profile's own and shared collections are missing while the server's show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwnError), nameof(IsOwnEmpty))]
    public partial string OwnErrorMessage { get; private set; } = "";

    /// <summary>Why the server's collections are missing while the profile's show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerError))]
    public partial string ServerErrorMessage { get; private set; } = "";

    /// <summary>"Yours and the server's.", naming the shared ones too on a login with other profiles.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "Yours and the server's.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnCountText), nameof(IsOwnEmpty), nameof(HasOwn))]
    public partial int OwnCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharedCountText), nameof(HasShared))]
    public partial int SharedCount { get; private set; }

    public string OwnCountText => OwnCount.ToString(CultureInfo.InvariantCulture);

    public string SharedCountText => SharedCount.ToString(CultureInfo.InvariantCulture);

    public bool HasOwn => OwnCount > 0;

    public bool IsOwnEmpty => OwnCount == 0 && !HasOwnError;

    public bool HasShared => SharedCount > 0;

    public bool HasOwnError => OwnErrorMessage.Length > 0;

    public bool HasServerError => ServerErrorMessage.Length > 0;

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    /// <summary>
    /// Loads the page, unless <paramref name="isReturn"/> (Back from a collection) finds it shown for the same profile.
    /// </summary>
    public Task ActivateAsync(bool isReturn)
    {
        bool current = _loadedFor == Key() && State is LibraryLoadState.Ready or LibraryLoadState.Loading;
        return isReturn && current ? Task.CompletedTask : LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        Own.Clear();
        _ownCollections.Clear();
        Notice = "";
        Shared.Clear();
        OwnCount = 0;
        SharedCount = 0;
        Server.Clear();
        OwnErrorMessage = "";
        ServerErrorMessage = "";
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _loadedFor = null;
            State = LibraryLoadState.Idle;
            return;
        }
        _loadedFor = Key();
        State = LibraryLoadState.Loading;
        var personal = _collections.ListAsync(server.BaseUri, token);
        var serverLists = _collections.ListServerAsync(server.BaseUri, token);
        var profiles = _profiles.ListAsync(server.BaseUri, token);
        var (personalList, personalError) = await TryAsync(personal, "personal collections");
        var (serverList, serverError) = await TryAsync(serverLists, "server collections");
        var (profileList, _) = await TryAsync(profiles, "profiles");
        if (token.IsCancellationRequested)
        {
            // Another load took over.
            return;
        }
        if (personalError is not null && serverError is not null)
        {
            ErrorMessage = LibraryErrors.Describe(personalError);
            State = LibraryLoadState.Error;
            return;
        }
        var others = profileList?.Items.Where(p => p.Id != _session.ProfileId).ToList() ?? [];
        _otherProfiles = [.. others.Select(p => p.Name)];
        if (personalList is not null)
        {
            ShowPersonal(personalList, profileList?.Items ?? [], server.BaseUri);
        }
        else
        {
            OwnErrorMessage = LibraryErrors.Describe(personalError!);
        }
        // A one-profile login has nobody to share with.
        Subtitle = others.Count > 0 || SharedCount > 0
            ? "Yours, the ones other profiles share with you, and the server's."
            : "Yours and the server's.";
        if (serverList is not null)
        {
            Server.Show(server.BaseUri, serverList);
        }
        else
        {
            ServerErrorMessage = LibraryErrors.Describe(serverError!);
        }
        State = LibraryLoadState.Ready;

        async Task<(T? Value, Exception? Error)> TryAsync<T>(Task<T> call, string what) where T : class
        {
            try
            {
                return (await call, null);
            }
            catch (Exception ex) when (LibraryErrors.IsExpected(ex))
            {
                if (!token.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not load the {What} of the Collections page", what);
                }
                return (null, ex);
            }
        }
    }

    /// <summary>The editor for a new collection of the profile's own, of the kind the New collection picker chose.</summary>
    [RelayCommand]
    private void NewCollection(string? kind) =>
        _navigation.NavigateTo(AppRoute.CollectionEditor, new CollectionEditorRouteParameter(null, kind ?? PersonalCollectionTypes.Manual));

    /// <summary>Opens one of the profile's own or shared collections.</summary>
    [RelayCommand]
    private void Open(MediaCard? card)
    {
        if (card is { IsPlaceholder: false })
        {
            _navigation.NavigateTo(AppRoute.Collection, new CollectionRouteParameter(CatalogSources.UserCollection, card.Id, card.Title));
        }
    }

    // The profile's own collections in listed order; other profiles' by owner, in the login's profile order, owners no
    // longer listed last.
    private void ShowPersonal(PersonalCollectionList list, IReadOnlyList<Profile> profiles, Uri server)
    {
        string? self = _session.ProfileId;
        var valid = list.Items.Where(c => !string.IsNullOrWhiteSpace(c.Id)).ToList();
        foreach (var collection in valid.Where(c => c.CreatorProfileId == self))
        {
            _ownCollections[collection.Id] = collection;
            Own.Add(CollectionCards.Of(collection, server, isOwn: true));
        }
        int Position(string owner)
        {
            int index = profiles.ToList().FindIndex(p => p.Id == owner);
            return index < 0 ? profiles.Count : index;
        }
        var byOwner = valid.Where(c => c.CreatorProfileId != self)
            .GroupBy(c => c.CreatorProfileId, StringComparer.Ordinal)
            .OrderBy(g => Position(g.Key));
        foreach (var owned in byOwner)
        {
            string name = profiles.FirstOrDefault(p => p.Id == owned.Key)?.Name ?? AnotherProfile;
            Shared.Add(new SharedCollectionShelf(name, [.. owned.Select(c => CollectionCards.Of(c, server, isOwn: false))], OpenCommand));
        }
        OwnCount = Own.Count;
        SharedCount = Shared.Sum(s => s.Cards.Count);
    }

    private (string? ServerId, string? ProfileId) Key() => (_session.ActiveServer?.Id, _session.ProfileId);
}
