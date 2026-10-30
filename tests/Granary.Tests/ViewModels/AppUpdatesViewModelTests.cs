using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>Granary's own updates: looking for one, the notice once it is ready, and handing it over as the app exits.</summary>
public sealed class AppUpdatesViewModelTests
{
    private const string AutomaticSummary =
        "Granary looks for a new release when it starts and every six hours, downloads it in the background, and installs it when it closes.";

    private readonly FakeUpdater _updater = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly LogSink _logs = new();
    private InMemoryUpdatePreferences _preferences = new();

    private AppUpdatesViewModel Create() => new(_updater, _preferences, _clock, _logs.For<AppUpdatesViewModel>());

    private AppUpdatesViewModel CreateOff()
    {
        _preferences = new InMemoryUpdatePreferences(isAutomatic: false);
        return Create();
    }

    [Theory]
    [InlineData(AppUpdateSupport.NotInstalled, "This copy wasn't installed with Granary's setup, so it doesn't update itself.")]
    [InlineData(AppUpdateSupport.NoReleaseSource, "This build doesn't look for updates.")]
    public async Task ACopyThatCannotUpdate_NeverLooks_AndSaysWhy(AppUpdateSupport support, string why)
    {
        _updater.Support = support;
        var updates = Create();

        await updates.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _updater.Checks);
        Assert.False(updates.IsNoticeShown);
        Assert.Equal(AppUpdateState.Unsupported, updates.State);
        Assert.Equal(why, updates.StatusText);
        Assert.False(updates.CheckNowCommand.CanExecute(null));
        Assert.False(updates.CanUpdate);
    }

    [Theory]
    [InlineData(AppUpdateSupport.NotInstalled, "This copy wasn't installed with Granary's setup, so it doesn't update itself.")]
    [InlineData(AppUpdateSupport.NoReleaseSource, "This build doesn't look for updates.")]
    public void ACopyThatCannotUpdate_HidesTheSwitch_AndSaysWhatItSaysTodayEvenWithItOff(AppUpdateSupport support, string why)
    {
        _updater.Support = support;
        var updates = CreateOff();

        Assert.False(updates.CanUpdate);
        Assert.Equal(why, updates.StatusText);
        Assert.Equal(AutomaticSummary, updates.Summary);
    }

    [Fact]
    public void UpdatesAreAutomatic_ByDefault()
    {
        var updates = Create();

        Assert.True(updates.CanUpdate);
        Assert.True(updates.IsAutomatic);
        Assert.Equal("Granary looks for updates when it starts and every six hours.", updates.StatusText);
        Assert.Equal(AutomaticSummary, updates.Summary);
        Assert.Empty(_preferences.Saved);
    }

    [Fact]
    public void TheSwitch_IsKeptInThePreferences_AndSaysThatUpdatesAreOff()
    {
        var updates = Create();
        var changed = new List<string?>();
        updates.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        updates.IsAutomatic = false;

        Assert.Equal([false], _preferences.Saved);
        Assert.Equal("Automatic updates are off.", updates.StatusText);
        Assert.Equal("Granary looks for a new release only when you check, and installs it only once you choose to.", updates.Summary);
        Assert.Contains(nameof(AppUpdatesViewModel.StatusText), changed);
        Assert.Contains(nameof(AppUpdatesViewModel.Summary), changed);
        // The next start reads it back.
        Assert.False(Create().IsAutomatic);
    }

    [Fact]
    public async Task Off_NeitherLooksAtTheStartNorEverySixHours()
    {
        var updates = CreateOff();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = updates.RunAsync(stop.Token);
        Assert.Null(_clock.NextDue);
        _clock.Advance(4 * AppUpdatesViewModel.CheckEvery);

        Assert.Equal(0, _updater.Checks);
        Assert.False(run.IsCompleted);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task SwitchedOff_TheSixHourLookIsCalledOff()
    {
        _updater.Answers.Enqueue(() => null);
        var updates = Create();
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = updates.RunAsync(stop.Token);
        await _clock.WaitForTimerAsync(ct);
        Assert.Equal(1, _updater.Checks);
        updates.IsAutomatic = false;
        await _clock.WaitUntilAsync(() => _clock.NextDue is null, ct);
        _clock.Advance(4 * AppUpdatesViewModel.CheckEvery);

        Assert.Equal(1, _updater.Checks);
        Assert.False(run.IsCompleted);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task SwitchedOnAgain_LooksAtOnce_ThenEverySixHours()
    {
        _updater.Answers.Enqueue(() => null);
        _updater.Answers.Enqueue(() => "0.3.0");
        var updates = CreateOff();
        var ct = TestContext.Current.CancellationToken;
        var run = updates.RunAsync(ct);
        _clock.Advance(AppUpdatesViewModel.CheckEvery);

        updates.IsAutomatic = true;
        await _clock.WaitForTimerAsync(ct);
        Assert.Equal(1, _updater.Checks);
        Assert.Equal(AppUpdatesViewModel.CheckEvery, await _clock.FireNextTimerAsync(ct));
        await run;

        Assert.Equal(2, _updater.Checks);
        Assert.Equal(AppUpdateState.Ready, updates.State);
        updates.OnExiting();
        Assert.Equal([false], _updater.Applied);
    }

    [Fact]
    public async Task SwitchedOnAgain_SoonAfterACheckByHand_LooksSixHoursAfterIt()
    {
        _updater.Answers.Enqueue(() => null);
        var updates = CreateOff();
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = updates.RunAsync(stop.Token);
        await updates.CheckNowCommand.ExecuteAsync(null);
        _clock.Advance(TimeSpan.FromHours(1));

        updates.IsAutomatic = true;
        await _clock.WaitForTimerAsync(ct);

        Assert.Equal(1, _updater.Checks);
        Assert.Equal(TimeSpan.FromHours(5), _clock.NextDue);
        Assert.False(run.IsCompleted);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Off_CheckForUpdates_OnlySaysWhichVersionIsAvailable()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.FeedNotes = "Notes from the unsigned feed, never shown.";
        var updates = CreateOff();

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(1, _updater.Checks);
        Assert.Equal(0, _updater.Downloads);
        Assert.Equal(AppUpdateState.Available, updates.State);
        Assert.True(updates.IsAvailable);
        Assert.Equal("0.3.0", updates.AvailableVersion);
        Assert.Equal("Granary 0.3.0 is available.", updates.StatusText);
        Assert.Equal("Last checked today at 12:00", updates.LastCheckedText);
        Assert.Equal("", updates.ReleaseNotes);
        Assert.Null(updates.ReadyVersion);
        Assert.False(updates.IsNoticeShown);
        Assert.False(updates.ShowsCheckNow);
        Assert.True(updates.DownloadAndInstallCommand.CanExecute(null));
        updates.OnExiting();
        Assert.Empty(_updater.Applied);
    }

    [Fact]
    public async Task Off_CheckForUpdates_WithNothingNew_SaysItIsUpToDate()
    {
        _updater.Answers.Enqueue(() => null);
        var updates = CreateOff();

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(AppUpdateState.UpToDate, updates.State);
        Assert.Equal("Granary is up to date.", updates.StatusText);
        Assert.False(updates.DownloadAndInstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Off_DownloadAndInstall_DownloadsAndVerifies_ThenItIsReady()
    {
        var download = new TaskCompletionSource();
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.Notes = "### Fixed\n- Posters load faster.";
        var updates = CreateOff();
        await updates.CheckNowCommand.ExecuteAsync(null);
        _updater.HoldDownload = download;

        var installing = updates.DownloadAndInstallCommand.ExecuteAsync(null);
        Assert.Equal(AppUpdateState.Downloading, updates.State);
        Assert.Equal("Downloading Granary 0.3.0… 0%", updates.StatusText);
        Assert.False(updates.DownloadAndInstallCommand.CanExecute(null));
        download.SetResult();
        await installing;

        Assert.Equal(1, _updater.Downloads);
        Assert.Equal(AppUpdateState.Ready, updates.State);
        Assert.Equal("Granary 0.3.0 is ready. It installs when you restart or close Granary.", updates.StatusText);
        Assert.Equal("### Fixed\n- Posters load faster.", updates.ReleaseNotes);
        Assert.Equal("Restart to update", updates.RestartText);
        Assert.True(updates.RestartNowCommand.CanExecute(null));
        updates.OnExiting();
        Assert.Equal([false], _updater.Applied);
    }

    [Fact]
    public async Task Off_RestartToUpdate_AppliesItNow()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        var updates = CreateOff();
        int closes = 0;
        updates.CloseRequested += (_, _) => closes++;
        await updates.CheckNowCommand.ExecuteAsync(null);
        await updates.DownloadAndInstallCommand.ExecuteAsync(null);

        updates.RestartNowCommand.Execute(null);
        updates.OnExiting();

        Assert.Equal(1, closes);
        Assert.Equal([true], _updater.Applied);
    }

    [Fact]
    public async Task Off_AnUpdateThatCannotBeVerified_IsNotApplied_AndCanBeCheckedAgain()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.Verifies = false;
        var updates = CreateOff();
        await updates.CheckNowCommand.ExecuteAsync(null);

        await updates.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(AppUpdateState.NotVerified, updates.State);
        Assert.Equal("The update to Granary 0.3.0 couldn't be verified, so it wasn't installed.", updates.StatusText);
        Assert.True(updates.CheckNowCommand.CanExecute(null));
        updates.OnExiting();
        Assert.Empty(_updater.Applied);
    }

    [Fact]
    public async Task Off_AFailedDownload_SaysSo_AndCanBeCheckedAgain()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        var updates = CreateOff();
        await updates.CheckNowCommand.ExecuteAsync(null);
        _updater.DownloadFails = true;

        await updates.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(AppUpdateState.Failed, updates.State);
        Assert.Contains("Could not download the Granary update: HttpRequestException", _logs.All, StringComparison.Ordinal);
        Assert.True(updates.CheckNowCommand.CanExecute(null));
        updates.OnExiting();
        Assert.Empty(_updater.Applied);
    }

    [Fact]
    public async Task SwitchedOff_ADownloadedUpdate_IsHeldBack_UntilItIsConfirmed()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.Notes = "### Fixed\n- Posters load faster.";
        var updates = Create();
        await updates.RunAsync(TestContext.Current.CancellationToken);
        Assert.True(updates.IsNoticeShown);

        updates.IsAutomatic = false;

        Assert.Equal(AppUpdateState.Available, updates.State);
        Assert.Equal("Granary 0.3.0 is available.", updates.StatusText);
        Assert.False(updates.IsNoticeShown);
        Assert.Equal("", updates.ReleaseNotes);
        Assert.False(updates.RestartNowCommand.CanExecute(null));
        updates.OnExiting();
        Assert.Empty(_updater.Applied);

        await updates.DownloadAndInstallCommand.ExecuteAsync(null);

        // Downloaded and verified already: confirmed, it is ready at once.
        Assert.Equal(1, _updater.Downloads);
        Assert.Equal(AppUpdateState.Ready, updates.State);
        Assert.Equal("### Fixed\n- Posters load faster.", updates.ReleaseNotes);
        updates.OnExiting();
        Assert.Equal([false], _updater.Applied);
    }

    [Fact]
    public async Task SwitchedOnAgain_AHeldBackUpdate_IsReadyAgain()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        var updates = Create();
        await updates.RunAsync(TestContext.Current.CancellationToken);
        updates.IsAutomatic = false;

        updates.IsAutomatic = true;

        Assert.Equal(AppUpdateState.Ready, updates.State);
        Assert.Equal("Restart now", updates.RestartText);
        Assert.Equal(1, _updater.Downloads);
        updates.OnExiting();
        Assert.Equal([false], _updater.Applied);
    }

    [Fact]
    public async Task SwitchedOffWhileDownloading_TheUpdateIsHeldBack()
    {
        var download = new TaskCompletionSource();
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.HoldDownload = download;
        var updates = Create();
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = updates.RunAsync(stop.Token);
        Assert.Equal(AppUpdateState.Downloading, updates.State);
        updates.IsAutomatic = false;
        download.SetResult();
        await _clock.WaitUntilAsync(() => updates.State != AppUpdateState.Downloading, ct);

        Assert.Equal(AppUpdateState.Available, updates.State);
        Assert.Equal("0.3.0", updates.AvailableVersion);
        Assert.False(updates.IsNoticeShown);
        updates.OnExiting();
        Assert.Empty(_updater.Applied);
        Assert.False(run.IsCompleted);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task SwitchedOnAgain_AnUpdateFoundByHand_IsDownloadedAtOnce()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.Answers.Enqueue(() => "0.3.0");
        var updates = CreateOff();
        var run = updates.RunAsync(TestContext.Current.CancellationToken);
        await updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(AppUpdateState.Available, updates.State);

        updates.IsAutomatic = true;
        await run;

        Assert.Equal(1, _updater.Downloads);
        Assert.Equal(AppUpdateState.Ready, updates.State);
        Assert.True(updates.IsNoticeShown);
    }

    [Fact]
    public async Task On_CheckForUpdates_StillDownloadsAtOnce()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        var updates = Create();

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(1, _updater.Downloads);
        Assert.Equal(AppUpdateState.Ready, updates.State);
        Assert.Equal("Restart now", updates.RestartText);
        Assert.False(updates.IsAvailable);
    }

    [Fact]
    public async Task CheckNow_LooksAtOnce_AndSaysWhenItLooked()
    {
        _updater.Answers.Enqueue(() => null);
        var updates = Create();
        Assert.Equal("Granary looks for updates when it starts and every six hours.", updates.StatusText);
        Assert.Equal("", updates.LastCheckedText);

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(1, _updater.Checks);
        Assert.Equal(AppUpdateState.UpToDate, updates.State);
        Assert.Equal("Granary is up to date.", updates.StatusText);
        Assert.Equal("Last checked today at 12:00", updates.LastCheckedText);
        Assert.True(updates.CheckNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckNow_DownloadsWhatItFinds_AndOffersItWithTheNotesOfItsSignedPackage()
    {
        var download = new TaskCompletionSource();
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.FeedNotes = "Notes from the unsigned feed, never shown.";
        _updater.Notes = "### Fixed\n- Posters load faster.";
        _updater.HoldDownload = download;
        var updates = Create();

        var looking = updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(AppUpdateState.Downloading, updates.State);
        Assert.Equal("Downloading Granary 0.3.0… 0%", updates.StatusText);
        Assert.True(updates.IsBusy);
        Assert.False(updates.CheckNowCommand.CanExecute(null));
        Assert.Equal("", updates.ReleaseNotes);
        download.SetResult();
        await looking;

        Assert.Equal(AppUpdateState.Ready, updates.State);
        Assert.Equal("Granary 0.3.0 is ready. It installs when you restart or close Granary.", updates.StatusText);
        Assert.Equal("### Fixed\n- Posters load faster.", updates.ReleaseNotes);
        Assert.True(updates.IsNoticeShown);
        Assert.True(updates.RestartNowCommand.CanExecute(null));
        Assert.False(updates.CheckNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailedLook_SaysSo_AndCanBeTriedAgain()
    {
        _updater.Answers.Enqueue(() => throw new HttpRequestException("offline"));
        _updater.Answers.Enqueue(() => null);
        var updates = Create();

        await updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(AppUpdateState.Failed, updates.State);
        Assert.Equal("Couldn't check for updates. Check the connection and try again.", updates.StatusText);

        await updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(AppUpdateState.UpToDate, updates.State);
    }

    [Fact]
    public async Task AFailedLook_IsLoggedByItsKind_NeverWithAMessageThatCouldCarryAnAddress()
    {
        _updater.Answers.Enqueue(() => throw new HttpRequestException("failed at https://objects.example/x?token=abc123"));
        var updates = Create();

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Contains("Could not look for a Granary update: HttpRequestException", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpdateThatCannotBeVerified_IsNeitherOfferedNorApplied_AndCanBeTriedAgain()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.Notes = "### Fixed\n- Posters load faster.";
        _updater.Verifies = false;
        var updates = Create();

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(AppUpdateState.NotVerified, updates.State);
        Assert.Equal("The update to Granary 0.3.0 couldn't be verified, so it wasn't installed. Granary will try again later.",
            updates.StatusText);
        Assert.False(updates.IsNoticeShown);
        Assert.Null(updates.ReadyVersion);
        Assert.Equal("", updates.ReleaseNotes);
        Assert.False(updates.RestartNowCommand.CanExecute(null));
        Assert.True(updates.CheckNowCommand.CanExecute(null));
        updates.OnExiting();
        Assert.Empty(_updater.Applied);

        _updater.Verifies = true;
        await updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(AppUpdateState.Ready, updates.State);
    }

    [Fact]
    public async Task AnUnverifiedUpdate_IsLookedForAgainSixHoursLater()
    {
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.Verifies = false;
        var updates = Create();
        var ct = TestContext.Current.CancellationToken;

        var run = updates.RunAsync(ct);
        await _clock.WaitForTimerAsync(ct);
        Assert.Equal(AppUpdateState.NotVerified, updates.State);
        _updater.Verifies = true;
        Assert.Equal(AppUpdatesViewModel.CheckEvery, await _clock.FireNextTimerAsync(ct));
        await run;

        Assert.Equal(2, _updater.Downloads);
        Assert.Equal("0.3.0", updates.ReadyVersion);
    }

    [Fact]
    public async Task TheTimersLookAndTheButtons_AreOne()
    {
        var download = new TaskCompletionSource();
        _updater.Answers.Enqueue(() => "0.3.0");
        _updater.HoldDownload = download;
        var updates = Create();

        var run = updates.RunAsync(TestContext.Current.CancellationToken);
        Assert.False(updates.CheckNowCommand.CanExecute(null));
        // Asked anyway, it waits for the timer's look rather than starting its own.
        var asked = updates.CheckNowCommand.ExecuteAsync(null);
        download.SetResult();
        await asked;
        await run;

        Assert.Equal(1, _updater.Checks);
        Assert.Equal(1, _updater.Downloads);
        Assert.Equal("0.3.0", updates.ReadyVersion);
    }

    [Fact]
    public async Task AReleaseOnTheFirstLook_IsOfferedAndAppliedWhenGranaryCloses()
    {
        _updater.Answers.Enqueue(() => "0.2.0");
        var updates = Create();

        await updates.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(updates.IsNoticeShown);
        Assert.Equal("Granary 0.2.0 is ready to install", updates.NoticeText);
        updates.LaterCommand.Execute(null);
        Assert.False(updates.IsNoticeShown);
        updates.OnExiting();
        Assert.Equal([false], _updater.Applied);
    }

    [Fact]
    public async Task NothingNew_LooksAgainSixHoursLater_AndAFailureToo()
    {
        _updater.Answers.Enqueue(() => null);
        _updater.Answers.Enqueue(() => throw new HttpRequestException("offline"));
        _updater.Answers.Enqueue(() => "0.3.0");
        var updates = Create();
        var ct = TestContext.Current.CancellationToken;

        var run = updates.RunAsync(ct);
        await _clock.WaitForTimerAsync(ct);
        Assert.Equal(1, _updater.Checks);
        Assert.Equal(AppUpdatesViewModel.CheckEvery, await _clock.FireNextTimerAsync(ct));
        await _clock.WaitUntilAsync(() => _updater.Checks == 2 && _clock.NextDue is not null, ct);
        Assert.Contains("Could not look for a Granary update", _logs.All, StringComparison.Ordinal);
        await _clock.FireNextTimerAsync(ct);
        await run;

        Assert.Equal(3, _updater.Checks);
        Assert.Equal("0.3.0", updates.ReadyVersion);
        Assert.True(updates.IsNoticeShown);
    }

    [Fact]
    public async Task RestartNow_ClosesTheWindow_AndTheUpdateRestartsGranary()
    {
        _updater.Answers.Enqueue(() => "0.2.0");
        var updates = Create();
        int closes = 0;
        updates.CloseRequested += (_, _) => closes++;
        await updates.RunAsync(TestContext.Current.CancellationToken);

        updates.RestartNowCommand.Execute(null);
        updates.OnExiting();

        Assert.Equal(1, closes);
        Assert.False(updates.IsNoticeShown);
        Assert.Equal([true], _updater.Applied);
    }

    [Fact]
    public async Task WithoutAnUpdate_ClosingAppliesNothing_AndRestartDoesNothing()
    {
        _updater.Answers.Enqueue(() => null);
        var updates = Create();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int closes = 0;
        updates.CloseRequested += (_, _) => closes++;

        var run = updates.RunAsync(stop.Token);
        await _clock.WaitForTimerAsync(TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await run;
        updates.RestartNowCommand.Execute(null);
        updates.OnExiting();

        Assert.Equal(0, closes);
        Assert.Empty(_updater.Applied);
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        private string? _found;

        public AppUpdateSupport Support { get; set; } = AppUpdateSupport.Supported;

        /// <summary>Each look's answer: the version found, or null for none.</summary>
        public Queue<Func<string?>> Answers { get; } = new();

        /// <summary>The notes the signed package carries.</summary>
        public string? Notes { get; set; }

        /// <summary>Notes the unsigned feed gives with the look.</summary>
        public string? FeedNotes { get; set; }

        /// <summary>Holds the download until set.</summary>
        public TaskCompletionSource? HoldDownload { get; set; }

        /// <summary>Whether a download turns out to be the signed release.</summary>
        public bool Verifies { get; set; } = true;

        /// <summary>Whether a download fails on the way, as offline.</summary>
        public bool DownloadFails { get; set; }

        public int Checks { get; private set; }

        public int Downloads { get; private set; }

        public List<bool> Applied { get; } = [];

        public Task<AppRelease?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            _found = Answers.Dequeue()();
            return Task.FromResult(_found is { } version ? new AppRelease(version, FeedNotes) : null);
        }

        public async Task<AppRelease?> DownloadAsync(IProgress<int>? progress, CancellationToken cancellationToken)
        {
            Downloads++;
            if (HoldDownload is { } hold)
            {
                await hold.Task;
            }
            if (DownloadFails)
            {
                throw new HttpRequestException("offline");
            }
            return Verifies && _found is { } version ? new AppRelease(version, Notes) : null;
        }

        public void ApplyWhenExited(bool restart) => Applied.Add(restart);
    }
}
