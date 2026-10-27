using System.Net;
using System.Text.Json;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Settings;
using Granary.Core.Overlays;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Overlays;

/// <summary>The profile's card badges, read and saved as the web's useOverlayPrefs does.</summary>
public sealed class CardOverlaySettingsTests
{
    private const string ConfigPath = "api/v2/settings/overlay-config";
    private const string EffectivePath = "api/v2/settings/values/effective";
    private const string CapabilitiesPath = "api/v2/settings/contract/capabilities";
    private const string RatingsPath = "api/v2/capabilities/ratings";
    private const string PrefsPath = "api/v2/settings/values/ui.card_overlays";
    private const string EnabledPath = "api/v2/settings/values/ui.card_overlays_enabled";

    private const string ServerDefaults = """
        { "defaults": "{\"version\":2,\"preset\":\"vibrant\",\"order\":[],\"items\":{\"year\":{\"enabled\":true,\"position\":\"top-right\"}}}",
          "enabled": true }
        """;

    private const string ProfilesOwn = """
        { "items": [
            { "key": "ui.card_overlays", "source": "profile",
              "value": { "version": 2, "preset": "pill", "order": [], "items": { "rating_imdb": { "enabled": true, "position": "bottom-left" } } } }
          ], "revision": 3 }
        """;

    private readonly LibraryHarness _harness = new();

    public CardOverlaySettingsTests()
    {
        _harness.Answer(ConfigPath, ServerDefaults);
        _harness.Answer(EffectivePath, """{ "items": [], "revision": 1 }""");
        _harness.Answer(CapabilitiesPath, """{ "api_version": 1, "manifest_revision": 15 }""");
        _harness.Answer(RatingsPath, """{ "state": "available", "sources": [ { "source": "imdb", "name": "IMDb" }, { "source": "rt_critic", "name": "RT" } ] }""");
        _harness.Silo.On(HttpMethod.Put, PrefsPath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Put, EnabledPath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Delete, PrefsPath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Delete, EnabledPath, ScriptedSilo.NoContent());
    }

    private CardOverlaySettings Create() => new(_harness.Session, new SettingsApi(_harness.Silo.Http), new CatalogApi(_harness.Silo.Http),
        new ImmediateDispatcher(), _harness.Logs.For<CardOverlaySettings>());

    private JsonElement Saved(string path, int index = 0) =>
        JsonDocument.Parse(_harness.Silo.SentTo(HttpMethod.Put, path)[index].Body!).RootElement.GetProperty("value");

    [Fact]
    public async Task AProfileWithoutAChoice_FollowsTheServersDefaults()
    {
        var overlays = Create();

        await overlays.StartAsync();

        Assert.Equal(OverlayPresetId.Vibrant, overlays.Shown?.Preset);
        Assert.Equal(new OverlayItemConfig(true, OverlayPosition.TopRight), overlays.Prefs.ItemOf("year"));
        Assert.False(overlays.HasOverride);
        Assert.True(overlays.CanEdit);
    }

    [Fact]
    public async Task TheProfilesOwnChoice_GoesBeforeTheServers()
    {
        _harness.Answer(EffectivePath, ProfilesOwn);
        var overlays = Create();

        await overlays.StartAsync();

        Assert.Equal(OverlayPresetId.Pill, overlays.Shown?.Preset);
        Assert.True(overlays.HasOverride);
    }

    [Fact]
    public async Task BadgesTurnedOff_ShowNone_ButStayEditable()
    {
        _harness.Answer(EffectivePath, """{ "items": [ { "key": "ui.card_overlays_enabled", "value": false, "source": "profile" } ], "revision": 2 }""");
        var overlays = Create();

        await overlays.StartAsync();

        Assert.Null(overlays.Shown);
        Assert.False(overlays.IsEnabled);
        Assert.Equal(OverlayPresetId.Vibrant, overlays.Prefs.Preset);
    }

