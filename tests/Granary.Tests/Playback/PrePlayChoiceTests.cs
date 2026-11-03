using Granary.Core.Playback;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>
/// A start with the file and tracks picked on the detail page (docs/api-contract.md, 8.4 and 8.8.1): Silo's web, Android
/// and Apple clients play the picked file without letting the server swap in another, and start with the picked tracks.
/// </summary>
public sealed class PrePlayChoiceTests
{
    private const string WatchPath = "api/v2/watch/" + MovieId;

    private readonly ServerPlaybackFixture _fixture = new();

    private async Task<System.Text.Json.JsonElement> StartedWith(PrePlayChoice choice, string watch)
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(watch));
        await _fixture.StartAsync(PlayRequest.Resume(MovieId, choice));
        return _fixture.Body(HttpMethod.Post, StartPath);
    }

    [Fact]
    public async Task APickedVersion_StartsPinned()
    {
        var start = await StartedWith(new PrePlayChoice("43", IsVersionPicked: true), ServerPlaybackSettingsTests.TwoFiles);

        Assert.Equal("43", start.GetProperty("file_id").GetString());
        Assert.False(start.GetProperty("allow_alternate_versions").GetBoolean());
    }

    [Fact]
    public async Task APickedVersion_CanStillBeSwitchedInThePlayer_AndTheSwitchStaysPinned()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(ServerPlaybackSettingsTests.TwoFiles));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        _fixture.Silo.On(HttpMethod.Delete, StopPath, Ok(PlaybackPayloads.StopCompleted));
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, new PrePlayChoice("43", IsVersionPicked: true)));
        _fixture.Engine.RaiseState(Frame(600));

        playback.SelectVersion("42");
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, StartPath).Count == 2, "the other file is started");

        var start = _fixture.Body(HttpMethod.Post, StartPath, 1);
        Assert.Equal("42", start.GetProperty("file_id").GetString());
        // The web's player pins a file switched to as well: the viewer chose it.
        Assert.False(start.GetProperty("allow_alternate_versions").GetBoolean());
    }

    [Fact]
    public async Task APickedAudioTrack_IsTheOneTheStartAsksFor_WithoutPinningTheFile()
    {
        var start = await StartedWith(new PrePlayChoice("42", IsVersionPicked: false, AudioTrack: 1),
            ServerPlaybackAudioTests.WithASecondAudioTrack(PlaybackPayloads.WatchStateOk));

        Assert.Equal("42", start.GetProperty("file_id").GetString());
        Assert.Equal(1, start.GetProperty("audio_track_index").GetInt32());
        Assert.False(start.TryGetProperty("allow_alternate_versions", out _));
    }

    [Fact]
    public async Task APickedSubtitle_IsTheOneTheStartAsksFor()
    {
        var start = await StartedWith(new PrePlayChoice("42", IsVersionPicked: false, SubtitleTrack: 2),
            ServerPlaybackSubtitleTests.WithSubtitleTracks(PlaybackPayloads.WatchStateOk));

        Assert.Equal(2, start.GetProperty("subtitle_track_index").GetInt32());
    }

    [Fact]
    public async Task SubtitlesPickedOff_StartWithNone()
    {
        var start = await StartedWith(new PrePlayChoice("42", IsVersionPicked: false, SubtitleTrack: -1),
            ServerPlaybackSubtitleTests.WithSubtitleTracks(PlaybackPayloads.WatchStateOk));

        // Left out, the start shows none, where the automatic choice would show the English forced signs.
        Assert.False(start.TryGetProperty("subtitle_track_index", out _));
    }

    [Fact]
    public async Task TracksTheFileDoesNotHave_AreLeftToTheAutomaticChoice()
    {
        var start = await StartedWith(new PrePlayChoice("42", IsVersionPicked: false, AudioTrack: 5, SubtitleTrack: 9),
            ServerPlaybackSubtitleTests.WithSubtitleTracks(PlaybackPayloads.WatchStateOk));

        // The file's own choice: no audio index of the server's, and the English forced signs.
        Assert.False(start.TryGetProperty("audio_track_index", out _));
        Assert.Equal(1, start.GetProperty("subtitle_track_index").GetInt32());
    }

    [Fact]
    public async Task AFileTheTitleNoLongerHas_PlaysAsWithoutAPick()
    {
        var start = await StartedWith(new PrePlayChoice("99", IsVersionPicked: true, AudioTrack: 1, SubtitleTrack: -1),
            ServerPlaybackSubtitleTests.WithSubtitleTracks(PlaybackPayloads.WatchStateOk));

        Assert.Equal("42", start.GetProperty("file_id").GetString());
        Assert.False(start.TryGetProperty("allow_alternate_versions", out _));
        Assert.False(start.TryGetProperty("audio_track_index", out _));
        Assert.Equal(1, start.GetProperty("subtitle_track_index").GetInt32());
    }

    [Fact]
    public async Task AStart_ReadsTheTitle_OnceAPickStillBeingSavedIsIn()
    {
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.TrackChoices = new TrackChoiceWrites(_fixture.Time, _fixture.Logs.For<TrackChoiceWrites>());
        var saving = _fixture.TrackChoices.RunAsync("audio", _ => saved.Task);
        var playback = _fixture.Create();

        var starting = playback.StartAsync(PlayRequest.Resume(MovieId), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, WatchPath));
        saved.SetResult();
        await starting;

        Assert.True(await saving);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, WatchPath));
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Post, StartPath));
    }
}
