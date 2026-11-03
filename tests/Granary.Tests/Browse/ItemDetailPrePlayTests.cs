using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Detail;
using Granary.Tests.Profiles;
using static Granary.Tests.Browse.ItemDetailFixture;

namespace Granary.Tests.Browse;

/// <summary>
/// The Version, Audio and Subs pickers under a movie's or episode's buttons, as on Silo's web detail page, and what Play
/// passes on (docs/ui-design.md, "Detail pages").
/// </summary>
public sealed class ItemDetailPrePlayTests
{
    private const string Movie = PrePlayPayloads.MovieId;
    private const string AudioPrefs = "api/v2/audio-prefs/" + Movie;
    private const string SubtitlePrefs = "api/v2/subtitle-prefs/" + Movie;

    private readonly ItemDetailFixture _fixture = new();

    private Task<ItemDetailViewModel> LoadAsync(params Func<Task<HttpResponseMessage>>[] item)
    {
        _fixture.Get(ItemPath(Movie), item.Length > 0 ? item : [Ok(PrePlayPayloads.Movie)])
            .Get(SimilarPath(Movie), Ok("""{ "items": [] }"""));
        _fixture.Silo.On(HttpMethod.Put, AudioPrefs, ScriptedSilo.NoContent());
        _fixture.Silo.On(HttpMethod.Delete, AudioPrefs, ScriptedSilo.NoContent());
        _fixture.Silo.On(HttpMethod.Put, SubtitlePrefs, ScriptedSilo.NoContent());
        _fixture.Silo.On(HttpMethod.Delete, SubtitlePrefs, ScriptedSilo.NoContent());
        return _fixture.LoadAsync(Movie);
    }

    private (AppRoute Route, object? Parameter) Played => _fixture.Navigation.Navigations[^1];

    private string SentBody(HttpMethod method, string path) => _fixture.Silo.SentTo(method, path).Single().Body!;

    [Fact]
    public async Task TwoFiles_AreListedLikeTheWeb_WithTheOnePlayedLastChosen()
    {
        var detail = await LoadAsync();

        var pickers = detail.Pickers;
        Assert.True(pickers.ShowsVersions);
        Assert.Equal(
            [("501", "2160p · HEVC · HDR10 · DTS", "HDR10", "38.9 GB · Remux", false), ("502", "1080p · H264 · DTS", "", "8.6 GB · BluRay", true)],
            pickers.Versions.Select(v => (v.FileId, v.Summary, v.RangeLabel, v.Detail, v.IsSelected)));
        Assert.Equal("1080p · H264 · DTS", pickers.VersionSummary);
        Assert.Equal("Version, 1080p · H264 · DTS", pickers.VersionName);
        // The picker names the files; the line under the buttons is for a single file.
        Assert.Equal("", detail.AvailableVersionLine);
    }

    [Fact]
    public async Task AudioAndSubs_ShowWhatPlaybackPicksByItself()
    {
        var detail = await LoadAsync();

        var pickers = detail.Pickers;
        Assert.Equal("Auto: English · DTS · 5.1", pickers.AudioSummary);
        Assert.Equal([(null, "Auto", "English · DTS · 5.1", true), (0, "English", "", false)],
            pickers.Audio.Select(a => (a.Ordinal, a.Title, a.Description, a.IsSelected)));
        Assert.Equal(("DTS", "5.1", true), (pickers.Audio[1].Codec, pickers.Audio[1].Channels, pickers.Audio[1].IsDefault));
        Assert.Equal("Auto: English · SRT", pickers.SubtitleSummary);
        Assert.Null(pickers.Choice);
    }

