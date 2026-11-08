using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>The profile's Title Pages settings beside the spoiler switches, read and saved as the web's Title Pages keeps them.</summary>
public sealed class TitlePageSettingsTests
{
    internal const string EffectivePath = "api/v2/settings/values/effective";
    private const string AdvisoryPath = "api/v2/settings/values/catalog.show_advisory_age";
    private const string TitleArtPath = "api/v2/settings/values/ui.title_art";
    private const string ThemeMusicPath = "api/v2/settings/values/ui.theme_music_enabled";
    private const string ThemeLoopPath = "api/v2/settings/values/ui.theme_music_loop";

    private readonly LibraryHarness _harness = new();

    public TitlePageSettingsTests()
    {
        _harness.Answer(EffectivePath, Answer());
        _harness.Silo.On(HttpMethod.Put, AdvisoryPath, ScriptedSilo.NoContent());
        foreach (string path in new[] { ThemeMusicPath, ThemeLoopPath })
        {
            _harness.Silo.On(HttpMethod.Put, path, ScriptedSilo.NoContent());
            _harness.Silo.On(HttpMethod.Delete, path, ScriptedSilo.NoContent());
        }
        _harness.Silo.On(HttpMethod.Put, TitleArtPath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Delete, TitleArtPath, ScriptedSilo.NoContent());
    }

    /// <summary>The effective values of the Title Pages settings, as the server answers them.</summary>
    internal static string Answer(bool advisory = false, bool titleArt = true, string titleArtSource = "default", bool themeMusic = false,
        bool loop = false, string themeMusicSource = "default") => $$"""
        { "items": [
            { "key": "catalog.show_advisory_age", "source": "{{(advisory ? "profile" : "default")}}", "value": {{Bool(advisory)}} },
            { "key": "ui.title_art", "source": "{{titleArtSource}}", "value": {{Bool(titleArt)}} },
            { "key": "ui.theme_music_enabled", "source": "{{themeMusicSource}}", "value": {{Bool(themeMusic)}} },
            { "key": "ui.theme_music_loop", "source": "{{themeMusicSource}}", "value": {{Bool(loop)}} }
          ], "revision": 6 }
        """;

    private static string Bool(bool value) => value ? "true" : "false";

    internal static TitlePageSettings Create(LibraryHarness harness) =>
        new(harness.Session, new ProfileSettings(new SettingsApi(harness.Silo.Http), harness.Session, harness.Logs.For<ProfileSettings>()),
            new SettingsApi(harness.Silo.Http), new ImmediateDispatcher(), harness.Logs.For<TitlePageSettings>());

    private List<string> TitleArtWrites() =>
        [.. _harness.Silo.Sent.Where(r => r.Uri.AbsolutePath.EndsWith("/ui.title_art", StringComparison.Ordinal))
            .Select(r => $"{r.Method} {r.Uri.Query} {r.Body}".TrimEnd())];

    private TitlePageSettings Create() => Create(_harness);

    private List<string?> Saved(string path) => [.. _harness.Silo.SentTo(HttpMethod.Put, path).Select(r => r.Body)];

    [Fact]
    public async Task TheAdvisoryAge_IsOff_UntilTheProfileTurnsItOn()
    {
        var settings = Create();

        await settings.StartAsync();

        Assert.False(settings.Shown.ShowsAdvisoryAge);
        Assert.Equal(TitlePageState.Ready, settings.State);
        Assert.Contains(TitlePageSettings.AdvisoryAgeKey, settings.Shown.Offered);
        Assert.Contains("keys=catalog.show_advisory_age", Assert.Single(_harness.Queries(EffectivePath)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProfilesChoice_IsRead()
    {
        _harness.Answer(EffectivePath, Answer(advisory: true));
        var settings = Create();

        await settings.StartAsync();

        Assert.True(settings.Shown.ShowsAdvisoryAge);
    }

    [Fact]
    public async Task TheAdvisorySwitch_AppliesAtOnce_AndSavesForTheProfile()
    {
        var settings = Create();
        await settings.StartAsync();

        await settings.SetShowsAdvisoryAgeAsync(true);

        Assert.True(settings.Shown.ShowsAdvisoryAge);
        var put = Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, AdvisoryPath));
        Assert.Equal("?scope=profile", put.Uri.Query);
        Assert.Equal("""{"value":true}""", put.Body);
        Assert.False(settings.SaveFailed);
    }

    [Fact]
    public async Task TheSameValueAgain_IsNotSaved()
    {
        var settings = Create();
        await settings.StartAsync();

        await settings.SetShowsAdvisoryAgeAsync(false);

        Assert.Empty(Saved(AdvisoryPath));
    }

    [Fact]
    public async Task QuickChanges_AreSavedOneAfterAnother_TheLastOneLast()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, AdvisoryPath, LibraryHarness.Held(release, "{}"), ScriptedSilo.NoContent());

