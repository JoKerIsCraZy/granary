using System.Diagnostics;
using System.Globalization;
using Granary.Core.Playback;
using Granary.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.Player;

/// <summary>
/// A changed mpv.conf with the real libmpv: the next load starts a new mpv that reads it, a load without a change leaves mpv
/// alone, and nothing else starts mpv again. Skipped without libmpv-2.dll.
/// </summary>
public sealed class MpvConfigNextTitleTests : IDisposable
{
    private const string StartLine = "mpv started with";
    private const string StopLine = "Granary stopped and plays without your mpv.conf";
    private const string LoopingProfile = "deband=yes\n[loop]\nkeep-open=no\n";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"), "mpv");
    private readonly LogSink _logs = new();
    private readonly MpvConfigStatus _status = new();
    private readonly List<string> _files = [];

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

    private MpvPlaybackEngine CreateEngine(string? config, VideoOutputKind output = VideoOutputKind.None, Func<string, string?>? readConfig = null)
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        Directory.CreateDirectory(_directory);
        if (config is not null)
        {
            File.WriteAllText(ConfigFile, config);
        }
        var options = new MpvEngineOptions { VideoOutput = output, ConfigDirectory = _directory };
        return readConfig is null
            ? new MpvPlaybackEngine(options, _logs.For<MpvPlaybackEngine>(), null, _status)
            : new MpvPlaybackEngine(options, _logs.For<MpvPlaybackEngine>(), null, readConfig, _status);
    }

    private int Count(string text) => _logs.Lines.Count(line => line.Contains(text, StringComparison.Ordinal));

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private string TempWav(double seconds)
    {
        string wav = WavFile.WriteTemp(seconds);
        _files.Add(wav);
        return wav;
    }

    [Fact]
    public async Task AChangedFile_PlaysTheNextTitleWithTheNewOptions_AndKeepsTheEnginesSettings()
    {
        using var engine = CreateEngine("deband=yes\n");
        engine.SetVolume(40);
        engine.SetSpeed(1.5);
        engine.SetFill(true);
        engine.SetSubtitleDelay(2);
        File.WriteAllText(ConfigFile, "deband=no\nsaturation=-50\nvo=gpu\n");

        await PlaysAWav(engine, 0.5);

        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("-50.000000", engine.Option("saturation"));
        // Granary's own options stay its own, and what the engine set carries over.
        Assert.Equal("null", engine.Option("vo"));
        Assert.Equal("40.000000", engine.Option("volume"));
        Assert.Equal("1.500000", engine.Option("speed"));
        Assert.Equal("1.000000", engine.Option("panscan"));
        Assert.Equal("2.000000", engine.Option("sub-delay"));
        Assert.Equal(2, Count(StartLine));
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used, KeptOptions: 1), _status.Current);
    }

    [Fact]
    public async Task AnUnchangedFile_StartsNoNewMpv_EvenWhenItWasWrittenAgain()
    {
        using var engine = CreateEngine("deband=yes\n");

        await PlaysAWav(engine, 0.5);
        await PlaysAWav(engine, 0.5);
        File.WriteAllText(ConfigFile, "deband=yes\n");
        await PlaysAWav(engine, 0.5);

        Assert.Equal(1, Count(StartLine));
        Assert.Equal(0, Count("mpv.conf changed since mpv read it"));
        Assert.Equal("yes", engine.Option("deband"));
    }

    [Fact]
    public async Task InGranarysWindow_AnUnchangedFile_KeepsTheSwapChain()
    {
        using var engine = CreateEngine("deband=yes\n", VideoOutputKind.D3D11Composition);
        await ASwapChainOrSkip(engine);
        nint before = engine.SwapChain;
        int changed = 0;
        engine.SwapChainChanged += (_, _) => Interlocked.Increment(ref changed);

        await PlaysAWav(engine, 0.5);
        await PlaysAWav(engine, 0.5);

        Assert.Equal(before, engine.SwapChain);
        Assert.Equal(0, Volatile.Read(ref changed));
        Assert.Equal(1, Count(StartLine));
    }

    [Fact]
    public async Task InGranarysWindow_AChangedFile_HandsTheNewSwapChainToTheWindow()
    {
        using var engine = CreateEngine("deband=yes\n", VideoOutputKind.D3D11Composition);
        await ASwapChainOrSkip(engine);
        nint before = engine.SwapChain;
        int changed = 0;
        engine.SwapChainChanged += (_, _) => Interlocked.Increment(ref changed);
        engine.Resize(1920, 1080);
        Interlocked.Exchange(ref changed, 0);
        File.WriteAllText(ConfigFile, "deband=no\n");

        await PlaysAWav(engine, 0.5);
        await Until(() => Volatile.Read(ref changed) >= 1, () => _logs.All);

        Assert.NotEqual(before, engine.SwapChain);
        Assert.NotEqual(0, engine.SwapChain);
        Assert.Equal("gpu-next", engine.Option("vo"));
        Assert.Equal("composition", engine.Option("d3d11-output-mode"));
        // The picture's size now, not the first one.
        Assert.Equal("1920x1080", engine.Option("d3d11-composition-size"));
        Assert.Equal("no", engine.Option("deband"));
    }

    [Fact]
    public async Task AChangeWhileATitlePlays_DoesNotCutIt_AndTheNextLoadAppliesIt()
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

        await Task.Delay(500, TestContext.Current.CancellationToken);

        // The title plays on in the same mpv.
        Assert.Equal(1, Count(StartLine));
        Assert.Equal("yes", engine.Option("deband"));
        Assert.True(engine.Current.IsLoaded);
        Assert.Equal(first, engine.Current.LoadId);
        lock (ends)
        {
            Assert.Empty(ends);
        }

        long second = await PlaysAWav(engine, 0.5);

        Assert.Equal(2, Count(StartLine));
        Assert.Equal("no", engine.Option("deband"));
        Assert.True(second > first);
    }

    [Fact]
    public async Task AStopAfterAChange_StartsNoNewMpv_TheNextLoadDoes()
    {
        using var engine = CreateEngine("deband=yes\n");
        await PlaysAWav(engine, 10);
        File.WriteAllText(ConfigFile, "deband=no\n");

        // The player closes.
        engine.Stop();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(1, Count(StartLine));
        Assert.Equal("yes", engine.Option("deband"));

        await PlaysAWav(engine, 0.5);

        Assert.Equal(2, Count(StartLine));
        Assert.Equal("no", engine.Option("deband"));
    }

    [Fact]
    public async Task AChangedInclude_Applies_AndSoDoesOneThatAppeared()
    {
        string include = Path.Combine(_directory, "more.conf");
        using var engine = CreateEngine("include=~~/more.conf\n");
        Assert.Equal("no", engine.Option("deband"));

        // It appears.
        File.WriteAllText(include, "deband=yes\n");
        await PlaysAWav(engine, 0.5);
        Assert.Equal("yes", engine.Option("deband"));
        Assert.Equal(2, Count(StartLine));

        // It changes, mpv.conf does not.
        File.WriteAllText(include, "deband=no\n");
        await PlaysAWav(engine, 0.5);
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal(3, Count(StartLine));
    }

    [Fact]
    public async Task AShaderThatChanged_StartsNoNewMpv()
    {
        Directory.CreateDirectory(_directory);
        string shader = Path.Combine(_directory, "a.glsl");
        File.WriteAllText(shader, "// one\n");
        using var engine = CreateEngine("glsl-shaders=~~/a.glsl\n");

        File.WriteAllText(shader, "// two\n");
        await PlaysAWav(engine, 0.5);

        Assert.Equal(1, Count(StartLine));
    }

    [Fact]
    public async Task ARemovedFile_StartsWithoutIt_AndAFileThatAppeared_Applies()
    {
        using var engine = CreateEngine("player-operation-mode=cplayer\ndeband=yes\n");
        Assert.Equal(MpvConfigUse.NotUsed, _status.Current.Use);

        File.WriteAllText(ConfigFile, "deband=yes\n");
        await PlaysAWav(engine, 0.5);
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used), _status.Current);
        Assert.Equal("yes", engine.Option("deband"));

        // Unload removed it: mpv starts as without a file.
        File.Delete(ConfigFile);
        await PlaysAWav(engine, 0.5);
        Assert.Equal(MpvConfigState.None, _status.Current);
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("cplayer", engine.Option("player-operation-mode"));

        File.WriteAllText(ConfigFile, "deband=yes\n");
        await PlaysAWav(engine, 0.5);
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used), _status.Current);
        Assert.Equal("yes", engine.Option("deband"));
    }

    [Fact]
    public async Task ARefusedFile_IsNotTriedAgain_UntilItChanges()
    {
        const string RefusalLine = "; mpv plays without it";
        using var engine = CreateEngine("deband=yes\n");
        File.WriteAllText(ConfigFile, "player-operation-mode=cplayer\ndeband=yes\n");

        await PlaysAWav(engine, 0.5);
        Assert.Equal(MpvConfigUse.NotUsed, _status.Current.Use);
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal(1, Count(RefusalLine));

        await PlaysAWav(engine, 0.5);
        await PlaysAWav(engine, 0.5);
        Assert.Equal(1, Count(RefusalLine));
        Assert.Equal(1, Count("mpv.conf changed since mpv read it"));

        // A changed file gets a fresh try.
        File.WriteAllText(ConfigFile, "deband=yes\n");
        await PlaysAWav(engine, 0.5);
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used), _status.Current);
        Assert.Equal("yes", engine.Option("deband"));
    }

    [Fact]
    public async Task AFileGranaryStoppedUsing_IsNotTriedAgain_UntilItChanges()
    {
        using var engine = CreateEngine(LoopingProfile);
        await PlaysAWav(engine, 60);
        await KeepChangingKeepOpen(engine, () => Count(StopLine) > 0);
        await Until(() => engine.Option("player-operation-mode") == "cplayer", () => _logs.All);

        await PlaysAWav(engine, 0.5);
        await PlaysAWav(engine, 0.5);

        // Later titles play without it.
        Assert.Equal(1, Count(StartLine));
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal(MpvConfigUse.StoppedDuringPlayback, _status.Current.Use);

        File.WriteAllText(ConfigFile, "deband=yes\n");
        await PlaysAWav(engine, 0.5);

        Assert.Equal(2, Count(StartLine));
        Assert.Equal("yes", engine.Option("deband"));
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used), _status.Current);
    }

    [Fact]
    public async Task AFailedStart_LeavesTheOldMpvPlayingTheTitle_AndIsNotTriedAgain_UntilTheFileChanges()
    {
        const string FailureLine = "A new mpv did not start with mpv.conf; the one before plays on";
        using var engine = CreateEngine("deband=yes\n");
        engine.SetVolume(40);
        File.WriteAllText(ConfigFile, "deband=no\n");
        // The new mpv starts, then something after it fails: it is closed, and the one before stays.
        engine.AfterNewMpvStarted = () => throw new InvalidOperationException("The new mpv failed (test).");

        long first = await PlaysAWav(engine, 0.5);

        Assert.Equal(first, engine.Current.LoadId);
        Assert.Equal("yes", engine.Option("deband"));
        Assert.Equal("40.000000", engine.Option("volume"));
        Assert.Equal(1, Count(FailureLine));
        Assert.Equal(new MpvConfigState(MpvConfigUse.NotApplied), _status.Current);

        // The same file is not tried on every title.
        await PlaysAWav(engine, 0.5);
        Assert.Equal(1, Count(FailureLine));
        Assert.Equal(new MpvConfigState(MpvConfigUse.NotApplied), _status.Current);

        // A change gets a fresh try.
        engine.AfterNewMpvStarted = null;
        File.WriteAllText(ConfigFile, "deband=no\nsaturation=-50\n");
        await PlaysAWav(engine, 0.5);
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used), _status.Current);
    }

    [Fact]
    public async Task ThePlayer_HearsOfATitleOpening_WhetherOrNotMpvStartedAgain()
    {
        using var engine = CreateEngine("deband=yes\n");
        int opening = 0;
        int opened = 0;
        _status.TitleOpening += (_, _) => Interlocked.Increment(ref opening);
        _status.TitleOpened += (_, _) => Interlocked.Increment(ref opened);

        await PlaysAWav(engine, 0.5);
        await Until(() => Volatile.Read(ref opened) == 1, () => _logs.All);
        File.WriteAllText(ConfigFile, "deband=no\n");
        await PlaysAWav(engine, 0.5);
        await Until(() => Volatile.Read(ref opened) == 2, () => _logs.All);

        Assert.Equal(2, Volatile.Read(ref opening));
    }

    [Fact]
    public void ALoadOnASlowDisk_ReturnsAtOnce()
    {
        using var slowDisk = new ManualResetEventSlim(initialState: true);
        using var engine = CreateEngine("deband=yes\n", readConfig: path =>
        {
            slowDisk.Wait(Timeout);
            return ReadFile(path);
        });
        slowDisk.Reset();

        long load = engine.Load(MediaSource.Local(TempWav(0.5)));

        // The id and the state of the new load are there while the disk has not answered yet.
        Assert.Equal(load, engine.Current.LoadId);
        Assert.False(engine.Current.IsLoaded);
        Assert.Null(engine.Option("path"));
        slowDisk.Set();
    }

    [Fact]
    public async Task OnASlowDisk_TheLatestLoadOpens_AndOnlyThat()
    {
        using var slowDisk = new ManualResetEventSlim(initialState: true);
        using var engine = CreateEngine("deband=yes\n", readConfig: path =>
        {
            slowDisk.Wait(Timeout);
            return ReadFile(path);
        });
        string firstWav = TempWav(5);
        string secondWav = TempWav(5);
        var opened = new List<string>();
        void OnState(object? sender, PlaybackSnapshot s)
        {
            if (s.IsLoaded && engine.Option("path") is { } path)
            {
                lock (opened)
                {
                    opened.Add(path);
                }
            }
        }
        engine.StateChanged += OnState;
        slowDisk.Reset();

        long first = engine.Load(MediaSource.Local(firstWav));
        long second = engine.Load(MediaSource.Local(secondWav));
        slowDisk.Set();

        await Until(() => engine.Current.IsLoaded, () => _logs.All);
        engine.StateChanged -= OnState;
        Assert.True(second > first);
        Assert.Equal(second, engine.Current.LoadId);
        Assert.Equal(secondWav, engine.Option("path"));
        lock (opened)
        {
            Assert.DoesNotContain(firstWav, opened);
        }
    }

    [Fact]
    public async Task AStopWhileATitleWaitsForTheDisk_PlaysNothing()
    {
        using var slowDisk = new ManualResetEventSlim(initialState: true);
        using var engine = CreateEngine("deband=yes\n", readConfig: path =>
        {
            slowDisk.Wait(Timeout);
            return ReadFile(path);
        });
        slowDisk.Reset();

        engine.Load(MediaSource.Local(TempWav(5)));
        engine.Stop();
        slowDisk.Set();
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Null(engine.Option("path"));
        Assert.False(engine.Current.IsLoaded);
    }

    [Fact]
    public async Task APauseAsked_WhileTheTitleWaits_HoldsForThatTitle()
    {
        using var slowDisk = new ManualResetEventSlim(initialState: true);
        using var engine = CreateEngine("deband=yes\n", readConfig: path =>
        {
            slowDisk.Wait(Timeout);
            return ReadFile(path);
        });
        // The file before sits at its end, where mpv pauses itself and an unpause does not last.
        await AWavPlaysToItsEnd(engine);
        await Until(() => engine.Option("pause") == "yes", () => _logs.All);
        slowDisk.Reset();

        long load = engine.Load(MediaSource.Local(TempWav(5)));
        engine.SetPaused(false);
        Assert.Equal("yes", engine.Option("pause"));
        slowDisk.Set();

        await Until(() => engine.Current is { IsLoaded: true } s && s.LoadId == load, () => _logs.All);
        await Until(() => engine.Current is { IsPaused: false, Position.TotalSeconds: > 0 }, () => _logs.All);
        Assert.Equal("no", engine.Option("pause"));

        // And the other way round.
        slowDisk.Reset();
        long next = engine.Load(MediaSource.Local(TempWav(5)));
        engine.SetPaused(true);
        slowDisk.Set();

        await Until(() => engine.Current is { IsLoaded: true } s && s.LoadId == next, () => _logs.All);
        Assert.Equal("yes", engine.Option("pause"));
    }

    [Fact]
    public async Task ThePauseOfTheFileBefore_CarriesToANewMpv_AsToAnyNextFile()
    {
        using var engine = CreateEngine("deband=yes\n");
        await PlaysAWav(engine, 5);
        engine.SetPaused(true);
        await Until(() => engine.Current.IsPaused, () => _logs.All);
        File.WriteAllText(ConfigFile, "deband=no\n");

        await PlaysAWav(engine, 5);

        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("yes", engine.Option("pause"));
    }

    [Fact]
    public async Task TheFileBeforeEnding_WhileATitleWaitsForTheDisk_DoesNotEndThatTitle()
    {
        using var slowDisk = new ManualResetEventSlim(initialState: true);
        using var engine = CreateEngine("deband=yes\n", readConfig: path =>
        {
            slowDisk.Wait(Timeout);
            return ReadFile(path);
        });
        var ends = new List<PlaybackEnded>();
        engine.Ended += (_, e) =>
        {
            lock (ends)
            {
                ends.Add(e);
            }
        };
        await PlaysAWav(engine, 0.5);
        slowDisk.Reset();

        long load = engine.Load(MediaSource.Local(TempWav(5)));
        // The file before runs out meanwhile.
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        var snapshot = engine.Current;
        slowDisk.Set();

        Assert.False(snapshot.IsAtEnd);
        await Until(() => engine.Current is { IsLoaded: true } s && s.LoadId == load, () => _logs.All);
        await Until(() => engine.Current.HasStarted, () => _logs.All);
        Assert.False(engine.Current.IsAtEnd);
        lock (ends)
        {
            Assert.DoesNotContain(ends, e => e.LoadId == load && e.Reason != PlaybackEndReason.Stopped);
        }
    }

    [Fact]
    public async Task AFileThatFailsToOpen_EndsAsAFailure_InANewMpvToo()
    {
        using var engine = CreateEngine("deband=yes\n");
        var failed = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Ended += (_, e) =>
        {
            if (e.Reason == PlaybackEndReason.Error)
            {
                failed.TrySetResult(e);
            }
        };
        File.WriteAllText(ConfigFile, "deband=no\n");

        long load = engine.Load(MediaSource.Local(Path.Combine(_directory, "not-there.wav")));

        Assert.Equal(load, (await failed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken)).LoadId);
        Assert.Equal("no", engine.Option("deband"));
    }

    [Fact]
    public async Task AChangeWhileMpvStartsAgain_ReachesTheNewMpv()
    {
        MpvPlaybackEngine? made = null;
        using var engine = CreateEngine("deband=yes\n", readConfig: path =>
        {
            // While the new mpv is made.
            made?.SetSpeed(2);
            return ReadFile(path);
        });
        made = engine;
        // Once it runs, before the one before goes.
        engine.AfterNewMpvStarted = () => engine.SetVolume(25);
        File.WriteAllText(ConfigFile, "deband=no\n");

        await PlaysAWav(engine, 0.5);

        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("2.000000", engine.Option("speed"));
        Assert.Equal("25.000000", engine.Option("volume"));
    }

    [Fact]
    public async Task AChangeWhileAStartFails_ReachesTheMpvThatStays()
    {
        using var engine = CreateEngine("deband=yes\n");
        engine.SetVolume(40);
        engine.AfterNewMpvStarted = () =>
        {
            // Sent to the new mpv, which is about to go.
            engine.SetVolume(25);
            throw new InvalidOperationException("The new mpv failed (test).");
        };
        File.WriteAllText(ConfigFile, "deband=no\n");

        await PlaysAWav(engine, 0.5);

        Assert.Equal("yes", engine.Option("deband"));
        Assert.Equal("25.000000", engine.Option("volume"));
    }

    [Fact]
    public async Task ChangesThroughoutANewStart_EndWithTheLatestValue()
    {
        using var engine = CreateEngine("deband=yes\n");
        using var stop = new CancellationTokenSource();
        int last = 0;
        var changes = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                last = last % 100 + 1;
                engine.SetVolume(last);
            }
        }, TestContext.Current.CancellationToken);

        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(ConfigFile, $"deband=yes\n# {i}\n");
            await PlaysAWav(engine, 0.5);
        }
        await stop.CancelAsync();
        await changes;

        Assert.Equal(6, Count(StartLine));
        Assert.Equal(last.ToString("0.000000", CultureInfo.InvariantCulture), engine.Option("volume"));
    }

    [Fact]
    public async Task AStopOfTheFileBefore_WhileATitleWaitsForTheDisk_LetsThatTitlePlayWithoutTheFile()
    {
        using var slowDisk = new ManualResetEventSlim(initialState: true);
        using var engine = CreateEngine(LoopingProfile, readConfig: path =>
        {
            slowDisk.Wait(Timeout);
            return ReadFile(path);
        });
        var ends = new List<PlaybackEnded>();
        engine.Ended += (_, e) =>
        {
            lock (ends)
            {
                ends.Add(e);
            }
        };
        await PlaysAWav(engine, 60);
        slowDisk.Reset();

        long load = engine.Load(MediaSource.Local(TempWav(5)));
        // The file before plays on until the title has read the disk, and its profile goes too far meanwhile.
        await KeepChangingKeepOpen(engine, () => Count(StopLine) > 0);
        slowDisk.Set();

        await Until(() => engine.Current is { IsLoaded: true } s && s.LoadId == load, () => _logs.All);
        Assert.Equal("cplayer", engine.Option("player-operation-mode"));
        Assert.Equal("no", engine.Option("deband"));
        lock (ends)
        {
            Assert.DoesNotContain(ends, e => e.LoadId == load && e.Reason == PlaybackEndReason.Error);
        }
    }

    [Fact]
    public async Task AChangedFileThatGoesTooFarAsItsMpvStarts_DoesNotLoseTheTitle()
    {
        using var engine = CreateEngine("deband=yes\n");
        var ends = RecordEnds(engine);
        string wav = TempWav(5);
        File.WriteAllText(ConfigFile, LoopingProfile);
        // The new mpv is up, the title has not reached it, and its profile makes Granary stop it.
        engine.AfterNewMpvStarted = () =>
        {
            engine.AfterNewMpvStarted = null;
            GoesTooFar(engine);
        };

        long load = engine.Load(MediaSource.Local(wav));

        // It plays in the mpv without the file.
        await Until(() => engine.Option("player-operation-mode") == "cplayer" && engine.Option("path") == wav, () => _logs.All);
        await Until(() => engine.Current is { IsLoaded: true } s && s.LoadId == load, () => _logs.All);
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal(MpvConfigUse.StoppedDuringPlayback, _status.Current.Use);
        lock (ends)
        {
            Assert.DoesNotContain(ends, e => e.LoadId == load && e.Reason != PlaybackEndReason.Stopped);
        }
    }

    [Fact]
    public async Task AChangedFileThatGoesTooFar_WithNoMpvWithoutTheFile_EndsTheTitleAsAFailure()
    {
        using var engine = CreateEngine("deband=yes\n");
        var ends = RecordEnds(engine);
        File.WriteAllText(ConfigFile, LoopingProfile);
        bool stopped = false;
        engine.AfterNewMpvStarted = () =>
        {
            if (stopped)
            {
                throw new InvalidOperationException("The new mpv failed (test).");
            }
            stopped = true;
            GoesTooFar(engine);
        };

        long load = engine.Load(MediaSource.Local(TempWav(5)));

        await Until(() =>
        {
            lock (ends)
            {
                return ends.Any(e => e.LoadId == load && e.Reason == PlaybackEndReason.Error);
            }
        }, () => _logs.All);
        // Never into the mpv Granary stopped.
        Assert.Null(engine.Option("path"));
        Assert.False(engine.Current.IsLoaded);
    }

    [Fact]
    public async Task AStopAsTheTitleGoesToMpv_EndsItAsAFailure_AndTheNextPlaysWithoutTheFile()
    {
        using var engine = CreateEngine(LoopingProfile);
        var ends = RecordEnds(engine);
        // The title no longer waits: from here on the stop is the one to say how it ended.
        engine.BeforeLoadFile = () =>
        {
            engine.BeforeLoadFile = null;
            GoesTooFar(engine);
        };

        long load = engine.Load(MediaSource.Local(TempWav(5)));

        await Until(() =>
        {
            lock (ends)
            {
                return ends.Any(e => e.LoadId == load && e.Reason == PlaybackEndReason.Error);
            }
        }, () => _logs.All);
        await Until(() => engine.Option("player-operation-mode") == "cplayer", () => _logs.All);
        Assert.Null(engine.Option("path"));

        await PlaysAWav(engine, 0.5);

        Assert.Equal("no", engine.Option("deband"));
    }

    [Fact]
    public async Task AFailedStartWithoutTheFile_AfterGranaryStoppedATitle_KeepsAWorkingMpv()
    {
        using var engine = CreateEngine(LoopingProfile);
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

        await KeepChangingKeepOpen(engine, () => failed.Task.IsCompleted);

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
        using var engine = CreateEngine(LoopingProfile);
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
        long refused = engine.Load(MediaSource.Local(TempWav(0.5)));

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
        using var engine = CreateEngine(LoopingProfile);
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
        await KeepChangingKeepOpen(engine, () => Count(StopLine) > 0);

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

    private static List<PlaybackEnded> RecordEnds(MpvPlaybackEngine engine)
    {
        var ends = new List<PlaybackEnded>();
        engine.Ended += (_, e) =>
        {
            lock (ends)
            {
                ends.Add(e);
            }
        };
        return ends;
    }

    // As KeepChangingKeepOpen, from inside a start or a load, so it blocks: Granary has stopped the mpv when it returns.
    private void GoesTooFar(MpvPlaybackEngine engine)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (Count(StopLine) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Granary did not stop the mpv in time.");
            engine.Command("apply-profile", "loop");
            Thread.Sleep(50);
        }
    }

    // mpv can make its swap chain a moment after it starts, while other tests' mpv instances start too.
    private static async Task ASwapChainOrSkip(MpvPlaybackEngine engine)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (engine.SwapChain == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.SkipWhen(engine.SwapChain == 0, "No d3d11 swap chain here.");
    }

    // A short WAV played until mpv holds it at its end.
    private async Task AWavPlaysToItsEnd(MpvPlaybackEngine engine)
    {
        long load = await PlaysAWav(engine, 0.2);
        engine.SetPaused(false);
        await Until(() => engine.Current is { IsAtEnd: true } s && s.LoadId == load, () => _logs.All);
    }

    // A WAV of that length loaded and open; the load's id.
    private async Task<long> PlaysAWav(MpvPlaybackEngine engine, double seconds)
    {
        string wav = TempWav(seconds);
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

/// <summary>A changed mpv.conf, again and again, with the real libmpv. Skipped without libmpv-2.dll.</summary>
[Collection(nameof(HandleCounting))]
public sealed class MpvConfigNextTitleHandleTests : IDisposable
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
    public async Task TwentyChanges_OneTitleEach_LeaveNoHandlesBehind()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        Directory.CreateDirectory(_directory);
        string config = Path.Combine(_directory, MpvConfigFolder.ConfigFileName);
        File.WriteAllText(config, "deband=yes\n");
        var logs = new LogSink();
        string wav = WavFile.WriteTemp(0.5);
        try
        {
            using var engine = new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = VideoOutputKind.None, ConfigDirectory = _directory },
                logs.For<MpvPlaybackEngine>(), null, new MpvConfigStatus());
            // Warmed up: libmpv and the runtime make what they keep for the process on the first starts.
            await ChangedTitle(engine, 100);
            await ChangedTitle(engine, 101);
            int before = Handles();

            for (int i = 0; i < 20; i++)
            {
                await ChangedTitle(engine, i);
            }

            // Twenty mpv instances made and closed: a leak would show as hundreds of handles (threads, events, files).
            Assert.InRange(Handles() - before, int.MinValue, 60);
            Assert.Equal(23, logs.Lines.Count(line => line.Contains("mpv started with", StringComparison.Ordinal)));

            async Task ChangedTitle(MpvPlaybackEngine player, int number)
            {
                await File.WriteAllTextAsync(config, $"deband=yes\n# {number}\n", TestContext.Current.CancellationToken);
                long load = player.Load(MediaSource.Local(wav));
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (player.Current is not { IsLoaded: true } s || s.LoadId != load)
                {
                    Assert.True(DateTime.UtcNow < deadline, "The title did not open in time. " + logs.All);
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }
            }
        }
        finally
        {
            File.Delete(wav);
        }

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
