using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>The profile's two spoiler switches, read and saved at profile scope as the web's Title Pages and the apps keep them.</summary>
public sealed class EpisodeSpoilerSettingsTests
{
    private const string EffectivePath = "api/v2/settings/values/effective";
    private const string ImagesPath = "api/v2/settings/values/catalog.hide_unwatched_episode_images";
    private const string OverviewsPath = "api/v2/settings/values/catalog.hide_unwatched_episode_overviews";

    private readonly LibraryHarness _harness = new();

    public EpisodeSpoilerSettingsTests()
    {
        _harness.Answer(EffectivePath, Answer(images: false, overviews: false));
        _harness.Silo.On(HttpMethod.Put, ImagesPath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Put, OverviewsPath, ScriptedSilo.NoContent());
    }

    /// <summary>The effective values of both switches, as the server answers them.</summary>
    internal static string Answer(bool images, bool overviews) => $$"""
        { "items": [
            { "key": "catalog.hide_unwatched_episode_images", "source": "{{(images ? "profile" : "default")}}", "value": {{(images ? "true" : "false")}} },
            { "key": "catalog.hide_unwatched_episode_overviews", "source": "{{(overviews ? "profile" : "default")}}", "value": {{(overviews ? "true" : "false")}} }
          ], "revision": 4 }
        """;

    private EpisodeSpoilerSettings Create() =>
        new(_harness.Session, new SettingsApi(_harness.Silo.Http), new ImmediateDispatcher(), _harness.Logs.For<EpisodeSpoilerSettings>());

    private List<string?> Saved(string path) => [.. _harness.Silo.SentTo(HttpMethod.Put, path).Select(r => r.Body)];

    [Fact]
    public async Task TheProfilesSwitches_AreReadTogether_AndApply()
    {
        _harness.Answer(EffectivePath, Answer(images: true, overviews: false));
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(new EpisodeSpoilers(HidesImages: true, HidesOverviews: false), settings.Shown);
        Assert.Equal(EpisodeSpoilerState.Ready, settings.State);
        Assert.True(settings.CanEdit);
        string query = Assert.Single(_harness.Queries(EffectivePath));
        Assert.Contains("keys=catalog.hide_unwatched_episode_images", query, StringComparison.Ordinal);
        Assert.Contains("keys=catalog.hide_unwatched_episode_overviews", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAChoice_NothingIsHidden()
    {
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(EpisodeSpoilers.Off, settings.Shown);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task AValueThatIsNotTrue_ReadsAsOff()
    {
        _harness.Answer(EffectivePath, """
            { "items": [ { "key": "catalog.hide_unwatched_episode_images", "source": "profile", "value": "yes" },
                         { "key": "catalog.hide_unwatched_episode_overviews", "source": "profile", "value": null } ], "revision": 2 }
            """);
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(EpisodeSpoilers.Off, settings.Shown);
    }

    [Fact]
    public async Task ASwitch_AppliesAtOnce_AndSavesForTheProfile()
    {
        var settings = Create();
        await settings.StartAsync();

        await settings.SetHidesImagesAsync(true);

        Assert.True(settings.Shown.HidesImages);
        Assert.False(settings.Shown.HidesOverviews);
        var put = Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, ImagesPath));
        Assert.Equal("?scope=profile", put.Uri.Query);
        Assert.Equal("""{"value":true}""", put.Body);
        Assert.Empty(Saved(OverviewsPath));
    }

    [Fact]
    public async Task TheSameValueAgain_IsNotSaved()
    {
        _harness.Answer(EffectivePath, Answer(images: false, overviews: true));
        var settings = Create();
        await settings.StartAsync();

        await settings.SetHidesOverviewsAsync(true);
        await settings.SetHidesImagesAsync(false);

        Assert.Empty(Saved(ImagesPath));
        Assert.Empty(Saved(OverviewsPath));
    }

    [Fact]
    public async Task QuickChanges_AreSavedOneAfterAnother_TheLastOneLast()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ImagesPath, LibraryHarness.Held(release, "{}"));

