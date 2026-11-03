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
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning ", StringComparison.Ordinal) && line.Contains("could not be saved", StringComparison.Ordinal));
    }

    [Fact]
    public void AtFirst_UpAndDownShowTheControlsAndMenus() =>
        Assert.Equal(PlayerUpDown.ControlsAndMenus, Create().UpDown);

    [Fact]
    public async Task UpAndDownForTheVolume_StayAtTheNextStart_WithTheOtherChoice()
    {
        var preferences = Create();

        await preferences.SetDecodesInSoftwareAsync(true);
        await preferences.SetUpDownAsync(PlayerUpDown.Volume);

        var again = Create();
        Assert.Equal(PlayerUpDown.Volume, again.UpDown);
        Assert.True(again.DecodesInSoftware);
        Assert.Contains("\"up_down\": \"volume\"", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownUpAndDown_ShowsTheControlsAndMenus()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "{ \"decodes_in_software\": true, \"up_down\": \"zoom\" }", TestContext.Current.CancellationToken);

        var preferences = Create();

        Assert.Equal(PlayerUpDown.ControlsAndMenus, preferences.UpDown);
        Assert.True(preferences.DecodesInSoftware);
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
