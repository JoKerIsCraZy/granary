using Granary.Core.Playback;
using Granary.Player;
using Granary.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Tests.Player;

/// <summary>Runs the real libmpv headless (no video or audio output). Skipped without libmpv-2.dll.</summary>
public sealed class MpvPlaybackEngineTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly List<string> _files = [];
    private readonly List<string> _folders = [];

    private static MpvPlaybackEngine CreateEngine()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        return new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = VideoOutputKind.None }, NullLogger<MpvPlaybackEngine>.Instance);
    }

    private string TempWav(double seconds)
    {
        string path = WavFile.WriteTemp(seconds);
        _files.Add(path);
        return path;
    }

    private string TempY4m(double seconds)
    {
        string path = Y4mFile.WriteTemp(seconds);
        _files.Add(path);
        return path;
    }

    [Fact]
    public async Task LocalFile_LoadsReportsDurationAndEnds()
    {
        using var engine = CreateEngine();
        var ended = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Ended += (_, e) => ended.TrySetResult(e);
        engine.StateChanged += (_, s) =>
        {
            if (s is { IsLoaded: true, Duration: not null })
            {
                loaded.TrySetResult(s);
            }
        };

        engine.Load(MediaSource.Local(TempWav(1.0)));

        var state = await loaded.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.InRange(state.Duration!.Value.TotalSeconds, 0.9, 1.1);
        var end = await ended.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackEndReason.EndOfFile, end.Reason);
        Assert.True(engine.Current.IsAtEnd);
    }

    [Fact]
    public async Task Seek_MovesThePosition()
    {
        using var engine = CreateEngine();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seeked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s.IsLoaded)
            {
                loaded.TrySetResult();
            }
            if (s.Position >= TimeSpan.FromSeconds(4))
            {
                seeked.TrySetResult();
            }
        };
        engine.Load(MediaSource.Local(TempWav(6.0)));
        await loaded.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        engine.Seek(TimeSpan.FromSeconds(4.5));

        await seeked.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APause_OutlastsANewLoad_AsMpvKeepsIt()
    {
        using var engine = CreateEngine();
        string file = TempWav(6.0);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long second = 0;
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s.IsLoaded)
            {
                loaded.TrySetResult();
            }
            if (s.IsPaused)
            {
                paused.TrySetResult();
            }
            if (s.IsLoaded && s.LoadId == Volatile.Read(ref second))
            {
                reloaded.TrySetResult();
            }
        };
        engine.Load(MediaSource.Local(file));
        await loaded.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        engine.SetPaused(true);
        await paused.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // A replan's new stream replaces the file: mpv stays paused, and so must what the player shows.
        Volatile.Write(ref second, engine.Load(MediaSource.Local(file) with { Start = TimeSpan.FromSeconds(2) }));
        await reloaded.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(engine.Current.IsPaused);
        Assert.True(engine.IsPauseAsked);
    }

    [Fact]
    public void PerFileOptions_CarryTheStartAndTheTracks()
    {
        var source = new MediaSource(new Uri("https://nas.example/stream"), [], TimeSpan.FromSeconds(12.5), AudioStream: 2, SubtitleStream: 0);

        Assert.Equal(["start=12.5", "aid=2", "sid=no"], MpvPlaybackEngine.PerFileOptions(source));
        Assert.Equal(["aid=auto", "sid=3"], MpvPlaybackEngine.PerFileOptions(source with { Start = null, AudioStream = null, SubtitleStream = 3 }));
        Assert.Equal(["aid=auto"], MpvPlaybackEngine.PerFileOptions(MediaSource.Local(@"C:\clip.mkv")));
    }

    [Fact]
    public void TheDefaultSubtitleLook_IsWhiteTextWithAShadow_AtTheClientsSize()
    {
        var options = MpvPlaybackEngine.SubtitleOptions(SubtitleAppearance.Default).ToDictionary(o => o.Name, o => o.Value);

        Assert.Equal("37.33", options["sub-font-size"]);
        Assert.Equal("sans-serif", options["sub-font"]);
        Assert.Equal("#FFFFFFFF", options["sub-color"]);
        Assert.Equal("outline-and-shadow", options["sub-border-style"]);
        Assert.Equal(("#FF000000", "2", "0"), (options["sub-back-color"], options["sub-shadow-offset"], options["sub-outline-size"]));
        Assert.Equal("bottom", options["sub-align-y"]);
    }

    [Fact]
    public void ABoxLook_DrawsTheBackgroundAtItsOpacity()
    {
        var look = SubtitleAppearance.Default with
        {
            Background = SubtitleBackground.Box, BackgroundColor = "#101010", BackgroundOpacity = 60, TextOpacity = 50,
            FontFamily = SubtitleAppearance.Monospace, TextOutline = true, Position = SubtitlePosition.Top,
        };

        var options = MpvPlaybackEngine.SubtitleOptions(look).ToDictionary(o => o.Name, o => o.Value);

        Assert.Equal("background-box", options["sub-border-style"]);
        Assert.Equal("#99101010", options["sub-back-color"]);
        Assert.Equal("#80FFFFFF", options["sub-color"]);
        Assert.Equal(("Consolas", "1.65", "top"), (options["sub-font"], options["sub-outline-size"], options["sub-align-y"]));
    }

    [Fact]
    public async Task AnAudioSwitch_TakesOnlyAStreamTheFileHas()
    {
        using var engine = CreateEngine();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s is { IsLoaded: true, HasStarted: true })
            {
                playing.TrySetResult();
            }
        };
        engine.Load(MediaSource.Local(TempWav(6.0)));
        await playing.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // The WAV has one audio stream: mpv would take the second and play on silent, so the first comes back.
        Assert.False(engine.SetAudioStream(2));
        Assert.Equal("1", engine.PlayingAudioStream);
        Assert.True(engine.SetAudioStream(1));
    }

    [Fact]
    public async Task ASubtitleFile_IsReadIn_ThenShownAndHidden()
    {
        using var engine = CreateEngine();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s is { IsLoaded: true, HasStarted: true })
            {
                playing.TrySetResult();
            }
        };
        long load = engine.Load(MediaSource.Local(TempWav(6.0)) with { SubtitleStream = 0 });
        await playing.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        string srt = Path.Combine(Path.GetTempPath(), $"granary-{Guid.NewGuid():N}.srt");
        _files.Add(srt);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:05,000\nHello\n", TestContext.Current.CancellationToken);

        int? stream = await engine.AddSubtitleAsync(new Uri(srt), load);

        Assert.NotNull(stream);
        Assert.True(engine.SetSubtitleStream(stream.Value));
        Assert.True(engine.SetSubtitleStream(0));
        // Read in for an earlier load, a file is left out.
        Assert.Null(await engine.AddSubtitleAsync(new Uri(srt), load - 1));
    }

    [Fact]
    public async Task TheSameSubtitleFileReadAgain_IsANewTrack_AndTheOldOneCanBeTakenOut()
    {
        using var engine = CreateEngine();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s is { IsLoaded: true, HasStarted: true })
            {
                playing.TrySetResult();
            }
        };
        long load = engine.Load(MediaSource.Local(TempWav(6.0)) with { SubtitleStream = 0 });
        await playing.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        string srt = Path.Combine(Path.GetTempPath(), $"granary-{Guid.NewGuid():N}.srt");
        _files.Add(srt);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:05,000\nHello\n", TestContext.Current.CancellationToken);
        int? first = await engine.AddSubtitleAsync(new Uri(srt), load);
        Assert.True(engine.SetSubtitleStream(first!.Value));

        // A timing change: the server serves the same subtitle anew, read in again beside the one that shows.
        await File.WriteAllTextAsync(srt, "1\n00:00:01,000 --> 00:00:06,000\nHello\n", TestContext.Current.CancellationToken);
        int? second = await engine.AddSubtitleAsync(new Uri(srt), load);

        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(engine.SetSubtitleStream(second.Value));
        engine.RemoveSubtitle(first.Value);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(engine.SetSubtitleStream(first.Value));
        Assert.True(engine.SetSubtitleStream(second.Value));
    }

    [Fact]
    public async Task ReadSubtitleTracks_ListsAnAddedFileAsExternal()
    {
        using var engine = CreateEngine();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s is { IsLoaded: true, HasStarted: true })
            {
                playing.TrySetResult();
            }
        };
        long load = engine.Load(MediaSource.Local(TempWav(6.0)) with { SubtitleStream = 0 });
        await playing.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Empty(engine.ReadSubtitleTracks());
        string srt = Path.Combine(Path.GetTempPath(), $"granary-{Guid.NewGuid():N}.srt");
        _files.Add(srt);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:05,000\nHello\n", TestContext.Current.CancellationToken);

        int? stream = await engine.AddSubtitleAsync(new Uri(srt), load);

        var track = Assert.Single(engine.ReadSubtitleTracks());
        Assert.Equal(stream, track.Stream);
        Assert.True(track.IsExternal);
        Assert.Equal("subrip", track.Codec);
        Assert.False(track.IsForced);
    }

    // A download's folder is as deep as the downloads folder the viewer chose makes it (docs/architecture.md, "Long paths").
    // The test host is not long-path aware, as a PC without Windows' LongPathsEnabled is for the whole app.
    [Fact]
    public async Task AFileWhoseFullPathIsLongerThan260Characters_Plays_WithASubtitleFileBesideIt()
    {
        var sink = new LogSink();
        using var engine = new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = VideoOutputKind.None }, sink.For<MpvPlaybackEngine>());
        string folder = LongFolder();
        string media = Path.Combine(folder, "media.wav");
        string srt = Path.Combine(folder, "sub-external_0-0123456789abcdef.srt");
        await File.WriteAllBytesAsync(media, WavFile.Sine(6.0), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:05,000\nHello\n", TestContext.Current.CancellationToken);
        Assert.True(media.Length > 300, "The path must be well over Windows' 260.");
        var playing = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s is { IsLoaded: true, HasStarted: true })
            {
                playing.TrySetResult(s);
            }
        };
        engine.Ended += (_, e) => ended.TrySetResult(e);

        long load = engine.Load(MediaSource.Local(media) with { SubtitleStream = 0 });

        var first = await Task.WhenAny(playing.Task, ended.Task).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(first == playing.Task, "The file did not play: " + (first == ended.Task ? (await ended.Task).Detail : ""));
        int? stream = await engine.AddSubtitleAsync(new Uri(srt), load);
        Assert.NotNull(stream);
        Assert.True(engine.SetSubtitleStream(stream.Value));
        // The same file read again is found beside the first, as a short path's is.
        Assert.NotEqual(stream, await engine.AddSubtitleAsync(new Uri(srt), load));
        // The folder holds the Windows user's name: whatever mpv says of the files, the log does not name it.
        Assert.DoesNotContain(Path.GetFileName(_folders[^1]), sink.All, StringComparison.OrdinalIgnoreCase);
    }

    // Without the extended form, mpv cannot open a subtitle file whose path is over 260 characters; a file it cannot open at
    // all is left out, and the log says so without its path.
    [Fact]
    public async Task ASubtitleFileThatIsNotThere_IsLeftOut_AndTheLogDoesNotNameItsFolder()
    {
        var sink = new LogSink();
        using var engine = new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = VideoOutputKind.None }, sink.For<MpvPlaybackEngine>());
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s is { IsLoaded: true, HasStarted: true })
            {
                playing.TrySetResult();
            }
        };
        long load = engine.Load(MediaSource.Local(TempWav(6.0)) with { SubtitleStream = 0 });
        await playing.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        string folder = LongFolder();

        int? stream = await engine.AddSubtitleAsync(new Uri(Path.Combine(folder, "sub-gone.srt")), load);

        Assert.Null(stream);
        // mpv's own lines come from its event thread, a moment after the command.
        for (int tries = 0; tries < 200 && !sink.All.Contains("Error", StringComparison.Ordinal); tries++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        Assert.Contains("Error", sink.All, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(_folders[^1]), sink.All, StringComparison.OrdinalIgnoreCase);
    }

    // A folder whose path is over 300 characters, made of names as long as a download's folders can be.
    private string LongFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
        _folders.Add(root);
        string folder = root;
        while (folder.Length < 300)
        {
            folder = Path.Combine(folder, new string('d', 40) + "-0123456789abcdef");
        }
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task Loads_AreCounted_AndTheirStatesSayWhichOne()
    {
        using var engine = CreateEngine();
        var second = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s is { LoadId: 2, HasStarted: true })
            {
                second.TrySetResult(s);
            }
        };

        long first = engine.Load(MediaSource.Local(TempWav(3.0)));
        long next = engine.Load(MediaSource.Local(TempWav(3.0)));

        Assert.Equal(1, first);
        Assert.Equal(2, next);
        var state = await second.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(state.IsLoaded);
    }

    [Fact]
    public async Task Playback_ReportsThatItStartedAfterTheLoad()
    {
        using var engine = CreateEngine();
        var started = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s.HasStarted)
            {
                started.TrySetResult(s);
            }
        };

        engine.Load(MediaSource.Local(TempWav(3.0)));

        var state = await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(state.IsLoaded);
    }

    [Fact]
    public async Task ASoundFile_IsAudioOnly_OnceLoaded()
    {
        using var engine = CreateEngine();
        var loaded = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s.IsLoaded)
            {
                loaded.TrySetResult(s);
            }
        };

        engine.Load(MediaSource.Local(TempWav(3.0)));

        var state = await loaded.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(state.IsAudioOnly);
    }

    [Fact]
    public async Task AVideoFile_HasAPicture_AndALoadCountsAsOneUntilItsFileSaysOtherwise()
    {
        using var engine = CreateEngine();
        string video = TempY4m(2.0);
        var sound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pictured = new TaskCompletionSource<PlaybackSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Loads are counted from 1, so the video is load 2 whenever its states arrive.
        engine.StateChanged += (_, s) =>
        {
            if (s is { LoadId: 1, IsLoaded: true, IsAudioOnly: true })
            {
                sound.TrySetResult();
            }
            if (s is { LoadId: 2, IsLoaded: true })
            {
                pictured.TrySetResult(s);
            }
        };
        // A libmpv built without FFmpeg's y4m demuxer cannot open the file: the test has nothing to show then.
        engine.Ended += (_, e) =>
        {
            if (e is { LoadId: 2, Reason: PlaybackEndReason.Error })
            {
                pictured.TrySetResult(null);
            }
        };
        engine.Load(MediaSource.Local(TempWav(3.0)));
        await sound.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(2, engine.Load(MediaSource.Local(video)));

        // Not known yet: a title about to play is taken to have a picture.
        Assert.False(engine.Current.IsAudioOnly);
        var state = await pictured.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.SkipWhen(state is null, "This libmpv cannot read YUV4MPEG2.");
        Assert.False(state!.IsAudioOnly);
    }

    [Fact]
    public void AnOpenedFile_IsTakenOnlyForItsOwnLoad()
    {
        var loading = PlaybackSnapshot.Idle with { LoadId = 3 };

        var own = MpvPlaybackEngine.Opened(loading, loadId: 3, audioOnly: true);
        Assert.True(own.IsLoaded);
        Assert.True(own.IsAudioOnly);
        // A newer load came between the file's opening and its tracks' read: that file is not the one loading, which
        // neither is open yet nor has the file's tracks.
        Assert.Equal(loading, MpvPlaybackEngine.Opened(loading, loadId: 2, audioOnly: true));
    }

    [Fact]
    public void AVideoTrackThatChangesAfterTheFileOpened_CountsForItsOwnLoadOnly()
    {
        var sound = PlaybackSnapshot.Idle with { LoadId = 3, IsLoaded = true, IsAudioOnly = true };

        // A stream that names its picture late: the open file has a picture after all.
        Assert.False(MpvPlaybackEngine.Retracked(sound, loadId: 3, audioOnly: false).IsAudioOnly);
        // The tracks of a load before.
        Assert.Equal(sound, MpvPlaybackEngine.Retracked(sound, loadId: 2, audioOnly: false));
        // Before the file is open, the tracks are the last file's, going: a title loading counts as a picture until then.
        var loading = PlaybackSnapshot.Idle with { LoadId = 4 };
        Assert.Equal(loading, MpvPlaybackEngine.Retracked(loading, loadId: 4, audioOnly: true));
    }

    [Fact]
    public async Task AVideoTrackSwitchedOffAndOn_IsFollowed()
    {
        using var engine = CreateEngine();
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var off = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var on = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new Lock();
        long newest = 0;
        engine.StateChanged += (_, s) =>
        {
            lock (gate)
            {
                // Snapshots come from more than one thread: one older than the newest seen says nothing new.
                if (s.Sequence < newest || !s.IsLoaded)
                {
                    return;
                }
                newest = s.Sequence;
                if (!s.IsAudioOnly)
                {
                    if (off.Task.IsCompleted)
                    {
                        on.TrySetResult();
                    }
                    opened.TrySetResult(true);
                }
                else if (opened.Task.IsCompleted)
                {
                    off.TrySetResult();
                }
            }
        };
        // A libmpv built without FFmpeg's y4m demuxer cannot open the file: the test has nothing to show then.
        engine.Ended += (_, e) =>
        {
            if (e.Reason == PlaybackEndReason.Error)
            {
                opened.TrySetResult(false);
            }
        };
        engine.Load(MediaSource.Local(TempY4m(6.0)));
        Assert.SkipUnless(await opened.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken), "This libmpv cannot read YUV4MPEG2.");

        Assert.Equal(0, engine.Command("set", "vid", "no"));
        await off.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, engine.Command("set", "vid", "1"));
        await on.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AStartedFile_IsTakenOnlyForItsOwnLoad()
    {
        var loading = PlaybackSnapshot.Idle with { LoadId = 3, IsLoaded = true };

        Assert.True(MpvPlaybackEngine.Started(loading, loadId: 3).HasStarted);
        Assert.Equal(loading, MpvPlaybackEngine.Started(loading, loadId: 2));
    }

    [Fact]
    public async Task TheAudioDelay_IsMpvsAudioDelay_BeforeAndWhileAFilePlays_AndForTheNextFile()
    {
        using var engine = CreateEngine();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s.HasStarted)
            {
                started.TrySetResult();
            }
        };

        engine.SetAudioDelay(0.25);
        Assert.Equal("0.250000", engine.Option("audio-delay"));
        engine.Load(MediaSource.Local(TempWav(3.0)));
        await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Positive is later sound, as mpv has it and as Android's DelayAudioProcessor pads the start with silence.
        engine.SetAudioDelay(-0.15);
        Assert.Equal("-0.150000", engine.Option("audio-delay"));
        engine.Load(MediaSource.Local(TempWav(1.0)));
        Assert.Equal("-0.150000", engine.Option("audio-delay"));
    }

    [Fact]
    public async Task VolumeAndMute_AreSetAndReported()
    {
        using var engine = CreateEngine();
        var changed = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s is { Volume: 40, IsMuted: true })
            {
                changed.TrySetResult(s);
            }
        };

        engine.SetVolume(40);
        engine.SetMuted(true);

        await changed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(250, 100)]
    public async Task SetVolume_KeepsToZeroToHundred(double requested, double expected)
    {
        using var engine = CreateEngine();
        // mpv reports a property only when it differs from what it reported last: start from 50 first.
        var atFifty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s.Volume == 50)
            {
                atFifty.TrySetResult();
            }
            else if (atFifty.Task.IsCompleted && s.Volume == expected)
            {
                changed.TrySetResult();
            }
        };
        engine.SetVolume(50);
        await atFifty.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        engine.SetVolume(requested);

        await changed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HttpSource_SendsTheConfiguredHeaders()
    {
        using var engine = CreateEngine();
        await using var server = new TinyHttpServer(200, "OK", WavFile.Sine(0.5), "audio/wav");
        var ended = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Ended += (_, e) => ended.TrySetResult(e);

        engine.Load(new MediaSource(server.Url("media.wav"),
            [new("Authorization", "Bearer test-token"), new("X-Profile-Id", "profile-1")]));

        var end = await ended.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackEndReason.EndOfFile, end.Reason);
        string first = Assert.IsType<string>(server.RequestHeaders.FirstOrDefault());
        Assert.Contains("Authorization: Bearer test-token", first, StringComparison.Ordinal);
        Assert.Contains("X-Profile-Id: profile-1", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpStream_ReportsWhatTheCacheHolds_InThePositionsTime()
    {
        using var engine = CreateEngine();
        await using var server = new TinyHttpServer(200, "OK", WavFile.Sine(20), "audio/wav");
        var held = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s.Buffered.Count > 0 && s.Buffered[^1].EndSeconds > 15)
            {
                held.TrySetResult(s);
            }
        };

        long load = engine.Load(new MediaSource(server.Url("media.wav"), []));

        // A network stream gets mpv's cache, which reads the whole short file ahead; its range starts where time-pos does.
        var state = await held.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(load, state.LoadId);
        var range = Assert.Single(state.Buffered);
        Assert.InRange(range.StartSeconds, 0, 0.1);
        Assert.InRange(range.EndSeconds, 15, 20.1);
    }

    [Fact]
    public async Task ANewLoad_ClearsWhatTheStreamBeforeHeld()
    {
        using var engine = CreateEngine();
        await using var server = new TinyHttpServer(200, "OK", WavFile.Sine(20), "audio/wav");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long second = 0;
        var secondPlays = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondHeldAny = false;
        engine.StateChanged += (_, s) =>
        {
            if (s.Buffered.Count > 0)
            {
                held.TrySetResult();
            }
            if (s.LoadId == Volatile.Read(ref second))
            {
                secondHeldAny |= s.Buffered.Count > 0;
                if (s is { IsLoaded: true, HasStarted: true })
                {
                    secondPlays.TrySetResult();
                }
            }
        };
        engine.Load(new MediaSource(server.Url("media.wav"), []));
        await held.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Volatile.Write(ref second, engine.Load(MediaSource.Local(TempWav(6.0))));

        Assert.Empty(engine.Current.Buffered);
        await secondPlays.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        // A local file has no cache of mpv's: nothing shows for it, and nothing of the stream before.
        Assert.False(secondHeldAny);
        Assert.Empty(engine.Current.Buffered);
    }

    [Fact]
    public async Task AStop_ClearsWhatTheStreamHeld()
    {
        using var engine = CreateEngine();
        await using var server = new TinyHttpServer(200, "OK", WavFile.Sine(20), "audio/wav");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s.Buffered.Count > 0)
            {
                held.TrySetResult();
            }
            else if (held.Task.IsCompleted && !s.IsLoaded)
            {
                stopped.TrySetResult(s);
            }
        };
        engine.Load(new MediaSource(server.Url("media.wav"), []));
        await held.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        engine.Stop();

        var state = await stopped.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Empty(state.Buffered);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.Empty(engine.Current.Buffered);
    }

    [Fact]
    public async Task Snapshots_AreNumberedInTheOrderTheEngineMadeThem_AcrossItsThreads()
    {
        using var engine = CreateEngine();
        await using var server = new TinyHttpServer(200, "OK", WavFile.Sine(20), "audio/wav");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(PlaybackSnapshot State, string? Thread)>();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            seen.Enqueue((s, Thread.CurrentThread.Name));
            if (s.Buffered.Count > 0)
            {
                held.TrySetResult();
            }
            else if (held.Task.IsCompleted && !s.IsLoaded)
            {
                stopped.TrySetResult();
            }
        };
        engine.Load(new MediaSource(server.Url("media.wav"), []));
        await held.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        engine.Stop();
        await stopped.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var snapshots = seen.ToArray();
        // Three raisers: the load (this thread), mpv's events, and the cache reads on a timer's thread, which raise only
        // what the cache holds.
        var load = Assert.Single(snapshots, s => s.Thread != "mpv-events" && !s.State.IsLoaded);
        var events = snapshots.Where(s => s.Thread == "mpv-events").ToArray();
        var reads = snapshots.Where(s => s.Thread != "mpv-events" && s != load).ToArray();
        Assert.NotEmpty(events);
        Assert.NotEmpty(reads);
        Assert.All(reads, s => Assert.NotEmpty(s.State.Buffered));
        // One count across all of them: every number once, the load's first, the events' on both sides of the reads',
        // and the stop's above every one before.
        Assert.Equal(snapshots.Length, snapshots.Select(s => s.State.Sequence).Distinct().Count());
        Assert.All(snapshots.Where(s => s != load), s => Assert.True(s.State.Sequence > load.State.Sequence));
        Assert.Contains(events, s => s.State.Sequence < reads.Min(r => r.State.Sequence));
        Assert.Contains(events, s => s.State.Sequence > reads.Max(r => r.State.Sequence));
        var stop = events.Where(s => !s.State.IsLoaded).MaxBy(s => s.State.Sequence);
        Assert.Empty(stop.State.Buffered);
        Assert.True(stop.State.Sequence > reads.Max(s => s.State.Sequence));
        Assert.True(engine.Current.Sequence >= snapshots.Max(s => s.State.Sequence));
    }

    [Fact]
    public async Task HttpSource_ReportsAuthenticationFailedOn401()
    {
        using var engine = CreateEngine();
        await using var server = new TinyHttpServer(401, "Unauthorized",
            """{"type":"https://siloserver.org/docs/api/v2/problems/invalid_token","status":401}"""u8.ToArray(),
            "application/problem+json");
        var ended = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Ended += (_, e) =>
        {
            if (e.Reason != PlaybackEndReason.Stopped)
            {
                ended.TrySetResult(e);
            }
        };

        engine.Load(new MediaSource(server.Url("media.wav"), [new("Authorization", "Bearer expired")]));

        var end = await ended.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackEndReason.AuthenticationFailed, end.Reason);
    }

    [Theory]
    [InlineData("Authorization", "Bearer a,b")]
    [InlineData("X-Bad", "line\r\nbreak")]
    [InlineData("Bad:Name", "value")]
    [InlineData("%3%Bad", "value")]
    [InlineData("\"Quoted\"", "value")]
    [InlineData("X Space", "value")]
    [InlineData("X-Value", "a\\b")]
    [InlineData("X-Value", "\"quoted")]
    [InlineData("X-Value", "[bracketed]")]
    [InlineData("X-Value", "%5%hello")]
    [InlineData("X-Value", "nul\0inside")]
    [InlineData("X-Value", "tab\tinside")]
    public void Load_RejectsHeadersMpvWouldSplit(string name, string value)
    {
        using var engine = CreateEngine();

        Assert.Throws<ArgumentException>(() =>
            engine.Load(new MediaSource(new Uri("http://127.0.0.1:9/x"), [new(name, value)])));
    }

    public void Dispose()
    {
        foreach (string file in _files)
        {
            File.Delete(file);
        }
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
