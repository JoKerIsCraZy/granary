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

    private readonly LibraryHarness _harness = new();

    public TitlePageSettingsTests()
    {
        _harness.Answer(EffectivePath, Answer());
        _harness.Silo.On(HttpMethod.Put, AdvisoryPath, ScriptedSilo.NoContent());
    }

    /// <summary>The effective values of the Title Pages settings, as the server answers them.</summary>
    internal static string Answer(bool advisory = false) => $$"""
        { "items": [
            { "key": "catalog.show_advisory_age", "source": "{{(advisory ? "profile" : "default")}}", "value": {{(advisory ? "true" : "false")}} }
          ], "revision": 6 }
        """;

    internal static TitlePageSettings Create(LibraryHarness harness) =>
        new(harness.Session, new ProfileSettings(new SettingsApi(harness.Silo.Http), harness.Session, harness.Logs.For<ProfileSettings>()),
            new ImmediateDispatcher(), harness.Logs.For<TitlePageSettings>());

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
}