    [Fact]
    public async Task PlayWithoutAPick_LeavesTheChoiceToPlayback()
    {
        var detail = await LoadAsync();

        detail.PlayCommand.Execute(null);

        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(Movie)), Played);
    }

    [Fact]
    public async Task APickedVersion_IsPlayedPinned_AndBringsItsOwnTracks()
    {
        var detail = await LoadAsync();

        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);

        var pickers = detail.Pickers;
        Assert.Equal("2160p · HEVC · HDR10 · DTS", pickers.VersionSummary);
        Assert.True(pickers.Versions[0].IsSelected);
        Assert.Equal("Auto: English · DTS · 7.1", pickers.AudioSummary);
        Assert.Equal(["Auto", "English", "German"], pickers.Audio.Select(a => a.Title));
        Assert.Equal("Deutsch · 5.1(side)", pickers.Audio[2].Description);
        detail.PlayCommand.Execute(null);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(Movie, new PrePlayChoice("501", IsVersionPicked: true))), Played);
        detail.StartOverCommand.Execute(null);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.FromStart(Movie, new PrePlayChoice("501", IsVersionPicked: true))), Played);
    }

    [Fact]
    public async Task SubtitleRows_AreGroupedAndOrderedLikeTheWeb()
    {
        var detail = await LoadAsync();
        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);

        var rows = detail.Pickers.Subtitles;

        Assert.Equal(
        [
            (DetailSubtitleKind.Auto, "Auto", "English", "", "", true),
            (DetailSubtitleKind.Off, "Off", "", "", "", false),
            (DetailSubtitleKind.Track, "English", "", "SRT", "Embedded", false),
            (DetailSubtitleKind.Track, "German", "", "PGS", "", false),
            (DetailSubtitleKind.Track, "English", "", "SRT", "External", false),
        ], rows.Select(r => (r.Kind, r.Title, r.Description, r.Format, r.Section, r.IsSelected)));
        // External tracks count first in the start request's ordinals.
        Assert.Equal([-1, -1, 2, 1, 0], rows.Select(r => r.SelectionIndex));
        // A title that only says "Forced" or "SDH" is a flag.
        Assert.Equal((true, false, false), (rows[3].IsForced, rows[3].IsHearingImpaired, rows[3].IsDefault));
        Assert.Equal((false, true), (rows[4].IsForced, rows[4].IsHearingImpaired));
        Assert.True(rows[2].IsDefault);
    }

    [Fact]
    public async Task AnAudioPick_PlaysAndIsSavedForTheTitle()
    {
        var detail = await LoadAsync();
        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);

        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[2]);

        Assert.Equal("German · AC3 · 5.1", detail.Pickers.AudioSummary);
        Assert.True(detail.Pickers.Audio[2].IsSelected);
        Assert.Equal("""{"audio_track_index":1,"audio_language":"ger","track_signature":{"language":"ger","title":"Deutsch","codec":"ac3","layout":"5.1(side)","channels":6}}""",
            SentBody(HttpMethod.Put, AudioPrefs));
        // The item is read again, so Auto shows what the server picks now.
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, ItemPath(Movie)).Count);
        detail.PlayCommand.Execute(null);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(Movie, new PrePlayChoice("501", true, AudioTrack: 1))), Played);
    }

    [Fact]
    public async Task TrackPicksOnTheFileThatPlaysAnyway_GoWithIt_WithoutPinningIt()
    {
        var detail = await LoadAsync();

        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[1]);
        detail.PlayCommand.Execute(null);

        Assert.Equal("Off", detail.Pickers.SubtitleSummary);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(Movie, new PrePlayChoice("502", false, SubtitleTrack: -1))), Played);
        Assert.Equal("""{"subtitle_language":"","subtitle_track_index":-1,"subtitle_mode":"off"}""", SentBody(HttpMethod.Put, SubtitlePrefs));
    }

    [Fact]
    public async Task ASubtitlePick_IsSavedWithTheFilesOwnCodecAndLabel()
    {
        var detail = await LoadAsync();
        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);

        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[4]);

        Assert.Equal("English · SRT", detail.Pickers.SubtitleSummary);
        Assert.True(detail.Pickers.Subtitles[4].IsSelected);
        Assert.False(detail.Pickers.Subtitles[0].IsSelected);
        Assert.Equal(
            """{"subtitle_language":"eng","subtitle_track_index":0,"subtitle_mode":"always","track_signature":{"source":"external","language":"eng","codec":"srt","label":"SDH","forced":false,"hearing_impaired":true}}""",
            SentBody(HttpMethod.Put, SubtitlePrefs));
        detail.PlayCommand.Execute(null);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(Movie, new PrePlayChoice("501", true, SubtitleTrack: 0))), Played);
    }

    [Fact]
    public async Task Auto_ForgetsTheSavedPicks()
    {
        var detail = await LoadAsync();
        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[1]);

        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[0]);
        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[0]);

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Delete, AudioPrefs));
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Delete, SubtitlePrefs));
        Assert.Equal("Auto: English · DTS · 5.1", detail.Pickers.AudioSummary);
        Assert.Null(detail.Pickers.Choice);
    }

    [Fact]
    public async Task AnotherVersion_StartsItsTracksFromAuto_WithoutForgettingWhatIsSaved()
    {
        var detail = await LoadAsync();
        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);
        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[2]);
        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[1]);

        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[1]);

        Assert.Equal("Auto: English · DTS · 5.1", detail.Pickers.AudioSummary);
        Assert.Equal("Auto: English · SRT", detail.Pickers.SubtitleSummary);
        Assert.Equal(new PrePlayChoice("502", true), detail.Pickers.Choice);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Delete, AudioPrefs));
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Delete, SubtitlePrefs));
    }

    [Fact]
    public async Task PickingTheChosenVersionAgain_KeepsItsTracks()
    {
        var detail = await LoadAsync();
        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[1]);

        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[1]);

        Assert.Equal(new PrePlayChoice("502", true, AudioTrack: 0), detail.Pickers.Choice);
    }

    [Fact]
    public async Task ARememberedSubtitle_ShowsAsTheChoice_UntilAutoForgetsIt()
    {
        var detail = await LoadAsync(Ok(PrePlayPayloads.Remembered), Ok(PrePlayPayloads.Movie));

        Assert.Equal("English · SRT", detail.Pickers.SubtitleSummary);
        Assert.Equal(("Auto", "Reset to profile defaults", false), (detail.Pickers.Subtitles[0].Title, detail.Pickers.Subtitles[0].Description,
            detail.Pickers.Subtitles[0].IsSelected));
        Assert.True(detail.Pickers.Subtitles[2].IsSelected);
        // Remembered by the server, it needs no pick of its own for Play.
        Assert.Null(detail.Pickers.Choice);

        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[0]);

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Delete, SubtitlePrefs));
        Assert.Equal("Auto: English · SRT", detail.Pickers.SubtitleSummary);
        Assert.True(detail.Pickers.Subtitles[0].IsSelected);
        Assert.False(detail.Pickers.Subtitles[2].IsSelected);
    }

    [Fact]
    public async Task AFailedSave_SaysSo_AndThePickStillPlays()
    {
        var detail = await LoadAsync();
        _fixture.Silo.On(HttpMethod.Put, AudioPrefs, ScriptedSilo.Problem(500, "internal"));

        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[1]);

        // The web's words for it (useSetAudioPreference).
        Assert.Equal("Failed to save audio preference", detail.ActionError);
        Assert.Equal("English · DTS · 5.1", detail.Pickers.AudioSummary);
        Assert.Equal(new PrePlayChoice("502", false, AudioTrack: 0), detail.Pickers.Choice);

        _fixture.Silo.On(HttpMethod.Put, AudioPrefs, ScriptedSilo.NoContent());
        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[1]);
        Assert.Equal("", detail.ActionError);
    }

    [Fact]
    public async Task AnAutoTheServerRefused_StaysAuto_AndPlaysWhatAutoShows()
    {
        var detail = await LoadAsync(Ok(PrePlayPayloads.Remembered));
        _fixture.Silo.On(HttpMethod.Delete, SubtitlePrefs, ScriptedSilo.Problem(500, "internal"));

        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[0]);

        Assert.Equal("Failed to reset subtitle preference", detail.ActionError);
        // Read again, the item still remembers the track; the page keeps the viewer's Auto all the same.
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, ItemPath(Movie)).Count);
        Assert.Equal("Auto: English · SRT", detail.Pickers.SubtitleSummary);
        Assert.True(detail.Pickers.Subtitles[0].IsSelected);
        Assert.False(detail.Pickers.Subtitles[2].IsSelected);
        // The start would apply the remembered track: it is told the automatic one instead.
        Assert.Equal(new PrePlayChoice("502", false, SubtitleTrack: 0), detail.Pickers.Choice);
    }

    [Fact]
    public async Task AFailedReadAfterAuto_LeavesNoBareAuto()
    {
        var detail = await LoadAsync();
        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);
        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[2]);
        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[1]);
        _fixture.Get(ItemPath(Movie), Busy());

        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[0]);
        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[0]);

        // Not read again: Auto shows what the item last read says it picks.
        Assert.Equal("Auto: English · DTS · 7.1", detail.Pickers.AudioSummary);
        Assert.Equal("English · DTS · 7.1", detail.Pickers.Audio[0].Description);
        Assert.Equal("Auto: English · SRT", detail.Pickers.SubtitleSummary);
        Assert.Equal("English", detail.Pickers.Subtitles[0].Description);
    }

    [Fact]
    public async Task AFailedAudioAuto_ShowsTheTrackTheServerKeeps()
    {
        var detail = await LoadAsync();
        _fixture.Silo.On(HttpMethod.Delete, AudioPrefs, ScriptedSilo.Problem(500, "internal"));
        _fixture.Get(ItemPath(Movie), Busy());

        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[0]);

        Assert.Equal("Failed to reset audio preference", detail.ActionError);
        Assert.Equal("Auto: English · DTS · 5.1", detail.Pickers.AudioSummary);
    }

    [Fact]
    public async Task PlayWhileAutoIsSaved_PlaysTheAutomaticSubtitle_NotTheRememberedOne()
    {
        var detail = await LoadAsync(Ok(PrePlayPayloads.Remembered));
        var (held, gate) = Held();
        _fixture.Silo.On(HttpMethod.Delete, SubtitlePrefs, held);

        var resetting = detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[0]);
        detail.PlayCommand.Execute(null);

        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(Movie, new PrePlayChoice("502", false, SubtitleTrack: 0))), Played);
        gate.SetResult(await ScriptedSilo.Problem(500, "internal")());
        await resetting;
        detail.PlayCommand.Execute(null);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(Movie, new PrePlayChoice("502", false, SubtitleTrack: 0))), Played);
    }

    [Fact]
    public async Task PlayAfterARefusedAutoAndAFailedRead_PlaysTheAutomaticSubtitle()
    {
        var detail = await LoadAsync(Ok(PrePlayPayloads.Remembered));
        _fixture.Silo.On(HttpMethod.Delete, SubtitlePrefs, ScriptedSilo.Problem(500, "internal"));
        _fixture.Get(ItemPath(Movie), Busy());

        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[0]);
        detail.PlayCommand.Execute(null);

        Assert.Equal("Auto: English · SRT", detail.Pickers.SubtitleSummary);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(Movie, new PrePlayChoice("502", false, SubtitleTrack: 0))), Played);
    }

    [Fact]
    public async Task Auto_ShowsOnlyAuto_UntilTheServerIsReadAgain()
    {
        var detail = await LoadAsync();
        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);
        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[2]);
        var (held, gate) = Held();
        _fixture.Silo.On(HttpMethod.Delete, AudioPrefs, held);

        var resetting = detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[0]);

        // What the server picks without the saved track is not known yet.
        Assert.Equal("Auto", detail.Pickers.AudioSummary);
        Assert.Equal(("Auto", "", true), (detail.Pickers.Audio[0].Title, detail.Pickers.Audio[0].Description, detail.Pickers.Audio[0].IsSelected));
        gate.SetResult(NoContentResponse());
        await resetting;
        Assert.Equal("Auto: English · DTS · 7.1", detail.Pickers.AudioSummary);
    }

    [Fact]
    public async Task AnOlderReadLandingLast_DoesNotBringBackWhatAutoForgot()
    {
        var (olderRead, olderGate) = Held();
        var detail = await LoadAsync(Ok(PrePlayPayloads.Remembered), olderRead, Ok(PrePlayPayloads.Movie));

        // Off is saved, and the item read again: that answer is held back.
        var off = detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[1]);
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Get, ItemPath(Movie)).Count == 2);
        // Auto is saved and read again, and that answer comes first.
        await detail.SelectSubtitleCommand.ExecuteAsync(detail.Pickers.Subtitles[0]);
        Assert.Equal("Auto: English · SRT", detail.Pickers.SubtitleSummary);

        olderGate.SetResult(await Ok(PrePlayPayloads.Remembered)());
        await off;

        Assert.Equal("Auto: English · SRT", detail.Pickers.SubtitleSummary);
        Assert.True(detail.Pickers.Subtitles[0].IsSelected);
    }

    [Fact]
    public async Task ALateSave_LeavesAPageTheViewerHasLeftAlone()
    {
        var detail = await LoadAsync();
        var (held, gate) = Held();
        _fixture.Silo.On(HttpMethod.Put, AudioPrefs, held);
        var saving = detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[1]);
        _fixture.Movie();
        await detail.LoadCommand.ExecuteAsync(ItemDetailPayloads.MovieId);

        gate.SetResult(await ScriptedSilo.Problem(500, "internal")());
        await saving;

        Assert.Equal("", detail.ActionError);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, ItemPath(Movie)));
        Assert.Equal("Masterplan", detail.Title);
        Assert.Equal("301", detail.Pickers.Version?.FileId);
        Assert.Null(detail.Pickers.Choice);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not come true.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // The episode page with two audio tracks on its first file; its picks are kept for its series.
    private const string SeriesAudioPrefs = "api/v2/audio-prefs/" + ItemDetailPayloads.SeriesId;

    private Task<ItemDetailViewModel> LoadEpisodeWithAudioAsync()
    {
        string episode = ItemDetailPayloads.Episode.Replace("\"file_id\": \"901\",",
            "\"file_id\": \"901\", \"audio_tracks\": [{ \"language\": \"fre\", \"codec\": \"truehd\", \"channels\": 8, \"default\": true }, { \"language\": \"eng\", \"codec\": \"eac3\", \"channels\": 6 }],",
            StringComparison.Ordinal);
        _fixture.Episode().Get(ItemPath(ItemDetailPayloads.EpisodeId), Ok(episode));
        _fixture.Silo.On(HttpMethod.Put, SeriesAudioPrefs, ScriptedSilo.NoContent());
        return _fixture.LoadAsync(ItemDetailPayloads.EpisodeId);
    }

    [Fact]
    public async Task APageReadsItsOwnItemAgain_WhileAnotherPagesSaveIsQueued()
    {
        // A title page kept behind the one the viewer is on: both save through the app's one queue.
        var behind = await LoadAsync();
        var current = await LoadEpisodeWithAudioAsync();
        var (held, gate) = Held();
        _fixture.Silo.On(HttpMethod.Delete, AudioPrefs, held);

        var reset = behind.SelectAudioCommand.ExecuteAsync(behind.Pickers.Audio[0]);
        var pick = current.SelectAudioCommand.ExecuteAsync(current.Pickers.Audio[2]);
        gate.SetResult(NoContentResponse());
        await reset;
        await pick;

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, ItemPath(Movie)).Count);
        Assert.Equal("Auto: English · DTS · 5.1", behind.Pickers.AudioSummary);
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, ItemPath(ItemDetailPayloads.EpisodeId)).Count);
    }

    [Fact]
    public async Task AnOlderSaveFailingLate_DoesNotSpeakForTheNewerPick()
    {
        var detail = await LoadAsync();
        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);
        var (held, gate) = Held();
        _fixture.Silo.On(HttpMethod.Put, AudioPrefs, held, ScriptedSilo.NoContent());

        var first = detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[1]);
        var second = detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[2]);
        gate.SetResult(await ScriptedSilo.Problem(500, "internal")());
        await first;
        await second;

        Assert.Equal("", detail.ActionError);
        Assert.Equal("German · AC3 · 5.1", detail.Pickers.AudioSummary);
        Assert.Equal(new PrePlayChoice("501", true, AudioTrack: 1), detail.Pickers.Choice);
    }

    [Theory]
    [InlineData("/media/movies/Night Harbor (2025)/Night.Harbor.2025.en.srt")]
    [InlineData(@"D:\\Media\\Night Harbor (2025)\\Night.Harbor.2025.en.srt")]
    public async Task AnExternalSubtitlesPath_ShowsAndSavesOnlyItsName(string path)
    {
        string movie = PrePlayPayloads.Movie.Replace("\"title\": \"SDH\", \"external\": true, \"file_name\": \"Night.Harbor.2025.en.sdh.srt\"",
            $"\"external\": true, \"file_name\": \"{path}\"", StringComparison.Ordinal);
        Assert.Contains("Media", movie, StringComparison.OrdinalIgnoreCase);
        var detail = await LoadAsync(Ok(movie));
        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);

        var external = detail.Pickers.Subtitles.Single(r => r.Section == "External");
        await detail.SelectSubtitleCommand.ExecuteAsync(external);

        Assert.Equal("Night.Harbor.2025.en.srt", external.Description);
        Assert.DoesNotContain("Media", external.AccessibleName, StringComparison.OrdinalIgnoreCase);
        string saved = SentBody(HttpMethod.Put, SubtitlePrefs);
        Assert.Contains("\"label\":\"Night.Harbor.2025.en.srt\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("Media", saved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEpisodesPick_IsSavedForItsSeries()
    {
        const string seriesPrefs = SeriesAudioPrefs;
        var detail = await LoadEpisodeWithAudioAsync();

        await detail.SelectAudioCommand.ExecuteAsync(detail.Pickers.Audio[2]);
        detail.PlayCommand.Execute(null);

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Put, seriesPrefs));
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(ItemDetailPayloads.EpisodeId, new PrePlayChoice("901", false, AudioTrack: 1))), Played);
    }

    [Fact]
    public async Task AFileWithoutTracks_HasNoAudioOrSubs()
    {
        var detail = await _fixture.Episode().LoadAsync(ItemDetailPayloads.EpisodeId);

        Assert.True(detail.Pickers.ShowsVersions);
        Assert.False(detail.Pickers.ShowsAudio);
        Assert.False(detail.Pickers.ShowsSubtitles);
        // No last played file: the first whole file, as playback chooses.
        Assert.Equal("901", detail.Pickers.Version?.FileId);
    }

    [Fact]
    public async Task OneFile_HasNoVersionPicker_AndKeepsItsLine()
    {
        var detail = await _fixture.Movie().LoadAsync(ItemDetailPayloads.MovieId);

        Assert.False(detail.Pickers.ShowsVersions);
        Assert.False(detail.Pickers.ShowsAny);
        Assert.Equal("Available version: 1080P · H264 · EAC3 · 3.8 GB", detail.AvailableVersionLine);
    }

    [Fact]
    public async Task ASeries_HasNoPickers()
    {
        var detail = await _fixture.Series().LoadAsync(ItemDetailPayloads.SeriesId);

        Assert.Same(PrePlayPickers.None, detail.Pickers);
        detail.PlayCommand.Execute(null);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(ItemDetailPayloads.EpisodeId)), Played);
    }

    [Fact]
    public async Task AnotherTitle_StartsWithoutPicks()
    {
        var detail = await LoadAsync();
        detail.SelectVersionCommand.Execute(detail.Pickers.Versions[0]);
        _fixture.Movie();

        await detail.LoadCommand.ExecuteAsync(ItemDetailPayloads.MovieId);
        await detail.LoadCommand.ExecuteAsync(Movie);

        Assert.Null(detail.Pickers.Choice);
        Assert.Equal("502", detail.Pickers.Version?.FileId);
    }
}
