using System.Diagnostics;
using Granary.Core.Playback;
using Granary.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.Player;

/// <summary>
/// Apply now with the real libmpv: mpv starts again and reads mpv.conf as it is now, at once when nothing plays and never
/// during a title. Skipped without libmpv-2.dll.
/// </summary>
public sealed class MpvConfigRestartTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"), "mpv");
    private readonly LogSink _logs = new();
    private readonly MpvConfigStatus _status = new();
    private readonly List<string> _files = [];
    private int _restarts;

    public void Dispose()
    {
        foreach (string file in _files)
        {
            File.Delete(file);
        }
        string root = Path.GetDirectoryName(_directory)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string ConfigFile => Path.Combine(_directory, MpvConfigFolder.ConfigFileName);

    private MpvPlaybackEngine CreateEngine(string? config, VideoOutputKind output = VideoOutputKind.None)
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        Directory.CreateDirectory(_directory);
        if (config is not null)
        {
            File.WriteAllText(ConfigFile, config);
        }
        var engine = new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = output, ConfigDirectory = _directory },
            _logs.For<MpvPlaybackEngine>(), null, _status);
        engine.ConfigRestarted += (_, _) => Interlocked.Increment(ref _restarts);
        return engine;
    }

    private int Count(string text) => _logs.Lines.Count(line => line.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task ApplyNow_WithNothingPlaying_StartsMpvAgainWithTheNewFile_AndKeepsTheEnginesSettings()
    {
        using var engine = CreateEngine("deband=yes\n");
        engine.SetVolume(40);
        engine.SetSpeed(1.5);
        File.WriteAllText(ConfigFile, "deband=no\nsaturation=-50\nvo=gpu\n");

        Assert.True(engine.RestartWithConfig());

        Assert.Equal(1, _restarts);
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("-50.000000", engine.Option("saturation"));
        // Granary's own options stay its own, and what the engine set carries over.
        Assert.Equal("null", engine.Option("vo"));
        Assert.Equal("40.000000", engine.Option("volume"));
        Assert.Equal("1.500000", engine.Option("speed"));
        Assert.Equal(2, Count("mpv started with"));
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used, KeptOptions: 1), _status.Current);
        await PlaysAWav(engine, 0.5);
    }

    [Fact]
    public async Task ApplyNow_WhileATitlePlays_NeverCutsIt_AndAppliesToTheNextTitle()
    {
        using var engine = CreateEngine("deband=yes\n");
        var ends = new List<PlaybackEnded>();
        engine.Ended += (_, e) =>
        {
            lock (ends)
            {
                ends.Add(e);
            }
        };
        long first = await PlaysAWav(engine, 10);
        File.WriteAllText(ConfigFile, "deband=no\n");

        Assert.False(engine.RestartWithConfig());
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // The title plays on in the same mpv.
        Assert.Equal(0, _restarts);
        Assert.Equal("yes", engine.Option("deband"));
        Assert.True(engine.Current.IsLoaded);
        Assert.Equal(first, engine.Current.LoadId);
        lock (ends)
        {
            Assert.Empty(ends);
        }

        long second = await PlaysAWav(engine, 0.5);

        Assert.Equal(1, _restarts);
        Assert.Equal("no", engine.Option("deband"));
        Assert.True(second > first);
    }

    [Fact]
    public async Task ApplyNow_WhileATitlePlays_AppliesOnceThePlayerStops()
    {
        using var engine = CreateEngine("deband=yes\n");
        await PlaysAWav(engine, 10);
        File.WriteAllText(ConfigFile, "deband=no\n");
        Assert.False(engine.RestartWithConfig());

        // The player closes.
        engine.Stop();

        await Until(() => Volatile.Read(ref _restarts) == 1, () => _logs.All);
        Assert.Equal("no", engine.Option("deband"));
        await PlaysAWav(engine, 0.5);
    }

    [Fact]
    public async Task ApplyNow_SaysHowTheNewStartFared()
    {
        using var engine = CreateEngine("player-operation-mode=cplayer\ndeband=yes\n");
        Assert.Equal(MpvConfigUse.NotUsed, _status.Current.Use);

        File.WriteAllText(ConfigFile, "deband=yes\n");
        Assert.True(engine.RestartWithConfig());
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used), _status.Current);
        Assert.Equal("yes", engine.Option("deband"));

        // Unload removed it: mpv starts as without a file.
        File.Delete(ConfigFile);
        Assert.True(engine.RestartWithConfig());
        Assert.Equal(MpvConfigState.None, _status.Current);
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("cplayer", engine.Option("player-operation-mode"));
        await PlaysAWav(engine, 0.5);
    }

    [Fact]
    public async Task ApplyNow_AfterGranaryStoppedUsingTheFile_SaysHowTheNewStartFared()
    {
        // The profile keeps changing keep-open: Granary stops using the file for the run.
        using var engine = CreateEngine("deband=yes\n[loop]\nprofile-cond=p[\"keep-open\"] ~= \"no\" and p[\"keep-open\"] ~= false\nkeep-open=no\n");
        await Until(() => _status.Current.Use == MpvConfigUse.StoppedDuringPlayback, () => _logs.All);
        await Until(() => engine.Option("player-operation-mode") == "cplayer", () => _logs.All);

        File.WriteAllText(ConfigFile, "deband=yes\n");
        // While Granary still finishes its own start without the file, Apply now waits for it, then starts mpv again.
        if (!engine.RestartWithConfig())
        {
            await Until(() => Volatile.Read(ref _restarts) == 1, () => _logs.All);
        }

        Assert.Equal(1, _restarts);
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used), _status.Current);
        Assert.Equal("yes", engine.Option("deband"));
    }

    [Fact]
    public async Task InGranarysWindow_ApplyNow_HandsTheNewSwapChainToTheWindow()
    {
        using var engine = CreateEngine("deband=yes\n", VideoOutputKind.D3D11Composition);
        Assert.SkipWhen(engine.SwapChain == 0, "No d3d11 swap chain here.");
        nint before = engine.SwapChain;
        int changed = 0;
        engine.SwapChainChanged += (_, _) => Interlocked.Increment(ref changed);
        engine.Resize(1920, 1080);
        Interlocked.Exchange(ref changed, 0);
        File.WriteAllText(ConfigFile, "deband=no\n");

        Assert.True(engine.RestartWithConfig());
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.NotEqual(before, engine.SwapChain);
        Assert.NotEqual(0, engine.SwapChain);
        Assert.True(Volatile.Read(ref changed) >= 1);
        Assert.Equal("gpu-next", engine.Option("vo"));
        Assert.Equal("composition", engine.Option("d3d11-output-mode"));
        // The picture's size now, not the first one.
        Assert.Equal("1920x1080", engine.Option("d3d11-composition-size"));
        Assert.Equal("no", engine.Option("deband"));
    }

    // A WAV of that length loaded and open; the load's id.
    private async Task<long> PlaysAWav(MpvPlaybackEngine engine, double seconds)
    {
        string wav = WavFile.WriteTemp(seconds);
        _files.Add(wav);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long load = 0;
        void OnState(object? sender, PlaybackSnapshot s)
        {
            if (s.IsLoaded && s.LoadId == Volatile.Read(ref load))
            {
                loaded.TrySetResult();
            }
        }
        engine.StateChanged += OnState;
        try
        {
            Volatile.Write(ref load, engine.Load(MediaSource.Local(wav)));
            if (engine.Current is { IsLoaded: true } now && now.LoadId == load)
            {
                loaded.TrySetResult();
            }
            await loaded.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            return load;
        }
        finally
        {
            engine.StateChanged -= OnState;
        }
    }

    private static async Task Until(Func<bool> condition, Func<string>? state = null)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "mpv did not get there in time. " + state?.Invoke());
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Tests that count the process's handles run alone: other tests open and close handles of their own meanwhile.</summary>
[CollectionDefinition(nameof(HandleCounting), DisableParallelization = true)]
public sealed class HandleCounting;