        var first = settings.SetHidesImagesAsync(true);
        var second = settings.SetHidesOverviewsAsync(true);
        var third = settings.SetHidesImagesAsync(false);
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, ImagesPath));
        Assert.Empty(Saved(OverviewsPath));
        release.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(["""{"value":true}""", """{"value":false}"""], Saved(ImagesPath));
        Assert.Equal(["""{"value":true}"""], Saved(OverviewsPath));
        Assert.Equal(new EpisodeSpoilers(HidesImages: false, HidesOverviews: true), settings.Shown);
    }

    [Fact]
    public async Task ARefusedSave_PutsBackWhatTheServerHas()
    {
        var settings = Create();
        await settings.StartAsync();
        _harness.Silo.On(HttpMethod.Put, OverviewsPath, ScriptedSilo.Problem(503, "unavailable"));

        await settings.SetHidesOverviewsAsync(true);

        Assert.Equal(EpisodeSpoilers.Off, settings.Shown);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task ARefusedSave_GoesBackToWhatWasSaved_AlsoWhenTheReadAfterItFails()
    {
        var settings = Create();
        await settings.StartAsync();
        await settings.SetHidesImagesAsync(true);
        _harness.Silo.On(HttpMethod.Put, OverviewsPath, ScriptedSilo.Problem(503, "unavailable"));
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));

        await settings.SetHidesOverviewsAsync(true);

        Assert.Equal(new EpisodeSpoilers(HidesImages: true, HidesOverviews: false), settings.Shown);
        Assert.True(settings.SaveFailed);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task AChangeWhileTheSwitchesAreReadAgain_IsNotPutBack()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(EffectivePath, LibraryHarness.Held(release, Answer(images: false, overviews: false)));

        var reading = settings.RefreshAsync();
        var saving = settings.SetHidesImagesAsync(true);
        release.SetResult();
        await reading;
        await saving;

        Assert.True(settings.Shown.HidesImages);
    }

    [Fact]
    public async Task AReadAgainWhileAChangeIsOnItsWay_WaitsForIt()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ImagesPath, LibraryHarness.Held(release, "{}"));
        var saving = settings.SetHidesImagesAsync(true);

        var reading = settings.RefreshAsync();
        Assert.Equal(1, _harness.Count(EffectivePath));
        _harness.Answer(EffectivePath, Answer(images: true, overviews: false));
        release.SetResult();
        await Task.WhenAll(saving, reading);

        Assert.Equal(2, _harness.Count(EffectivePath));
        Assert.True(settings.Shown.HidesImages);
    }

    [Fact]
    public async Task AnOlderServer_HidesNothing_AndCannotBeEdited()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "unknown_setting"));
        var settings = Create();

        await settings.StartAsync();
        await settings.SetHidesImagesAsync(true);

        Assert.Equal(EpisodeSpoilerState.Unsupported, settings.State);
        Assert.False(settings.CanEdit);
        Assert.Equal(EpisodeSpoilers.Off, settings.Shown);
        Assert.Empty(Saved(ImagesPath));
    }

    [Fact]
    public async Task AFailedFirstRead_HidesNothing_StopsEditing_AndTheNextRefreshReadsAgain()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));
        var settings = Create();
        await settings.StartAsync();

        Assert.Equal(EpisodeSpoilerState.Unavailable, settings.State);
        Assert.False(settings.CanEdit);
        Assert.Equal(EpisodeSpoilers.Off, settings.Shown);

        _harness.Answer(EffectivePath, Answer(images: true, overviews: true));
        await settings.RefreshAsync();

        Assert.Equal(EpisodeSpoilerState.Ready, settings.State);
        Assert.Equal(new EpisodeSpoilers(true, true), settings.Shown);
    }

    [Fact]
    public async Task AFailedReadAgain_KeepsTheSwitches_AndEditing()
    {
        _harness.Answer(EffectivePath, Answer(images: true, overviews: true));
        var settings = Create();
        await settings.StartAsync();
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));

        await settings.RefreshAsync();

        Assert.Equal(new EpisodeSpoilers(true, true), settings.Shown);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task AnotherProfile_IsReadAgain()
    {
        var settings = Create();
        await settings.StartAsync();
        _harness.Answer(EffectivePath, Answer(images: true, overviews: false));

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        await settings.Working;

        Assert.True(settings.Shown.HidesImages);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task ProfileSwitch_HidesNothingUntilTheNewProfileIsRead()
    {
        _harness.Answer(EffectivePath, Answer(images: true, overviews: true));
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(EffectivePath, LibraryHarness.Held(release, Answer(images: false, overviews: true)));

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();

        // Not the other profile's choice meanwhile, as the web hides nothing until it knows.
        Assert.Equal(EpisodeSpoilers.Off, settings.Shown);
        Assert.Equal(EpisodeSpoilerState.Loading, settings.State);
        release.SetResult();
        await settings.Working;
        Assert.Equal(new EpisodeSpoilers(HidesImages: false, HidesOverviews: true), settings.Shown);
    }

    [Fact]
    public async Task SignOut_HidesNothing_AndTheNextSignInReadsAgain()
    {
        _harness.Answer(EffectivePath, Answer(images: true, overviews: true));
        var settings = Create();
        await settings.StartAsync();

        _harness.Session.State = SessionState.SignedOut;
        _harness.Session.ProfileId = null;
        _harness.Session.RaiseChanged();
        await settings.Working;

        Assert.Equal(EpisodeSpoilers.Off, settings.Shown);
        Assert.False(settings.CanEdit);

        _harness.Session.State = SessionState.Ready;
        _harness.Session.ProfileId = "7";
        _harness.Session.RaiseChanged();
        await settings.Working;

        Assert.Equal(new EpisodeSpoilers(HidesImages: true, HidesOverviews: true), settings.Shown);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task ARefusedSave_IsNotReported_WhenALaterSaveOfTheSameSwitchLanded()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ImagesPath, async () =>
        {
            await release.Task;
            return await ScriptedSilo.Problem(503, "unavailable")();
        }, ScriptedSilo.NoContent());

        var first = settings.SetHidesImagesAsync(true);
        var second = settings.SetHidesImagesAsync(false);
        release.SetResult();
        await Task.WhenAll(first, second, settings.Working);

        Assert.Equal(2, Saved(ImagesPath).Count);
        Assert.False(settings.SaveFailed);
        Assert.Equal(EpisodeSpoilers.Off, settings.Shown);
        Assert.Equal(1, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task AChangeTheSessionCannotSave_GoesBack_AndSaysSo()
    {
        var settings = Create();
        await settings.StartAsync();
        // Signed out, and the store not told yet: the change is let through, and cannot be saved.
        _harness.Session.State = SessionState.SignedOut;

        await settings.SetHidesImagesAsync(true);

        Assert.Empty(Saved(ImagesPath));
        Assert.True(settings.SaveFailed);
        Assert.Equal(EpisodeSpoilers.Off, settings.Shown);
    }

    [Fact]
    public async Task AChangeMadeForAProfile_IsNotSavedForTheNextOne()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ImagesPath, LibraryHarness.Held(release, "{}"));
        var first = settings.SetHidesImagesAsync(true);
        var second = settings.SetHidesImagesAsync(false);

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        release.SetResult();
        await Task.WhenAll(first, second, settings.Working);

        // The first was on its way already; the second belonged to the profile that left.
        Assert.Equal(["""{"value":true}"""], Saved(ImagesPath));
    }

    [Fact]
    public async Task ThePage_ShowsTheSwitches_AndASwitchSavesIt()
    {
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(settings);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(page.IsReady);
        Assert.False(page.HidesImages);
        page.HidesImages = true;
        await settings.Working;
        page.HidesOverviews = true;
        await settings.Working;

        Assert.True(page.HidesImages);
        Assert.True(page.HidesOverviews);
        Assert.Contains(nameof(TitlePagesSettingsViewModel.HidesImages), changed);
        Assert.Equal(["""{"value":true}"""], Saved(ImagesPath));
        Assert.Equal(["""{"value":true}"""], Saved(OverviewsPath));
    }

    [Fact]
    public async Task ThePage_ReadsTheSwitchesAgain_WhenItOpens()
    {
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(settings);
        _harness.Answer(EffectivePath, Answer(images: false, overviews: true));

        await page.LoadAsync();

        Assert.True(page.HidesOverviews);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task ThePage_SaysWhenASaveFailed_UntilTheNextChange()
    {
        var settings = Create();
        await settings.StartAsync();
        using var page = new TitlePagesSettingsViewModel(settings);
        _harness.Silo.On(HttpMethod.Put, ImagesPath, ScriptedSilo.Problem(503, "unavailable"));

        page.HidesImages = true;
        await settings.Working;

        Assert.False(page.HidesImages);
        Assert.Equal("Failed to save title page setting", page.ErrorMessage);

        _harness.Silo.On(HttpMethod.Put, ImagesPath, ScriptedSilo.NoContent());
        page.HidesImages = true;
        await settings.Working;

        Assert.True(page.HidesImages);
        Assert.Equal("", page.ErrorMessage);
    }

    [Fact]
    public async Task ThePage_SaysWhyItCannotEdit()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "unknown_setting"));
        var settings = Create();
        using var page = new TitlePagesSettingsViewModel(settings);

        Assert.True(page.IsLoading);
        await settings.StartAsync();

        Assert.False(page.IsLoading);
        Assert.False(page.IsReady);
        Assert.Equal(TitlePagesSettingsViewModel.UnsupportedTitle, page.BlockerTitle);
        Assert.Equal(TitlePagesSettingsViewModel.UnsupportedText, page.BlockerText);
    }
}
