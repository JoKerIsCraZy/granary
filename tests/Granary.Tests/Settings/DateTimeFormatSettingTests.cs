using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Settings;
using Granary.Core.Texts;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>
/// The profile's date and time formats (<c>ui.date_format</c>, <c>ui.time_format</c>), read with every profile, applied at
/// once and kept on this PC for the next start, as the web's DateTimeFormatProvider (hooks/useDateTimeFormat.tsx).
/// </summary>
public sealed class DateTimeFormatSettingTests : IDisposable
{
    private const string EffectivePath = "api/v2/settings/values/effective";
    private const string DatePath = "api/v2/settings/values/ui.date_format";
    private const string TimePath = "api/v2/settings/values/ui.time_format";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LibraryHarness _harness = new();
    private readonly List<DateTimePreferences> _applied = [];

    public DateTimeFormatSettingTests()
    {
        _harness.Answer(EffectivePath, Answer("DD/MM/YYYY", "24h"));
        _harness.Silo.On(HttpMethod.Put, DatePath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Put, TimePath, ScriptedSilo.NoContent());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_directory, "date-time-format.json");

    private static string Answer(string? date, string? time) => $$"""
        { "items": [
            { "key": "ui.date_format", "source": "profile", "value": {{Value(date)}} },
            { "key": "ui.time_format", "source": "profile", "value": {{Value(time)}} }],
          "revision": 30 }
        """;

    private static string Value(string? value) => value is null ? "\"auto\"" : $"\"{value}\"";

    private DateTimeFormatFile KeptFile() => new(FilePath, _harness.Logs.For<DateTimeFormatFile>());

    private DateTimeFormatSetting Create(DateTimeFormatFile? file = null)
    {
        var api = new SettingsApi(_harness.Silo.Http);
        return new(_harness.Session, new ProfileSettings(api, _harness.Session, _harness.Logs.For<ProfileSettings>()), api, file ?? KeptFile(),
            new ImmediateDispatcher(), _harness.Logs.For<DateTimeFormatSetting>(), _applied.Add);
    }

    [Fact]
    public void BeforeAnyRead_TheFormatsKeptOnThisPcApply()
    {
        var setting = Create();

        Assert.Equal(DateTimePreferences.Default, setting.Preferences);
        Assert.False(setting.IsReady);
    }

    [Fact]
    public async Task TheProfilesFormats_AreRead_AppliedAtOnce_AndKeptForTheNextStart()
    {
        var setting = Create();

        await setting.StartAsync();

        var expected = DateTimePreferences.Parse("DD/MM/YYYY", "24h");
        Assert.True(setting.IsReady);
        Assert.Equal(expected, setting.Preferences);
        Assert.Equal(expected, _applied[^1]);
        Assert.Equal(expected, KeptFile().AtStart);
        Assert.Equal("keys=ui.date_format&keys=ui.time_format", Assert.Single(_harness.Queries(EffectivePath)));
    }

    [Fact]
    public async Task AnUnknownValue_OrAMissingKey_IsAuto()
    {
        _harness.Answer(EffectivePath, """{ "items": [{ "key": "ui.date_format", "source": "profile", "value": "DD.MM.YYYY" }], "revision": 30 }""");
        var setting = Create();

        await setting.StartAsync();

        Assert.True(setting.IsReady);
        Assert.Same(DateTimePreferences.Default, setting.Preferences);
    }

    [Fact]
    public async Task AFailedRead_KeepsTheFormatsKept()
    {
        var kept = DateTimePreferences.Parse("YYYY-MM-DD", "12h");
        await KeptFile().KeepAsync(kept);
        _harness.Answer(EffectivePath, ScriptedSilo.Problem(503, "unavailable"));
        var setting = Create();

        await setting.StartAsync();

        Assert.False(setting.IsReady);
        Assert.True(setting.LoadFailed);
        Assert.Equal(kept, setting.Preferences);
        Assert.Equal(kept, KeptFile().AtStart);
    }

