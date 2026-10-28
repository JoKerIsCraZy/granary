using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Collections;

// The Look card, as the web's LookPanel and ArtworkFields for a personal collection: the poster it shows (an image of
// its own, the collage of its titles, or none), and an image file, a link or the collage staged until Save. The
// poster saves after the collection; one that fails stays staged with Retry, and the collection still counts as saved.
public sealed partial class CollectionEditorViewModel
{
    /// <summary>How long a save waits for the collage of a collection with titles and no poster.</summary>
    public static readonly TimeSpan CollageWait = TimeSpan.FromSeconds(15);

    /// <summary>How often the list is read while waiting for the collage.</summary>
    public static readonly TimeSpan CollagePoll = TimeSpan.FromSeconds(3);

    // The poster as the Collections list has it (a single read carries none), and whether it is the collage.
    private Uri? _listedPoster;
    private bool _listedIsCollage;
    private int _listedItemCount;
    // Counts the list reads: only the newest one's answer is shown.
    private int _listedReads;
    private CancellationTokenSource? _collageRun;

    /// <summary>The poster the next save sends: a file, a link, or the image taken away; null while nothing is staged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPosterStaged), nameof(PosterState), nameof(LookSummary), nameof(PosterCaption), nameof(HasPosterCaption),
        nameof(PosterPreviewUrl), nameof(PosterPreviewImage), nameof(HasPosterPreview), nameof(CanClearPoster), nameof(StagedPosterText),
        nameof(ShowsStagedPoster))]
    public partial PosterChange? StagedPoster { get; private set; }

    /// <summary>Why the poster didn't save; it stays staged, with Retry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPosterError), nameof(PosterErrorText), nameof(ShowsStagedPoster))]
    public partial string PosterError { get; private set; } = "";

    /// <summary>A file or link that can't be a poster: too big, another type, not a web address.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPosterPickError))]
    public partial string PosterPickError { get; private set; } = "";

    /// <summary>The card shows its tile and menu; closed it is one line with "Change".</summary>
    [ObservableProperty]
    public partial bool IsLookOpen { get; set; }

    /// <summary>"Paste a link…": the link box shows.</summary>
    [ObservableProperty]
    public partial bool IsPastingLink { get; private set; }

    /// <summary>The link box's text.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UsePosterLinkCommand))]
    public partial string PosterLink { get; set; } = "";

    /// <summary>The server takes a poster for a collection (its <c>artwork</c> capability).</summary>
    public bool ShowsLook => IsReady && _capabilities.TakesArtwork;

    /// <summary>Without its own poster the collection shows a collage of its titles.</summary>
    public bool UsesCollage => _capabilities.PosterCollages;

    public bool IsPosterStaged => StagedPoster is not null;

    public bool HasPosterError => PosterError.Length > 0;

    public bool HasPosterPickError => PosterPickError.Length > 0;

    public string PosterErrorText => $"Couldn't save the poster. {PosterError}";

    /// <summary>What the poster will show after Save: none, awaiting-collage, collage, new or saved.</summary>
    public string PosterState => CollectionArtwork.State(StagedPoster, SavedPoster, UsesCollage, CollagePoster);

    /// <summary>"Poster: a collage of its titles", for the closed card.</summary>
    public string LookSummary => CollectionArtwork.Summary(PosterState);

    /// <summary>Under the tile: "Collage of its titles", "A collage once its titles have posters", "No poster", or nothing.</summary>
    public string PosterCaption => CollectionArtwork.Caption(PosterState);

    public bool HasPosterCaption => PosterCaption.Length > 0;

    /// <summary>The image the tile shows from a link, the saved poster or the collage; null for a chosen file or none.</summary>
    public Uri? PosterPreviewUrl => StagedPoster switch
    {
        { File: not null } => null,
        { Link: { } link } => Uri.TryCreate(link, UriKind.Absolute, out var url) ? url : null,
        { Remove: true } => null,
        _ => SavedPoster ?? (UsesCollage ? CollagePoster : null),
    };

    /// <summary>A chosen file's bytes, for the tile to show before Save.</summary>
    public byte[]? PosterPreviewImage => StagedPoster?.File;

    /// <summary>The tile has an image to show; otherwise it shows the collage's or no poster's mark.</summary>
    public bool HasPosterPreview => PosterPreviewImage is not null || PosterPreviewUrl is not null;

    /// <summary>"Use the collage" (or "Remove poster") is offered for a new or saved image.</summary>
    public bool CanClearPoster => PosterState is CollectionArtwork.New or CollectionArtwork.Saved;

    public string ClearPosterLabel => UsesCollage ? "Use the collage" : "Remove poster";

    /// <summary>"The new poster saves when you press Save." with Undo.</summary>
    public string StagedPosterText => StagedPoster is { IsRemovalOnly: true }
        ? "The poster is removed when you save."
        : "The new poster saves when you press Save.";

    public bool ShowsStagedPoster => IsPosterStaged && !HasPosterError;

    // An image someone chose and saved, never the server's collage.
    private Uri? SavedPoster => _listedIsCollage ? null : _listedPoster;

    private Uri? CollagePoster => _listedIsCollage ? _listedPoster : null;

    /// <summary>
    /// Whether a file of <paramref name="length"/> bytes and <paramref name="contentType"/> can be a poster, before it is
    /// read; says why not in <see cref="PosterPickError"/>.
    /// </summary>
    public bool CanTakePosterFile(long length, string contentType)
    {
        PosterPickError = CollectionArtwork.FileProblem(length, contentType);
        return PosterPickError.Length == 0;
    }

    /// <summary>A chosen image file, staged for Save. Answers false (with <see cref="PosterPickError"/>) for one the server would refuse.</summary>
    public bool UsePosterFile(byte[] image, string fileName, string contentType)
    {
        ArgumentNullException.ThrowIfNull(image);
        PosterPickError = CollectionArtwork.FileProblem(image.Length, contentType);
        if (PosterPickError.Length > 0)
        {
            return false;
        }
        Stage(PosterChange.FromFile(image, fileName, contentType));
        return true;
    }

    [RelayCommand]
    private void OpenLook() => IsLookOpen = true;

    /// <summary>Done: the card closes to its line.</summary>
    [RelayCommand]
    private void CloseLook()
    {
        IsLookOpen = false;
        IsPastingLink = false;
    }

    /// <summary>"Paste a link…": the link box, with the link staged so far.</summary>
    [RelayCommand]
    private void PastePosterLink()
    {
        PosterLink = StagedPoster?.Link ?? "";
        PosterPickError = "";
        IsPastingLink = true;
    }

    [RelayCommand]
    private void CancelPosterLink()
    {
        IsPastingLink = false;
        PosterPickError = "";
    }

    /// <summary>"Use link": an absolute http(s) address, which the server fetches on Save.</summary>
    [RelayCommand(CanExecute = nameof(CanUsePosterLink))]
    private void UsePosterLink()
    {
        string link = PosterLink.Trim();
        if (!CollectionArtwork.IsWebAddress(link))
        {
            PosterPickError = CollectionArtwork.NotAWebAddress;
            return;
        }
        Stage(PosterChange.FromLink(link));
        IsPastingLink = false;
    }

    private bool CanUsePosterLink() => PosterLink.Trim().Length > 0;

    /// <summary>"Use the collage" or "Remove poster": the saved image goes on Save, or a chosen one is dropped.</summary>
    [RelayCommand]
    private void ClearPoster() => Stage(SavedPoster is not null ? PosterChange.Removal : null);

    /// <summary>Drops the staged poster: the card shows what is saved.</summary>
    [RelayCommand]
    private void UndoPoster() => Stage(null);

    private void Stage(PosterChange? change)
    {
        StagedPoster = change;
        PosterError = "";
        PosterPickError = "";
        DraftChanged();
    }

    /// <summary>The page went: the collage wait, a list read on its way and the smart preview stop.</summary>
    public void Deactivate()
    {
        StopCollageWait();
        _listedReads++;
        CancelPreview();
    }

    // A visit starts with nothing staged and the card closed; a read from the last visit is dropped.
    private void ResetLook()
    {
        StopCollageWait();
        _listedReads++;
        _listedPoster = null;
        _listedIsCollage = false;
        _listedItemCount = 0;
        StagedPoster = null;
        PosterError = "";
        PosterPickError = "";
        IsLookOpen = false;
        IsPastingLink = false;
        LookChanged();
    }

    // After the collection saved: the staged poster, then the poster as the list has it now. A link a new collection
    // was made with went in its create already.
    private async Task SavePosterAsync(Uri server, string id, bool justCreated)
    {
        PosterChange? saved = null;
        if (StagedPoster is { } change && !(justCreated && change.File is null))
        {
            try
            {
                if (change.File is { } file)
                {
                    await _collections.SetPosterAsync(server, id, file, change.FileName ?? "poster", change.ContentType ?? "image/jpeg");
                }
                else if (change.Link is { } link)
                {
                    await _collections.SetPosterFromLinkAsync(server, id, link);
                }
                else
                {
                    await _collections.RemovePosterAsync(server, id);
                }
                saved = change;
                // The poster moves the collection's version: the next save goes at the new one.
                await RefreshVersionAsync(server);
            }
            catch (Exception ex) when (LibraryErrors.IsExpected(ex))
            {
                _logger.LogWarning(ex, "Could not save a collection's poster");
                PosterError = LibraryErrors.Describe(ex);
                IsLookOpen = true;
            }
        }
        else if (StagedPoster is { } created)
        {
            saved = created;
        }
        // The staged image shows until the list names the saved one, so the old poster doesn't flash in between.
        await ReadListedPosterAsync(server, id, CancellationToken.None);
        if (saved is not null)
        {
            Settle(saved);
        }
        WaitForCollage(server, id);
    }

    // A change saved: unstaged, unless another was chosen meanwhile.
    private void Settle(PosterChange change)
    {
        if (ReferenceEquals(StagedPoster, change))
        {
            StagedPoster = null;
        }
        PosterError = "";
        DraftChanged();
    }

    // The poster lives on the list's copy of the collection. The list isn't paged (GET /api/v2/collections takes no
    // limit or cursor), so the collection is in it, as the web's useListedPoster finds it. Only the newest read counts:
    // one that a later read, another visit or the page's leaving overtook is dropped.
    private async Task ReadListedPosterAsync(Uri server, string id, CancellationToken token)
    {
        if (!_capabilities.TakesArtwork)
        {
            return;
        }
        int read = ++_listedReads;
        try
        {
            var list = await _collections.ListAsync(server, token);
            if (read != _listedReads || token.IsCancellationRequested)
            {
                return;
            }
            if (list.Items.FirstOrDefault(c => c.Id == id) is { } listed && CollectionId == id)
            {
                _listedPoster = ArtworkUrl.Resolve(server, listed.PosterUrl);
                _listedIsCollage = _listedPoster is not null && listed.PosterIsCollage;
                _listedItemCount = listed.ItemCount;
                LookChanged();
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                // The card shows what it knew; the next save reads it again.
                _logger.LogWarning(ex, "Could not read a collection's poster");
            }
        }
    }

    // A collection with titles and no poster gets a collage soon after a save: the list is read until it shows, for a while.
    private void WaitForCollage(Uri server, string id)
    {
        StopCollageWait();
        if (!UsesCollage || !_capabilities.TakesArtwork || _listedPoster is not null || _listedItemCount == 0)
        {
            return;
        }
        var run = new CancellationTokenSource();
        _collageRun = run;
        CollageWaiting = WaitForCollageAsync(server, id, run.Token);
    }

    private void StopCollageWait()
    {
        _collageRun?.Cancel();
        _collageRun?.Dispose();
        _collageRun = null;
    }

    /// <summary>The wait for a collage a save set off (for tests).</summary>
    internal Task CollageWaiting { get; private set; } = Task.CompletedTask;

    private async Task WaitForCollageAsync(Uri server, string id, CancellationToken token)
    {
        try
        {
            var started = _time.GetUtcNow();
            while (!token.IsCancellationRequested && _listedPoster is null && _time.GetUtcNow() - started < CollageWait)
            {
                await Task.Delay(CollagePoll, _time, token);
                await ReadListedPosterAsync(server, id, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A later save, another visit or the page's leaving ended the wait.
        }
        catch (Exception ex)
        {
            // Nobody awaits this wait: whatever goes wrong in it is logged, not lost.
            _logger.LogError(ex, "Waiting for a collection's collage failed");
        }
    }

    private void LookChanged()
    {
        OnPropertyChanged(nameof(ShowsLook));
        OnPropertyChanged(nameof(UsesCollage));
        OnPropertyChanged(nameof(PosterState));
        OnPropertyChanged(nameof(LookSummary));
        OnPropertyChanged(nameof(PosterCaption));
        OnPropertyChanged(nameof(HasPosterCaption));
        OnPropertyChanged(nameof(PosterPreviewUrl));
        OnPropertyChanged(nameof(HasPosterPreview));
        OnPropertyChanged(nameof(CanClearPoster));
        OnPropertyChanged(nameof(ClearPosterLabel));
    }
}

/// <summary>
/// A poster staged in the editor: an image file, a link the server fetches, or the saved image taken away. A file or a
/// link wins over a removal.
/// </summary>
public sealed record PosterChange
{
    public static PosterChange Removal { get; } = new() { Remove = true };

    public byte[]? File { get; private init; }

    public string? FileName { get; private init; }

    public string? ContentType { get; private init; }

    public string? Link { get; private init; }

    public bool Remove { get; private init; }

    public bool IsRemovalOnly => Remove && File is null && Link is null;

    public static PosterChange FromFile(byte[] file, string fileName, string contentType) =>
        new() { File = file, FileName = fileName, ContentType = contentType };

    public static PosterChange FromLink(string link) => new() { Link = link };
}

/// <summary>The Look card's words and rules, as the web's <c>copy.ts</c> and <c>artworkPreview.ts</c> have them.</summary>
public static class CollectionArtwork
{
    public const string None = "none";

    public const string AwaitingCollage = "awaiting-collage";

    public const string Collage = "collage";

    public const string New = "new";

    public const string Saved = "saved";

    public const string NotAWebAddress = "Paste a link that starts with https:// or http://.";

    /// <summary>What a poster shows after Save: a staged image, the saved one, the collage, one to come, or none.</summary>
    public static string State(PosterChange? staged, Uri? saved, bool collages, Uri? collage)
    {
        if (staged is { File: not null } or { Link: not null })
        {
            return New;
        }
        if (saved is not null && staged is not { Remove: true })
        {
            return Saved;
        }
        if (!collages)
        {
            return None;
        }
        // Removing an image brings a collage that isn't made yet.
        return collage is not null && staged is not { Remove: true } ? Collage : AwaitingCollage;
    }

    public static string Summary(string state) => "Poster: " + state switch
    {
        AwaitingCollage => "a collage once its titles have posters",
        Collage => "a collage of its titles",
        New => "a new image",
        Saved => "an image",
        _ => "none",
    };

    public static string Caption(string state) => state switch
    {
        Collage => "Collage of its titles",
        AwaitingCollage => "A collage once its titles have posters",
        None => "No poster",
        _ => "",
    };

    /// <summary>Why the server would refuse this file as a poster; empty when it takes it.</summary>
    public static string FileProblem(long length, string contentType)
    {
        if (!CollectionsApi.PosterTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            return "Choose a JPEG, PNG or WebP image.";
        }
        if (length == 0)
        {
            return "That image is empty.";
        }
        return length > CollectionsApi.PosterMaxBytes ? "Choose an image of 10 MB or less." : "";
    }

    /// <summary>An absolute http or https address: what the server fetches a poster from.</summary>
    public static bool IsWebAddress(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
        && url.Host.Length > 0;
}
