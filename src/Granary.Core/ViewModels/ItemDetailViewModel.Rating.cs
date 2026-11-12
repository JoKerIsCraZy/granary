using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels;

// The profile's stars for a movie or series, as the web's StarRating: a star sets that rating, the current star clears it.
// The stars change at once; the app's one writer for the title sends them (RatingWrites). Once its writes are done the page
// shows what the server keeps, so a refused click goes back with the reason, unless a newer click came after it, as
// Android's setRating keeps only the newest write's outcome. The writes are made for the profile the title was read for.
// Every showing of a title's page loads it (the page is not cached), and a load takes writes still on their way, or ones
// that ended while it read, over its own answer.
public sealed partial class ItemDetailViewModel
{
    // Whose rating of what this page shows; null for a title without stars. A newer load makes a new one.
    private RatingTarget? _ratingTarget;

    // What the page knows the server keeps: the rating it read, then each end of its writes.
    private int _ratingKept;

    /// <summary>Movies and series take the profile's stars, as on the web; seasons and episodes do not.</summary>
    [ObservableProperty]
    public partial bool CanRate { get; private set; }

    /// <summary>The profile's stars for the title as the page shows them, 1 to 5; 0 when it has none.</summary>
    [ObservableProperty]
    public partial int UserRating { get; private set; }

    /// <summary>
    /// Rates the title with <paramref name="stars"/> (1 to 5), or clears the rating when they are the current ones. Ends once
    /// the title's writes are all done.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RateAsync(int stars)
    {
        if (_ratingTarget is not { } target || _ratings is not { } ratings || stars is < RatingSet.Min or > RatingSet.Max)
        {
            return;
        }
        int rating = stars == UserRating ? 0 : stars;
        ActionError = "";
        UserRating = rating;
        ShowSettled(target, await ratings.RateAsync(target, rating));
    }

    /// <param name="mark">Where the writes' ends stood when the read began (<see cref="RatingWrites.Mark"/>).</param>
    private void ShowRating(CatalogItemDetail item, Uri server, string? profileId, long mark)
    {
        bool takesStars = item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Series;
        _ratingKept = item.UserRating is int stars and >= RatingSet.Min and <= RatingSet.Max ? stars : 0;
        _ratingTarget = takesStars && _ratings is not null && profileId is { Length: > 0 } ? new RatingTarget(server, profileId, _contentId) : null;
        CanRate = _ratingTarget is not null;
        UserRating = CanRate ? _ratingKept : 0;
        if (_ratingTarget is not { } target || _ratings is not { } ratings)
        {
            return;
        }
        if (ratings.InFlight(target) is { } inFlight)
        {
            // The title's writes from a page left meanwhile are still on their way: their newest click shows, not the
            // answer read alongside them, and the page follows them to their end.
            UserRating = inFlight.Latest;
            _ = FollowRatingAsync(target, inFlight.Settled);
        }
        else if (ratings.KeptSince(target, mark) is { } kept)
        {
            // They ended while the page read: the answer may be from before them.
            _ratingKept = kept;
            UserRating = kept;
        }
    }

    private async Task FollowRatingAsync(RatingTarget target, Task<RatingSettled> settled) => ShowSettled(target, await settled);

    // Not for a title loaded since, nor while newer clicks are on their way: their end shows instead.
    private void ShowSettled(RatingTarget target, RatingSettled settled)
    {
        if (!ReferenceEquals(target, _ratingTarget) || _ratings?.InFlight(target) is not null)
        {
            return;
        }
        // None of the writes stuck: the server keeps what the page knew.
        _ratingKept = settled.Kept ?? _ratingKept;
        UserRating = _ratingKept;
        if (settled.Failure is { } failure)
        {
            ActionError = DescribeWriteFailure(DetailWrite.Rating, failure);
        }
    }

    private void ResetRating()
    {
        _ratingTarget = null;
        _ratingKept = 0;
        CanRate = false;
        UserRating = 0;
    }
}