    [Fact]
    public async Task AChoice_AppliesAtOnce_AndIsSavedForTheProfile()
    {
        var setting = Create();
        await setting.StartAsync();

        await setting.ChooseDateFormatAsync(DateTimePreferences.MonthDayYear);

        var expected = DateTimePreferences.Parse("MM/DD/YYYY", "24h");
        Assert.Equal(expected, setting.Preferences);
        Assert.Equal(expected, _applied[^1]);
        Assert.Equal(expected, KeptFile().AtStart);
        var put = Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, DatePath));
        Assert.Equal("?scope=profile", put.Uri.Query);
        Assert.Equal("""{"value":"MM/DD/YYYY"}""", put.Body);
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Delete, DatePath));
    }

    [Fact]
    public async Task ATimeChoice_IsSavedUnderItsKey()
    {
        var setting = Create();
        await setting.StartAsync();

        await setting.ChooseTimeFormatAsync(DateTimePreferences.Auto);

        Assert.Equal(DateTimePreferences.Parse("DD/MM/YYYY", "auto"), setting.Preferences);
        Assert.Equal("""{"value":"auto"}""", Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, TimePath)).Body);
    }

    [Fact]
    public async Task ARefusedChoice_GoesBackToWhatTheServerHas()
    {
        _harness.Silo.On(HttpMethod.Put, DatePath, ScriptedSilo.Problem(500, "internal"));
        var setting = Create();
        await setting.StartAsync();

        await setting.ChooseDateFormatAsync(DateTimePreferences.YearMonthDay);

        var expected = DateTimePreferences.Parse("DD/MM/YYYY", "24h");
        Assert.Equal(expected, setting.Preferences);
        Assert.Equal(expected, _applied[^1]);
        Assert.Equal(expected, KeptFile().AtStart);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task ASaveRefusedLater_GoesBack_AndTheNextChoiceIsStillSaved()
    {
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, DatePath,
            async () =>
            {
                await release.Task;
                return await ScriptedSilo.Problem(500, "internal")();
            },
            ScriptedSilo.NoContent());
        var setting = Create();
        await setting.StartAsync();

        var refused = setting.ChooseDateFormatAsync(DateTimePreferences.YearMonthDay);
        release.SetResult();
        await refused.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(DateTimePreferences.DayMonthYear, setting.Preferences.DateFormat);
        Assert.Equal(2, _harness.Count(EffectivePath));

        await setting.ChooseDateFormatAsync(DateTimePreferences.MonthDayYear)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, _harness.Silo.SentTo(HttpMethod.Put, DatePath).Count);
        Assert.Equal(DateTimePreferences.MonthDayYear, setting.Preferences.DateFormat);
    }

    [Fact]
    public async Task AChoice_GoesOutAsTheProfileItWasMadeFor()
    {
        var setting = Create();
        await setting.StartAsync();

        await setting.ChooseTimeFormatAsync(DateTimePreferences.TwelveHours);

        Assert.Equal("7", Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, TimePath)).AsProfile);
    }

    [Fact]
    public async Task BeforeTheProfilesFormatsAreKnown_NoChoiceIsSaved()
    {
        _harness.Answer(EffectivePath, ScriptedSilo.Problem(503, "unavailable"));
        var setting = Create();
        await setting.StartAsync();

        await setting.ChooseDateFormatAsync(DateTimePreferences.YearMonthDay);

        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Put, DatePath));
        Assert.Same(DateTimePreferences.Default, setting.Preferences);
    }

    [Fact]
    public async Task AnotherProfile_IsReadAfresh()
    {
        var setting = Create();
        await setting.StartAsync();
        _harness.Answer(EffectivePath, Answer(null, "12h"));

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        await setting.Working;

        Assert.Equal(DateTimePreferences.Parse("auto", "12h"), setting.Preferences);
        Assert.Equal(DateTimePreferences.Parse("auto", "12h"), KeptFile().AtStart);
    }

    [Fact]
    public async Task SignedOut_TheFormatsInUseStay()
    {
        var setting = Create();
        await setting.StartAsync();

        _harness.Session.State = SessionState.SignedOut;
        _harness.Session.ActiveServer = null;
        _harness.Session.ProfileId = null;
        _harness.Session.RaiseChanged();
        await setting.Working;

        Assert.False(setting.IsReady);
        Assert.Equal(DateTimePreferences.Parse("DD/MM/YYYY", "24h"), setting.Preferences);
    }

    [Fact]
    public async Task AReadThatBeganBeforeAChoice_DoesNotPutTheOldFormatBack()
    {
        var setting = Create();
        await setting.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(EffectivePath, LibraryHarness.Held(release, Answer("DD/MM/YYYY", "24h")));
        var read = setting.RefreshAsync();

        var choice = setting.ChooseDateFormatAsync(DateTimePreferences.YearMonthDay);
        release.SetResult();
        await read;
        await choice;

        Assert.Equal(DateTimePreferences.YearMonthDay, setting.Preferences.DateFormat);
    }
}