    [Fact]
    public async Task AChange_ShowsAtOnce_AndSavesTheWholeDocumentToTheProfile()
    {
        var overlays = Create();
        await overlays.StartAsync();

        await overlays.SetPrefsAsync(overlays.Prefs with { Preset = OverlayPresetId.Square });

        Assert.Equal(OverlayPresetId.Square, overlays.Shown?.Preset);
        var put = Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, PrefsPath));
        Assert.Equal("?scope=profile", put.Uri.Query);
        Assert.Equal("square", Saved(PrefsPath).GetProperty("preset").GetString());
        Assert.True(overlays.HasOverride);
    }

    [Fact]
    public async Task AnOlderServer_IsNotSentTheBadgesItsManifestLacks()
    {
        _harness.Answer(CapabilitiesPath, """{ "api_version": 1, "manifest_revision": 12 }""");
        var overlays = Create();
        await overlays.StartAsync();

        await overlays.SetPrefsAsync(overlays.Prefs with { Preset = OverlayPresetId.Pill });

        var items = Saved(PrefsPath).GetProperty("items");
        Assert.False(items.TryGetProperty("advisory_age", out _));
        Assert.False(items.TryGetProperty("request_status", out _));
        Assert.False(overlays.IsOffered(OverlayRegistry.Find("request_status")!));
    }

    [Fact]
    public async Task ARefusedSave_PutsBackWhatTheServerHas()
    {
        var overlays = Create();
        await overlays.StartAsync();
        _harness.Silo.On(HttpMethod.Put, PrefsPath, ScriptedSilo.Problem(422, "validation_failed"));

        await overlays.SetPrefsAsync(overlays.Prefs with { Preset = OverlayPresetId.Minimal });

        Assert.Equal(OverlayPresetId.Vibrant, overlays.Shown?.Preset);
    }

    [Fact]
    public async Task TurningBadgesOff_SavesThatAlone()
    {
        var overlays = Create();
        await overlays.StartAsync();

        await overlays.SetEnabledAsync(false);

        Assert.Null(overlays.Shown);
        Assert.Equal(JsonValueKind.False, Saved(EnabledPath).ValueKind);
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Put, PrefsPath));
    }

    [Fact]
    public async Task Reset_ClearsBothChoices_AndFollowsTheServerAgain()
    {
        _harness.Answer(EffectivePath, ProfilesOwn, """{ "items": [], "revision": 4 }""");
        var overlays = Create();
        await overlays.StartAsync();

        await overlays.ResetAsync();

        Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, PrefsPath));
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, EnabledPath));
        Assert.Equal(OverlayPresetId.Vibrant, overlays.Shown?.Preset);
        Assert.False(overlays.HasOverride);
    }

    [Fact]
    public async Task AServerThatDoesNotKnowTheBadgeSettings_ShowsItsDefaults_AndCannotBeEdited()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "unknown_setting"));
        var overlays = Create();

        await overlays.StartAsync();

        Assert.False(overlays.CanEdit);
        Assert.Equal(OverlayPresetId.Vibrant, overlays.Shown?.Preset);
    }

    [Fact]
    public async Task ARottenTomatoesBadge_IsOffered_OnlyWhileTheServerShowsItsSource()
    {
        var overlays = Create();
        await overlays.StartAsync();

        Assert.True(overlays.IsOffered(OverlayRegistry.Find("rating_rt")!));
        Assert.False(overlays.IsOffered(OverlayRegistry.Find("rating_rt_audience")!));
        Assert.True(overlays.IsOffered(OverlayRegistry.Find("rating_imdb")!));
    }

    [Fact]
    public async Task AServerWithoutRatings_OffersNoRottenTomatoes()
    {
        _harness.Silo.On(HttpMethod.Get, RatingsPath, ScriptedSilo.Problem(404, "not_found"));
        var overlays = Create();

        await overlays.StartAsync();

        Assert.False(overlays.IsOffered(OverlayRegistry.Find("rating_rt")!));
        Assert.NotNull(overlays.Shown);
    }

    [Fact]
    public async Task AnotherProfile_IsReadAgain()
    {
        var overlays = Create();
        await overlays.StartAsync();
        _harness.Answer(EffectivePath, ProfilesOwn);

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        await overlays.Working;

        Assert.Equal(OverlayPresetId.Pill, overlays.Shown?.Preset);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task AChangeWhileTheBadgesAreReadAgain_IsNotPutBack()
    {
        var overlays = Create();
        await overlays.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(EffectivePath, LibraryHarness.Held(release, """{ "items": [], "revision": 1 }"""));

        var reading = overlays.RefreshAsync();
        var saving = overlays.SetPrefsAsync(overlays.Prefs with { Preset = OverlayPresetId.Square });
        release.SetResult();
        await reading;
        await saving;

        Assert.Equal(OverlayPresetId.Square, overlays.Shown?.Preset);
        Assert.True(overlays.HasOverride);
    }

    [Fact]
    public async Task AFailedReadAgain_KeepsTheProfilesBadges()
    {
        _harness.Answer(EffectivePath, ProfilesOwn);
        var overlays = Create();
        await overlays.StartAsync();
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));

        await overlays.RefreshAsync();

        Assert.Equal(OverlayPresetId.Pill, overlays.Shown?.Preset);
        Assert.True(overlays.HasOverride);
        Assert.True(overlays.CanEdit);
    }

    [Fact]
    public async Task AFailedFirstRead_ShowsTheServersBadges_AndTheNextRefreshReadsAgain()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));
        var overlays = Create();
        await overlays.StartAsync();

        Assert.Equal(OverlayPresetId.Vibrant, overlays.Shown?.Preset);
        Assert.False(overlays.CanEdit);

        _harness.Answer(EffectivePath, ProfilesOwn);
        await overlays.RefreshAsync();

        Assert.Equal(OverlayPresetId.Pill, overlays.Shown?.Preset);
        Assert.True(overlays.CanEdit);
    }

    [Fact]
    public async Task QuickChanges_AreSavedOneAfterAnother_TheLastOneLast()
    {
        var overlays = Create();
        await overlays.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, PrefsPath, LibraryHarness.Held(release, "{}"));

        var first = overlays.SetPrefsAsync(overlays.Prefs with { Preset = OverlayPresetId.Minimal });
        var second = overlays.SetPrefsAsync(overlays.Prefs with { Preset = OverlayPresetId.Pill });
        var third = overlays.SetPrefsAsync(overlays.Prefs with { Preset = OverlayPresetId.Square });
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, PrefsPath));
        release.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(["minimal", "square"], Enumerable.Range(0, 2).Select(i => Saved(PrefsPath, i).GetProperty("preset").GetString()));
        Assert.Equal(OverlayPresetId.Square, overlays.Shown?.Preset);
    }

    [Fact]
    public async Task TheSameBadgesAgain_AreNotSaved()
    {
        _harness.Answer(EffectivePath, ProfilesOwn);
        var overlays = Create();
        await overlays.StartAsync();
        var shown = overlays.Shown;

        await overlays.SetPrefsAsync(CardOverlayPrefs.Parse(overlays.Prefs.ToJson()));
        await overlays.RefreshAsync();

        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Put, PrefsPath));
        Assert.Same(shown, overlays.Shown);
    }

    [Fact]
    public async Task TurningBadgesOff_IsTheProfilesOwnChoice()
    {
        var overlays = Create();
        await overlays.StartAsync();

        await overlays.SetEnabledAsync(false);

        Assert.True(overlays.HasOverride);
    }

    [Fact]
    public async Task TheSettingsPage_ListsTheOfferedBadges_AndAToggleSavesOnlyThatBadge()
    {
        var overlays = Create();
        await overlays.StartAsync();
        using var page = new BadgeSettingsViewModel(overlays);

        Assert.Equal(["Media Info", "Ratings & Certifications", "Content Metadata", "Status & Awards"], page.Groups.Select(g => g.Title));
        var ratings = page.Groups[1];
        Assert.DoesNotContain(ratings.Rows, row => row.Id == "rating_rt_audience");
        Assert.Equal("None on", ratings.OnText);

        var imdb = ratings.Rows.Single(row => row.Id == "rating_imdb");
        imdb.IsEnabled = true;
        await overlays.Working;

        Assert.True(Saved(PrefsPath).GetProperty("items").GetProperty("rating_imdb").GetProperty("enabled").GetBoolean());
        Assert.Equal($"1 of {ratings.Rows.Count} on", ratings.OnText);
        Assert.Same(imdb, page.Groups[1].Rows.Single(row => row.Id == "rating_imdb"));
    }

    [Fact]
    public async Task TheSettingsPage_PicksACornerAndAColour_AndShowsTheChoice()
    {
        var overlays = Create();
        await overlays.StartAsync();
        using var page = new BadgeSettingsViewModel(overlays);
        var year = page.Groups.SelectMany(g => g.Rows).Single(row => row.Id == "year");

        await year.PickCornerCommand.ExecuteAsync("BottomRight");
        await year.PickColorCommand.ExecuteAsync("#ec4899");

        Assert.True(year.IsBottomRight);
        Assert.False(year.IsDefaultColor);
        Assert.True(year.Swatches.Single(s => s.Hex == "#ec4899").IsSelected);
        var saved = Saved(PrefsPath, 1).GetProperty("items").GetProperty("year");
        Assert.Equal(("bottom-right", "#ec4899"), (saved.GetProperty("position").GetString(), saved.GetProperty("accentColor").GetString()));
    }

    [Fact]
    public async Task TheSettingsPage_ShowingTheSavedBadges_SavesNothing()
    {
        _harness.Answer(EffectivePath, ProfilesOwn);
        var overlays = Create();
        await overlays.StartAsync();
        using var page = new BadgeSettingsViewModel(overlays);

        await page.LoadAsync();
        await page.PickStyleCommand.ExecuteAsync(OverlayPresetId.Pill);

        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Put, PrefsPath));
        Assert.Contains(page.Groups.SelectMany(g => g.Rows), row => row is { Id: "resolution", Hint: null });
        Assert.True(page.Styles.Single(s => s.Id == OverlayPresetId.Pill).IsSelected);
    }

    [Fact]
    public async Task TheCombinedBadge_SaysWhyResolutionAndHdrAreHidden()
    {
        var overlays = Create();
        await overlays.StartAsync();
        using var page = new BadgeSettingsViewModel(overlays);
        var rows = page.Groups[0].Rows;

        rows.Single(row => row.Id == "resolution_hdr").IsEnabled = true;
        await overlays.Working;

        Assert.Equal(BadgeRowViewModel.SuppressedHint, rows.Single(row => row.Id == "hdr").Hint);
    }
}
