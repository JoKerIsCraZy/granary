using System.Text.Json;
using Granary.Core.Controller;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;

namespace Granary.Tests.Controller;

/// <summary>
/// Settings → Navigation &amp; Cards → Game controller: off by default, kept on this PC in <c>controller.json</c>; while it
/// is off no controller is read at all.
/// </summary>
public sealed class ControllerSettingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();

    private string FilePath => Path.Combine(_directory, "controller.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ControllerSetting Create() => new(FilePath, _logs.For<ControllerSetting>());

    [Fact]
    public void AtFirst_NoControllerIsRead_AndNothingIsWritten()
    {
        var setting = Create();

        Assert.False(setting.IsEnabled);
        Assert.False(setting.ReadsPads(isInFront: true, hasPads: true));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void TheFile_IsInTheDataDirectory()
    {
        Assert.Equal(Path.Combine(Granary.Core.AppPaths.DataDirectory, "controller.json"), ControllerSetting.DefaultPath);
    }

    [Fact]
    public async Task SwitchedOn_ItStaysOn_AtTheNextStart()
    {
        var setting = Create();

        await setting.SetEnabledAsync(true);

        Assert.True(setting.IsEnabled);
        Assert.True(Create().IsEnabled);
    }

    [Fact]
    public async Task SwitchedOffAgain_ItIsOff_AtTheNextStart()
    {
        var setting = Create();
        await setting.SetEnabledAsync(true);

        await setting.SetEnabledAsync(false);

        Assert.False(setting.IsEnabled);
        Assert.False(Create().IsEnabled);
    }

    [Fact]
    public async Task TheFile_SaysWhetherItIsOn()
    {
        await Create().SetEnabledAsync(true);

        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.True(file.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{ \"enabled\": null }")]
    [InlineData("null")]
    public void AFileThatCannotBeRead_LeavesItOff_AndSaysSo(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);

        Assert.False(Create().IsEnabled);
        Assert.Contains("game controller", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatCannotBeRead_IsWrittenAgainWhenTheSwitchIsUsed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");
        var setting = Create();

        await setting.SetEnabledAsync(true);

        Assert.True(Create().IsEnabled);
    }

    [Fact]
    public async Task AFileThatCannotBeWritten_StillTurnsItOnForThisRun()
    {
        // A directory where the file should be: the save fails.
        Directory.CreateDirectory(FilePath);
        var setting = Create();

        await setting.SetEnabledAsync(true);

        Assert.True(setting.IsEnabled);
        Assert.Contains("could not be saved", _logs.All, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task AChange_IsToldAtOnce_NotOnceSaved()
    {
        var setting = Create();
        var changed = new List<string?>();
        setting.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var saving = setting.SetEnabledAsync(true);
        var toldAtOnce = changed.ToList();
        await saving;

        Assert.Equal([nameof(ControllerSetting.IsEnabled)], toldAtOnce);
    }

    [Fact]
    public async Task TheSameChoiceAgain_ChangesAndWritesNothing()
    {
        var setting = Create();
        var changed = new List<string?>();
        setting.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await setting.SetEnabledAsync(false);

        Assert.Empty(changed);
        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SwitchedOff_NoPadIsRead(bool isInFront, bool hasPads)
    {
        var setting = Create();

        Assert.False(setting.ReadsPads(isInFront, hasPads));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public async Task SwitchedOn_PadsAreRead_OnlyWhileThereIsOneAndTheWindowIsInFront(bool isInFront, bool hasPads, bool reads)
    {
        var setting = Create();
        await setting.SetEnabledAsync(true);

        Assert.Equal(reads, setting.ReadsPads(isInFront, hasPads));
    }

    [Fact]
    public async Task TheSettingsSwitch_SavesWhatItIsSetTo()
    {
        var setting = Create();
        using var page = new ControllerSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.IsEnabled = true;
        await setting.Saving;

        Assert.True(page.IsEnabled);
        Assert.Equal([nameof(ControllerSettingsViewModel.IsEnabled)], changed);
        Assert.True(Create().IsEnabled);
    }

    [Fact]
    public async Task TheSettingsSwitch_LetsGoOfTheSetting_AsThePageLeaves()
    {
        var setting = Create();
        var page = new ControllerSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.Dispose();
        await setting.SetEnabledAsync(true);

        Assert.Empty(changed);
    }
}
