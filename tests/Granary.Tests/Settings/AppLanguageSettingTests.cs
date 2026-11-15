using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Settings;
using Granary.Core.Texts;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>
/// The profile's app language (<c>ui.app_language</c>), read with every profile and kept on this PC for the next start, as
/// Silo's apps keep the language they last applied (silo-server docs/architecture/i18n.md).
/// </summary>
public sealed class AppLanguageSettingTests : IDisposable
{
    private const string EffectivePath = "api/v2/settings/values/effective";
    private const string ValuePath = "api/v2/settings/values/ui.app_language";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LibraryHarness _harness = new();

    public AppLanguageSettingTests()
    {
        _harness.Answer(EffectivePath, Answer("de"));
        _harness.Silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Delete, ValuePath, ScriptedSilo.NoContent());
    }

    private string FilePath => Path.Combine(_directory, "app-language.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>The effective app language as the server answers it; null follows the device.</summary>
    private static string Answer(string? language, string source = "profile") => $$"""
        { "items": [{ "key": "ui.app_language", "source": "{{(language is null ? "default" : source)}}", "value": {{(language is null ? "null" : $"\"{language}\"")}} }],
          "revision": 28 }
        """;

    private AppLanguageFile KeptFile() => new(FilePath, _harness.Logs.For<AppLanguageFile>());

    private AppLanguageSetting Create(AppLanguageFile? file = null)
    {
        var api = new SettingsApi(_harness.Silo.Http);
        return new(_harness.Session, new ProfileSettings(api, _harness.Session, _harness.Logs.For<ProfileSettings>()), api, file ?? KeptFile(),
            new ImmediateDispatcher(), _harness.Logs.For<AppLanguageSetting>());
    }

    private List<string> Writes() =>
        [.. _harness.Silo.Sent.Where(r => r.Uri.AbsolutePath.EndsWith("/ui.app_language", StringComparison.Ordinal))
            .Select(r => $"{r.Method} {r.Uri.Query} {r.Body}".TrimEnd())];

    private async Task<AppLanguageSetting> ReadyAsync()
    {
        var setting = Create();
        await setting.StartAsync();
        return setting;
    }

    [Fact]
    public async Task TheProfilesLanguage_IsRead_AndKeptForTheNextStart()
    {
        var setting = Create();

        await setting.StartAsync();

        Assert.Equal(AppLanguageState.Ready, setting.State);
        Assert.Equal("de", setting.Stored);
        Assert.Equal("de", KeptFile().AtStart);
        Assert.Contains("keys=ui.app_language", Assert.Single(_harness.Queries(EffectivePath)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProfileThatFollowsTheDevice_LetsTheNextStartFollowWindows()
    {
        await KeptFile().KeepAsync("fr");
        _harness.Answer(EffectivePath, Answer(null));
        var setting = Create();

        await setting.StartAsync();

        Assert.Equal(AppLanguageState.Ready, setting.State);
        Assert.Null(setting.Stored);
        Assert.Null(KeptFile().AtStart);
    }

    [Fact]
    public async Task AServerWithoutTheSetting_OffersNoChoice_AndTheNextStartFollowsWindows()
    {
        // Settings contract revision 28 added it; an older server refuses the key.
        await KeptFile().KeepAsync("fr");
        _harness.Answer(EffectivePath, ScriptedSilo.Problem(422, "validation_failed"));
        var setting = Create();

        await setting.StartAsync();

        Assert.Equal(AppLanguageState.Unsupported, setting.State);
        Assert.Null(KeptFile().AtStart);
    }

    [Fact]
    public async Task AFailedRead_KeepsTheLanguageKept()
    {
        await KeptFile().KeepAsync("fr");
        _harness.Answer(EffectivePath, ScriptedSilo.Problem(503, "unavailable"));
        var setting = Create();

        await setting.StartAsync();

        Assert.Equal(AppLanguageState.Unavailable, setting.State);
        Assert.Equal("fr", KeptFile().AtStart);
    }

    [Fact]
    public async Task AnotherProfile_IsReadAfresh()
    {
        var setting = Create();
        await setting.StartAsync();
        _harness.Answer(EffectivePath, Answer("ja"));

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        await setting.Working;

        Assert.Equal("ja", setting.Stored);
        Assert.Equal("ja", KeptFile().AtStart);
    }

    [Fact]
    public async Task SignedOut_NothingIsKnown_AndTheLanguageKeptStays()
    {
        var setting = Create();
        await setting.StartAsync();

        _harness.Session.State = SessionState.SignedOut;
        _harness.Session.ActiveServer = null;
        _harness.Session.ProfileId = null;
        _harness.Session.RaiseChanged();
        await setting.Working;

        Assert.Equal(AppLanguageState.Loading, setting.State);
        Assert.Null(setting.Stored);
        Assert.Equal("de", KeptFile().AtStart);
    }

    [Fact]
    public async Task ARefresh_ReadsAgain()
    {
        var setting = Create();
        await setting.StartAsync();
        _harness.Answer(EffectivePath, Answer("es"));

        await setting.RefreshAsync();

        Assert.Equal("es", setting.Stored);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task AnAnswerForAnEarlierProfile_IsDropped()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(EffectivePath, LibraryHarness.Held(release, Answer("de")), ScriptedSilo.Json(System.Net.HttpStatusCode.OK, Answer("ja")));
        var setting = Create();
        var first = setting.StartAsync();

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        var second = setting.Working;
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal("ja", setting.Stored);
        Assert.Equal("ja", KeptFile().AtStart);
    }

    [Fact]
    public async Task AChoice_IsSavedForTheProfile_AndKeptForTheNextStart()
    {
        // As the web's AccessibilitySettings and the apps write it: the profile's value, which follows the person to every device.
        var setting = await ReadyAsync();

        await setting.ChooseAsync("fr");

        Assert.Equal("fr", setting.Stored);
        Assert.Equal(["PUT ?scope=profile {\"value\":\"fr\"}"], Writes());
        Assert.Equal(AppLanguageFailure.None, setting.Failure);
        Assert.Equal("fr", KeptFile().AtStart);
    }

    [Fact]
    public async Task MatchDevice_SavesNull()
    {
        var setting = await ReadyAsync();

        await setting.ChooseAsync(null);

        Assert.Null(setting.Stored);
        Assert.Equal(["PUT ?scope=profile {\"value\":null}"], Writes());
        Assert.Null(KeptFile().AtStart);
    }

    [Fact]
    public async Task AChoiceOverThisPCsOwnValue_ClearsIt_SoTheChoiceAppliesHereToo()
    {
        _harness.Answer(EffectivePath, Answer("ja", source: SettingScopes.ProfileDevice));
        var setting = await ReadyAsync();

        await setting.ChooseAsync("es");

        Assert.Equal(["PUT ?scope=profile {\"value\":\"es\"}", "DELETE ?scope=profile_device"], Writes());
        Assert.False(setting.StoredForThisDevice);
        Assert.Equal("es", KeptFile().AtStart);
    }

    [Fact]
    public async Task ThisPCsValue_ThatIsAlreadyGone_IsNoFailure()
    {
        _harness.Answer(EffectivePath, Answer("ja", source: SettingScopes.ProfileDevice));
        _harness.Silo.On(HttpMethod.Delete, ValuePath, ScriptedSilo.Problem(404, "not_found"));
        var setting = await ReadyAsync();

        await setting.ChooseAsync("es");

        Assert.Equal(AppLanguageFailure.None, setting.Failure);
        Assert.Equal("es", KeptFile().AtStart);
    }

    [Fact]
    public async Task ARefusedSave_GoesBack_AndSaysSo()
    {
        var setting = await ReadyAsync();
        _harness.Silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.Problem(503, "unavailable"));

        await setting.ChooseAsync("fr");

        Assert.Equal("de", setting.Stored);
        Assert.Equal(AppLanguageFailure.NotSaved, setting.Failure);
        Assert.False(setting.IsSaving);
        Assert.Equal("de", KeptFile().AtStart);
    }

    [Fact]
    public async Task AFailedClear_SaysThisPCKeepsItsOwn_AndCanBeTriedAgain()
    {
        _harness.Answer(EffectivePath, Answer("ja", source: SettingScopes.ProfileDevice));
        _harness.Silo.On(HttpMethod.Delete, ValuePath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.NoContent());
        var setting = await ReadyAsync();

        await setting.ChooseAsync("es");

        Assert.Equal(AppLanguageFailure.DeviceNotCleared, setting.Failure);
        Assert.Equal("es", setting.Stored);
        Assert.Equal("ja", KeptFile().AtStart);

        await setting.RetryDeviceClearAsync();

        Assert.Equal(AppLanguageFailure.None, setting.Failure);
        Assert.Equal("es", KeptFile().AtStart);
        Assert.Equal(["PUT ?scope=profile {\"value\":\"es\"}", "DELETE ?scope=profile_device", "DELETE ?scope=profile_device"], Writes());
    }

    [Fact]
    public async Task OneChoiceAtATime_ASecondWhileTheFirstSaves_IsRefused()
    {
        // As the web: two saves in flight could land in either order and leave the earlier choice stored.
        var setting = await ReadyAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ValuePath, LibraryHarness.Held(release, "{}"));

        var first = setting.ChooseAsync("fr");
        Assert.True(setting.IsSaving);
        await setting.ChooseAsync("es");
        release.SetResult();
        await first;

        Assert.Equal("fr", setting.Stored);
        Assert.Single(Writes());
    }

    [Fact]
    public async Task TheSameChoiceAgain_IsNotSaved()
    {
        var setting = await ReadyAsync();

        await setting.ChooseAsync("de");

        Assert.Empty(Writes());
    }

    [Fact]
    public async Task BeforeTheProfilesLanguageIsKnown_NothingIsSaved()
    {
        _harness.Answer(EffectivePath, ScriptedSilo.Problem(503, "unavailable"));
        var setting = await ReadyAsync();

        await setting.ChooseAsync("fr");

        Assert.Empty(Writes());
        Assert.Null(setting.Stored);
    }

    [Fact]
    public async Task AChoiceMadeForOneProfile_IsNotSavedForTheNext()
    {
        _harness.Silo.ActingProfile = () => _harness.Session.ProfileId;
        var setting = await ReadyAsync();
        _harness.Answer(EffectivePath, Answer("ja"));
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ValuePath, LibraryHarness.Held(release, "{}"));

        var saving = setting.ChooseAsync("fr");
        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        release.SetResult();
        await saving;
        await setting.Working;

        Assert.Equal("ja", setting.Stored);
        Assert.Equal("ja", KeptFile().AtStart);
    }
}
