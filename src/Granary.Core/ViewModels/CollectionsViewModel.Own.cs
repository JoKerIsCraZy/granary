using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Collections;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// The profile's own collections' ⋯ menu, as the web's list card: Edit collection, Show to other profiles, Delete….
public sealed partial class CollectionsViewModel : ICardMenuSource
{
    private readonly Dictionary<string, PersonalCollection> _ownCollections = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _otherProfiles = [];

    /// <summary>"Shown to other profiles", "Collection deleted", or what failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = "";

    [ObservableProperty]
    public partial bool NoticeFailed { get; private set; }

    public bool HasNotice => Notice.Length > 0;

    public IReadOnlyList<CardMenuEntry> EntriesFor(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!_ownCollections.TryGetValue(card.Id, out var collection))
        {
            return [];
        }
        // The editor takes manual collections; a synced list is changed where it syncs from.
        List<CardMenuEntry> entries = collection.IsManual ? [new("Edit collection", EditCollectionCommand)] : [];
        if (_otherProfiles.Count > 0)
        {
            entries.Add(new("Show to other profiles", ToggleSharingCommand, collection.IsShared));
        }
        entries.Add(new("Delete…", DeleteCollectionCommand));
        return entries;
    }

    [RelayCommand]
    private void EditCollection(MediaCard? card)
    {
        if (card is not null && _ownCollections.TryGetValue(card.Id, out var collection) && collection.IsManual)
        {
            _navigation.NavigateTo(AppRoute.CollectionEditor, new CollectionEditorRouteParameter(card.Id));
        }
    }

    /// <summary>Shares the collection with the login's other profiles at once, or stops sharing it after asking.</summary>
    [RelayCommand]
    private async Task ToggleSharingAsync(MediaCard? card)
    {
        if (card is null || !_ownCollections.TryGetValue(card.Id, out var collection)
            || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        bool share = !collection.IsShared;
        if (!share && !await _confirmation.AskAsync($"Stop sharing {collection.Name}?", CollectionEditorText.UnshareConsequence(_otherProfiles),
                "Stop sharing"))
        {
            return;
        }
        Notice = "";
        try
        {
            var read = await _collections.GetAsync(server.BaseUri, collection.Id);
            var change = CollectionDraft.Of(read.Value) with { IsShared = share };
            await _collections.UpdateAsync(server.BaseUri, collection.Id, read.Version, change.ToUpdate());
            // The list's copy keeps the poster, which a single collection's answer leaves out.
            Replace(collection with { IsShared = share }, server.BaseUri);
            Say(share ? "Shown to other profiles" : "Only you see it now", failed: false);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not change a collection's sharing");
            Say("Couldn't change sharing", failed: true);
        }
    }

    /// <summary>Deletes the collection after asking, at the version it has now.</summary>
    [RelayCommand]
    private async Task DeleteCollectionAsync(MediaCard? card)
    {
        if (card is null || !_ownCollections.TryGetValue(card.Id, out var collection)
            || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        Notice = "";
        try
        {
            var read = await _collections.GetAsync(server.BaseUri, collection.Id);
            if (!await _confirmation.AskAsync($"Delete “{collection.Name}”?", CollectionEditorText.DeleteDescription(read.Value.IsShared), "Delete"))
            {
                return;
            }
            await _collections.DeleteAsync(server.BaseUri, collection.Id, read.Version);
            _ownCollections.Remove(collection.Id);
            if (Own.FirstOrDefault(c => c.Id == collection.Id) is { } shown)
            {
                Own.Remove(shown);
            }
            OwnCount = Own.Count;
            Say("Collection deleted", failed: false);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not delete a collection");
            Say(ex is SiloApiException { Status: 412 } ? CollectionEditorText.Stale : "Failed to delete", failed: true);
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = "";

    private void Replace(PersonalCollection collection, Uri server)
    {
        _ownCollections[collection.Id] = collection;
        int index = Own.ToList().FindIndex(c => c.Id == collection.Id);
        if (index >= 0)
        {
            Own[index] = CollectionCards.Of(collection, server, isOwn: true);
        }
    }

    private void Say(string notice, bool failed)
    {
        NoticeFailed = failed;
        Notice = notice;
    }
}
