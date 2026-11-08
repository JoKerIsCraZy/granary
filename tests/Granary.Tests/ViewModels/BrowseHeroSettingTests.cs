using System.Text.Json;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>Settings → Navigation &amp; Cards → Title preview at the top: on by default, kept on this PC for each server and profile.</summary>
public sealed class BrowseHeroSettingTests : IDisposable
{
    private static readonly ServerEntry Server = new("srv1", new Uri("https://silo.test"), "Elena's Silo", null, default, default, null);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSessionManager _session = new();
    private readonly LogSink _logs = new();

    public BrowseHeroSettingTests()
    {
        _session.State = SessionState.Ready;
        _session.ActiveServer = Server;
        _session.ProfileId = "7";
    }

    private string FilePath => Path.Combine(_directory, "browse-hero.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private BrowseHeroSetting Create() => new(FilePath, _session, new ImmediateDispatcher(), _logs.For<BrowseHeroSetting>());

    [Fact]
    public void AtFirst_TheTitlePreviewShows_AndNothingIsWritten()
    {
        var setting = Create();

        Assert.True(setting.IsShown);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task TurnedOff_ItStaysOff_AtTheNextStart()
    {
        var setting = Create();

        await setting.SetShownAsync(false);

        Assert.False(setting.IsShown);
        Assert.False(Create().IsShown);
    }

    [Fact]
    public async Task TurnedOnAgain_ItShowsAgain_AtTheNextStart()
    {
        var setting = Create();
        await setting.SetShownAsync(false);

        await setting.SetShownAsync(true);

        Assert.True(setting.IsShown);
        Assert.True(Create().IsShown);
    }

    [Fact]
    public async Task TheSettingsSwitch_SavesWhatItIsSetTo()
    {
        var setting = Create();
        using var page = new TitlePreviewSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.IsShown = false;
        await setting.Saving;

        Assert.False(page.IsShown);
        Assert.Equal([nameof(TitlePreviewSettingsViewModel.IsShown)], changed);
        Assert.False(Create().IsShown);
    }

    [Fact]
    public async Task TheSettingsSwitch_ShowsTheChoiceOfTheProfileActing()
    {
        var setting = Create();
        await setting.SetShownAsync(false);
        using var page = new TitlePreviewSettingsViewModel(setting);
        Assert.False(page.IsShown);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.True(page.IsShown);
        Assert.Equal([nameof(TitlePreviewSettingsViewModel.IsShown)], changed);
    }

    [Fact]
    public async Task TheSettingsSwitch_LetsGoOfTheSetting_AsThePageLeaves()
    {
        var setting = Create();
        var page = new TitlePreviewSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.Dispose();
        await setting.SetShownAsync(false);

        Assert.Empty(changed);
    }

    [Fact]
    public async Task TheFile_NamesTheServerAndTheProfile()
    {
        await Create().SetShownAsync(false);

        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(["srv1/7"], file.RootElement.GetProperty("hidden_for").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task EachProfile_HasItsOwnSwitch()
    {
        var setting = Create();
        await setting.SetShownAsync(false);
        var changed = new List<string?>();
        setting.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.True(setting.IsShown);
        Assert.Contains(nameof(BrowseHeroSetting.IsShown), changed);

        _session.ProfileId = "7";
        _session.RaiseChanged();

        Assert.False(setting.IsShown);
    }

    [Fact]
    public async Task WithoutAProfile_TheSwitchGoesBack_AndNothingIsWritten()
    {
        _session.ProfileId = null;
        var setting = Create();
        using var page = new TitlePreviewSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.IsShown = false;
        await setting.Saving;

        Assert.True(page.IsShown);
        Assert.Equal([nameof(TitlePreviewSettingsViewModel.IsShown)], changed);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void AFileThatCannotBeRead_ShowsTheTitlePreview()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        Assert.True(Create().IsShown);
    }
}
