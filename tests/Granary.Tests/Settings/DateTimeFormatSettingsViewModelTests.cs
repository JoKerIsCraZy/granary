using Granary.Core.Api.Settings;
using Granary.Core.Settings;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>Settings → Accessibility → Date &amp; time, as the web's group of that name (pages/settings/AccessibilitySettings.tsx).</summary>
public sealed class DateTimeFormatSettingsViewModelTests : IDisposable
{
    private const string EffectivePath = "api/v2/settings/values/effective";
    private const string DatePath = "api/v2/settings/values/ui.date_format";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LibraryHarness _harness = new();
    private readonly List<IDisposable> _pages = [];
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2013, 9, 12, 15, 4, 9, TimeSpan.Zero));

    public DateTimeFormatSettingsViewModelTests()
    {
        _harness.Answer(EffectivePath, """
            { "items": [
                { "key": "ui.date_format", "source": "profile", "value": "DD/MM/YYYY" },
                { "key": "ui.time_format", "source": "default", "value": "auto" }],
              "revision": 30 }
            """);
        _harness.Silo.On(HttpMethod.Put, DatePath, ScriptedSilo.NoContent());
    }

    public void Dispose()
    {
        _pages.ForEach(page => page.Dispose());
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<DateTimeFormatSettingsViewModel> OpenAsync()
    {
        var api = new SettingsApi(_harness.Silo.Http);
        var setting = new DateTimeFormatSetting(_harness.Session, new ProfileSettings(api, _harness.Session, _harness.Logs.For<ProfileSettings>()),
            new DateTimeFormatFile(Path.Combine(_directory, "date-time-format.json"), _harness.Logs.For<DateTimeFormatFile>()),
            new ImmediateDispatcher(), _harness.Logs.For<DateTimeFormatSetting>(), _ => { });
        var page = new DateTimeFormatSettingsViewModel(setting, _clock);
        _pages.Add(page);
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task TheChoices_AreTheWebs_WithMatchDeviceForAuto()
    {
        var page = await OpenAsync();

        Assert.Equal(["Match device", "DD/MM/YYYY", "MM/DD/YYYY", "YYYY-MM-DD"], page.DateChoices.Select(c => c.Label));
        Assert.Equal(["Match device", "12-hour", "24-hour"], page.TimeChoices.Select(c => c.Label));
        Assert.True(page.CanChoose);
    }

    [Fact]
    public async Task TheProfilesFormats_AreChosen()
    {
        var page = await OpenAsync();

        Assert.Equal([false, true, false, false], page.DateChoices.Select(c => c.IsSelected));
        Assert.Equal([true, false, false], page.TimeChoices.Select(c => c.IsSelected));
    }

    [Fact]
    public async Task Choosing_SavesAtOnce_AndThePreviewFollows()
    {
        var page = await OpenAsync();

        await page.DateChoices[3].PickCommand.ExecuteAsync(null);

        Assert.True(page.DateChoices[3].IsSelected);
        Assert.False(page.DateChoices[1].IsSelected);
        Assert.Equal("""{"value":"YYYY-MM-DD"}""", Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, DatePath)).Body);
        Assert.StartsWith("Preview: 2013-09-12 · 2013-09-12 · ", page.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeforeTheFormatsAreRead_NothingCanBeChosen()
    {
        _harness.Answer(EffectivePath, ScriptedSilo.Problem(503, "unavailable"));

        var page = await OpenAsync();

        Assert.False(page.CanChoose);
    }
}
