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

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LibraryHarness _harness = new();

    public AppLanguageSettingTests()
    {
        _harness.Answer(EffectivePath, Answer("de"));
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
        return new(_harness.Session, new ProfileSettings(api, _harness.Session, _harness.Logs.For<ProfileSettings>()), file ?? KeptFile(),
            new ImmediateDispatcher(), _harness.Logs.For<AppLanguageSetting>());
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
}
