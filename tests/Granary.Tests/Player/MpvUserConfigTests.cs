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

    private MpvPlaybackEngine CreateEngine(string? config, params (string Name, string Text)[] files)
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
        return new MpvPlaybackEngine(new MpvEngineOptions { VideoOutput = VideoOutputKind.None, ConfigDirectory = _directory },
            _logs.For<MpvPlaybackEngine>());
    }

    private bool Logged(string text) => _logs.Lines.Any(line => line.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void WithoutAFile_MpvStartsAsBefore()
    {
        using var engine = CreateEngine(config: null);

        Assert.Equal("cplayer", engine.Option("player-operation-mode"));
        Assert.Equal("no", engine.Option("deband"));
        Assert.False(Logged("mpv started with"));
    }

    [Fact]
    public async Task TheFilesOptionsApply_AndGranaryKeepsItsOwn()
    {
        // idle=no would end mpv as soon as it started, and vo=gpu would make a window of its own.
        using var engine = CreateEngine("deband=yes\nkeep-open=no\nidle=no\nvo=gpu\nload-scripts=yes\ninclude=~~/more.conf\n",
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
        Assert.True(Logged("mpv did not start with"));
        await PlaysAWav(engine);
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
