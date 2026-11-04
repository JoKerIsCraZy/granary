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

        Assert.Equal(MpvRestartOutcome.Started, engine.RestartWithConfig());

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

        Assert.Equal(MpvRestartOutcome.Waiting, engine.RestartWithConfig());
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
        Assert.Equal(MpvRestartOutcome.Waiting, engine.RestartWithConfig());

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
        Assert.Equal(MpvRestartOutcome.Started, engine.RestartWithConfig());
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used), _status.Current);
        Assert.Equal("yes", engine.Option("deband"));

        // Unload removed it: mpv starts as without a file.
        File.Delete(ConfigFile);
        Assert.Equal(MpvRestartOutcome.Started, engine.RestartWithConfig());
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
        if (engine.RestartWithConfig() != MpvRestartOutcome.Started)
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

        Assert.Equal(MpvRestartOutcome.Started, engine.RestartWithConfig());
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

    [Fact]
    public async Task AFailedApplyNow_KeepsTheMpvBefore_AndSaysSo_UntilATryThatWorks()
    {
        using var engine = CreateEngine("deband=yes\n");
        int failures = 0;
        engine.ConfigRestartFailed += (_, _) => Interlocked.Increment(ref failures);
        engine.SetVolume(40);
        File.WriteAllText(ConfigFile, "deband=no\n");
        // The new mpv starts, then something after it fails: it is closed, and the one before stays.
        engine.AfterNewMpvStarted = () => throw new InvalidOperationException("The new mpv failed (test).");

        Assert.Equal(MpvRestartOutcome.Failed, engine.RestartWithConfig());

        Assert.Equal(1, failures);
        Assert.Equal(0, _restarts);
        Assert.Equal("yes", engine.Option("deband"));
        Assert.Equal("40.000000", engine.Option("volume"));
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used), _status.Current);

        // The start stays to be done: the next load tries it again first, and the title plays either way.
        await PlaysAWav(engine, 0.5);
        Assert.Equal(2, failures);
        Assert.Equal("yes", engine.Option("deband"));

        // Once a start works, mpv runs with the file as it is now.
        engine.AfterNewMpvStarted = null;
        engine.Stop();
        await Until(() => Volatile.Read(ref _restarts) == 1, () => _logs.All);
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal(2, failures);
    }

    [Fact]
    public async Task AFailedStartWithoutTheFile_AfterGranaryStoppedATitle_KeepsAWorkingMpv()
    {
        using var engine = CreateEngine("deband=yes\n[loop]\nkeep-open=no\n");
        engine.SetSpeed(1.5);
        var failed = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Ended += (_, e) =>
        {
            if (e.Reason == PlaybackEndReason.Error)
            {
                failed.TrySetResult(e);
            }
        };
        engine.AfterNewMpvStarted = () => throw new InvalidOperationException("The new mpv failed (test).");
        long first = await PlaysAWav(engine, 60);

        await KeepChangingKeepOpen(engine, failed.Task);

        // The title ends as a failure, as when the start works, and the mpv before plays on.
        Assert.Equal(first, (await failed.Task).LoadId);
        await Until(() => Count("A new mpv did not start without mpv.conf; the one before plays on") == 1, () => _logs.All);
        Assert.Equal("1.500000", engine.Option("speed"));
        Assert.Equal("yes", engine.Option("deband"));
        Assert.Equal(MpvConfigUse.StoppedDuringPlayback, _status.Current.Use);
        engine.AfterNewMpvStarted = null;
        await PlaysAWav(engine, 0.5);
    }

    [Fact]
    public async Task AfterAFailedStartWithoutTheFile_TheNextTitleEndsAsAnError_AndTheOneAfterItTriesAgain()
    {
        using var engine = CreateEngine("deband=yes\n[loop]\nkeep-open=no\n");
        var ends = new List<PlaybackEnded>();
        engine.Ended += (_, e) =>
        {
            lock (ends)
            {
                ends.Add(e);
            }
        };
        bool Ended(long load, PlaybackEndReason reason)
        {
            lock (ends)
            {
                return ends.Any(e => e.LoadId == load && e.Reason == reason);
            }
        }
        engine.AfterNewMpvStarted = () => throw new InvalidOperationException("The new mpv failed (test).");
        long first = await PlaysAWav(engine, 60);
        await KeepChangingKeepOpen(engine, () => Ended(first, PlaybackEndReason.Error));
        Assert.Equal(1, Count("A new mpv did not start without mpv.conf; the one before plays on"));

        // The plain start fails again: the title never reaches the mpv that still holds the rejected file.
        string wav = WavFile.WriteTemp(0.5);
        _files.Add(wav);
        long refused = engine.Load(MediaSource.Local(wav));

        await Until(() => Ended(refused, PlaybackEndReason.Error), () => _logs.All);
        Assert.True(refused > first);
        Assert.Null(engine.Option("path"));
        Assert.False(engine.Current.IsLoaded);
        Assert.Equal(refused, engine.Current.LoadId);
        Assert.Equal(2, Count("A new mpv did not start without mpv.conf; the one before plays on"));

        // The next load tries again; once it works, the title plays there.
        engine.AfterNewMpvStarted = null;
        long next = await PlaysAWav(engine, 0.5);

        Assert.True(next > refused);
        Assert.Equal("cplayer", engine.Option("player-operation-mode"));
        Assert.Equal("no", engine.Option("deband"));
    }

    [Fact]
    public async Task ALoadRightAfterGranaryStoppedATitle_PlaysInTheMpvWithoutTheFile()
    {
        using var engine = CreateEngine("deband=yes\n[loop]\nkeep-open=no\n");
        var ends = new List<PlaybackEnded>();
        var failed = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Ended += (_, e) =>
        {
            lock (ends)
            {
                ends.Add(e);
            }
            if (e.Reason == PlaybackEndReason.Error)
            {
                failed.TrySetResult(e);
            }
        };
        // The thread pool is slow to run the start after the stop: the next title's load comes first.
        using var slowPool = new ManualResetEventSlim();
        engine.BeforeStartWithoutConfig = () => slowPool.Wait(Timeout);
        long first = await PlaysAWav(engine, 60);
        await KeepChangingKeepOpen(engine, () => Count("Granary stopped and plays without your mpv.conf") > 0);

        var played = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Ended += (_, e) =>
        {
            if (e.Reason == PlaybackEndReason.EndOfFile)
            {
                played.TrySetResult(e.LoadId);
            }
        };
        long second = await PlaysAWav(engine, 0.5);

        // The load started mpv without the file itself, and its title plays there, before the stop's own start ran.
        Assert.Equal("cplayer", engine.Option("player-operation-mode"));
        Assert.Equal("no", engine.Option("deband"));
        slowPool.Set();
        Assert.Equal(first, (await failed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken)).LoadId);
        Assert.Equal(second, await played.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(1, Count("mpv.conf keeps changing keep-open"));
        lock (ends)
        {
            Assert.Single(ends, e => e.Reason == PlaybackEndReason.Error);
            Assert.DoesNotContain(ends, e => e.LoadId == second && e.Reason != PlaybackEndReason.EndOfFile);
        }
    }

    // A profile applied again and again, as one whose condition follows keep-open does after every set-back; applied by
    // name, so the test does not wait on how fast mpv's auto_profiles.lua sees each change. Until Granary stops the title.
    private Task KeepChangingKeepOpen(MpvPlaybackEngine engine, Task stopped) => KeepChangingKeepOpen(engine, () => stopped.IsCompleted);

    private static async Task KeepChangingKeepOpen(MpvPlaybackEngine engine, Func<bool> stopped)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!stopped())
        {
            Assert.True(DateTime.UtcNow < deadline, "Granary did not stop the title in time.");
            engine.Command("apply-profile", "loop");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
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
            Assert.Equal(MpvRestartOutcome.Started, engine.RestartWithConfig());
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
