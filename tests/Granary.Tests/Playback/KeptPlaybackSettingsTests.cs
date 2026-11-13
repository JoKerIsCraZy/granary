using System.Net;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>
/// The playback settings as the server last answered them stay on this PC for each server, account and profile, and a play
/// that goes on without the server starts with them.
/// </summary>
public sealed class KeptPlaybackSettingsTests : IAsyncDisposable
{
    private const string EffectivePath = "api/v2/settings/values/effective";

    private const string Answered = """
        { "items": [
            { "key": "player.playback_speed", "value": 1.5, "source": "profile_device" },
            { "key": "player.video_skip_forward_seconds", "value": 45, "source": "profile" },
            { "key": "player.subtitle_sync_ms", "value": 250, "source": "profile" },
            { "key": "playback.subtitle_appearance", "value": { "fontSize": "xlarge", "fontColor": "#ffff00" }, "source": "profile" },
            { "key": "playback.intro_skip_mode", "value": "always", "source": "profile" },
            { "key": "player.sleep_timer_default_minutes", "value": 90, "source": "profile_device" }
          ], "revision": 7 }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();
    private readonly List<KeptPlaybackSettings> _opened = [];
    private readonly FakeSessionManager _session = new()
    {
        State = SessionState.Ready,
        ActiveServer = ScriptedSilo.Server,
        Account = new Account("1", "elena", "", "user", [], true, false, null),
        ProfileId = "7",
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string FilePath => Path.Combine(_folder, "playback-settings.json");

    public async ValueTask DisposeAsync()
    {
        foreach (var kept in _opened)
        {
            await kept.LatestSave;
        }
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private KeptPlaybackSettings Open()
    {
        var kept = new KeptPlaybackSettings(FilePath, _session, _logs.For<KeptPlaybackSettings>());
        _opened.Add(kept);
        return kept;
    }

    private PlaybackPreferencesReader Reader(KeptPlaybackSettings? kept) =>
        new(new SettingsApi(_silo.Http), _logs.For<PlaybackPreferencesReader>(), kept);

    private void ServerAnswers() => _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Json(HttpStatusCode.OK, Answered));

    private static void AssertTheProfilesOwn(PlaybackPreferences read)
    {
        Assert.Equal((1.5, 45d, 250, 68d, "#ffff00", MarkerSettings.Always, 90),
            (read.Speed, read.SkipForwardSeconds, read.SubtitleDelayMs, read.Subtitles.FontSize, read.Subtitles.FontColor, read.Markers.IntroSkipMode,
                read.SleepTimerMinutes));
        Assert.False(read.SubtitleDelayOnDevice);
    }

    [Fact]
    public async Task WithoutTheServer_APlayStartsWithTheSettingsLastRead()
    {
        var kept = Open();
        ServerAnswers();
        AssertTheProfilesOwn(await Reader(kept).ReadAsync(ScriptedSilo.Base, Token));

        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.NetworkError());
        var offline = await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token);

        AssertTheProfilesOwn(offline);
        Assert.Contains("last read on this PC", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheyAreStillThere_AfterGranaryStartedAgain()
    {
        ServerAnswers();
        var first = Open();
        await Reader(first).ReadAsync(ScriptedSilo.Base, Token);
        await first.LatestSave;

        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.NetworkError());

        AssertTheProfilesOwn(await Reader(Open()).ReadOrKeptAsync(ScriptedSilo.Base, Token));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("503")]
    [InlineData("cut")]
    public async Task AServerThatDoesNotAnswer_OrFails_CountsAsNoServer(string failure)
    {
        var kept = Open();
        ServerAnswers();
        await Reader(kept).ReadAsync(ScriptedSilo.Base, Token);
        _silo.On(HttpMethod.Get, EffectivePath, failure switch
        {
            "timeout" => () => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out", new TimeoutException())),
            "503" => ScriptedSilo.Problem(503, "unavailable"),
            _ => () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off")),
        });

        AssertTheProfilesOwn(await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token));
    }

    [Fact]
    public async Task AStreamsRead_NeverTakesWhatWasKept()
    {
        var kept = Open();
        ServerAnswers();
        await Reader(kept).ReadAsync(ScriptedSilo.Base, Token);
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.NetworkError());

        var read = await Reader(kept).ReadAsync(ScriptedSilo.Base, Token);

        // A stream cannot play without the server anyway; its read says what the server says, or the defaults.
        Assert.Equal((1d, 30d), (read.Speed, read.SkipForwardSeconds));
    }

    [Fact]
    public async Task ABatchTheServerRefuses_IsItsAnswer_NotWhatWasKept()
    {
        var kept = Open();
        ServerAnswers();
        await Reader(kept).ReadAsync(ScriptedSilo.Base, Token);
        // An older server, restored from a backup: it does not know the keys.
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.Problem(422, "validation_failed"));

        var read = await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token);

        Assert.Equal((1d, 0, PlaybackPreferences.DefaultSleepTimerMinutes), (read.Speed, read.SubtitleDelayMs, read.SleepTimerMinutes));
        Assert.True(read.AudioDelayRefused);
    }

    [Fact]
    public async Task OnlyTheBatchesThatDidNotCome_AreTakenFromWhatWasKept()
    {
        var kept = Open();
        ServerAnswers();
        await Reader(kept).ReadAsync(ScriptedSilo.Base, Token);
        // The speed changed on the server since; the subtitle settings do not arrive this time.
        _silo.OnUrl(HttpMethod.Get, EffectivePath, (url, _) => url.Query.Contains("subtitle_appearance", StringComparison.Ordinal)
            ? ScriptedSilo.NetworkError()()
            : ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [ { "key": "player.playback_speed", "value": 2, "source": "profile_device" } ], "revision": 8 }""")());

        var read = await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token);

        Assert.Equal((2d, 250, 68d), (read.Speed, read.SubtitleDelayMs, read.Subtitles.FontSize));
    }

    [Fact]
    public async Task AnotherProfile_AnotherAccount_AndAnotherServer_HaveTheirOwn()
    {
        var kept = Open();
        ServerAnswers();
        await Reader(kept).ReadAsync(ScriptedSilo.Base, Token);
        _silo.On(HttpMethod.Get, EffectivePath, ScriptedSilo.NetworkError());

        _session.ProfileId = "8";
        Assert.Equal(1, (await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token)).Speed);
        _session.ProfileId = "7";
        _session.Account = _session.Account! with { Id = "2" };
        Assert.Equal(1, (await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token)).Speed);
        _session.Account = _session.Account! with { Id = "1" };
        _session.ActiveServer = ScriptedSilo.Entry("srv2", ScriptedSilo.Base);
        Assert.Equal(1, (await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token)).Speed);

        _session.ActiveServer = ScriptedSilo.Server;
        Assert.Equal(1.5, (await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token)).Speed);
    }

    [Fact]
    public async Task AnAnswerThatCameForAProfileSwitchedAwayFromMeanwhile_IsNotKeptAsTheNewOnes()
    {
        var kept = Open();
        // The batch with the speed is asked for as profile 7; profile 8 is the active one by the time its answer is there.
        _silo.OnUrl(HttpMethod.Get, EffectivePath, (url, _) =>
        {
            if (url.Query.Contains("playback_speed", StringComparison.Ordinal))
            {
                _session.ProfileId = "8";
            }
            return ScriptedSilo.Json(HttpStatusCode.OK, Answered)();
        });
        await Reader(kept).ReadAsync(ScriptedSilo.Base, Token);
        _silo.OnUrl(HttpMethod.Get, EffectivePath, (_, _) => ScriptedSilo.NetworkError()());

        Assert.Equal(1, (await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token)).Speed);
        _session.ProfileId = "7";
        Assert.Equal(1, (await Reader(kept).ReadOrKeptAsync(ScriptedSilo.Base, Token)).Speed);
    }

    [Fact]
    public async Task TheSameAnswerAgain_WritesNothing()
    {
        var kept = Open();
        ServerAnswers();
        await Reader(kept).ReadAsync(ScriptedSilo.Base, Token);
        await kept.LatestSave;
        var written = File.GetLastWriteTimeUtc(FilePath);
        var save = kept.LatestSave;

        await Reader(kept).ReadAsync(ScriptedSilo.Base, Token);

        Assert.Same(save, kept.LatestSave);
        Assert.Equal(written, File.GetLastWriteTimeUtc(FilePath));
    }

    [Fact]
    public async Task SigningOut_ForgetsTheAccountsSettings_AndRemovingAServerEveryAccounts()
    {
        var kept = Open();
        var values = new EffectiveSettings([], 1);
        kept.Keep("srv1/1/7", ["a"], values);
        kept.Keep("srv1/1/8", ["a"], values);
        kept.Keep("srv1/2/7", ["a"], values);
        kept.Keep("srv10/1/7", ["a"], values);

        kept.Remove("srv1", "1");

        Assert.Null(kept.Find("srv1/1/7", ["a"]));
        Assert.Null(kept.Find("srv1/1/8", ["a"]));
        Assert.NotNull(kept.Find("srv1/2/7", ["a"]));

        kept.Remove("srv1", null);

        Assert.Null(kept.Find("srv1/2/7", ["a"]));
        Assert.NotNull(kept.Find("srv10/1/7", ["a"]));
        await kept.LatestSave;
        Assert.DoesNotContain("srv1/", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatSaysNothingUsable_IsStartedAnew_WithoutNamingTheFolder()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(FilePath, "{ not json", Token);
        var kept = Open();

        Assert.Null(kept.Find("srv1/1/7", ["a"]));
        kept.Keep("srv1/1/7", ["a"], new EffectiveSettings([], 1));
        await kept.LatestSave;

        Assert.NotNull(Open().Find("srv1/1/7", ["a"]));
        Assert.DoesNotContain(_folder, _logs.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFileThatCannotBeReadNow_IsNeverWrittenOver_AndItsOtherProfilesSettingsStay()
    {
        var before = Open();
        before.Keep("srv1/1/8", ["a"], new EffectiveSettings([], 3));
        before.Keep("srv1/1/7", ["a"], new EffectiveSettings([], 3));
        await before.LatestSave;
        string keptText = await File.ReadAllTextAsync(FilePath, Token);
        var kept = Open();

        // Something has the file open and shares nothing, as a virus scanner at the start.
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(kept.Find("srv1/1/8", ["a"]));
            kept.Keep("srv1/1/7", ["a"], new EffectiveSettings([], 4));
            await kept.LatestSave;
        }

        // Profile 8's settings were not written over with nothing.
        Assert.Equal(keptText, await File.ReadAllTextAsync(FilePath, Token));
        Assert.Contains("could not be read", _logs.All, StringComparison.Ordinal);

        // The next write reads the file first: profile 8's are back, and profile 7's newer answer holds.
        kept.Keep("srv1/1/7", ["b"], new EffectiveSettings([], 4));
        await kept.LatestSave;

        Assert.Equal(3, kept.Find("srv1/1/8", ["a"])!.Revision);
        Assert.Equal(4, kept.Find("srv1/1/7", ["a"])!.Revision);
        var again = Open();
        Assert.Equal((3L, 4L, 4L), (again.Find("srv1/1/8", ["a"])!.Revision, again.Find("srv1/1/7", ["a"])!.Revision, again.Find("srv1/1/7", ["b"])!.Revision));
    }

    [Fact]
    public async Task WhatWasForgottenWhileTheFileCouldNotBeRead_DoesNotComeBackWithIt()
    {
        var before = Open();
        before.Keep("srv1/1/7", ["a"], new EffectiveSettings([], 3));
        before.Keep("srv1/2/7", ["a"], new EffectiveSettings([], 3));
        await before.LatestSave;
        var kept = Open();

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            kept.Remove("srv1", "1");
            await kept.LatestSave;
        }
        kept.Keep("srv1/2/7", ["b"], new EffectiveSettings([], 4));
        await kept.LatestSave;

        Assert.Null(kept.Find("srv1/1/7", ["a"]));
        Assert.NotNull(kept.Find("srv1/2/7", ["a"]));
        Assert.DoesNotContain("srv1/1/", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
    }

    // Where a sign-out notes what it forgot, until the file is written without it.
    private string NotePath => FilePath + ".removed";

    // Four owners on the disk: the account's two profiles, another account there, and one on a server whose id starts alike.
    private async Task FourOnTheDiskAsync()
    {
        var before = Open();
        foreach (string owner in (string[])["srv1/1/7", "srv1/1/8", "srv1/2/7", "srv10/1/7"])
        {
            before.Keep(owner, ["a"], new EffectiveSettings([], 3));
        }
        await before.LatestSave;
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData(null, false)]
    public async Task WhatWasForgottenWhileTheFileCouldNotBeRead_StaysForgotten_AfterGranaryStartedAgain(string? accountId, bool otherAccountStays)
    {
        await FourOnTheDiskAsync();
        var kept = Open();

        // Something has the file open through the sign-out or the server's removal, and Granary closes before it lets go.
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            kept.Remove("srv1", accountId);
            await kept.LatestSave;
        }
        var next = Open();

        Assert.Null(next.Find("srv1/1/7", ["a"]));
        Assert.Null(next.Find("srv1/1/8", ["a"]));
        Assert.Equal(otherAccountStays, next.Find("srv1/2/7", ["a"]) is not null);
        Assert.NotNull(next.Find("srv10/1/7", ["a"]));
        await next.LatestSave;
        Assert.DoesNotContain("srv1/1/", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
        // The file is written without them: the note has done its work.
        Assert.False(File.Exists(NotePath));
    }

    [Fact]
    public async Task ASignOutWhoseWriteDidNotGetThrough_StillHolds_AfterGranaryStartedAgain()
    {
        await FourOnTheDiskAsync();
        var kept = Open();
        Assert.NotNull(kept.Find("srv1/1/7", ["a"]));

        // The file was read; something reads it as the sign-out writes it, and Granary closes before it lets go.
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            kept.Remove("srv1", "1");
            await kept.LatestSave;
        }
        var next = Open();

        Assert.Null(next.Find("srv1/1/7", ["a"]));
        Assert.Null(next.Find("srv1/1/8", ["a"]));
        Assert.NotNull(next.Find("srv1/2/7", ["a"]));
    }

    [Fact]
    public async Task WhatIsKeptForTheSameAccountAfterItSignedInAgain_Stays_AlsoWhileTheNoteOfItsSignOutIsStillThere()
    {
        await FourOnTheDiskAsync();
        var kept = Open();
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            kept.Remove("srv1", "1");
            await kept.LatestSave;
        }

        // The next start writes the file without them, but something has the note open: it cannot be deleted. The account
        // signs in again, and the server's answer is kept for it.
        using (new FileStream(NotePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var next = Open();
            Assert.Null(next.Find("srv1/1/7", ["a"]));
            next.Keep("srv1/1/7", ["a"], new EffectiveSettings([], 9));
            await next.LatestSave;
        }
        Assert.True(File.Exists(NotePath));
        var third = Open();

        // The file was written after the note: the note takes nothing that was kept since, and goes with the next write.
        Assert.Equal(9, third.Find("srv1/1/7", ["a"])!.Revision);
        Assert.Null(third.Find("srv1/1/8", ["a"]));
        await third.LatestSave;
        Assert.False(File.Exists(NotePath));
        Assert.Equal(9, Open().Find("srv1/1/7", ["a"])!.Revision);
    }

    [Fact]
    public async Task ASignOutThatCannotEvenBeNoted_SaysSo_WithoutNamingWhoseOrWhere_AndStillHoldsForThisRun()
    {
        await FourOnTheDiskAsync();
        // A note of an earlier sign-out, done with, that something still has open: a new one cannot take its place.
        await File.WriteAllTextAsync(NotePath, """{ "version": 1, "removals": [] }""", Token);
        var kept = Open();

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(NotePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            kept.Remove("srv1", "1");
            await kept.LatestSave;
        }

        Assert.Contains("could not be noted", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(_folder, _logs.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("srv1", _logs.All, StringComparison.Ordinal);

        // This run still knows: the next write reads the file and writes it without them.
        kept.Keep("srv10/1/7", ["b"], new EffectiveSettings([], 4));
        await kept.LatestSave;
        Assert.Null(kept.Find("srv1/1/7", ["a"]));
        Assert.NotNull(kept.Find("srv1/2/7", ["a"]));
        Assert.DoesNotContain("srv1/1/", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
        Assert.False(File.Exists(NotePath));
    }

    [Fact]
    public async Task WhileTheNoteOfASignOutCannotBeRead_NothingTheFileHoldsIsUsed_AndTheFileIsNotWritten()
    {
        await FourOnTheDiskAsync();
        var kept = Open();
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            kept.Remove("srv1", "1");
            await kept.LatestSave;
        }
        string keptText = await File.ReadAllTextAsync(FilePath, Token);
        var next = Open();

        // The note may name anyone: until it says whom, nobody's settings of the file are used.
        using (new FileStream(NotePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(next.Find("srv1/1/7", ["a"]));
            Assert.Null(next.Find("srv10/1/7", ["a"]));
            next.Keep("srv10/1/7", ["b"], new EffectiveSettings([], 4));
            await next.LatestSave;
        }
        Assert.Equal(keptText, await File.ReadAllTextAsync(FilePath, Token));
        Assert.DoesNotContain(_folder, _logs.All, StringComparison.OrdinalIgnoreCase);

        // Once it can be read, the next write takes out what it names; the others' settings are back.
        next.Keep("srv10/1/7", ["c"], new EffectiveSettings([], 4));
        await next.LatestSave;

        Assert.Null(next.Find("srv1/1/7", ["a"]));
        Assert.Null(next.Find("srv1/1/8", ["a"]));
        Assert.NotNull(next.Find("srv1/2/7", ["a"]));
        Assert.Equal((3L, 4L), (next.Find("srv10/1/7", ["a"])!.Revision, next.Find("srv10/1/7", ["b"])!.Revision));
        Assert.DoesNotContain("srv1/1/", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
        Assert.False(File.Exists(NotePath));
    }

    [Fact]
    public async Task ANoteOfASignOutThatNoLongerSaysWhose_ForgetsEverythingTheFileHolds_RatherThanKeepWhatWasToGo()
    {
        await FourOnTheDiskAsync();
        await File.WriteAllTextAsync(NotePath, "{ not json", Token);
        var next = Open();

        Assert.Null(next.Find("srv1/1/7", ["a"]));
        Assert.Null(next.Find("srv10/1/7", ["a"]));
        // What the server answers from now on is kept as ever.
        next.Keep("srv10/1/7", ["b"], new EffectiveSettings([], 4));
        await next.LatestSave;

        Assert.False(File.Exists(NotePath));
        var third = Open();
        Assert.Null(third.Find("srv10/1/7", ["a"]));
        Assert.NotNull(third.Find("srv10/1/7", ["b"]));
        Assert.DoesNotContain("srv1/", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
        Assert.Contains("damaged", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(_folder, _logs.All, StringComparison.OrdinalIgnoreCase);
    }
}
