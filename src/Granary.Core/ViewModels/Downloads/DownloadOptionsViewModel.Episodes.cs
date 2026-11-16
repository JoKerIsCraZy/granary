using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Downloads;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// Which episodes a season's or a series' One time starts. "Only unwatched episodes" leaves out the ones the viewer watched,
/// so a viewer half-way through the seventh episode gets the seventh to the end: it shows only while one of the episodes left
/// is watched, and then starts on. Monitor keeps its own rules.
/// </summary>
public sealed partial class DownloadOptionsViewModel
{
    public static string OnlyUnwatchedLabel => AppText.Get("granary.downloadOptions.onlyUnwatched");

    /// <summary>"Only unwatched episodes" is on: the episodes the viewer watched are not started.</summary>
    [ObservableProperty]
    public partial bool OnlyUnwatched { get; set; } = true;

    /// <summary>The switch shows: what is chosen is a season's or a series' episodes, and one of them is watched.</summary>
    [ObservableProperty]
    public partial bool ShowsOnlyUnwatched { get; private set; }

    // The switch counts and starts again what the offer chooses; its last error says nothing about the new choice.
    partial void OnOnlyUnwatchedChanged(bool value)
    {
        if (_offer is null)
        {
            return;
        }
        ErrorText = "";
        Choose();
        ShowSize();
    }

    // The offer as the choices take it: a season's or a series' episodes without the watched ones while the switch is on.
    private DownloadOffer? Chosen()
    {
        ShowsOnlyUnwatched = _offer is SeasonDownloadOffer { HasWatched: true } or SeriesDownloadOffer { HasWatched: true };
        return _offer switch
        {
            SeasonDownloadOffer { HasWatched: true } season when OnlyUnwatched => season.Unwatched(),
            SeriesDownloadOffer { HasWatched: true } series when OnlyUnwatched => series.Unwatched(),
            var offer => offer,
        };
    }
}