        var first = settings.SetShowsAdvisoryAgeAsync(true);
        var second = settings.SetShowsAdvisoryAgeAsync(false);
        Assert.Single(Saved(AdvisoryPath));
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["""{"value":true}""", """{"value":false}"""], Saved(AdvisoryPath));
        Assert.False(settings.Shown.ShowsAdvisoryAge);
    }

    [Fact]
    public async Task ARefusedSave_GoesBack_AndSaysSo()
    {
        var settings = Create();
        await settings.StartAsync();
        _harness.Silo.On(HttpMethod.Put, AdvisoryPath, ScriptedSilo.Problem(503, "unavailable"));

        await settings.SetShowsAdvisoryAgeAsync(true);

        Assert.False(settings.Shown.ShowsAdvisoryAge);
        Assert.True(settings.SaveFailed);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task ARefusedSave_ThatALaterOneMakesGood_IsNotReported()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, AdvisoryPath, LibraryHarness.Held(release, "{}"), ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.NoContent());

        var first = settings.SetShowsAdvisoryAgeAsync(true);
        var second = settings.SetShowsAdvisoryAgeAsync(false);
        var third = settings.SetShowsAdvisoryAgeAsync(true);
        release.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.False(settings.SaveFailed);
        Assert.True(settings.Shown.ShowsAdvisoryAge);
    }

    [Fact]
    public async Task AChangeWhileTheSettingsAreReadAgain_IsNotPutBack()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(EffectivePath, LibraryHarness.Held(release, Answer()));

        var reading = settings.RefreshAsync();
        var saving = settings.SetShowsAdvisoryAgeAsync(true);
        release.SetResult();
        await Task.WhenAll(reading, saving);

        Assert.True(settings.Shown.ShowsAdvisoryAge);
    }

    [Fact]
    public async Task AnOlderServer_OffersNoAdvisorySwitch_AndShowsNoAge()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "unknown_setting"));
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(TitlePageState.Ready, settings.State);
        Assert.DoesNotContain(TitlePageSettings.AdvisoryAgeKey, settings.Shown.Offered);
        Assert.False(settings.Shown.ShowsAdvisoryAge);
        await settings.SetShowsAdvisoryAgeAsync(true);
        Assert.Empty(Saved(AdvisoryPath));
    }

    [Fact]
    public async Task AFailedFirstRead_LeavesTheSwitchesOff()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(TitlePageState.Unavailable, settings.State);
        Assert.False(settings.CanEdit);
        Assert.False(settings.Shown.ShowsAdvisoryAge);
    }

    [Fact]
    public async Task AFailedReadAgain_KeepsWhatWasRead()
    {
        _harness.Answer(EffectivePath, Answer(advisory: true));
        var settings = Create();
        await settings.StartAsync();
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));

        await settings.RefreshAsync();

        Assert.True(settings.Shown.ShowsAdvisoryAge);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task AnotherProfile_IsReadAfresh_AndShowsNothingOfTheLastOnesMeanwhile()
    {
        _harness.Answer(EffectivePath, Answer(advisory: true));
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(EffectivePath, LibraryHarness.Held(release, Answer(advisory: false)));

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();

        Assert.Equal(TitlePageState.Loading, settings.State);
        Assert.False(settings.Shown.ShowsAdvisoryAge);
        release.SetResult();
        await settings.Working;
        Assert.Equal(TitlePageState.Ready, settings.State);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task ThePage_OffersAgeGuidance_AndItsSwitchSaves()
    {
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(await SpoilerSwitches.StartedAsync(), settings);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(page.OffersAdvisoryAge);
        Assert.True(page.CanEditTitlePage);
        page.ShowsAdvisoryAge = true;
        await settings.Working;

        Assert.True(page.ShowsAdvisoryAge);
        Assert.Contains(nameof(TitlePagesSettingsViewModel.ShowsAdvisoryAge), changed);
        Assert.Equal(["""{"value":true}"""], Saved(AdvisoryPath));
    }

    [Fact]
    public async Task ThePage_LeavesOutAgeGuidance_ThatTheServerDoesNotKeep()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "unknown_setting"));
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(await SpoilerSwitches.StartedAsync(), settings);

        Assert.False(page.OffersAdvisoryAge);
    }

    [Fact]
    public async Task ThePage_SaysWhenASaveFailed_AndReadsTheSettingsAgain_WhenItOpens()
    {
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(await SpoilerSwitches.StartedAsync(), settings);
        _harness.Silo.On(HttpMethod.Put, AdvisoryPath, ScriptedSilo.Problem(503, "unavailable"));

        page.ShowsAdvisoryAge = true;
        await settings.Working;

        Assert.False(page.ShowsAdvisoryAge);
        Assert.Equal(TitlePagesSettingsViewModel.SaveFailedText, page.ErrorMessage);

        _harness.Answer(EffectivePath, Answer(advisory: true));
        await page.LoadAsync();

        Assert.True(page.ShowsAdvisoryAge);
    }

    [Fact]
    public async Task SignedOut_NothingShows()
    {
        _harness.Answer(EffectivePath, Answer(advisory: true));
        var settings = Create();
        await settings.StartAsync();

        _harness.Session.State = SessionState.SignedOut;
        _harness.Session.ActiveServer = null;
        _harness.Session.ProfileId = null;
        _harness.Session.RaiseChanged();

        Assert.False(settings.Shown.ShowsAdvisoryAge);
        Assert.Equal(TitlePageState.Loading, settings.State);
    }

    [Fact]
    public async Task TitleArt_IsNotKnown_UntilTheProfilesChoiceIsRead()
    {
        _harness.Answer(EffectivePath, Answer(titleArt: false, titleArtSource: "profile_device"));
        var settings = Create();

        Assert.Null(settings.Shown.ShowsTitleArt);
        await settings.StartAsync();

        Assert.False(settings.Shown.ShowsTitleArt);
        Assert.Equal("profile_device", settings.Shown.TitleArtSource);
        Assert.False(settings.Shown.TitleArtForAllDevices);
        Assert.False(settings.Shown.TitleArtFollowsServerDefault);
    }

    [Fact]
    public async Task TheServersDefault_Applies_WithoutAChoice()
    {
        _harness.Answer(EffectivePath, Answer(titleArt: false));
        var settings = Create();

        await settings.StartAsync();

        Assert.False(settings.Shown.ShowsTitleArt);
        Assert.True(settings.Shown.TitleArtFollowsServerDefault);
    }

    [Fact]
    public async Task AnOlderServer_OrAFailedRead_NamesTitlesWithTheirLogos()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "unknown_setting"));
        var older = Create();
        await older.StartAsync();
        var failing = new LibraryHarness();
        failing.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));
        var failed = Create(failing);
        await failed.StartAsync();

        Assert.True(older.Shown.ShowsTitleArt);
        Assert.DoesNotContain(TitlePageSettings.TitleArtKey, older.Shown.Offered);
        Assert.True(failed.Shown.ShowsTitleArt);
        Assert.Equal(TitlePageState.Unavailable, failed.State);
    }

    [Fact]
    public async Task TheTitleArtSwitch_SavesForThisPC()
    {
        var settings = Create();
        await settings.StartAsync();

        await settings.SetShowsTitleArtAsync(false);

        Assert.False(settings.Shown.ShowsTitleArt);
        Assert.Equal("profile_device", settings.Shown.TitleArtSource);
        Assert.Equal(["PUT ?scope=profile_device {\"value\":false}"], TitleArtWrites());
    }

    [Fact]
    public async Task WhileItAppliesToAllDevices_TheTitleArtSwitch_SavesForTheProfile()
    {
        _harness.Answer(EffectivePath, Answer(titleArtSource: "profile"));
        var settings = Create();
        await settings.StartAsync();

        await settings.SetShowsTitleArtAsync(false);

        Assert.True(settings.Shown.TitleArtForAllDevices);
        Assert.Equal(["PUT ?scope=profile {\"value\":false}"], TitleArtWrites());
    }

    [Fact]
    public async Task ApplyToAllDevices_SavesTheChoiceForTheProfile()
    {
        _harness.Answer(EffectivePath, Answer(titleArt: false, titleArtSource: "profile_device"));
        var settings = Create();
        await settings.StartAsync();

        await settings.SetTitleArtForAllDevicesAsync(true);

        Assert.True(settings.Shown.TitleArtForAllDevices);
        Assert.False(settings.Shown.ShowsTitleArt);
        Assert.Equal(["PUT ?scope=profile {\"value\":false}"], TitleArtWrites());
    }

    [Fact]
    public async Task ApplyToAllDevicesOff_KeepsThisPCsLook_ThenClearsTheProfiles()
    {
        _harness.Answer(EffectivePath, Answer(titleArt: false, titleArtSource: "profile"));
        var settings = Create();
        await settings.StartAsync();

        await settings.SetTitleArtForAllDevicesAsync(false);

        Assert.False(settings.Shown.TitleArtForAllDevices);
        Assert.False(settings.Shown.ShowsTitleArt);
        Assert.Equal(["PUT ?scope=profile_device {\"value\":false}", "DELETE ?scope=profile"], TitleArtWrites());
    }

    [Fact]
    public async Task ApplyToAllDevicesOff_ClearsNothing_OnceAnotherProfileActs()
    {
        _harness.Answer(EffectivePath, Answer(titleArtSource: "profile"));
        _harness.Silo.ActingProfile = () => _harness.Session.ProfileId;
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, TitleArtPath, LibraryHarness.Held(release, "{}"));

        var saving = settings.SetTitleArtForAllDevicesAsync(false);
        _harness.Session.ProfileId = "8";
        release.SetResult();
        await saving;

        Assert.Equal(["PUT ?scope=profile_device {\"value\":true}"], TitleArtWrites());
    }

    [Fact]
    public async Task UseServerDefault_ClearsThisPCsChoice_ThenReadsTheDefault()
    {
        _harness.Answer(EffectivePath, Answer(titleArt: false, titleArtSource: "profile_device"), Answer(titleArt: true));
        var settings = Create();
        await settings.StartAsync();

        await settings.UseServerDefaultTitleArtAsync();

        Assert.Equal(["DELETE ?scope=profile_device"], TitleArtWrites());
        Assert.True(settings.Shown.TitleArtFollowsServerDefault);
        Assert.True(settings.Shown.ShowsTitleArt);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task UseServerDefault_AlsoClearsTheProfiles_WhileItAppliesToAllDevices()
    {
        _harness.Answer(EffectivePath, Answer(titleArt: false, titleArtSource: "profile"), Answer(titleArt: true));
        _harness.Silo.On(HttpMethod.Delete, TitleArtPath, ScriptedSilo.Problem(404, "not_found"), ScriptedSilo.NoContent());
        var settings = Create();
        await settings.StartAsync();

        await settings.UseServerDefaultTitleArtAsync();

        Assert.Equal(["DELETE ?scope=profile_device", "DELETE ?scope=profile"], TitleArtWrites());
        Assert.False(settings.SaveFailed);
        Assert.True(settings.Shown.ShowsTitleArt);
    }

    [Theory]
    [InlineData("default", true, "Using the server default (on). Change it here to choose for this PC.", false, false)]
    [InlineData("default", false, "Using the server default (off). Change it here to choose for this PC.", false, false)]
    [InlineData("profile_device", false, "Only affects this PC. Your other devices keep their own setting.", false, true)]
    [InlineData("profile", false,
        "Title art is off on every device signed into this profile. Changing it here changes it everywhere. Turn off “Apply to all devices” to choose per device again.",
        true, true)]
    public async Task ThePage_SaysWhoseTitleArtChoiceApplies(string source, bool titleArt, string note, bool boxed, bool offersDefault)
    {
        _harness.Answer(EffectivePath, Answer(titleArt: titleArt, titleArtSource: source));
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(await SpoilerSwitches.StartedAsync(), settings);

        Assert.True(page.OffersTitleArt);
        Assert.Equal(titleArt, page.ShowsTitleArt);
        Assert.Equal(source == "profile", page.TitleArtForAllDevices);
        Assert.Equal(note, page.TitleArtNote);
        Assert.Equal(boxed, page.TitleArtNoteIsBoxed);
        Assert.Equal(offersDefault, page.CanUseServerDefaultTitleArt);
    }

    [Fact]
    public async Task ThePage_TitleArtSwitches_SaveAndFollow()
    {
        _harness.Answer(EffectivePath, Answer(), Answer(titleArt: true));
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(await SpoilerSwitches.StartedAsync(), settings);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.ShowsTitleArt = false;
        await settings.Working;
        page.TitleArtForAllDevices = true;
        await settings.Working;
        Assert.True(page.CanUseServerDefaultTitleArt);
        await page.UseServerDefaultTitleArtCommand.ExecuteAsync(null);

        Assert.Equal(
            ["PUT ?scope=profile_device {\"value\":false}", "PUT ?scope=profile {\"value\":false}", "DELETE ?scope=profile_device", "DELETE ?scope=profile"],
            TitleArtWrites());
        Assert.True(page.ShowsTitleArt);
        Assert.False(page.CanUseServerDefaultTitleArt);
        Assert.Contains(nameof(TitlePagesSettingsViewModel.TitleArtNote), changed);
    }

    [Fact]
    public async Task ThePage_SaysWhenTheTitleArtDidNotSave_InTheWebsWords()
    {
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(await SpoilerSwitches.StartedAsync(), settings);
        _harness.Silo.On(HttpMethod.Put, TitleArtPath, ScriptedSilo.Problem(503, "unavailable"));

        page.ShowsTitleArt = false;
        await settings.Working;

        Assert.True(page.ShowsTitleArt);
        Assert.Equal(TitlePagesSettingsViewModel.TitleArtSaveFailedText, page.ErrorMessage);
    }

    private List<string> Writes(string key) =>
        [.. _harness.Silo.Sent.Where(r => r.Uri.AbsolutePath.EndsWith("/" + key, StringComparison.Ordinal))
            .Select(r => $"{r.Method} {r.Uri.Query} {r.Body}".TrimEnd())];

    [Fact]
    public async Task ThemeMusic_IsOff_UntilChosen_AndBothSwitchesAreRead()
    {
        var off = Create();
        await off.StartAsync();
        var harness = new LibraryHarness();
        harness.Answer(EffectivePath, Answer(themeMusic: true, loop: true, themeMusicSource: "profile_device"));
        var on = Create(harness);
        await on.StartAsync();

        Assert.False(off.Shown.PlaysThemeMusic);
        Assert.False(off.Shown.LoopsThemeMusic);
        Assert.True(on.Shown.PlaysThemeMusic);
        Assert.True(on.Shown.LoopsThemeMusic);
        Assert.True(on.Shown.ThemeMusicFromDevice);
        string query = Assert.Single(harness.Queries(EffectivePath));
        Assert.Contains("keys=ui.theme_music_enabled", query, StringComparison.Ordinal);
        Assert.Contains("keys=ui.theme_music_loop", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheThemeSwitches_SaveForTheProfile()
    {
        var settings = Create();
        await settings.StartAsync();

        await settings.SetPlaysThemeMusicAsync(true);
        await settings.SetLoopsThemeMusicAsync(true);

        Assert.True(settings.Shown.PlaysThemeMusic);
        Assert.True(settings.Shown.LoopsThemeMusic);
        Assert.Equal(["PUT ?scope=profile {\"value\":true}"], Writes(TitlePageSettings.ThemeMusicKey));
        Assert.Equal(["PUT ?scope=profile {\"value\":true}"], Writes(TitlePageSettings.ThemeMusicLoopKey));
        Assert.All(_harness.Silo.Sent.Where(r => r.Method == HttpMethod.Put), put => Assert.Equal("7", put.AsProfile));
    }

    [Fact]
    public async Task AValueThisPCKeeps_IsCleared_AfterTheProfilesIsSaved()
    {
        _harness.Answer(EffectivePath, Answer(themeMusic: true, themeMusicSource: "profile_device"));
        var settings = Create();
        await settings.StartAsync();

        await settings.SetPlaysThemeMusicAsync(false);
        await settings.SetPlaysThemeMusicAsync(true);

        Assert.Equal(["PUT ?scope=profile {\"value\":false}", "DELETE ?scope=profile_device", "PUT ?scope=profile {\"value\":true}"],
            Writes(TitlePageSettings.ThemeMusicKey));
        Assert.False(settings.Shown.ThemeMusicFromDevice);
    }

    [Fact]
    public async Task AProfileSwitchBetweenTheSaveAndTheClear_ClearsNothingForTheNextProfile_AndIsRefusedUnsent()
    {
        _harness.Answer(EffectivePath, Answer(themeMusic: true, themeMusicSource: "profile_device"));
        _harness.Silo.ActingProfile = () => _harness.Session.ProfileId;
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ThemeMusicPath, LibraryHarness.Held(release, "{}"));

        var saving = settings.SetPlaysThemeMusicAsync(false);
        _harness.Session.ProfileId = "8";
        release.SetResult();
        await saving;

        Assert.Equal(["PUT ?scope=profile {\"value\":false}"], Writes(TitlePageSettings.ThemeMusicKey));
        Assert.Equal("7", Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, ThemeMusicPath)).AsProfile);
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Delete, ThemeMusicPath));
    }

    [Fact]
    public async Task AClearThatFails_CountsAsAnIncompleteSave_AndGoesBack()
    {
        _harness.Answer(EffectivePath, Answer(themeMusic: true, themeMusicSource: "profile_device"));
        _harness.Silo.On(HttpMethod.Delete, ThemeMusicPath, ScriptedSilo.Problem(503, "unavailable"));
        var settings = Create();
        await settings.StartAsync();

        await settings.SetPlaysThemeMusicAsync(false);

        Assert.True(settings.SaveFailed);
        Assert.True(settings.Shown.PlaysThemeMusic);
        Assert.Equal("7", Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, ThemeMusicPath)).AsProfile);
    }

    [Fact]
    public async Task UseServerDefault_ClearsAsTheProfileItWasChosenFor()
    {
        _harness.Answer(EffectivePath, Answer(titleArt: false, titleArtSource: "profile"), Answer());
        var settings = Create();
        await settings.StartAsync();

        await settings.UseServerDefaultTitleArtAsync();

        Assert.All(_harness.Silo.SentTo(HttpMethod.Delete, TitleArtPath), clear => Assert.Equal("7", clear.AsProfile));
        Assert.Equal(2, _harness.Silo.SentTo(HttpMethod.Delete, TitleArtPath).Count);
    }

    [Fact]
    public async Task ThePage_OffersTheSoundGroup_AndItsSwitchesSave()
    {
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(await SpoilerSwitches.StartedAsync(), settings);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(page.OffersThemeMusic);
        Assert.True(page.OffersThemeMusicLoop);
        page.PlaysThemeMusic = true;
        await settings.Working;
        page.LoopsThemeMusic = true;
        await settings.Working;

        Assert.True(page.PlaysThemeMusic);
        Assert.True(page.LoopsThemeMusic);
        Assert.Contains(nameof(TitlePagesSettingsViewModel.PlaysThemeMusic), changed);
        Assert.Single(Writes(TitlePageSettings.ThemeMusicLoopKey));
    }

    [Fact]
    public async Task ThePage_LeavesOutTheSoundGroup_OnAServerWithoutThemeMusic()
    {
        _harness.Answer(EffectivePath, """
            { "items": [ { "key": "catalog.show_advisory_age", "source": "default", "value": false } ], "revision": 6 }
            """);
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(await SpoilerSwitches.StartedAsync(), settings);

        Assert.False(page.OffersThemeMusic);
        Assert.False(page.OffersThemeMusicLoop);
        Assert.False(settings.Shown.PlaysThemeMusic);
    }

    [Fact]
    public async Task ARefusedTitleArtSave_GoesBack()
    {
        var settings = Create();
        await settings.StartAsync();
        _harness.Silo.On(HttpMethod.Put, TitleArtPath, ScriptedSilo.Problem(503, "unavailable"));

        await settings.SetShowsTitleArtAsync(false);

        Assert.True(settings.Shown.ShowsTitleArt);
        Assert.True(settings.Shown.TitleArtFollowsServerDefault);
        Assert.True(settings.SaveFailed);
    }
}
