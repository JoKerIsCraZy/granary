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

    private readonly LibraryHarness _harness = new();

    public TitlePageSettingsTests()
    {
        _harness.Answer(EffectivePath, Answer());
        _harness.Silo.On(HttpMethod.Put, AdvisoryPath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Put, TitleArtPath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Delete, TitleArtPath, ScriptedSilo.NoContent());
    }

    /// <summary>The effective values of the Title Pages settings, as the server answers them.</summary>
    internal static string Answer(bool advisory = false, bool titleArt = true, string titleArtSource = "default") => $$"""
        { "items": [
            { "key": "catalog.show_advisory_age", "source": "{{(advisory ? "profile" : "default")}}", "value": {{(advisory ? "true" : "false")}} },
            { "key": "ui.title_art", "source": "{{titleArtSource}}", "value": {{(titleArt ? "true" : "false")}} }
          ], "revision": 6 }
        """;

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
