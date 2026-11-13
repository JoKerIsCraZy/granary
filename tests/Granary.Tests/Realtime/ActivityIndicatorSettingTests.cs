using System.Text.Json;
using Granary.Core.Auth;
using Granary.Core.Realtime;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>
/// Settings → Navigation &amp; Cards → Server activity: the administrator's activity indicator, on by default, kept on this
/// PC for each server and profile, and offered only to an administrator's account.
/// </summary>
public sealed class ActivityIndicatorSettingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSessionManager _session = new();
    private readonly LogSink _logs = new();

    public ActivityIndicatorSettingTests()
    {
        _session.State = SessionState.Ready;
        _session.ActiveServer = ScriptedSilo.Server;
        _session.ProfileId = "7";
        _session.Account = ServerActivityHarness.Administrator;
    }

    private string FilePath => Path.Combine(_directory, "activity-indicator.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ActivityIndicatorSetting Create() => new(FilePath, _session, new ImmediateDispatcher(), _logs.For<ActivityIndicatorSetting>());

    [Fact]
    public void AtFirst_TheIndicatorIsOn_AndNothingIsWritten()
    {
        var setting = Create();

        Assert.True(setting.IsShown);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task SwitchedOff_ItStaysOff_AtTheNextStart()
    {
        var setting = Create();

        await setting.SetShownAsync(false);

        Assert.False(setting.IsShown);
        Assert.False(Create().IsShown);
    }

    [Fact]
    public async Task SwitchedOnAgain_ItIsOn_AtTheNextStart()
    {
        var setting = Create();
        await setting.SetShownAsync(false);

        await setting.SetShownAsync(true);

        Assert.True(setting.IsShown);
        Assert.True(Create().IsShown);
    }

    [Fact]
    public async Task TheFile_NamesTheServerAndTheProfile()
    {
        await Create().SetShownAsync(false);

        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Equal([$"{ScriptedSilo.Server.Id}/7"], file.RootElement.GetProperty("hidden_for").EnumerateArray().Select(e => e.GetString()));
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
        Assert.True(setting.IsShownFor(ScriptedSilo.Server.Id, "8"));
        Assert.False(setting.IsShownFor(ScriptedSilo.Server.Id, "7"));
        Assert.Contains(nameof(ActivityIndicatorSetting.IsShown), changed);

        _session.ProfileId = "7";
        _session.RaiseChanged();

        Assert.False(setting.IsShown);
    }

    [Fact]
    public async Task WithoutAProfile_TheSwitchGoesBack_OnceItsChangeIsDone_AndNothingIsWritten()
    {
        _session.ProfileId = null;
        var dispatcher = new QueuedDispatcher();
        var setting = new ActivityIndicatorSetting(FilePath, _session, dispatcher, _logs.For<ActivityIndicatorSetting>());
        using var page = new ActivityIndicatorSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.IsShown = false;
        await setting.Saving;

        // Not from inside the switch's own change, which its binding would not take.
        Assert.Empty(changed);
        dispatcher.RunAll();
        Assert.True(page.IsShown);
        Assert.Equal([nameof(ActivityIndicatorSettingsViewModel.IsShown)], changed);
        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "hidden_for": null }""")]
    [InlineData("""{ "hidden_for": [ null, "srv1/8" ] }""")]
    public void AFileThatCannotBeRead_OrNamesAnotherProfile_LeavesTheIndicatorOn(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);

        Assert.True(Create().IsShown);
    }

    [Fact]
    public void TheSwitch_IsOfferedToAnAdministratorsAccount()
    {
        using var page = new ActivityIndicatorSettingsViewModel(Create());

        Assert.True(page.IsOffered);
        Assert.True(page.IsShown);
    }

    [Fact]
    public void TheSwitch_IsNotOffered_ToAnAccountThatIsNoAdministrator()
    {
        _session.Account = ServerActivityHarness.Administrator with { Role = "user" };

        using var page = new ActivityIndicatorSettingsViewModel(Create());

        Assert.False(page.IsOffered);
    }

    [Fact]
    public void TheSwitch_ComesAndGoes_AsTheAccountIsMadeOrUnmadeAnAdministrator()
    {
        _session.Account = ServerActivityHarness.Administrator with { Role = "user" };
        using var page = new ActivityIndicatorSettingsViewModel(Create());
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _session.Account = ServerActivityHarness.Administrator;
        _session.RaiseChanged();

        Assert.True(page.IsOffered);
        Assert.Contains(nameof(ActivityIndicatorSettingsViewModel.IsOffered), changed);

        _session.State = SessionState.SignedOut;
        _session.RaiseChanged();

        Assert.False(page.IsOffered);
    }

    [Fact]
    public async Task TheSettingsSwitch_SavesWhatItIsSetTo()
    {
        var setting = Create();
        using var page = new ActivityIndicatorSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.IsShown = false;
        await setting.Saving;

        Assert.False(page.IsShown);
        Assert.Equal([nameof(ActivityIndicatorSettingsViewModel.IsShown)], changed);
        Assert.False(Create().IsShown);
    }

    [Fact]
    public async Task TheSettingsSwitch_LetsGoOfTheSetting_AsThePageLeaves()
    {
        var setting = Create();
        var page = new ActivityIndicatorSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.Dispose();
        await setting.SetShownAsync(false);

        Assert.Empty(changed);
    }
}
