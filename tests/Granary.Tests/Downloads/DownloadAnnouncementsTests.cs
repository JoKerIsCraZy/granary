using Granary.Core.Downloads;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Which downloads finished or failed between two looks, and how that is said: a film by its title, an episode as the Apple
/// app names it, a season or a series that finishes together in one message, a failure with its reason, in the Apple app's
/// words where it has them.
/// </summary>
public sealed class DownloadAnnouncementsTests
{
    private readonly DownloadAnnouncements _announcements = new();

    private static DownloadItem Film(string id, DownloadState state, string title = "Example Movie", DownloadFailure failure = DownloadFailure.None,
        bool gone = false, bool paused = false, bool missing = false) =>
        new(id, "mv_" + id, title, null, null, null, null, state, failure, paused, gone, missing, false, ExpectedBytes: 1000,
            BytesDone: 0, PreparationProgress: null, PosterPath: null, MediaFileId: null);

    private static DownloadItem Episode(string id, int season, int episode, DownloadState state, string seriesId = "sr_1",
        string seriesTitle = "Example Series", DownloadFailure failure = DownloadFailure.None, bool paused = false, bool missing = false,
        bool gone = false) =>
        new(id, "ep_" + id, "Chapter " + episode, seriesId, seriesTitle, season, episode, state, failure, paused, gone, missing, false,
            ExpectedBytes: 1000, BytesDone: 0, PreparationProgress: null, PosterPath: null, MediaFileId: null);

    private IReadOnlyList<DownloadAnnouncement> Look(params DownloadItem[] items) => _announcements.Look(Scope7, items);

    [Fact]
    public void AFilmThatFinishes_IsAnnouncedOnce_ByItsTitle()
    {
        Assert.Empty(Look(Film("dl_1", DownloadState.Downloading)));

        var announced = Assert.Single(Look(Film("dl_1", DownloadState.Completed)));

        Assert.Equal(new DownloadAnnouncement("Example Movie is ready to watch offline", IsFailure: false, PlayDownloadId: "dl_1"), announced);
        Assert.Empty(Look(Film("dl_1", DownloadState.Completed)));
    }

    [Fact]
    public void WhatWasThereAlready_IsNotAnnounced()
    {
        // The first look, as Granary starts: finished and failed downloads are old news.
        Assert.Empty(Look(Film("dl_1", DownloadState.Completed), Film("dl_2", DownloadState.Failed, failure: DownloadFailure.Server)));
        // A download that shows up done (its folder came back) did not finish now either.
        Assert.Empty(Look(Film("dl_1", DownloadState.Completed), Film("dl_2", DownloadState.Failed, failure: DownloadFailure.Server),
            Film("dl_3", DownloadState.Completed)));
    }

    [Fact]
    public void ADownloadWhoseFolderComesBack_IsNotAnnounced()
    {
        // Granary started while the drive was away: each download is the row the index knew, queued, its files missing.
        Look(Film("dl_1", DownloadState.Queued, missing: true), Film("dl_2", DownloadState.Queued, missing: true));

        // The drive is back, and the folders' own records say how they ended, long ago.
        Assert.Empty(Look(Film("dl_1", DownloadState.Completed), Film("dl_2", DownloadState.Failed, failure: DownloadFailure.Server)));
    }

    [Fact]
    public void ASingleEpisode_IsNamedAsTheAppleAppNamesIt()
    {
        Look(Episode("dl_5", 2, 8, DownloadState.Downloading));

        var announced = Assert.Single(Look(Episode("dl_5", 2, 8, DownloadState.Completed)));

        Assert.Equal("Example Series (S2E8) is ready to watch offline", announced.Text);
        Assert.Equal("dl_5", announced.PlayDownloadId);
    }

    [Fact]
    public void ASeasonThatFinishesTogether_IsOneMessage_ThatPlaysItsFirstEpisode()
    {
        Look(Episode("dl_3", 1, 3, DownloadState.Queued), Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Downloading));

