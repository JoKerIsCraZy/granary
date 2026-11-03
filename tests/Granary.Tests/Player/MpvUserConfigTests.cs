using Granary.Core.Playback;
using Granary.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.Player;

/// <summary>
/// The viewer's own mpv.conf with the real libmpv, headless: mpv applies it, and Granary's own options win. Skipped
/// without libmpv-2.dll.
/// </summary>
public sealed class MpvUserConfigTests : IDisposable
{
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

    private MpvPlaybackEngine CreateEngine(string? config, params (string Name, string Text)[] files) =>
        CreateEngine(VideoOutputKind.None, config, files);

    private MpvPlaybackEngine CreateEngine(VideoOutputKind output, string? config, params (string Name, string Text)[] files)
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        Directory.CreateDirectory(_directory);
        if (config is not null)
        {
            File.WriteAllText(Path.Combine(_directory, MpvConfigFolder.ConfigFileName), config);
        }
        foreach (var (name, text) in files)
        {
            File.WriteAllText(Path.Combine(_directory, name), text);
        }
        return new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = output, ConfigDirectory = _directory },
            _logs.For<MpvPlaybackEngine>(), null, _status);
    }

    private bool Logged(string text) => _logs.Lines.Any(line => line.Contains(text, StringComparison.Ordinal));

    private int Count(string text) => _logs.Lines.Count(line => line.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void WithoutAFile_MpvStartsAsBefore()
    {
        using var engine = CreateEngine(config: null);

        Assert.Equal("cplayer", engine.Option("player-operation-mode"));
        Assert.Equal("no", engine.Option("deband"));
        // Only Granary's core options: mpv's defaults for the rest.
        Assert.Equal("yes", engine.Option("load-scripts"));
        Assert.Equal("", engine.Option("input-conf"));
        Assert.False(Logged("mpv started with"));
        Assert.Equal(MpvConfigState.None, _status.Current);
    }

    [Fact]
    public void ThisMpv_KnowsEveryOptionGranaryKeepsWithAFile()
    {
        using var engine = CreateEngine("deband=yes\n");

        Assert.True(Logged("mpv started with"));
        Assert.False(Logged("mpv does not take"), _logs.All);
        // No empty command of Granary's own.
        Assert.False(Logged("Command name missing"), _logs.All);
        Assert.Equal("memory://", engine.Option("input-conf"));
        Assert.Equal("", engine.Option("input-commands"));
        Assert.Equal("no", engine.Option("register"));
    }

    [Fact]
    public async Task TheFilesOptionsApply_AndGranaryKeepsItsOwn()
    {
        // idle=no would end mpv as soon as it started, and vo=gpu would make a window of its own.
        using var engine = CreateEngine("deband=yes\nkeep-open=no\nidle=no\nvo=gpu\nload-scripts=no\ninclude=~~/more.conf\n",
            ("more.conf", "deband-iterations=3\n"));

        Assert.Equal("yes", engine.Option("deband"));
        // ~~/ is the folder.
        Assert.Equal("3", engine.Option("deband-iterations"));
        Assert.Equal("yes", engine.Option("keep-open"));
        Assert.Equal("yes", engine.Option("idle"));
        Assert.Equal("null", engine.Option("vo"));
        Assert.Equal("no", engine.Option("load-scripts"));
        Assert.True(Logged("mpv started with " + Path.Combine(_directory, "mpv.conf")));
        Assert.True(Logged("mpv.conf sets keep-open; Granary keeps its own"));
        Assert.True(Logged("mpv.conf sets vo; Granary keeps its own"));
        Assert.False(Logged("mpv.conf sets deband"));
        // keep-open, idle and vo; load-scripts=no is what Granary sets anyway.
        Assert.Equal(new MpvConfigState(MpvConfigUse.Used, "", 3), _status.Current);
        await PlaysAWav(engine);
    }

    [Fact]
    public async Task AProfileThatSetsOneOfGranarysOptions_IsSetBack()
    {
        using var engine = CreateEngine("[later]\nprofile-cond=true\nkeep-open=no\nsaturation=-50\n");

        // The profile applied (saturation) and keep-open is Granary's again.
        await Until(() => engine.Option("saturation") == "-50.000000" && engine.Option("keep-open") == "yes",
            () => $"saturation {engine.Option("saturation")}, keep-open {engine.Option("keep-open")}; {_logs.All}");
        await Until(() => Logged("mpv.conf set keep-open; Granary keeps its own"));
    }

    [Fact]
    public async Task AFileMpvRefuses_PlaysAsIfThereWereNone()
    {
        // An option that only lists the audio devices: mpv will not start with it.
        using var engine = CreateEngine("audio-device=help\ndeband=yes\n");

        Assert.Equal("no", engine.Option("deband"));
        Assert.True(Logged("mpv refused to start with"));
        Assert.Equal(new MpvConfigState(MpvConfigUse.NotUsed, "mpv refused to start with it"), _status.Current);
        await PlaysAWav(engine);
    }

    [Fact]
    public async Task AFileThatTurnsOffThePseudoGuiMode_IsRefused()
    {
        // In the player's own mode, mpv would not apply Granary's options over the file.
        using var engine = CreateEngine("player-operation-mode=cplayer\nidle=no\nvo=gpu\ndeband=yes\n");

        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("null", engine.Option("vo"));
        Assert.True(Logged("mpv.conf sets player-operation-mode, which Granary needs to keep its own options; mpv plays without it"));
        Assert.Equal(new MpvConfigState(MpvConfigUse.NotUsed, "it sets player-operation-mode, which Granary needs to keep its own options"),
            _status.Current);
        await PlaysAWav(engine);
    }

    [Fact]
    public async Task WhatTheCheckBeforeTheStartMissed_IsRefusedOnceMpvReadIt()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "mpv.conf"), "include=~~/more.conf\n");
        File.WriteAllText(Path.Combine(_directory, "more.conf"), "player-operation-mode=cplayer\nidle=no\ndeband=yes\n");
        // The check before the start reads the include as missing; mpv reads it.
        using var engine = new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = VideoOutputKind.None, ConfigDirectory = _directory },
            _logs.For<MpvPlaybackEngine>(), null, path => path.EndsWith("more.conf", StringComparison.Ordinal) ? null : File.ReadAllText(path));

        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("cplayer", engine.Option("player-operation-mode"));
        Assert.True(Logged("mpv.conf sets player-operation-mode, which Granary needs to keep its own options; mpv starts again without it"));
        await PlaysAWav(engine);
    }

    [Fact]
    public void AFilesOwnPseudoGuiProfile_JoinsGranarys_AfterIt()
    {
        // A harmless option there is played: mpv adds the file's section to the profile, after Granary's options.
        using var engine = CreateEngine("[pseudo-gui]\ndeband=yes\n");

        Assert.Equal("yes", engine.Option("deband"));
        Assert.Equal("yes", engine.Option("keep-open"));
        string profiles = engine.Option("profile-list") ?? "";
        int granarys = profiles.IndexOf("""{"key":"keep-open","value":"yes"}""", StringComparison.Ordinal);
        int files = profiles.IndexOf("""{"key":"deband","value":"yes"}""", StringComparison.Ordinal);
        Assert.True(granarys >= 0 && files > granarys, profiles);
    }

    [Theory]
    [InlineData("[pseudo-gui]\nkeep-open=no\ndeband=yes\n", null)]
    [InlineData("include=~~/more.conf\n", "[pseudo-gui]\nkeep-open=no\ndeband=yes\n")]
    public void AFilesOwnPseudoGuiProfile_ThatSetsOneOfGranarysOptions_IsRefused(string config, string? more)
    {
        using var engine = more is null ? CreateEngine(config) : CreateEngine(config, ("more.conf", more));

        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("yes", engine.Option("keep-open"));
        Assert.True(Logged("sets keep-open in its [pseudo-gui] profile"), _logs.All);
    }

    [Theory]
    [InlineData("log-file=\"{0}\"\ndeband=yes\n", null)]
    [InlineData("deband=yes\n[later]\nprofile-cond=true\nlog-file=\"{0}\"\n", null)]
    [InlineData("deband=yes\n[protocol.http]\nlog-file=\"{0}\"\nhttp-proxy={1}\nstream-lavf-o=http_proxy={1}\n", null)]
    [InlineData("deband=yes\n[protocol.http]\ninclude=~~/http.conf\n", "log-file=\"{0}\"\nhttp-proxy={1}\n")]
    public async Task AFileThatSetsAnOptionThatTakesDataOut_IsNotUsed_AndNothingLeaves(string config, string? included)
    {
        // mpv opens its own log as soon as a profile sets it, and writes the stream's address there before any set-back.
        await using var proxy = new TinyHttpServer(502, "Bad Gateway", [], "text/plain");
        await using var server = new TinyHttpServer(200, "OK", WavFile.Sine(0.5), "audio/wav");
        string log = Path.Combine(Path.GetDirectoryName(_directory)!, "mpv-own.log");
        string address = proxy.Url("").OriginalString;
        using var engine = included is null
            ? CreateEngine(string.Format(System.Globalization.CultureInfo.InvariantCulture, config, log, address))
            : CreateEngine(config, ("http.conf", string.Format(System.Globalization.CultureInfo.InvariantCulture, included, log, address)));
        var ended = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Ended += (_, e) => ended.TrySetResult(e);

        engine.Load(new MediaSource(server.Url("secret-proxy-key/media.wav"), []));

        Assert.Equal(PlaybackEndReason.EndOfFile, (await ended.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken)).Reason);
        Assert.True(Logged("which Granary does not let mpv.conf change; mpv plays without it"), _logs.All);
        Assert.Equal(MpvConfigUse.NotUsed, _status.Current.Use);
        Assert.StartsWith("it sets ", _status.Current.Reason, StringComparison.Ordinal);
        Assert.Equal("no", engine.Option("deband"));
        Assert.Empty(proxy.RequestHeaders);
        Assert.NotEmpty(server.RequestHeaders);
        Assert.False(File.Exists(log));
    }

    [Fact]
    public async Task TheFoldersKeyBindingsAreNotRead()
    {
        using var engine = CreateEngine("deband=yes\n", ("input.conf", "a quit\n"));

        Assert.Equal("yes", engine.Option("deband"));
        Assert.Equal("memory://", engine.Option("input-conf"));
        Assert.Equal("", engine.Option("input-commands"));
        await PlaysAWav(engine);
    }

    [Theory]
    [InlineData("input-commands=quit\ndeband=yes\n")]
    [InlineData("deband=yes\n[protocol.file]\ninput-commands=quit\n")]
    public void AFileThatRunsCommands_IsNotUsed(string config)
    {
        using var engine = CreateEngine(config);

        Assert.Equal("no", engine.Option("deband"));
        Assert.True(Logged("mpv.conf sets input-commands, which Granary does not let mpv.conf change; mpv plays without it"), _logs.All);
        Assert.Equal(new MpvConfigState(MpvConfigUse.NotUsed, "it sets input-commands, which Granary does not let mpv.conf change"),
            _status.Current);
    }

    [Fact]
    public async Task AProfileThatKeepsChangingAnOption_StopsTheTitle_AndTheNextPlaysWithoutTheFile()
    {
        // Long enough to still play while the set-backs run on a slow machine; the title after it is short.
        await using var server = new TinyHttpServer(200, "OK", WavFile.Sine(60.0), "audio/wav");
        await using var next = new TinyHttpServer(200, "OK", WavFile.Sine(0.5), "audio/wav");
        // A profile applied again and again, as a profile-cond that follows keep-open does after every set-back. Applied
        // here by name, so the test does not hang on how fast mpv's auto_profiles.lua sees each change.
        using var engine = CreateEngine("deband=yes\n[loop]\nkeep-open=no\n");
        engine.SetSpeed(1.5);
        var ends = new List<PlaybackEnded>();
        var failed = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (e.Reason == PlaybackEndReason.EndOfFile)
            {
                played.TrySetResult();
            }
        };

        long first = engine.Load(new MediaSource(server.Url("first/media.wav"), []));
        var deadline = DateTime.UtcNow + Timeout;
        while (!failed.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            engine.Command("apply-profile", "loop");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // The title ends as a failure, so the player offers to try again, or a server playback reconnects.
        var end = await failed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(first, end.LoadId);
        Assert.Equal(1, Count("mpv.conf keeps changing keep-open; Granary stopped and plays without your mpv.conf"));
        Assert.Equal(1, Count("mpv.conf set keep-open; Granary keeps its own"));
        Assert.Equal(new MpvConfigState(MpvConfigUse.StoppedDuringPlayback, "it keeps changing keep-open"), _status.Current);
        // A new mpv, without the file; the engine's own settings carry over.
        Assert.Equal("cplayer", engine.Option("player-operation-mode"));
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal("yes", engine.Option("keep-open"));
        Assert.Equal("1.500000", engine.Option("speed"));

        engine.Load(new MediaSource(next.Url("second/media.wav"), []));

        await played.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        lock (ends)
        {
            Assert.Single(ends, e => e.Reason == PlaybackEndReason.Error);
        }
    }

    [Fact]
    public async Task AProfileThatWouldKeepChangingTheLogFile_IsNotUsed_AndNoAddressReachesALog()
    {
        await using var server = new TinyHttpServer(200, "OK", WavFile.Sine(0.5), "audio/wav");
        string log = Path.Combine(Path.GetDirectoryName(_directory)!, "mpv-own.log");
        // Played, every set-back would start it over, and mpv would write each time.
        using var engine = CreateEngine($"deband=yes\n[loop]\nprofile-cond=p[\"path\"] ~= nil and p[\"log-file\"] == \"\"\nlog-file=\"{log}\"\n");
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal(new MpvConfigState(MpvConfigUse.NotUsed, "it sets log-file, which Granary does not let mpv.conf change"), _status.Current);

        foreach (string title in new[] { "secret-one", "secret-two" })
        {
            var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnEnded(object? sender, PlaybackEnded e)
            {
                if (e.Reason == PlaybackEndReason.EndOfFile)
                {
                    played.TrySetResult();
                }
            }
            engine.Ended += OnEnded;
            engine.Load(new MediaSource(server.Url(title + "/media.wav"), []));
            await played.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            engine.Ended -= OnEnded;
        }

        // No log of mpv's own is ever made, so neither title's address is in one.
        Assert.False(File.Exists(log));
        string logText = File.Exists(log) ? await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken) : "";
        Assert.DoesNotContain("secret-one", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-two", logText, StringComparison.Ordinal);
        Assert.False(Logged("keeps changing"), _logs.All);
    }

    [Fact]
    public async Task AFileGranaryCannotCheck_IsNotUsed_AndPlaybackStarts()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, MpvConfigFolder.ConfigFileName), "deband=yes\n");

        using var engine = new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = VideoOutputKind.None, ConfigDirectory = _directory },
            _logs.For<MpvPlaybackEngine>(), null, _ => throw new FormatException("Something Granary did not see coming."));

        Assert.Equal("cplayer", engine.Option("player-operation-mode"));
        Assert.Equal("no", engine.Option("deband"));
        Assert.True(Logged("mpv.conf could not be checked; mpv plays without it"), _logs.All);
        await PlaysAWav(engine);
    }

    [Theory]
    [InlineData("player-operation-mode=cplayer\nload-scripts=yes\n", "sets player-operation-mode")]
    [InlineData("[pseudo-gui]\nvo=gpu\n", "sets vo in its [pseudo-gui] profile")]
    [InlineData("[pseudo-gui]\nload-scripts=yes\n", "sets load-scripts, which Granary does not let mpv.conf change")]
    [InlineData("[protocol.file]\ninput-commands=quit\n", "sets input-commands, which Granary does not let mpv.conf change")]
    [InlineData("[protocol.http]\nlog-file=~~/mpv.log\n", "sets log-file, which Granary does not let mpv.conf change")]
    public void AnIncludeThatWouldUndoGranarysOptions_IsRefusedBeforeMpvStarts(string included, string reason)
    {
        string marker = Path.Combine(Path.GetDirectoryName(_directory)!, "script-ran.txt");
        Directory.CreateDirectory(Path.Combine(_directory, "scripts"));
        File.WriteAllText(Path.Combine(_directory, "scripts", "marker.lua"), $"local f = io.open([[{marker}]], \"w\") f:write(\"ran\") f:close()\n");

        using var engine = CreateEngine("include=~~/more.conf\n", ("more.conf", "deband=yes\n" + included));

        Assert.True(Logged($"mpv.conf {reason}"), _logs.All);
        Assert.True(Logged("mpv plays without it"), _logs.All);
        Assert.False(Logged("mpv starts again without it"), _logs.All);
        Assert.Equal("no", engine.Option("deband"));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void AnIncludeWithinAnInclude_IsFollowed()
    {
        using var engine = CreateEngine("include=~~/a.conf\n", ("a.conf", "include=~~/b.conf\n"), ("b.conf", "player-operation-mode=cplayer\ndeband=yes\n"));

        Assert.True(Logged("mpv.conf sets player-operation-mode, which Granary needs to keep its own options; mpv plays without it"));
        Assert.Equal("no", engine.Option("deband"));
        Assert.Equal(MpvConfigUse.NotUsed, _status.Current.Use);
    }

    [Fact]
    public async Task IncludesThatIncludeEachOther_AreRefused_AndMpvStartsWithoutThem()
    {
        // mpv itself would read them without end in mpv_initialize, and never start.
        using var engine = CreateEngine("include=~~/a.conf\n", ("a.conf", "deband=yes\ninclude=~~/mpv.conf\n"));

        Assert.True(Logged("mpv.conf has includes that include each other, which mpv reads without end; mpv plays without it"), _logs.All);
        Assert.Equal("no", engine.Option("deband"));
        await PlaysAWav(engine);
    }

    [Fact]
    public async Task InGranarysWindow_AHarmlessFile_KeepsTheOneSwapChain()
    {
        using var engine = CreateEngine(VideoOutputKind.D3D11Composition, "deband=yes\nscale=ewa_lanczossharp\n");
        // mpv makes its output on its own thread once it started: the swap chain can come a moment after the engine.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (engine.SwapChain == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.SkipWhen(engine.SwapChain == 0, "No d3d11 swap chain here.");

        await Task.Delay(1000, TestContext.Current.CancellationToken);

        // Counted from the engine's start, construction included: it logs each swap chain it takes.
        Assert.Equal(1, Count("Composition swap chain ready"));
        Assert.Equal("gpu-next", engine.Option("vo"));
        Assert.Equal("composition", engine.Option("d3d11-output-mode"));
        Assert.Equal("yes", engine.Option("deband"));
        Assert.False(Logged("Granary keeps its own"), _logs.All);
        Assert.False(Logged("mpv does not take"), _logs.All);
    }

    [Fact]
    public async Task ABadOption_IsLoggedInMpvsWords_AndTheRestApplies()
    {
        using var engine = CreateEngine("no-such-option=1\ndeband=yes\n");

        Assert.Equal("yes", engine.Option("deband"));
        await Until(() => Logged("no-such-option"));
    }

    private async Task PlaysAWav(MpvPlaybackEngine engine)
    {
        string wav = WavFile.WriteTemp(1.0);
        _files.Add(wav);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, s) =>
        {
            if (s.IsLoaded)
            {
                loaded.TrySetResult();
            }
        };
        engine.Load(MediaSource.Local(wav));
        await loaded.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    // mpv applies a profile and reports its options on its own threads.
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
