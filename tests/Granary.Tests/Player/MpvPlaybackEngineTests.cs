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
    }
}
