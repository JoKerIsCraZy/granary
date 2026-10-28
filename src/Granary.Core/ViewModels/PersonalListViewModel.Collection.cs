using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Collections;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// A profile's own collection on its page, as the web's CollectionPageActions: Edit, and ⋯ Delete…. Other profiles'
// collections and the server's have neither.
public sealed partial class PersonalListViewModel
{
    private string? _collectionVersion;
    private bool _collectionShared;

    /// <summary>The collection shown is the profile's own: ⋯ Delete… is there.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCollectionCommand))]
    public partial bool IsOwnCollection { get; private set; }

    /// <summary>The profile's own manual collection: Edit is there too.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCollectionCommand))]
    public partial bool CanEditCollection { get; private set; }

    /// <summary>A delete that failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollectionNotice))]
    public partial string CollectionNotice { get; private set; } = "";

    public bool HasCollectionNotice => CollectionNotice.Length > 0;

    [RelayCommand(CanExecute = nameof(CanEditCollection))]
    private void EditCollection()
    {
        if (_collection is { } collection)
        {
            _navigation.NavigateTo(AppRoute.CollectionEditor, new CollectionEditorRouteParameter(collection.CollectionId));
        }
    }

    /// <summary>Deletes the collection after asking, then goes to Collections.</summary>
    [RelayCommand(CanExecute = nameof(IsOwnCollection))]
    private async Task DeleteCollectionAsync()
    {
        if (_collection is not { } collection || _collections is null || _confirmation is null
            || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        CollectionNotice = "";
        try
        {
            // Read now: an edit since the page showed changed the version, and maybe the sharing the question names.
            var read = await _collections.GetAsync(server.BaseUri, collection.CollectionId);
            _collectionVersion = read.Version;
            _collectionShared = read.Value.IsShared;
            if (!await _confirmation.AskAsync($"Delete “{read.Value.Name}”?", CollectionEditorText.DeleteDescription(_collectionShared), "Delete"))
            {
                return;
            }
            await _collections.DeleteAsync(server.BaseUri, collection.CollectionId, _collectionVersion);
            _navigation.ShowSection(AppRoute.Collections);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not delete a collection from its page");
            // A version changed meanwhile is read again for the next try.
            _collectionVersion = null;
            CollectionNotice = ex is SiloApiException { Status: 412 } ? CollectionEditorText.Stale : "Failed to delete";
        }
    }

    // Whose collection it is, and the version a delete sends back: read for a personal collection, once per showing.
    private async Task ReadCollectionAsync(CollectionRouteParameter collection)
    {
        IsOwnCollection = false;
        CanEditCollection = false;
        _collectionVersion = null;
        CollectionNotice = "";
        if (collection.Source != CatalogSources.UserCollection || _collections is null
            || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            var read = await _collections.GetAsync(server.BaseUri, collection.CollectionId);
            if (_collection != collection)
            {
                return;
            }
            _collectionVersion = read.Version;
            _collectionShared = read.Value.IsShared;
            IsOwnCollection = read.Value.CreatorProfileId == _session.ProfileId;
            CanEditCollection = IsOwnCollection && read.Value.IsManual;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            // Without it the page shows the titles, without Edit and Delete.
            _logger.LogWarning(ex, "Could not read whose collection this is");
        }
    }
}
