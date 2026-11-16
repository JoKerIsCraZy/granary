using Granary.Core.Settings.Search;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Settings;

namespace Granary.Tests.Settings;

/// <summary>
/// The search box at the top of the settings' sidebar, as the web's SettingsSearchInput and UserSettingsSidebar: it narrows
/// the sidebar to the pages that match, says how many under the box (nothing while it is empty), and knows which settings of a
/// page matched, so the page can show the first. It searches the texts in the language on screen, and English too.
/// </summary>
public sealed class SettingsSearchViewModelTests
{
    // The sidebar as SettingsPage.xaml has it, with a page's markup each.
    private static readonly SettingsSidebarGroup[] Sidebar =
    [
        new("Pages", "web.settings.nav.group.playback",
        [
            new("playback", "web.settings.playback.title", ["bandwidth"]),
            new("subtitles", "web.settings.subtitleAppearance.title", []),
        ]),
        new("AppPages", "granary.settings.nav.group.app", [new("about", "granary.settings.nav.about", [])]),
    ];

    private static readonly Dictionary<string, SettingsMarkupPage> Markup = new(StringComparer.Ordinal)
    {
        ["playback"] = new("web.settings.playback.subtitle",
        [
            new(SettingsEntryKind.Section, "web.settings.playback.quality.title", null, []),
            new(SettingsEntryKind.Row, "settingsManifest.playback.max_bitrate_kbps.label", "web.settings.playback.maxBitrate.description", []),
            new(SettingsEntryKind.Row, "settingsManifest.player.audio_sync_ms.label", null, ["lip sync"]),
        ]),
        ["subtitles"] = new(null, [new(SettingsEntryKind.Row, "web.settings.subtitleAppearance.subtitles.languageLabel", null, [])]),
    };

    [Fact]
    public void TheIndex_HasThePagesTextsInTheLanguageOnScreen()
    {
        var groups = SettingsSearchIndex.Build(Sidebar, Markup.GetValueOrDefault);

        var playback = groups[0].Pages[0];
        Assert.Equal(("Playback", "Playback"), (groups[0].Label, playback.Label));
        Assert.Equal(AppText.Get("web.settings.playback.subtitle"), playback.Description);
        Assert.Equal(["bandwidth"], playback.Keywords);
        Assert.Equal("Maximum bitrate", playback.Settings[1].Label);
        Assert.Equal(["lip sync"], playback.Settings[2].Keywords);
        // A page without markup is still in the sidebar.
        Assert.Empty(groups[1].Pages[0].Settings);
    }

    [Fact]
    public void InAnotherLanguage_TheEnglishTexts_AreKeywords()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));

        var playback = SettingsSearchIndex.Build(Sidebar, Markup.GetValueOrDefault)[0].Pages[0];

        Assert.Equal("Wiedergabe", playback.Label);
        Assert.Contains("Playback", playback.Keywords);
        Assert.Contains("Maximum bitrate", playback.Settings[1].Keywords);
    }

    [Fact]
    public void WhileNothingIsTyped_EveryPageShows_AndNothingIsSaid()
    {
        var search = Search();

        search.Query = "   ";

        Assert.False(search.HasQuery);
        Assert.Null(search.Results);
        Assert.Equal("", search.Status);
        Assert.False(search.ShowsNoMatches);
    }

    [Fact]
    public void AQuery_NarrowsThePages_AndCountsThem()
    {
        var search = Search();

        search.Query = "bitrate";

        Assert.Equal(["playback"], Tags(search));
        Assert.Equal("1 match", search.Status);
        Assert.False(search.ShowsNoMatches);
    }

    [Fact]
    public void AGroupsName_CountsAllItsPages()
    {
        var search = Search();

        search.Query = "playback";

        Assert.Equal(["playback", "subtitles"], Tags(search));
        Assert.Equal("2 matches", search.Status);
    }

    [Fact]
    public void NothingThatFits_SaysNoMatches()
    {
        var search = Search();

        search.Query = "dolby";

        Assert.Equal([], Tags(search));
        Assert.Equal("No matching settings", search.Status);
        Assert.True(search.ShowsNoMatches);
    }

    [Fact]
    public void ThePagesSettingsThatMatch_AreKnown_InThePagesOrder()
    {
        var search = Search();

        search.Query = "lip";

        Assert.Equal(["Audio sync offset"], search.MatchesOn("playback").Select(entry => entry.Label));
        Assert.Empty(search.MatchesOn("subtitles"));
    }

    [Fact]
    public void APageThatMatchesByItsGroupsName_HasNoSettingToShow()
    {
        var search = Search();

        search.Query = "playback";

        Assert.Empty(search.MatchesOn("subtitles"));
    }

    [Fact]
    public void Clear_EmptiesTheBox()
    {
        var search = Search();
        search.Query = "bitrate";

        search.ClearCommand.Execute(null);

        Assert.Equal("", search.Query);
        Assert.Null(search.Results);
        Assert.Equal("", search.Status);
    }

    [Fact]
    public void TheStatus_ChangesOnlyWhenItsWordsDo()
    {
        var search = Search();
        var said = new List<string>();
        search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsSearchViewModel.Status))
            {
                said.Add(search.Status);
            }
        };

        search.Query = "bit";
        search.Query = "bitr";
        search.Query = "bitrate";
        search.Query = "";

        Assert.Equal(["1 match", ""], said);
    }

    [Fact]
    public void ThePagesAreRead_OnceTheFirstWordIsTyped()
    {
        int reads = 0;
        var search = new SettingsSearchViewModel(() =>
        {
            reads++;
            return SettingsSearchIndex.Build(Sidebar, Markup.GetValueOrDefault);
        });

        Assert.Equal(0, reads);
        search.Query = "bit";
        search.Query = "bitrate";
        Assert.Equal(1, reads);
    }

    /// <summary>The pages could not be read: the box finds nothing, says so, and does not try again at every key.</summary>
    [Fact]
    public void AnIndexThatCannotBeRead_FindsNothing()
    {
        int reads = 0;
        var search = new SettingsSearchViewModel(() =>
        {
            reads++;
            throw new InvalidOperationException("The markup is broken.");
        });

        search.Query = "bit";
        search.Query = "bitrate";

        Assert.Equal(1, reads);
        Assert.Equal([], Tags(search));
        Assert.Equal("No matching settings", search.Status);
        Assert.Empty(search.MatchesOn("playback"));
    }

    [Fact]
    public void InAnotherLanguage_TheStatusIsInIt()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));
        var search = Search();

        search.Query = "dolby";

        Assert.Equal("Keine passenden Einstellungen", search.Status);
    }

    private static SettingsSearchViewModel Search() => new(() => SettingsSearchIndex.Build(Sidebar, Markup.GetValueOrDefault));

    private static string[] Tags(SettingsSearchViewModel search) =>
        [.. (search.Results ?? []).SelectMany(group => group.Pages).Select(page => page.Tag)];
}
