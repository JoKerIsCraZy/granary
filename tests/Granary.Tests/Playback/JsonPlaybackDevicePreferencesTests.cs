using System.Globalization;
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
    public void AtFirst_TheStillWatchingPromptComesAfterThreeEpisodes() =>
        Assert.Equal(AutoPlayStreak.DefaultThreshold, Create().PassoutThreshold);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(20)]
    public async Task TheStillWatchingPrompt_StaysAtTheNextStart_WithTheOtherChoices(int episodes)
    {
        var preferences = Create();

        await preferences.SetUpDownAsync(PlayerUpDown.Volume);
        await preferences.SetPassoutThresholdAsync(episodes);

        var again = Create();
        Assert.Equal(episodes, again.PassoutThreshold);
        Assert.Equal(PlayerUpDown.Volume, again.UpDown);
    }

    [Theory]
    [InlineData("{ \"passout_threshold\": 21, \"up_down\": \"volume\" }")]
    [InlineData("{ \"passout_threshold\": -2, \"up_down\": \"volume\" }")]
    [InlineData("{ \"passout_threshold\": 99, \"up_down\": \"volume\" }")]
    [InlineData("{ \"up_down\": \"volume\" }")]
    public async Task ANumberOutOfBounds_OrAnOlderFile_MeansTheDefault_AndKeepsTheOtherChoices(string file)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, file, TestContext.Current.CancellationToken);

        var preferences = Create();

        Assert.Equal(AutoPlayStreak.DefaultThreshold, preferences.PassoutThreshold);
        Assert.Equal(PlayerUpDown.Volume, preferences.UpDown);
    }

    [Fact]
    public void AtFirst_AResumeIsNotRewound() => Assert.Equal(0, Create().ResumeRewindSeconds);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    public async Task RewindOnResume_StaysAtTheNextStart_WithTheOtherChoices(int seconds)
    {
        var preferences = Create();

        await preferences.SetUpDownAsync(PlayerUpDown.Volume);
        await preferences.SetResumeRewindSecondsAsync(seconds);

        var again = Create();
        Assert.Equal(seconds, again.ResumeRewindSeconds);
        Assert.Equal(PlayerUpDown.Volume, again.UpDown);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"\"resume_rewind_seconds\": {seconds}"),
            await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"resume_rewind_seconds\": 31, \"up_down\": \"volume\" }")]
    [InlineData("{ \"resume_rewind_seconds\": -1, \"up_down\": \"volume\" }")]
    [InlineData("{ \"up_down\": \"volume\" }")]
    public async Task ARewindOutOfBounds_OrAnOlderFile_MeansNone_AndKeepsTheOtherChoices(string file)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, file, TestContext.Current.CancellationToken);

        var preferences = Create();

        Assert.Equal(0, preferences.ResumeRewindSeconds);
        Assert.Equal(PlayerUpDown.Volume, preferences.UpDown);
    }

    [Fact]
    public void AtFirst_TheDiscordStatusIsOff() => Assert.False(Create().DiscordStatus);

    [Fact]
    public async Task TheDiscordStatus_StaysOnAtTheNextStart_WithTheOtherChoices()
    {
        var preferences = Create();

        await preferences.SetUpDownAsync(PlayerUpDown.Volume);
        await preferences.SetDiscordStatusAsync(true);

        var again = Create();
        Assert.True(again.DiscordStatus);
        Assert.Equal(PlayerUpDown.Volume, again.UpDown);
        Assert.Contains("\"discord_status\": true", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOlderFile_LeavesTheDiscordStatusOff_AndKeepsTheOtherChoices()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "{ \"decodes_in_software\": true, \"up_down\": \"volume\" }", TestContext.Current.CancellationToken);

        var preferences = Create();

        Assert.False(preferences.DiscordStatus);
        Assert.True(preferences.DecodesInSoftware);
    }

    [Fact]
    public async Task TurningTheDiscordStatusOnOrOff_SaysSo_ButTheSameChoiceAgainDoesNot()
    {
        var preferences = Create();
        int changes = 0;
        preferences.DiscordStatusChanged += (_, _) => changes++;

        await preferences.SetDiscordStatusAsync(true);
        await preferences.SetDiscordStatusAsync(true);
        await preferences.SetDiscordStatusAsync(false);

        Assert.Equal(2, changes);
        Assert.False(Create().DiscordStatus);
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
    public async Task TheLatestSave_IsOnDiskWhenItEnds_AsTheWindowWaitsForItOnClosing()
    {
        var preferences = Create();

        var saves = new[]
        {
            preferences.SetDecodesInSoftwareAsync(true),
            preferences.SetUpDownAsync(PlayerUpDown.Volume),
            preferences.SetDecodesInSoftwareAsync(false),
            preferences.SetDecodesInSoftwareAsync(true),
        };
        try
        {
            await preferences.LatestSave.WaitAsync(TestContext.Current.CancellationToken);

            // Each save runs after the one before, so the latest ends last.
            Assert.All(saves, save => Assert.True(save.IsCompleted));
            var again = Create();
            Assert.True(again.DecodesInSoftware);
            Assert.Equal(PlayerUpDown.Volume, again.UpDown);
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
