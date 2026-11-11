using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Servers;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>The profile's player buttons: hidden and moved, kept on this PC for each server and profile, and their editor.</summary>
public sealed class PlayerButtonsLayoutTests : IDisposable
{
    private static readonly ServerEntry Server = new("srv1", new Uri("https://silo.test"), "Elena's Silo", null, default, default, null);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSessionManager _session = new();
    private readonly LogSink _logs = new();
    private readonly InMemoryPlaybackDevicePreferences _device = new();

    public PlayerButtonsLayoutTests()
    {
        _session.State = SessionState.Ready;
        _session.ActiveServer = Server;
        _session.ProfileId = "7";
    }

    private string FilePath => Path.Combine(_directory, "player-buttons.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private PlayerButtonsLayout Create(bool sleepTimerSwitchOff = false)
    {
        _device.SleepTimerSwitchOff = sleepTimerSwitchOff;
        return new(FilePath, _session, new ImmediateDispatcher(), _device, _logs.For<PlayerButtonsLayout>());
    }

    private void ActAs(string profile)
    {
        _session.ProfileId = profile;
        _session.RaiseChanged();
    }

    private async Task<string> FileTextAsync() => await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken);

    private async Task WriteAsync(string json)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, json, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AtFirst_TheBarIsTodays_AndNothingIsWritten()
    {
        var layout = Create();

        Assert.Equal(PlayerBar.Default, layout.Current);
        Assert.False(layout.IsCustomized);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task AHiddenButton_AndAMovedOne_StaySoAtTheNextStart()
    {
        var layout = Create();

        await layout.SetShownAsync(PlayerButton.PlaybackInfo, false);
        await layout.MoveAsync(PlayerButton.FullScreen, -1);
        var again = Create();

        Assert.False(again.Current.Shows(PlayerButton.PlaybackInfo));
        Assert.Equal([PlayerButton.MiniView, PlayerButton.FullScreen, PlayerButton.Fill], again.Current.Order.TakeLast(3));
        Assert.True(again.IsCustomized);
    }

    [Fact]
    public async Task EachChange_SaysSoWithTheNewBar()
    {
        var layout = Create();
        var bars = new List<PlayerBar>();
        layout.Changed += (_, bar) => bars.Add(bar);

        await layout.SetShownAsync(PlayerButton.Volume, false);
        await layout.SetShownAsync(PlayerButton.Volume, false);
        await layout.SetShownAsync(PlayerButton.Volume, true);

        Assert.Equal(2, bars.Count);
        Assert.False(bars[0].Volume);
        Assert.Equal(PlayerBar.Default, bars[1]);
    }

    [Fact]
    public async Task EachProfile_HasItsOwnButtons_AndThePlayerHearsOfTheSwitch()
    {
        var layout = Create();
        await layout.SetShownAsync(PlayerButton.Chapters, false);
        var bars = new List<PlayerBar>();
        layout.Changed += (_, bar) => bars.Add(bar);
        int before = layout.Revision;

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.True(layout.Current.Chapters);
        Assert.True(layout.Revision > before);
        Assert.Equal([PlayerBar.Default], bars);
        _session.ProfileId = "7";
        _session.RaiseChanged();
        Assert.False(layout.Current.Chapters);
    }

    [Fact]
    public async Task Reset_BringsBackTodaysBar()
    {
        var layout = Create();
        await layout.SetShownAsync(PlayerButton.Subtitles, false);
        await layout.MoveAsync(PlayerButton.Settings, -1);

        await layout.ResetAsync();

        Assert.Equal(PlayerBar.Default, layout.Current);
        Assert.Equal(PlayerBar.Default, Create().Current);
        Assert.False(layout.IsCustomized);
    }

    [Fact]
    public async Task WithoutAProfile_NothingChanges()
    {
        _session.ProfileId = null;
        var layout = Create();

        await layout.SetShownAsync(PlayerButton.Volume, false);
        await layout.MoveAsync(PlayerButton.Audio, -1);

        Assert.Equal(PlayerBar.Default, layout.Current);
        Assert.Equal(0, layout.Revision);
    }

    [Fact]
    public async Task AButtonTheFileDoesNotKnow_ShowsInItsOwnPlace_AndOneThatIsGone_IsLeftOut()
    {
        await WriteAsync("""
            {
              "profiles": {
                "srv1/7": {
                  "order": [ "full_screen", "volume", "hologram", "sleep_timer", "audio", "subtitles", "settings", "edit_markers", "playback_info", "mini_view", "fill" ],
                  "hidden": [ "hologram", "audio" ]
                }
              }
            }
            """);

        var bar = Create().Current;

        Assert.Equal(PlayerButton.FullScreen, bar.Order[0]);
        Assert.Equal([PlayerButton.Subtitles, PlayerButton.Chapters, PlayerButton.Settings],
            bar.Order.SkipWhile(button => button != PlayerButton.Subtitles).Take(3));
        Assert.True(bar.Chapters);
        Assert.False(bar.Audio);
        Assert.Equal(PlayerBar.Buttons.Count, bar.Order.Count);
    }

    [Fact]
    public async Task AnUnreadableFile_ShowsTodaysBar()
    {
        await WriteAsync("{ not json");

        var layout = Create(sleepTimerSwitchOff: true);

        Assert.Equal(PlayerBar.Default, layout.Current);
        Assert.Contains("could not be read", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSleepTimerSwitchTurnedOff_HidesTheSleepTimer_ForEveryProfile_Once()
    {
        var layout = Create(sleepTimerSwitchOff: true);
        await layout.Saving;

        Assert.False(layout.Current.SleepTimer);
        Assert.True(layout.IsCustomized);
        Assert.True(File.Exists(FilePath));
        _session.ProfileId = "8";
        _session.RaiseChanged();
        Assert.False(layout.Current.SleepTimer);
        Assert.True(layout.Current.Volume);
    }

    [Fact]
    public async Task AfterTheSwitchWasTakenOver_TheEditorDecides()
    {
        var layout = Create(sleepTimerSwitchOff: true);
        await layout.Saving;

        await layout.SetShownAsync(PlayerButton.SleepTimer, true);
        var again = Create(sleepTimerSwitchOff: true);

        Assert.True(again.Current.SleepTimer);
        _session.ProfileId = "8";
        _session.RaiseChanged();
        Assert.False(again.Current.SleepTimer);
    }

    [Fact]
    public async Task Reset_AfterTheSwitchWasTakenOver_BringsTheSleepTimerBack_AndKeepsItSo()
    {
        var layout = Create(sleepTimerSwitchOff: true);
        await layout.Saving;

        await layout.ResetAsync();

        Assert.True(layout.Current.SleepTimer);
        Assert.False(layout.IsCustomized);
        Assert.True(Create(sleepTimerSwitchOff: true).Current.SleepTimer);
    }

    [Fact]
    public async Task AProfileThatHidesTheSleepTimerAsTheSwitchDid_HasNoLayoutOfItsOwn()
    {
        var layout = Create(sleepTimerSwitchOff: true);
        await layout.Saving;
        await layout.SetShownAsync(PlayerButton.SleepTimer, true);

        await layout.SetShownAsync(PlayerButton.SleepTimer, false);

        Assert.DoesNotContain("srv1/7", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnceTheFileIsThere_TheSwitchIsNotTakenOverAgain()
    {
        var layout = Create(sleepTimerSwitchOff: false);
        await layout.SetShownAsync(PlayerButton.Fill, false);

        var again = Create(sleepTimerSwitchOff: true);

        Assert.True(again.Current.SleepTimer);
        _session.ProfileId = "8";
        _session.RaiseChanged();
        Assert.Equal(PlayerBar.Default, again.Current);
    }

    [Fact]
    public void TheSwitchOn_ChangesNothing()
    {
        var layout = Create(sleepTimerSwitchOff: false);

        Assert.True(layout.Current.SleepTimer);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task TheSettings_ListEveryButton_AndMoveAndHideThem()
    {
        var layout = Create();
        using var settings = new PlayerButtonsSettingsViewModel(layout);

        Assert.Equal(PlayerBar.Buttons.Select(info => info.Label), settings.Rows.Select(row => row.Label));
        var volume = settings.Rows[0];
        Assert.False(volume.MoveUpCommand.CanExecute(null));
        Assert.False(settings.Rows[^1].MoveDownCommand.CanExecute(null));
        var audio = settings.Rows.Single(row => row.Button == PlayerButton.Audio);

        audio.MoveUpCommand.Execute(null);
        settings.Rows.Single(row => row.Button == PlayerButton.PlaybackInfo).IsShown = false;
        await layout.Saving;

        Assert.Same(audio, settings.Rows[1]);
        Assert.Equal(PlayerButton.Audio, layout.Current.Order[1]);
        Assert.False(layout.Current.PlaybackInfo);
        Assert.True(settings.IsCustomized);
        Assert.Equal("Speed, audio delay, version and quality.", settings.Rows.Single(row => row.Button == PlayerButton.Settings).Note);
    }

    [Fact]
    public async Task TheSettings_ShowTheBarWithoutChangingIt_AndResetShowsTodaysBar()
    {
        var layout = Create();
        await layout.SetShownAsync(PlayerButton.Fill, false);
        int revision = layout.Revision;
        using var settings = new PlayerButtonsSettingsViewModel(layout);
        var fill = settings.Rows.Single(row => row.Button == PlayerButton.Fill);

        Assert.False(fill.IsShown);
        Assert.Equal(revision, layout.Revision);

        await settings.ResetCommand.ExecuteAsync(null);

        Assert.True(fill.IsShown);
        Assert.False(settings.IsCustomized);
    }

    [Fact]
    public async Task WithoutAProfile_TheSwitch_GoesBackToWhatTheBarShows()
    {
        _session.ProfileId = null;
        var layout = Create();
        using var settings = new PlayerButtonsSettingsViewModel(layout);
        var volume = settings.Rows[0];

        volume.IsShown = false;
        await layout.Saving;

        Assert.True(volume.IsShown);
        Assert.True(layout.Current.Volume);
    }

    [Fact]
    public async Task TheSettings_FollowAnotherProfile()
    {
        var layout = Create();
        await layout.SetShownAsync(PlayerButton.MiniView, false);
        using var settings = new PlayerButtonsSettingsViewModel(layout);
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.True(settings.Rows.Single(row => row.Button == PlayerButton.MiniView).IsShown);
        Assert.False(settings.IsCustomized);
        Assert.Contains(nameof(PlayerButtonsSettingsViewModel.IsCustomized), changed);
    }

    [Fact]
    public async Task MovePressedTwice_WhileTheFirstSaveWaits_MovesTwice_AndBothAreSaved()
    {
        var layout = Create();
        var gate = new TaskCompletionSource();
        layout.BeforeWrite = () => gate.Task;
        using var settings = new PlayerButtonsSettingsViewModel(layout);
        var fullScreen = settings.Rows.Single(row => row.Button == PlayerButton.FullScreen);

        fullScreen.MoveUpCommand.Execute(null);
        Assert.True(fullScreen.MoveUpCommand.CanExecute(null));
        fullScreen.MoveUpCommand.Execute(null);
        gate.SetResult();
        await layout.Saving;

        PlayerButton[] expected = [PlayerButton.PlaybackInfo, PlayerButton.FullScreen, PlayerButton.MiniView, PlayerButton.Fill];
        Assert.Equal(expected, layout.Current.Order.TakeLast(4));
        Assert.Equal(expected, Create().Current.Order.TakeLast(4));
        Assert.Same(fullScreen, settings.Rows[^3]);
    }

    [Fact]
    public async Task ChangesInARow_WhileASaveIsSlow_TheLastIsWhatIsWritten()
    {
        var layout = Create();
        var gate = new TaskCompletionSource();
        layout.BeforeWrite = () => gate.Task;

        _ = layout.SetShownAsync(PlayerButton.Volume, false);
        _ = layout.SetShownAsync(PlayerButton.Chapters, false);
        var last = layout.SetShownAsync(PlayerButton.Volume, true);
        gate.SetResult();
        await last;

        var again = Create().Current;
        Assert.True(again.Volume);
        Assert.False(again.Chapters);
    }

    [Fact]
    public async Task AFailedWrite_KeepsTheChangeForThisRun_AndTheNextWriteSavesEverything()
    {
        var layout = Create();
        int writes = 0;
        layout.BeforeWrite = () => ++writes == 1 ? throw new IOException("The disk is full") : Task.CompletedTask;

        await layout.SetShownAsync(PlayerButton.Fill, false);

        Assert.False(layout.Current.Fill);
        Assert.True(layout.IsCustomized);
        Assert.False(File.Exists(FilePath));
        Assert.Contains("could not be saved", _logs.All, StringComparison.Ordinal);

        await layout.MoveAsync(PlayerButton.Audio, -1);

        var again = Create().Current;
        Assert.False(again.Fill);
        Assert.Equal(PlayerButton.Audio, again.Order[1]);
    }

    [Fact]
    public async Task ChangesInARow_AfterAFailedWrite_TheLastWins()
    {
        var layout = Create();
        var gate = new TaskCompletionSource();
        int writes = 0;
        layout.BeforeWrite = async () =>
        {
            if (++writes == 1)
            {
                await gate.Task;
                throw new IOException("The disk went away");
            }
        };

        _ = layout.SetShownAsync(PlayerButton.Settings, false);
        var last = layout.MoveAsync(PlayerButton.Settings, -1);
        gate.SetResult();
        await last;

        var again = Create().Current;
        Assert.False(again.Settings);
        Assert.Equal(PlayerButton.Settings, again.Order[4]);
    }

    [Fact]
    public async Task TheSwitchTakenOver_HidesTheSleepTimerForEveryProfile_AndOneThatChangesSomethingElseKeepsItHidden()
    {
        var layout = Create(sleepTimerSwitchOff: true);
        await layout.Saving;

        await layout.SetShownAsync(PlayerButton.Fill, false);
        var again = Create(sleepTimerSwitchOff: false);

        Assert.False(again.Current.SleepTimer);
        Assert.False(again.Current.Fill);
        ActAs("8");
        Assert.False(again.Current.SleepTimer);
        Assert.True(again.Current.Fill);
        ActAs("9");
        Assert.False(again.Current.SleepTimer);
        Assert.True(again.IsCustomized);
    }

    [Fact]
    public async Task TheSwitchTakenOver_IsDroppedFromThisPcsChoices_SoItIsNotTakenOverAgain()
    {
        var layout = Create(sleepTimerSwitchOff: true);
        await layout.Saving;

        Assert.Equal(1, _device.SleepTimerSwitchForgotten);
        Assert.False(_device.SleepTimerSwitchOff);
        File.Delete(FilePath);
        Assert.True(new PlayerButtonsLayout(FilePath, _session, new ImmediateDispatcher(), _device, _logs.For<PlayerButtonsLayout>()).Current.SleepTimer);
    }

    [Fact]
    public async Task TheSwitch_IsKeptWhileTheTakeOverCouldNotBeWritten_AndTakenOverAtTheNextStart()
    {
        // Something in the way of the file: the write fails.
        Directory.CreateDirectory(FilePath + ".tmp");

        var layout = Create(sleepTimerSwitchOff: true);
        await layout.Saving;

        Assert.False(layout.Current.SleepTimer);
        Assert.Equal(0, _device.SleepTimerSwitchForgotten);
        Directory.Delete(FilePath + ".tmp");
        var again = new PlayerButtonsLayout(FilePath, _session, new ImmediateDispatcher(), _device, _logs.For<PlayerButtonsLayout>());
        await again.Saving;
        Assert.False(again.Current.SleepTimer);
        Assert.Equal(1, _device.SleepTimerSwitchForgotten);
        Assert.Contains("sleep_timer", await FileTextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASwitchLeftOverBesideAFile_IsDropped_AndNotTakenOver()
    {
        await WriteAsync("""{ "profiles": {} }""");

        var layout = Create(sleepTimerSwitchOff: true);

        Assert.True(layout.Current.SleepTimer);
        Assert.Equal(1, _device.SleepTimerSwitchForgotten);
    }

    [Fact]
    public async Task AnUnreadableFile_IsKeptAsABackup_BeforeItIsWrittenOver()
    {
        await WriteAsync("{ not json");
        var layout = Create();

        await layout.SetShownAsync(PlayerButton.Fill, false);

        Assert.Equal("{ not json", await File.ReadAllTextAsync(FilePath + ".bak", TestContext.Current.CancellationToken));
        Assert.False(Create().Current.Fill);
    }

    [Fact]
    public async Task AnUnreadableFile_ThatCannotBeKept_IsNotWrittenOver_WhileTheChangeHoldsForThisRun()
    {
        await WriteAsync("{ not json");
        // A folder where the backup would go: the copy fails.
        Directory.CreateDirectory(FilePath + ".bak");
        var layout = Create();

        await layout.SetShownAsync(PlayerButton.Fill, false);

        Assert.False(layout.Current.Fill);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AProfileWithoutABarOfItsOwn_WhileTheSleepTimerStartsHidden_IsCustomized_AndResetGivesItTodaysBar()
    {
        var layout = Create(sleepTimerSwitchOff: true);
        await layout.Saving;
        ActAs("8");
        Assert.True(layout.IsCustomized);
        var changed = new List<string?>();
        layout.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await layout.ResetAsync();

        Assert.False(layout.IsCustomized);
        Assert.Contains(nameof(PlayerButtonsLayout.IsCustomized), changed);
        var again = Create();
        Assert.Equal(PlayerBar.Default, again.Current);
        ActAs("7");
        Assert.False(again.Current.SleepTimer);
        Assert.True(again.IsCustomized);
    }
}