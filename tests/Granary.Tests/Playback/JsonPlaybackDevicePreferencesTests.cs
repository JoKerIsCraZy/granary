using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>Whether this PC plays HEVC, VP9, AV1 and HDR directly without full hardware decoding, kept across starts.</summary>
public sealed class JsonPlaybackDevicePreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();

    private string FilePath => Path.Combine(_directory, "playback-device.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonPlaybackDevicePreferences Create() => new(FilePath, _logs.For<JsonPlaybackDevicePreferences>());

    [Fact]
    public void AtFirst_DirectPlayNeedsTheGpu()
    {
        Assert.False(Create().DecodesInSoftware);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void TheFile_IsInTheDataDirectory() =>
        Assert.Equal(Path.Combine(Granary.Core.AppPaths.DataDirectory, "playback-device.json"), JsonPlaybackDevicePreferences.DefaultPath);

    [Fact]
    public async Task TurnedOn_StaysOnAtTheNextStart()
    {
        var preferences = Create();

        await preferences.SetDecodesInSoftwareAsync(true);

        Assert.True(preferences.DecodesInSoftware);
        Assert.True(Create().DecodesInSoftware);
        Assert.Contains("\"decodes_in_software\": true", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangesInARow_SaveTheLastOne()
    {
        var preferences = Create();

        await Task.WhenAll(preferences.SetDecodesInSoftwareAsync(true), preferences.SetDecodesInSoftwareAsync(false), preferences.SetDecodesInSoftwareAsync(true));

        Assert.True(Create().DecodesInSoftware);
    }

    [Fact]
    public async Task ASaveThatFails_KeepsTheChoiceForThisRun_AndLeavesNoHalfWrittenFile()
    {
        // A folder where the file belongs: the move onto it fails.
        Directory.CreateDirectory(FilePath);
        var preferences = Create();

        await preferences.SetDecodesInSoftwareAsync(true);

        Assert.True(preferences.DecodesInSoftware);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning ", StringComparison.Ordinal) && line.Contains("could not be saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLatestSave_IsOnDiskWhenItEnds_AsTheWindowWaitsForItOnClosing()
    {
        var preferences = Create();

        var saves = new[]
        {
            preferences.SetDecodesInSoftwareAsync(true),
            preferences.SetDecodesInSoftwareAsync(false),
            preferences.SetDecodesInSoftwareAsync(true),
        };
        try
        {
            await preferences.LatestSave.WaitAsync(TestContext.Current.CancellationToken);

            // Each save runs after the one before, so the latest ends last.
            Assert.All(saves, save => Assert.True(save.IsCompleted));
            Assert.True(Create().DecodesInSoftware);
        }
        finally
        {
            await Task.WhenAll(saves);
        }
    }

    [Fact]
    public void BeforeAnySave_TheLatestSaveIsDone() => Assert.True(Create().LatestSave.IsCompleted);

    [Fact]
    public async Task ASave_LeavesNoTemporaryFile()
    {
        var preferences = Create();

        await preferences.SetDecodesInSoftwareAsync(true);

        Assert.Equal(["playback-device.json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AnUnreadableFile_LeavesItOff()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "{ \"decodes_in_software\": tr", TestContext.Current.CancellationToken);

        Assert.False(Create().DecodesInSoftware);
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning ", StringComparison.Ordinal));
    }
}
