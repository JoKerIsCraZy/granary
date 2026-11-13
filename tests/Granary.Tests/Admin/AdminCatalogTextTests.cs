using Granary.Core.Api.Admin;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Admin;

namespace Granary.Tests.Admin;

/// <summary>
/// The admin area's and the activity indicator's words come from the catalogs: the web's admin pages' where they have
/// them, in the language on screen, and Granary's own for what only Granary shows.
/// </summary>
public sealed class AdminCatalogTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly AdminPlaybackSession Episode = new()
    {
        MediaType = "episode", SeriesName = "Dark", SeasonNumber = 1, EpisodeNumber = 3, EpisodeName = "Past and Present", UserId = "9",
    };

    [Fact]
    public void TheDashboardsWords_AreTheWebs_InTheLanguageOnScreen()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal("Vor weniger als 1 Minute aktualisiert", AdminText.Updated(Now.AddSeconds(-30), Now));
        Assert.Equal("Vor 5 Minuten aktualisiert", AdminText.Updated(Now.AddMinutes(-5), Now));
        Assert.Equal("2.048 Dateien", AdminText.Files(2048));
        Assert.Equal(("Gerade eben", "vor 5 Min.", "vor 2 Std.", "vor 3 Tg."),
            (AdminText.Ago(Now.AddSeconds(-30), Now), AdminText.Ago(Now.AddMinutes(-5), Now), AdminText.Ago(Now.AddHours(-2), Now),
                AdminText.Ago(Now.AddDays(-3), Now)));
        Assert.Equal("S1 · F3 — Dark", AdminText.Subtitle(Episode));
        Assert.Equal("Film", AdminText.Subtitle(new AdminPlaybackSession { MediaType = "movie" }));
        Assert.Equal("Filme · 2 Pfade", AdminText.LibraryDetail("movies", 2));
        Assert.Equal("Top-Titel · letzte 7 Tg.", AdminText.LastWeek(AppText.Get("web.admin.dashboard.widgets.topTitles.title")));
        Assert.Equal(("Eigentümer", "Admin"), (AdminText.Role("admin", isOwner: true), AdminText.Role("admin", isOwner: false)));
    }

    [Fact]
    public void ALibrarysKind_IsNamedAsTheWebNamesIt_OrAsStored()
    {
        Assert.Equal("Movies · 1 path", AdminText.LibraryDetail("movies", 1));
        Assert.Equal("Series · 2 paths", AdminText.LibraryDetail(" TV ", 2));
        Assert.Equal("radio · 3 paths", AdminText.LibraryDetail("radio", 3));
    }

    [Fact]
    public void ActivitysWords_AreTheWebs_InTheLanguageOnScreen()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal(("Direktwiedergabe", "Direktstream"), (AdminPlayMethod.Label(AdminPlayMethod.Direct), AdminPlayMethod.Label(AdminPlayMethod.DirectStream)));
        Assert.Equal("Unbekannte Quelle", AdminSessionText.Container(new AdminPlaybackSession { PlayMethod = "direct" }));
        Assert.Equal("Audio wird transkodiert", AdminSessionText.TranscodeMode(new AdminPlaybackSession { PlayMethod = "remux", VideoDecision = "copy", TranscodeAudio = true }));
        Assert.Equal([new AdminRouteNode("server:local", "Server", "Lokaler Server")],
            AdminSessionText.Route(new AdminPlaybackSession { RoutingExecution = "api", RoutingEgress = "api", ReportingNode = "local" }));
        Assert.Equal([new AdminRouteNode("transcode:gpu", "Transkodierung", "gpu")],
            AdminSessionText.Route(new AdminPlaybackSession { RoutingExecution = "transcode", RoutingExecutionNodeName = "gpu" }));
    }

    [Fact]
    public void ARoutesKeys_AreTheSameInEveryLanguage()
    {
        var legacy = new AdminPlaybackSession { ReportingNode = "local" };
        var unnamed = new AdminPlaybackSession { RoutingExecution = "transcode" };
        string english = AdminSessionText.Route(legacy)[0].Key + AdminSessionText.Route(unnamed)[0].Key;

        using var german = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal(english, AdminSessionText.Route(legacy)[0].Key + AdminSessionText.Route(unnamed)[0].Key);
        Assert.Equal("legacy:local", AdminSessionText.Route(legacy)[0].Key);
    }

    [Fact]
    public void TheIndicatorsWords_AreTheWebs_InTheLanguageOnScreen()
    {
        var job = new AdminDownloadPreparation
        {
            Format = "transcode", Output = new("h264", "1080p"), Progress = new(600, 6000, 1),
        };

        using var german = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal("3 weitere Scans in der Warteschlange", ServerActivityText.MoreScans(3, isLowerBound: false));
        Assert.Equal("Mindestens 1.475 weitere Scans in der Warteschlange", ServerActivityText.MoreScans(1475, isLowerBound: true));
        Assert.Equal("Gesamte Mediathek", ServerActivityText.ScanMode("library"));
        Assert.Equal("Transkodierung 1080p H.264", ServerActivityText.PreparationKind(job));
        Assert.Equal("noch etwa 1 Std. 30 Min.", ServerActivityText.PreparationRemaining(job.Progress));
        Assert.Equal("12,5 %", ServerActivityText.TaskProgress(12.5));
        Assert.Equal("Abgleich · 1.200 / 4.800 (25 %)",
            ServerActivityText.ScanProgress(new AdminScanResult { Message = "Abgleich", FilesProcessed = 1200, TotalFiles = 4800 }));
    }

    [Fact]
    public void ARunningTasksName_IsTheWebsForSilosOwnTasks_AndTheServersOtherwise()
    {
        var row = new ActivityTaskRow("backup");
        var plugin = new ActivityTaskRow("plugin.sync");

        using var german = AppText.Use(TextLanguage.Load("de"));
        row.Show(new AdminTask { Key = "backup", Name = "Back Up Server", State = "running" });
        plugin.Show(new AdminTask { Key = "plugin.sync", Name = "Sync the plugin", State = "running" });

        Assert.Equal("Server sichern", row.Name);
        Assert.Equal("Sync the plugin", plugin.Name);
    }

    [Fact]
    public void AStreamsRow_SaysWhereAndHowLong_InTheLanguageOnScreen()
    {
        var session = new AdminPlaybackSession { StreamLocation = "remote", StartedAt = Now.AddMinutes(-5), PlayMethod = "direct" };

        using var german = AppText.Use(TextLanguage.Load("de"));
        var row = new AdminSessionRow(session, new Uri("https://silo.example/"), Now);

        Assert.Equal(("Extern", "Sitzung läuft seit 5:00", "Pausieren"), (row.Location, row.ActiveText, row.PauseLabel));
        Assert.Equal("Datei #42", new AdminSessionRow(session with { MediaFileId = "42" }, row.Server, Now).Title);
    }
}
