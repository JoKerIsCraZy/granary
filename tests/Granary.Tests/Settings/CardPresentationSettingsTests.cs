using System.Text.Json;
using Granary.Core.Api.Settings;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>The poster size and caption, read and saved for the desktop apps as the web saves them for its browsers.</summary>
public sealed class CardPresentationSettingsTests
{
    private const string EffectivePath = "api/v2/settings/values/effective";
    private const string ValuePath = "api/v2/settings/values/ui.card_presentation";

    private const string DesktopsOwn = """
        { "items": [ { "key": "ui.card_presentation", "source": "profile_client",
            "value": { "poster_size": "compact", "caption": "title" } } ], "revision": 3 }
        """;

    private const string NoneOfItsOwn = """
        { "items": [ { "key": "ui.card_presentation", "source": "default",
            "value": { "poster_size": "standard", "caption": "title_metadata" } } ], "revision": 1 }
        """;

    private readonly LibraryHarness _harness = new();

    public CardPresentationSettingsTests()
    {
        _harness.Answer(EffectivePath, NoneOfItsOwn);
        _harness.Silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Delete, ValuePath, ScriptedSilo.NoContent());
    }

    private CardPresentationSettings Create() =>
        new(_harness.Session, new SettingsApi(_harness.Silo.Http), new ImmediateDispatcher(), _harness.Logs.For<CardPresentationSettings>());

    private static string Answer(string source, string value) =>
        $$"""{ "items": [ { "key": "ui.card_presentation", "source": "{{source}}", "value": {{value}} } ], "revision": 2 }""";

    private JsonElement Saved(int index = 0) =>
        JsonDocument.Parse(_harness.Silo.SentTo(HttpMethod.Put, ValuePath)[index].Body!).RootElement.GetProperty("value");

    [Fact]
    public async Task TheDesktopAppsOwnChoice_IsShown_AndCanBeReset()
    {
        _harness.Answer(EffectivePath, DesktopsOwn);
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(new CardPresentation(PosterSize.Compact, CardCaption.Title), settings.Shown);
        Assert.True(settings.HasOverride);
        Assert.False(settings.HasDeviceOverride);
        Assert.Equal(CardPresentationState.Ready, settings.State);
        Assert.True(settings.CanEdit);
        Assert.Contains("keys=ui.card_presentation", Assert.Single(_harness.Queries(EffectivePath)));
    }

    [Fact]
    public async Task WithoutAChoice_TheDefaultShows()
    {
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(CardPresentation.Default, settings.Shown);
        Assert.False(settings.HasOverride);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task TheProfilesChoiceForEveryApp_Shows_ButIsNotTheDesktopAppsOwn()
    {
        _harness.Answer(EffectivePath, Answer("profile", """{ "poster_size": "large", "caption": "artwork" }"""));
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(new CardPresentation(PosterSize.Large, CardCaption.Artwork), settings.Shown);
        Assert.False(settings.HasOverride);
    }

    [Fact]
    public async Task AValueTheWebWouldNotRead_ShowsTheDefault()
    {
        _harness.Answer(EffectivePath, Answer("profile_client", """{ "poster_size": "gigantic", "caption": "title" }"""));
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(CardPresentation.Default, settings.Shown);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task ThisPcsOwnValue_StopsEditing_UntilItIsDropped()
    {
        _harness.Answer(EffectivePath, Answer("profile_device", """{ "poster_size": "large", "caption": "title" }"""), DesktopsOwn);
        var settings = Create();
        await settings.StartAsync();

        Assert.True(settings.HasDeviceOverride);
        Assert.False(settings.CanEdit);
        await settings.SetAsync(settings.Shown with { PosterSize = PosterSize.Compact });
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Put, ValuePath));

        await settings.FollowOtherComputersAsync();

        var delete = Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, ValuePath));
        Assert.Equal("?scope=profile_device", delete.Uri.Query);
        Assert.False(settings.HasDeviceOverride);
        Assert.True(settings.CanEdit);
        Assert.Equal(new CardPresentation(PosterSize.Compact, CardCaption.Title), settings.Shown);
    }

    [Fact]
    public async Task AChange_ShowsAtOnce_AndSavesForTheDesktopApps()
    {
        var settings = Create();
        await settings.StartAsync();

        await settings.SetAsync(settings.Shown with { PosterSize = PosterSize.Compact });

        Assert.Equal(PosterSize.Compact, settings.Shown.PosterSize);
        Assert.True(settings.HasOverride);
        var put = Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, ValuePath));
        Assert.Equal("?scope=profile_client", put.Uri.Query);
        Assert.Equal("""{"poster_size":"compact","caption":"title_metadata"}""", Saved().GetRawText());
    }

    [Fact]
    public async Task TheSameChoiceAgain_IsNotSaved()
    {
        _harness.Answer(EffectivePath, DesktopsOwn);
        var settings = Create();
        await settings.StartAsync();

        await settings.SetAsync(new CardPresentation(PosterSize.Compact, CardCaption.Title));

        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Put, ValuePath));
    }

    [Fact]
    public async Task ARefusedSave_PutsBackWhatTheServerHas()
    {
        var settings = Create();
        await settings.StartAsync();
        _harness.Silo.On(HttpMethod.Put, ValuePath, ScriptedSilo.Problem(422, "validation_failed"));

        await settings.SetAsync(settings.Shown with { Caption = CardCaption.Artwork });

        Assert.Equal(CardPresentation.Default, settings.Shown);
        Assert.False(settings.HasOverride);
    }

    [Fact]
    public async Task QuickChanges_AreSavedOneAfterAnother_TheLastOneLast()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ValuePath, LibraryHarness.Held(release, "{}"));

        var first = settings.SetAsync(settings.Shown with { PosterSize = PosterSize.Compact });
        var second = settings.SetAsync(settings.Shown with { PosterSize = PosterSize.Large });
        var third = settings.SetAsync(settings.Shown with { Caption = CardCaption.Title });
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, ValuePath));
        release.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(["compact", "large"], Enumerable.Range(0, 2).Select(i => Saved(i).GetProperty("poster_size").GetString()));
        Assert.Equal("title", Saved(1).GetProperty("caption").GetString());
        Assert.Equal(new CardPresentation(PosterSize.Large, CardCaption.Title), settings.Shown);
    }

    [Fact]
    public async Task Reset_DropsTheDesktopAppsChoice_AndReadsWhatAppliesThen()
    {
        _harness.Answer(EffectivePath, DesktopsOwn, NoneOfItsOwn);
        var settings = Create();
        await settings.StartAsync();

        await settings.ResetAsync();

        var delete = Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, ValuePath));
        Assert.Equal("?scope=profile_client", delete.Uri.Query);
        Assert.Equal(CardPresentation.Default, settings.Shown);
        Assert.False(settings.HasOverride);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResetThatLanded_LeavesNoChoiceOfItsOwn_ThoughTheReadAfterFails(bool thisPc)
    {
        _harness.Answer(EffectivePath, thisPc ? Answer("profile_device", """{ "poster_size": "large", "caption": "title" }""") : DesktopsOwn);
        var settings = Create();
        await settings.StartAsync();
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));

        await (thisPc ? settings.FollowOtherComputersAsync() : settings.ResetAsync());

        Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, ValuePath));
        Assert.False(settings.HasOverride);
        Assert.False(settings.HasDeviceOverride);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task AResetAskedBeforeAnotherProfileTookOver_DropsNothingOfTheOthers()
    {
        _harness.Answer(EffectivePath, DesktopsOwn);
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ValuePath, LibraryHarness.Held(release, "{}"));
        var saving = settings.SetAsync(settings.Shown with { PosterSize = PosterSize.Large });
        var resetting = settings.ResetAsync();

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        release.SetResult();
        await Task.WhenAll(saving, resetting, settings.Working);

        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Delete, ValuePath));
    }

    [Fact]
    public async Task AnOlderServer_ShowsTheDefault_AndCannotBeEdited()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "unknown_setting"));
        var settings = Create();

        await settings.StartAsync();

        Assert.Equal(CardPresentationState.Unsupported, settings.State);
        Assert.False(settings.CanEdit);
        Assert.Equal(CardPresentation.Default, settings.Shown);
    }

    [Fact]
    public async Task AFailedFirstRead_StopsEditing_AndTheNextRefreshReadsAgain()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));
        var settings = Create();
        await settings.StartAsync();

        Assert.Equal(CardPresentationState.Unavailable, settings.State);
        Assert.False(settings.CanEdit);

        _harness.Answer(EffectivePath, DesktopsOwn);
        await settings.RefreshAsync();

        Assert.Equal(CardPresentationState.Ready, settings.State);
        Assert.Equal(PosterSize.Compact, settings.Shown.PosterSize);
    }

    [Fact]
    public async Task AFailedReadAgain_KeepsTheChoice_AndEditing()
    {
        _harness.Answer(EffectivePath, DesktopsOwn);
        var settings = Create();
        await settings.StartAsync();
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(503, "unavailable"));

        await settings.RefreshAsync();

        Assert.Equal(PosterSize.Compact, settings.Shown.PosterSize);
        Assert.True(settings.HasOverride);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task AnotherProfile_IsReadAgain()
    {
        var settings = Create();
        await settings.StartAsync();
        _harness.Answer(EffectivePath, DesktopsOwn);

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        await settings.Working;

        Assert.Equal(PosterSize.Compact, settings.Shown.PosterSize);
        Assert.Equal(2, _harness.Count(EffectivePath));
    }

    [Fact]
    public async Task AChangeWhileTheChoiceIsReadAgain_IsNotPutBack()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Answer(EffectivePath, LibraryHarness.Held(release, NoneOfItsOwn));

        var reading = settings.RefreshAsync();
        var saving = settings.SetAsync(settings.Shown with { PosterSize = PosterSize.Large });
        release.SetResult();
        await reading;
        await saving;

        Assert.Equal(PosterSize.Large, settings.Shown.PosterSize);
        Assert.True(settings.HasOverride);
    }

    [Fact]
    public async Task AReadAgainWhileAChangeIsOnItsWay_WaitsForIt_AndDoesNotPutTheOldChoiceBack()
    {
        var settings = Create();
        await settings.StartAsync();
        var release = new TaskCompletionSource();
        _harness.Silo.On(HttpMethod.Put, ValuePath, LibraryHarness.Held(release, "{}"));
        var saving = settings.SetAsync(settings.Shown with { PosterSize = PosterSize.Large });

        var reading = settings.RefreshAsync();
        Assert.Equal(1, _harness.Count(EffectivePath));
        // What the server has once the change landed.
        _harness.Answer(EffectivePath, Answer("profile_client", """{ "poster_size": "large", "caption": "title_metadata" }"""));
        release.SetResult();
        await Task.WhenAll(saving, reading);

        Assert.Equal(2, _harness.Count(EffectivePath));
        Assert.Equal(PosterSize.Large, settings.Shown.PosterSize);
        Assert.True(settings.HasOverride);
    }

    [Fact]
    public async Task ThePage_OffersTheWebsChoices_AndAPickSavesIt()
    {
        var settings = Create();
        await settings.StartAsync();
        using var page = new PosterSettingsViewModel(settings);

        Assert.Equal(["Compact", "Standard", "Large"], page.Sizes.Select(c => c.Label));
        Assert.Equal(["Title and details", "Title only", "Nothing"], page.Captions.Select(c => c.Label));
        Assert.Equal("Standard", Assert.Single(page.Sizes, c => c.IsSelected).Label);
        Assert.False(page.HasOverride);

        var compact = page.Sizes[0];
        compact.PickCommand.Execute(null);
        await settings.Working;
        await page.Captions[1].PickCommand.ExecuteAsync(null);

        Assert.True(compact.IsSelected);
        Assert.Same(compact, page.Sizes[0]);
        Assert.Equal("Title only", Assert.Single(page.Captions, c => c.IsSelected).Label);
        Assert.Equal("""{"poster_size":"compact","caption":"title"}""", Saved(1).GetRawText());
        Assert.True(page.HasOverride);
    }

    [Fact]
    public async Task ThePage_SaysWhyItCannotEdit()
    {
        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "unknown_setting"));
        var settings = Create();
        using var page = new PosterSettingsViewModel(settings);

        Assert.True(page.IsLoading);
        await settings.StartAsync();

        Assert.False(page.IsLoading);
        Assert.False(page.IsReady);
        Assert.Equal(PosterSettingsViewModel.UnsupportedTitle, page.BlockerTitle);
        Assert.Equal(PosterSettingsViewModel.UnsupportedText, page.BlockerText);
    }

    [Fact]
    public async Task ThePage_ShowsNoReset_WhileItCannotEdit()
    {
        _harness.Answer(EffectivePath, DesktopsOwn);
        var settings = Create();
        await settings.StartAsync();
        using var page = new PosterSettingsViewModel(settings);
        Assert.True(page.HasOverride);

        _harness.Silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "unknown_setting"));
        await page.LoadAsync();

        Assert.False(page.HasOverride);
        Assert.False(page.IsReady);
        Assert.Equal(PosterSettingsViewModel.UnsupportedTitle, page.BlockerTitle);
    }
}
