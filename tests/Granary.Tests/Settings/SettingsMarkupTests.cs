using System.Text.RegularExpressions;
using System.Xml.Linq;
using Granary.Core.Settings.Search;
using Granary.Core.Texts;

namespace Granary.Tests.Settings;

/// <summary>
/// The settings' search finds what the pages show by reading their markup, so there is no second list to keep in step:
/// a section's title, a row's label, a heading and a control named for screen readers, each by the text key the page shows,
/// with the description beside it and the keywords kept next to it.
/// </summary>
public sealed partial class SettingsMarkupTests
{
    private const string Open = """
        <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:controls="using:Granary.App.Controls" xmlns:t="using:Granary.Core.Texts">
        """;

    private const string Close = "</UserControl>";

    [Fact]
    public void SectionsRowsHeadingsAndNamedControls_AreFound_InTheirOrder()
    {
        var page = SettingsMarkup.ReadPage(Open + """
            <StackPanel>
              <controls:SettingsGroup Title="{x:Bind t:AppText.Get('quality.title')}" Description="{x:Bind t:AppText.Get('quality.about')}">
                <controls:SettingsRow Label="{x:Bind t:AppText.Get('bitrate.label')}" Description="{x:Bind t:AppText.Get('bitrate.about'), Mode=OneTime}">
                  <ComboBox AutomationProperties.Name="{x:Bind t:AppText.Get('bitrate.label')}" />
                </controls:SettingsRow>
              </controls:SettingsGroup>
              <TextBlock Text="{x:Bind t:AppText.Get('seek.title')}" AutomationProperties.HeadingLevel="Level2" />
              <Button Content="{x:Bind t:AppText.Get('common.edit')}" AutomationProperties.Name="{x:Bind t:AppText.Get('mpv.edit')}" />
              <ProgressRing AutomationProperties.Name="{x:Bind t:AppText.Get('common.working')}" />
              <TextBlock Text="{x:Bind t:AppText.Get('mpv.status')}" AutomationProperties.Name="{x:Bind t:AppText.Get('mpv.status')}" />
            </StackPanel>
            """ + Close);

        Assert.Equal(
            [
                (SettingsEntryKind.Section, "quality.title", "quality.about"),
                (SettingsEntryKind.Row, "bitrate.label", "bitrate.about"),
                (SettingsEntryKind.Heading, "seek.title", null),
                (SettingsEntryKind.Control, "mpv.edit", null),
            ],
            page.Entries.Select(entry => (entry.Kind, entry.LabelKey, entry.DescriptionKey)));
    }

    [Fact]
    public void TheTextAfterThePagesTitle_IsItsDescription()
    {
        var page = SettingsMarkup.ReadPage(Open + """
            <StackPanel>
              <TextBlock Text="{x:Bind t:AppText.Get('playback.title')}" AutomationProperties.HeadingLevel="Level1" />
              <TextBlock Text="{x:Bind t:AppText.Get('playback.subtitle')}" />
            </StackPanel>
            """ + Close);

        Assert.Equal("playback.subtitle", page.DescriptionKey);
        Assert.Empty(page.Entries);
    }

    [Fact]
    public void WhatATemplateOrTheResourcesHold_IsLeftOut()
    {
        var page = SettingsMarkup.ReadPage(Open + """
            <UserControl.Resources>
              <DataTemplate x:Key="Row">
                <controls:SettingsRow Label="{x:Bind t:AppText.Get('template.row')}" />
              </DataTemplate>
            </UserControl.Resources>
            <ItemsControl>
              <ItemsControl.ItemTemplate>
                <DataTemplate>
                  <TextBlock Text="{x:Bind t:AppText.Get('template.heading')}" AutomationProperties.HeadingLevel="Level3" />
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
            """ + Close);

        Assert.Empty(page.Entries);
    }

    [Fact]
    public void ATextFromTheViewModel_IsNotAKeyAndIsLeftOut()
    {
        var page = SettingsMarkup.ReadPage(Open + """
            <StackPanel>
              <controls:SettingsRow Label="{x:Bind ViewModel.LimitText, Mode=OneWay}" />
              <controls:SettingsRow Label="{x:Bind t:AppText.Get('row.label')}" Description="{x:Bind ViewModel.QualityDescription, Mode=OneWay}" />
              <TextBlock Text="Granary" AutomationProperties.HeadingLevel="Level2" />
            </StackPanel>
            """ + Close);

        var entry = Assert.Single(page.Entries);
        Assert.Equal(("row.label", (string?)null), (entry.LabelKey, entry.DescriptionKey));
    }

