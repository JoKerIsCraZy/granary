using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The changelog as Keep a Changelog writes it, and Settings → About around it.</summary>
public sealed class ChangelogTests
{
    private const string Markdown = """
        # Changelog

        All notable changes to Granary are documented in this file.

        ## [Unreleased]

        ### Added

        - Search shows **Request to add**: titles from [TMDB](https://www.themoviedb.org) the library
          does not have.
        - The watchlist's `Not in your library yet` tab.

        ## [0.1.0] - 2026-10-27

        ### Fixed
        * Posters load faster.

        [Unreleased]: https://github.com/owner/granary/compare/v0.1.0...HEAD
        """;

    [Fact]
    public void Releases_ReadNewestFirst_WithTheirGroupsAndPlainItems()
    {
        var releases = Changelog.Parse(Markdown);

        Assert.Equal(["Unreleased", "0.1.0 · Oct 27, 2026"], releases.Select(r => r.Heading));
        var added = Assert.Single(releases[0].Groups);
        Assert.Equal("Added", added.Title);
        Assert.Equal(["Search shows Request to add: titles from TMDB the library does not have.", "The watchlist's Not in your library yet tab."],
            added.Items);
        Assert.Equal(["Posters load faster."], Assert.Single(releases[1].Groups).Items);
    }

    [Fact]
    public void AnEmptyUnreleasedSection_IsNotShown()
    {
        // Right after a release, the changelog keeps an empty Unreleased section for what comes next.
        var release = Assert.Single(Changelog.Parse("## [Unreleased]\n\n## [0.1.0] - 2026-10-30\n\n### Added\n\n- A thing.\n"));

        Assert.Equal("0.1.0 · Oct 30, 2026", release.Heading);
    }

    [Fact]
    public void ALinkedReleaseHeading_ReadsAsItsVersion()
    {
        var release = Assert.Single(Changelog.Parse("## [0.2.0](https://github.com/owner/granary/releases/tag/v0.2.0) - 2026-11-01\n- A thing.\n"));

        Assert.Equal("0.2.0 · Nov 1, 2026", release.Heading);
    }

    [Fact]
    public void ReleaseNotesWithoutAHeading_AreOneRelease()
    {
        var release = Assert.Single(Changelog.Parse("### Fixed\n- Posters load faster.\n"));

        Assert.Equal("", release.Version);
        Assert.Equal(["Posters load faster."], Assert.Single(release.Groups).Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# Changelog\n\nNothing yet.")]
    public void NothingToList_IsNoRelease(string? markdown)
    {
        Assert.Empty(Changelog.Parse(markdown));
    }

    [Fact]
    public async Task About_ShowsTheVersionTheChangelogAndWhatAReadyUpdateChanges()
    {
        var updater = new ReadyUpdater();
        var updates = new AppUpdatesViewModel(updater, new InMemoryUpdatePreferences(), new ManualTimeProvider(), new LogSink().For<AppUpdatesViewModel>());
        var about = new AboutViewModel(new FakeAppInfo(Markdown), updates);
        var changed = new List<string?>();
        about.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("Version 0.1.0", about.VersionText);
        Assert.Equal(2, about.Releases.Count);
        Assert.False(about.HasReadyChanges);
        Assert.Equal(new Uri("https://github.com/owner/granary"), about.SourceUrl);

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Contains(nameof(AboutViewModel.ReadyChanges), changed);
        Assert.Equal("What's new in 0.2.0", about.ReadyChangesTitle);
        Assert.Equal(["Posters load faster."], Assert.Single(about.ReadyChanges).Items);

        about.Detach();
        changed.Clear();
        updates.LaterCommand.Execute(null);
        Assert.Empty(changed);
    }

    [Fact]
    public async Task WithAutomaticUpdatesOff_AboutLinksTheAvailableReleasesPage_AndShowsNoNotesBeforeItIsVerified()
    {
        var updates = new AppUpdatesViewModel(new ReadyUpdater(), new InMemoryUpdatePreferences(isAutomatic: false), new ManualTimeProvider(),
            new LogSink().For<AppUpdatesViewModel>());
        var about = new AboutViewModel(new FakeAppInfo(Markdown), updates);
        var changed = new List<string?>();
        about.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Null(about.AvailableReleaseUrl);
        Assert.False(about.HasAvailableReleaseUrl);

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Contains(nameof(AboutViewModel.AvailableReleaseUrl), changed);
        Assert.Equal(new Uri("https://github.com/owner/granary/releases/tag/v0.2.0"), about.AvailableReleaseUrl);
        Assert.True(about.HasAvailableReleaseUrl);
        Assert.False(about.HasReadyChanges);

        await updates.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Null(about.AvailableReleaseUrl);
        Assert.Equal(["Posters load faster."], Assert.Single(about.ReadyChanges).Items);
    }

    [Fact]
    public void WithoutAChangelog_AboutSaysNothingOfIt()
    {
        var updates = new AppUpdatesViewModel(new NoAppUpdater(), new InMemoryUpdatePreferences(), new ManualTimeProvider(),
            new LogSink().For<AppUpdatesViewModel>());

        var about = new AboutViewModel(new FakeAppInfo(null), updates);

        Assert.False(about.HasChangelog);
    }

    private sealed class FakeAppInfo(string? changelog) : IAppInfo
    {
        public string Version => "0.1.0";

        public Uri? SourceUrl => new("https://github.com/owner/granary");

        public string? ReadChangelog() => changelog;

        public string LicensePath => @"C:\Granary\LICENSE";

        public string NoticesPath => @"C:\Granary\THIRD_PARTY_NOTICES.md";

        public string DataDirectory => @"C:\Data\Granary";

        public string LogsDirectory => @"C:\Data\Granary\logs";
    }

    private sealed class ReadyUpdater : IAppUpdater
    {
        public AppUpdateSupport Support => AppUpdateSupport.Supported;

        public Task<AppRelease?> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult<AppRelease?>(new AppRelease("0.2.0", null));

        // The notes come with the verified download, from the signed package.
        public Task<AppRelease?> DownloadAsync(IProgress<int>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<AppRelease?>(new AppRelease("0.2.0", "### Fixed\n- Posters load faster.\n"));

        public void ApplyWhenExited(bool restart)
        {
        }
    }
}