/// <summary>Apply now, again and again, with the real libmpv. Skipped without libmpv-2.dll.</summary>
[Collection(nameof(HandleCounting))]
public sealed class MpvConfigRestartHandleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"), "mpv");

    public void Dispose()
    {
        string root = Path.GetDirectoryName(_directory)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestartsOneAfterAnother_LeaveNoHandlesBehind()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, MpvConfigFolder.ConfigFileName), "deband=yes\n");
        using var engine = new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = VideoOutputKind.None, ConfigDirectory = _directory },
            new LogSink().For<MpvPlaybackEngine>(), null, new MpvConfigStatus());
        int restarts = 0;
        engine.ConfigRestarted += (_, _) => restarts++;
        // Warmed up: libmpv and the runtime make what they keep for the process on the first starts.
        engine.RestartWithConfig();
        engine.RestartWithConfig();
        int before = Handles();

        for (int i = 0; i < 20; i++)
        {
            Assert.True(engine.RestartWithConfig());
        }

        // Twenty mpv instances made and closed: a leak would show as hundreds of handles (threads, events, files).
        Assert.InRange(Handles() - before, int.MinValue, 60);
        Assert.Equal(22, restarts);

        static int Handles()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var self = Process.GetCurrentProcess();
            self.Refresh();
            return self.HandleCount;
        }
    }
}