    [Fact]
    public void AControlNamedAsItsRow_IsFoundOnce()
    {
        var page = SettingsMarkup.ReadPage(Open + """
            <StackPanel>
              <controls:SettingsRow Label="{x:Bind t:AppText.Get('decoding.label')}">
                <ToggleSwitch AutomationProperties.Name="{x:Bind t:AppText.Get('decoding.label')}" />
              </controls:SettingsRow>
            </StackPanel>
            """ + Close);

        Assert.Equal([SettingsEntryKind.Row], page.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public void KeywordsNextToASetting_AreItsKeywords()
    {
        var page = SettingsMarkup.ReadPage(Open + """
            <controls:SettingsRow Label="{x:Bind t:AppText.Get('delay.label')}" controls:SearchKeywords.Terms="lip sync, sound delay ,, " />
            """ + Close);

        Assert.Equal(["lip sync", "sound delay"], Assert.Single(page.Entries).Keywords);
    }

    [Fact]
    public void TheSidebar_GivesEachListsPages_ByTagNameAndKeywords()
    {
        var lists = SettingsMarkup.ReadSidebar("""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:controls="using:Granary.App.Controls" xmlns:t="using:Granary.Core.Texts">
              <StackPanel>
                <ListView x:Name="Pages">
                  <ListViewItem Tag="playback" AutomationProperties.Name="{x:Bind t:AppText.Get('playback.title')}" controls:SearchKeywords.Terms="bitrate" />
                  <ListViewItem Tag="subtitles" AutomationProperties.Name="{x:Bind t:AppText.Get('subtitles.title')}" />
                </ListView>
              </StackPanel>
            </Page>
            """);

        var pages = lists["Pages"];
        Assert.Equal([("playback", "playback.title"), ("subtitles", "subtitles.title")], pages.Select(page => (page.Tag, page.LabelKey)));
        Assert.Equal(["bitrate"], pages[0].Keywords);
    }

    // The app's own markup.

    [Fact]
    public void TheSettingsSidebar_ListsEveryPage_InItsGroups()
    {
        var lists = SettingsMarkup.ReadSidebar(File.ReadAllText(AppFile("Views/SettingsPage.xaml")));

        Assert.Equal(
            [
                ("Pages", "playback subtitles"),
                ("AppearancePages", "interface title-pages accessibility"),
                ("AccountPages", "profiles devices"),
                ("AppPages", "servers downloads about"),
            ],
            lists.Select(list => (list.Key, string.Join(' ', list.Value.Select(page => page.Tag)))));
    }

    /// <summary>
    /// SettingsPage names each sidebar list's group by a key in its table of groups, which the search also reads: each list
    /// has one, and each key is in the English catalog, as TextKeysTests checks the keys given to AppText.Get.
    /// </summary>
    [Fact]
    public void EverySidebarList_HasAGroupNameInTheCatalog()
    {
        var english = TextCatalog.Embedded(AppLanguage.English);
        var groups = GroupName().Matches(File.ReadAllText(AppFile("Views/SettingsPage.xaml.cs")))
            .ToDictionary(match => match.Groups["list"].Value, match => match.Groups["key"].Value);
        var lists = SettingsMarkup.ReadSidebar(File.ReadAllText(AppFile("Views/SettingsPage.xaml"))).Keys;

        Assert.Equal(lists.Order(StringComparer.Ordinal), groups.Keys.Order(StringComparer.Ordinal));
        Assert.All(groups.Values, key => Assert.True(english.Contains(key), $"{key} is not in the English catalog"));
    }

    /// <summary>
    /// Every sidebar entry opens a view whose markup the app embeds (Views/Settings/*SettingsView.xaml), by SettingsPage's
    /// table of views, so the search reads the page that opens.
    /// </summary>
    [Fact]
    public void EverySidebarPage_HasAViewWhoseMarkupSearchReads()
    {
        string code = File.ReadAllText(AppFile("Views/SettingsPage.xaml.cs"));
        var views = PageView().Matches(code).ToDictionary(
            match => match.Groups["tag"].Value is "PlaybackPage" ? "playback" : match.Groups["tag"].Value.Trim('"'),
            match => match.Groups["view"].Value);
        var tags = SettingsMarkup.ReadSidebar(File.ReadAllText(AppFile("Views/SettingsPage.xaml"))).Values.SelectMany(pages => pages)
            .Select(page => page.Tag);

        Assert.All(tags, tag =>
        {
            Assert.True(views.TryGetValue(tag, out var view), $"{tag} has no view");
            Assert.EndsWith("SettingsView", view, StringComparison.Ordinal);
            Assert.True(File.Exists(AppFile($"Views/Settings/{view}.xaml")), $"{view}.xaml is missing");
        });
    }

    [Fact]
    public void EverySettingsPage_ShowsSomethingSearchFinds()
    {
        var empty = SettingsViews().Where(file => SettingsMarkup.ReadPage(File.ReadAllText(file)).Entries.Count == 0)
            .Select(RepositoryFiles.Relative);

        Assert.Empty(empty);
    }

    [Fact]
    public void TheSettingsOnThePages_AreFound()
    {
        var playback = SettingsMarkup.ReadPage(File.ReadAllText(AppFile("Views/Settings/PlaybackSettingsView.xaml")));
        var devices = SettingsMarkup.ReadPage(File.ReadAllText(AppFile("Views/Settings/DevicesSettingsView.xaml")));

        Assert.Contains(playback.Entries, entry => entry is { Kind: SettingsEntryKind.Row, LabelKey: "granary.settings.playback.softwareDecoding" });
        Assert.Contains(playback.Entries, entry => entry is
        {
            Kind: SettingsEntryKind.Row, LabelKey: "granary.settings.playback.readAhead",
            DescriptionKey: "granary.settings.playback.readAheadDescription",
        } && entry.Keywords.Contains("cache") && entry.Keywords.Contains("stutter"));
        Assert.Contains(playback.Entries, entry => entry is { Kind: SettingsEntryKind.Section, LabelKey: "web.settings.playback.quality.title" });
        Assert.Equal("web.settings.playback.subtitle", playback.DescriptionKey);
        Assert.Contains(devices.Entries, entry => entry is { Kind: SettingsEntryKind.Heading, LabelKey: "web.settings.sessions.title" });
    }

    /// <summary>
    /// A row or section whose label is not a text key search can read: these few name a state, not a setting. Any other
    /// one would be a setting search cannot find, so it should name its text by key.
    /// </summary>
    [Fact]
    public void OnlyRowsThatNameAState_AreLabelledFromTheViewModel()
    {
        var unread = SettingsViews()
            .SelectMany(file => XDocument.Load(file).Descendants()
                .Where(element => element.Name.NamespaceName == "using:Granary.App.Controls"
                    && element.Name.LocalName is "SettingsRow" or "SettingsGroup")
                .Select(element => (string?)element.Attribute("Label") ?? (string?)element.Attribute("Title"))
                .Where(label => label is not null && SettingsMarkup.KeyOf(label) is null)
                .Select(label => $"{Path.GetFileName(file)}: {label}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "AboutSettingsView.xaml: {x:Bind ViewModel.Updates.StatusText, Mode=OneWay}",
                "DownloadsSettingsView.xaml: {x:Bind ViewModel.DefaultMonitorLimitText, Mode=OneWay}",
                "PlaybackSettingsView.xaml: {x:Bind ViewModel.LearnedLimitText, Mode=OneWay}",
            ],
            unread);
    }

    [Theory]
    [InlineData("{x:Bind t:AppText.Get('web.settings.playback.title')}", "web.settings.playback.title")]
    [InlineData("{x:Bind t:AppText.Get( 'common.save' ), Mode=OneTime}", "common.save")]
    [InlineData("{x:Bind ViewModel.Title}", null)]
    [InlineData("Granary", null)]
    [InlineData(null, null)]
    public void AKey_IsReadFromABindingToAppTextAlone(string? value, string? key)
    {
        Assert.Equal(key, SettingsMarkup.KeyOf(value));
    }

    private static IEnumerable<string> SettingsViews() =>
        RepositoryFiles.Under("src/Granary.App/Views/Settings", "*SettingsView.xaml");

    private static string AppFile(string path) => Path.Combine(RepositoryFiles.Root, "src", "Granary.App", path);

    // An entry of SettingsPage's table: ["tag"] = (nameof(SomeSettingsView), …).
    [GeneratedRegex("""\[(?<tag>PlaybackPage|"[a-z-]+")\]\s*=\s*\(nameof\((?<view>\w+)\)""")]
    private static partial Regex PageView();

    // A group of SettingsPage's table: (SomeGroup, SomeList, "the.name.key").
    [GeneratedRegex("""\(\s*\w+Group\s*,\s*(?<list>\w+)\s*,\s*"(?<key>[a-z][\w.]+)"\s*\)""")]
    private static partial Regex GroupName();
}
