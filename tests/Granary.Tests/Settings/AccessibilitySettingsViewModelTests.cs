using Granary.Core.Api.Settings;
using Granary.Core.Settings;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>
/// Settings → Accessibility → Language, as Silo's web has it (pages/settings/AccessibilitySettings.tsx): Match device, then
/// each language Granary offers by its own name, the profile's choice saved at once and shown from the next start.
/// </summary>
public sealed class AccessibilitySettingsViewModelTests : IDisposable
{
    private const string EffectivePath = "api/v2/settings/values/effective";
    private const string ValuePath = "api/v2/settings/values/ui.app_language";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LibraryHarness _harness = new();
    private readonly List<AccessibilitySettingsViewModel> _pages = [];
    private readonly QueuedDispatcher _queued = new();
    private AppLanguageSetting? _setting;

    public AccessibilitySettingsViewModelTests()
    {
        Answer(null);
        _harness.Silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Delete, ValuePath, ScriptedSilo.NoContent());
    }

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            page.Dispose();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void Answer(string? language, string source = "profile") => _harness.Answer(EffectivePath, $$"""
        { "items": [{ "key": "ui.app_language", "source": "{{(language is null ? "default" : source)}}", "value": {{(language is null ? "null" : $"\"{language}\"")}} }],
          "revision": 28 }
        """);

    private AppLanguageFile KeptFile() => new(Path.Combine(_directory, "app-language.json"), _harness.Logs.For<AppLanguageFile>());

    private async Task<AccessibilitySettingsViewModel> OpenAsync(string[]? windows = null, string regional = "en-US", AppLanguageFile? file = null,
        Core.Abstractions.IUiDispatcher? pageDispatcher = null)
    {
        var api = new SettingsApi(_harness.Silo.Http);
        file ??= KeptFile();
        var setting = _setting = new AppLanguageSetting(_harness.Session, new ProfileSettings(api, _harness.Session, _harness.Logs.For<ProfileSettings>()), api,
            file, new ImmediateDispatcher(), _harness.Logs.For<AppLanguageSetting>());
        var page = new AccessibilitySettingsViewModel(setting, file, new FakeDeviceLanguages(windows ?? ["en-US"], regional),
            pageDispatcher ?? new ImmediateDispatcher());
        _pages.Add(page);
        await page.LoadAsync();
        return page;
    }

    private List<string?> Saved() => [.. _harness.Silo.SentTo(HttpMethod.Put, ValuePath).Select(r => r.Body)];

    [Fact]
    public async Task ThePicker_OffersMatchDevice_ThenEveryEnabledLanguage_ByItsOwnName()
    {
        var page = await OpenAsync();

        Assert.Equal(["Match device", .. AppLanguage.Enabled.Select(tag => AppLanguage.Endonym(tag))], page.Choices.Select(c => c.Label));
        Assert.Equal("", page.SelectedChoice?.Value);
        Assert.True(page.CanChoose);
        Assert.Equal(1, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task UntilTheProfilesLanguageIsRead_ThePickerWaits()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(EffectivePath, LibraryHarness.Held(release, """{ "items": [], "revision": 28 }"""));

        var opening = OpenAsync();

        Assert.False(_pages.Single().CanChoose);
        release.SetResult();
        await opening;
    }

    [Fact]
    public async Task ALanguageGranaryDoesNotOffer_StaysListedUnderItsName_AndThePageSaysGranaryFollowsTheDevice()
    {
        // As the web lists a stored tag it does not ship, so saving here never clears what another app chose.
        Answer("it");

        var page = await OpenAsync();

        Assert.Equal(("it", "Italian"), (page.Choices[1].Value, page.Choices[1].Label));
        Assert.Equal("it", page.SelectedChoice?.Value);
        Assert.Equal("This language isn't available in Granary yet, so Granary follows your device.", page.UnavailableNote);
    }

    [Fact]
    public async Task AShippedLanguageGranaryDoesNotOffer_IsNamedInItself()
    {
        Answer("ar");

        var page = await OpenAsync();

        Assert.Equal(("ar", "العربية"), (page.Choices[1].Value, page.Choices[1].Label));
        Assert.NotEmpty(page.UnavailableNote);
    }

    [Fact]
    public async Task AnOfferedLanguage_HasNoNote()
    {
        Answer("en");

        var page = await OpenAsync();

        Assert.Equal("en", page.SelectedChoice?.Value);
        Assert.Equal(1 + AppLanguage.Enabled.Count, page.Choices.Count);
        Assert.Empty(page.UnavailableNote);
    }

    [Fact]
    public async Task AChoice_IsSavedAtOnce()
    {
        var page = await OpenAsync();

        page.SelectedChoice = page.Choices.Single(c => c.Value == "en");
        await _pages.Single().Working;

        Assert.Equal(["""{"value":"en"}"""], Saved());
        Assert.Equal("en", page.SelectedChoice?.Value);
    }

    [Fact]
    public async Task WhatTheStartShowsAlready_NeedsNoRestart()
    {
        var page = await OpenAsync(windows: ["en-US"], regional: "en-US");

        Assert.Empty(page.RestartNote);
    }

    [Fact]
    public async Task AChoiceThatChangesTheNextStart_AppliesAfterARestart()
    {
        // English chosen on a German regional format writes English dates and numbers, which this run does not show.
        var page = await OpenAsync(windows: ["en-US"], regional: "de-DE");

        page.SelectedChoice = page.Choices.Single(c => c.Value == "en");
        await page.Working;

        Assert.Equal("Applies after a restart.", page.RestartNote);

        page.SelectedChoice = page.Choices[0];
        await page.Working;

        Assert.Empty(page.RestartNote);
    }

    [Fact]
    public async Task ALanguageReadFromAnotherDevice_AppliesAfterARestartToo()
    {
        Answer("en");

        var page = await OpenAsync(windows: ["de-DE"], regional: "de-DE");

        Assert.Equal("Applies after a restart.", page.RestartNote);
    }

    [Fact]
    public async Task ARefusedChoice_GoesBack_InTheWebsWords()
    {
        var page = await OpenAsync();
        _harness.Silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.Problem(503, "unavailable"));

        page.SelectedChoice = page.Choices.Single(c => c.Value == "en");
        await page.Working;

        Assert.Equal("", page.SelectedChoice?.Value);
        Assert.Equal("Couldn’t save the app language", page.FailureText);
        Assert.False(page.OffersTryAgain);
    }

    [Fact]
    public async Task AFailedClearOfThisPCsOwnValue_OffersTryAgain()
    {
        Answer("ar", source: SettingScopes.ProfileDevice);
        _harness.Silo.On(HttpMethod.Delete, ValuePath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.NoContent());
        var page = await OpenAsync();

        page.SelectedChoice = page.Choices.Single(c => c.Value == "en");
        await page.Working;

        Assert.Equal("Saved, but this PC still uses its own language. Try again.", page.FailureText);
        Assert.True(page.OffersTryAgain);

        await page.TryAgainCommand.ExecuteAsync(null);

        Assert.Empty(page.FailureText);
        Assert.False(page.OffersTryAgain);
    }

    [Fact]
    public async Task AFailedRead_OffersRetry()
    {
        _harness.Answer(EffectivePath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.Json(System.Net.HttpStatusCode.OK,
            """{ "items": [{ "key": "ui.app_language", "source": "profile", "value": "en" }], "revision": 28 }"""));

        var page = await OpenAsync();

        Assert.True(page.LoadFailed);
        Assert.False(page.CanChoose);

        await page.RetryCommand.ExecuteAsync(null);

        Assert.False(page.LoadFailed);
        Assert.Equal("en", page.SelectedChoice?.Value);
    }

    [Fact]
    public async Task AServerWithoutTheSetting_SaysWhyThereIsNoChoice()
    {
        _harness.Answer(EffectivePath, ScriptedSilo.Problem(422, "validation_failed"));

        var page = await OpenAsync();

        Assert.True(page.IsUnsupported);
        Assert.False(page.OffersLanguage);
    }

    [Fact]
    public async Task WhileAChoiceSaves_TheSelectStaysOn_AndAnotherChoiceGoesBack()
    {
        // As the web: one save at a time, the select keeps the keyboard.
        var page = await OpenAsync(regional: "de-DE");
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ValuePath, LibraryHarness.Held(release, "{}"));

        page.SelectedChoice = page.Choices.Single(c => c.Value == "en");
        var first = page.Working;
        page.SelectedChoice = page.Choices[0];

        Assert.True(page.CanChoose);
        Assert.Equal("en", page.SelectedChoice?.Value);
        Assert.Empty(page.RestartNote);
        release.SetResult();
        await first;

        Assert.Equal(["""{"value":"en"}"""], Saved());
        Assert.Equal("Applies after a restart.", page.RestartNote);
    }

    [Fact]
    public async Task AChoiceThatDoesNotApplyHereYet_SaysNothingOfARestart()
    {
        // This run started in Japanese, which this PC's own value keeps; English is saved for the profile, not cleared here.
        await KeptFile().KeepAsync("ja");
        Answer("ja", source: SettingScopes.ProfileDevice);
        _harness.Silo.On(HttpMethod.Delete, ValuePath, ScriptedSilo.Problem(503, "unavailable"));
        var page = await OpenAsync(windows: ["de-DE"], regional: "de-DE");
        Assert.Empty(page.RestartNote);

        page.SelectedChoice = page.Choices.Single(c => c.Value == "en");
        await page.Working;

        Assert.True(page.OffersTryAgain);
        Assert.Empty(page.RestartNote);
    }

    [Fact]
    public async Task AChoiceMadeForOneProfile_IsNotSavedForTheNext_ThatTookOverBeforeItWent()
    {
        // The choice goes out after the select's own change; another profile took over and was read meanwhile.
        var page = await OpenAsync(pageDispatcher: _queued);
        page.SelectedChoice = page.Choices.Single(c => c.Value == "en");

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        await _setting!.Working;
        Assert.Equal(AppLanguageState.Ready, _setting.State);
        _queued.RunAll();
        await page.Working;

        Assert.Empty(Saved());
    }
}