        Assert.Empty(Look(Episode("dl_3", 1, 3, DownloadState.Queued), Episode("dl_1", 1, 1, DownloadState.Completed),
            Episode("dl_2", 1, 2, DownloadState.Downloading)));
        Assert.Empty(Look(Episode("dl_3", 1, 3, DownloadState.Downloading), Episode("dl_1", 1, 1, DownloadState.Completed),
            Episode("dl_2", 1, 2, DownloadState.Completed)));
        var announced = Assert.Single(Look(Episode("dl_3", 1, 3, DownloadState.Completed), Episode("dl_1", 1, 1, DownloadState.Completed),
            Episode("dl_2", 1, 2, DownloadState.Completed)));

        Assert.Equal(new DownloadAnnouncement("3 episodes of Example Series are ready to watch offline", IsFailure: false, PlayDownloadId: "dl_1"),
            announced);
    }

    [Fact]
    public void ASeriesAcrossSeasons_IsOneMessage_ItsSpecialsPlayedLast()
    {
        Look(Episode("dl_s", 0, 1, DownloadState.Queued), Episode("dl_21", 2, 1, DownloadState.Queued), Episode("dl_11", 1, 1, DownloadState.Queued));

        var announced = Assert.Single(Look(Episode("dl_s", 0, 1, DownloadState.Completed), Episode("dl_21", 2, 1, DownloadState.Completed),
            Episode("dl_11", 1, 1, DownloadState.Completed)));

        Assert.Equal("3 episodes of Example Series are ready to watch offline", announced.Text);
        Assert.Equal("dl_11", announced.PlayDownloadId);
    }

    [Fact]
    public void TwoSeriesAndAFilm_AreEachTheirOwnMessage()
    {
        Look(Episode("dl_a", 1, 1, DownloadState.Queued), Episode("dl_b", 1, 1, DownloadState.Queued, seriesId: "sr_2", seriesTitle: "Another Series"),
            Film("dl_m", DownloadState.Queued));

        var announced = Look(Episode("dl_a", 1, 1, DownloadState.Completed),
            Episode("dl_b", 1, 1, DownloadState.Completed, seriesId: "sr_2", seriesTitle: "Another Series"), Film("dl_m", DownloadState.Completed));

        // In the order the downloads come.
        Assert.Equal(["Example Series (S1E1) is ready to watch offline", "Another Series (S1E1) is ready to watch offline",
            "Example Movie is ready to watch offline"], announced.Select(a => a.Text));
    }

    [Theory]
    [InlineData(DownloadFailure.NotEnoughSpace, false, "Example Movie didn't download: Not enough storage on this PC")]
    [InlineData(DownloadFailure.Integrity, false, "Example Movie didn't download: The file arrived incomplete")]
    [InlineData(DownloadFailure.Server, true, "Example Movie didn't download: No longer available on the server")]
    [InlineData(DownloadFailure.Server, false, "Example Movie didn't download")]
    public void AFailure_IsAnnouncedWithItsReason_WhenOneIsKnown(DownloadFailure failure, bool gone, string expected)
    {
        Look(Film("dl_1", DownloadState.Downloading));

        var announced = Assert.Single(Look(Film("dl_1", DownloadState.Failed, failure: failure, gone: gone)));

        Assert.Equal(new DownloadAnnouncement(expected, IsFailure: true, PlayDownloadId: null), announced);
    }

    [Fact]
    public void ASeasonWithAFailure_SaysBothInOneMessage()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Queued), Episode("dl_2", 1, 2, DownloadState.Queued), Episode("dl_3", 1, 3, DownloadState.Queued));

        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Failed, failure: DownloadFailure.NotEnoughSpace),
            Episode("dl_2", 1, 2, DownloadState.Completed), Episode("dl_3", 1, 3, DownloadState.Downloading)));
        var announced = Assert.Single(Look(Episode("dl_1", 1, 1, DownloadState.Failed, failure: DownloadFailure.NotEnoughSpace),
            Episode("dl_2", 1, 2, DownloadState.Completed), Episode("dl_3", 1, 3, DownloadState.Completed)));

        Assert.Equal(new DownloadAnnouncement("2 episodes of Example Series are ready to watch offline; 1 didn't download", IsFailure: true,
            PlayDownloadId: "dl_2"), announced);
    }

    [Fact]
    public void ASeasonThatOnlyFailed_SaysHowMany()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Queued), Episode("dl_2", 1, 2, DownloadState.Queued));

        var announced = Assert.Single(Look(Episode("dl_1", 1, 1, DownloadState.Failed, failure: DownloadFailure.Server),
            Episode("dl_2", 1, 2, DownloadState.Failed, failure: DownloadFailure.Server)));

        Assert.Equal(new DownloadAnnouncement("2 episodes of Example Series didn't download", IsFailure: true, PlayDownloadId: null), announced);
    }

    [Fact]
    public void OneEpisodeDone_AndOneFailed_NamesTheDoneOne()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Queued), Episode("dl_2", 1, 2, DownloadState.Queued));

        var announced = Assert.Single(Look(Episode("dl_1", 1, 1, DownloadState.Completed),
            Episode("dl_2", 1, 2, DownloadState.Failed, failure: DownloadFailure.Server)));

        Assert.Equal("Example Series (S1E1) is ready to watch offline; 1 didn't download", announced.Text);
    }

    [Fact]
    public void AnEpisodeThatFailed_AndFinishedAfterARetry_CountsAsFinished()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Queued));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Failed, failure: DownloadFailure.Integrity), Episode("dl_2", 1, 2, DownloadState.Downloading)));
        // Retry: queued again, then whole.
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Queued), Episode("dl_2", 1, 2, DownloadState.Downloading)));

        var announced = Assert.Single(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Completed)));

        Assert.Equal(new DownloadAnnouncement("2 episodes of Example Series are ready to watch offline", IsFailure: false, PlayDownloadId: "dl_1"),
            announced);
    }

    [Fact]
    public void ASeasonHeldBack_WhenTheProfileChanges_IsNotSaidForTheNextProfile()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Queued));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Queued)));

        Assert.Empty(_announcements.Look(Scope8, [Film("dl_8", DownloadState.Downloading)]));
        Assert.Equal(["Example Movie is ready to watch offline"],
            _announcements.Look(Scope8, [Film("dl_8", DownloadState.Completed)]).Select(a => a.Text));
    }

    [Fact]
    public void AnEpisodePausedByTheViewer_DoesNotHoldTheSeasonBack()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Queued));

        var announced = Assert.Single(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Queued, paused: true)));

        Assert.Equal("Example Series (S1E1) is ready to watch offline", announced.Text);
    }

    [Fact]
    public void ADeletedDownload_IsNotAnnounced_ButWhatFinishedBeforeIt_Is()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Queued));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Queued)));

        var announced = Assert.Single(Look(Episode("dl_1", 1, 1, DownloadState.Completed)));

        Assert.Equal("Example Series (S1E1) is ready to watch offline", announced.Text);
        Assert.Empty(Look());
    }

    [Fact]
    public void AnEpisodeDeletedWhileItsSeasonWasHeldBack_IsNotCounted_NorPlayed()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Queued));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Queued)));
        // The viewer deletes the finished one while the other is still to come.
        Assert.Empty(Look(Episode("dl_2", 1, 2, DownloadState.Downloading)));

        var announced = Assert.Single(Look(Episode("dl_2", 1, 2, DownloadState.Completed)));

        Assert.Equal(new DownloadAnnouncement("Example Series (S1E2) is ready to watch offline", IsFailure: false, PlayDownloadId: "dl_2"), announced);
    }

    [Fact]
    public void AnEpisodeThatFinishedWhileItsSeasonWasHeldBack_IsNeitherCountedNorPlayed_OnceItCanNoLongerPlay()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Downloading),
            Episode("dl_3", 1, 3, DownloadState.Downloading));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Downloading),
            Episode("dl_3", 1, 3, DownloadState.Downloading)));
        // Its file goes, as when it is deleted by hand, while the others still come.
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed, missing: true), Episode("dl_2", 1, 2, DownloadState.Completed),
            Episode("dl_3", 1, 3, DownloadState.Downloading)));

        var announced = Assert.Single(Look(Episode("dl_1", 1, 1, DownloadState.Completed, missing: true), Episode("dl_2", 1, 2, DownloadState.Completed),
            Episode("dl_3", 1, 3, DownloadState.Completed)));

        Assert.Equal(new DownloadAnnouncement("2 episodes of Example Series are ready to watch offline", IsFailure: false, PlayDownloadId: "dl_2"),
            announced);
    }

    [Fact]
    public void ASeasonHeldBack_WhoseDriveGoesAway_SaysNothing()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Downloading));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Downloading)));

        // Neither can play now, and the other no longer comes: nothing is ready to watch.
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed, missing: true), Episode("dl_2", 1, 2, DownloadState.Downloading, missing: true)));
    }

    [Fact]
    public void AnEpisodeThatFailedWhileItsSeasonWasHeldBack_AndWasTriedAgain_IsNotSaidToHaveFailed()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Downloading));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Failed, failure: DownloadFailure.Integrity), Episode("dl_2", 1, 2, DownloadState.Downloading)));

        // Retry, and the viewer pauses it; the other finishes.
        var announced = Assert.Single(Look(Episode("dl_1", 1, 1, DownloadState.Queued, paused: true), Episode("dl_2", 1, 2, DownloadState.Completed)));

        Assert.Equal(new DownloadAnnouncement("Example Series (S1E2) is ready to watch offline", IsFailure: false, PlayDownloadId: "dl_2"), announced);
    }

    [Fact]
    public void AnEpisodeHeldBack_IsSaidAsItIsWhenTheMessageComes()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Downloading));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Failed, failure: DownloadFailure.Server), Episode("dl_2", 1, 2, DownloadState.Downloading)));

        // Meanwhile the server says it no longer has it; the other is paused.
        var announced = Assert.Single(Look(Episode("dl_1", 1, 1, DownloadState.Failed, failure: DownloadFailure.Server, gone: true),
            Episode("dl_2", 1, 2, DownloadState.Queued, paused: true)));

        Assert.Equal("Example Series (S1E1) didn't download: No longer available on the server", announced.Text);
    }

    [Fact]
    public void ASeasonWhoseHeldEpisodesWereAllDeleted_SaysNothing()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Downloading), Episode("dl_2", 1, 2, DownloadState.Queued));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Queued)));

        Assert.Empty(Look(Episode("dl_2", 1, 2, DownloadState.Queued, paused: true)));
    }

    [Fact]
    public void AnotherProfile_StartsAfresh()
    {
        Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Downloading));
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Downloading)));

        // Profile 8's downloads, finished or not, are its own: nothing of profile 7's is said for it.
        Assert.Empty(_announcements.Look(Scope8, [Film("dl_8", DownloadState.Completed), Film("dl_9", DownloadState.Downloading)]));
        var announced = Assert.Single(_announcements.Look(Scope8, [Film("dl_8", DownloadState.Completed), Film("dl_9", DownloadState.Completed)]));
        Assert.Equal("dl_9", announced.PlayDownloadId);

        // Back on profile 7, its own look starts again too.
        Assert.Empty(Look(Episode("dl_1", 1, 1, DownloadState.Completed), Episode("dl_2", 1, 2, DownloadState.Completed)));
        Assert.Empty(_announcements.Look(null, []));
    }

    [Fact]
    public void AnEpisodeWithoutNumbersOrSeriesTitle_IsNamedByWhatItHas()
    {
        var bare = new DownloadItem("dl_1", "ep_1", "Pilot", "sr_1", null, null, null, DownloadState.Downloading, DownloadFailure.None, false, false,
            false, false, ExpectedBytes: 1000, BytesDone: 0, PreparationProgress: null, PosterPath: null, MediaFileId: null);
        Look(bare, bare with { DownloadId = "dl_2" });

        Assert.Empty(Look(bare with { State = DownloadState.Completed }, bare with { DownloadId = "dl_2" }));
        var announced = Assert.Single(Look(bare with { State = DownloadState.Completed }, bare with { DownloadId = "dl_2", State = DownloadState.Completed }));

        Assert.Equal("2 episodes are ready to watch offline", announced.Text);
        Look(bare);
        Assert.Equal("Pilot is ready to watch offline", Assert.Single(Look(bare with { State = DownloadState.Completed })).Text);
    }
}
