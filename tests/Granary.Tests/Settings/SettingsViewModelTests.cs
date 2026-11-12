using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Settings;

/// <summary>The profile's values as the settings pages read them; writes are recorded, and can be refused.</summary>
internal sealed class FakeProfileSettings : IProfileSettings
{
    public Dictionary<string, EffectiveSetting> Values { get; } = [];

    public List<string> Writes { get; } = [];

    public bool Refuses { get; set; }

    public void Set(string key, string json, string source = "profile") =>
        Values[key] = new EffectiveSetting(key, JsonDocument.Parse(json).RootElement.Clone(), source);

    public Task<IReadOnlyDictionary<string, EffectiveSetting>> ReadAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, EffectiveSetting>>(keys.Where(Values.ContainsKey).ToDictionary(k => k, k => Values[k]));

    public Task SaveAsync(string key, JsonElement value, bool clearDevice, CancellationToken cancellationToken) =>
        Write($"save {key} {value.GetRawText()}{(clearDevice ? " clearing the device" : "")}");

    public Task ResetAsync(string key, CancellationToken cancellationToken) => Write($"reset {key}");

    /// <summary>While set, writes wait for it: saves on their way.</summary>
    public TaskCompletionSource? Gate { get; set; }

    private async Task Write(string write)
    {
        Writes.Add(write);
        if (Gate is { } gate)
        {
            await gate.Task;
        }
        if (Refuses)
        {
            throw new SiloApiException(422, new ApiProblem(ApiProblem.TypeOrigin + "validation_failed", "validation_failed", 422, null, null, null));
        }
    }
}

/// <summary>Settings → Playback and Language &amp; Subtitles.</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly FakeProfileSettings _settings = new();
    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeNetworkLink _network = new();
    private readonly InMemoryPlaybackDevicePreferences _device = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingFileLauncher _launcher = new();
    private readonly MpvConfigStatus _mpvStatus = new();
    private readonly MpvConfigChanges _mpvChanges;

    public SettingsViewModelTests() => _mpvChanges = new MpvConfigChanges(_mpvStatus, new ImmediateDispatcher());

    private MpvConfigFolder MpvFolder => new(Path.Combine(_data, "mpv"), new LogSink().For<MpvConfigFolder>());

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private PlaybackSettingsViewModel Playback() => new(_settings, new PlaybackApi(_silo.Http), new PlaybackConnectionApi(_silo.Http),
        ConnectionSpeedTestTests.SpeedTest(_silo, _time, _network), _network,
        new FakeSessionManager { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" }, _time,
        new LogSink().For<PlaybackSettingsViewModel>(), _device, MpvFolder, _launcher, _mpvStatus, _mpvChanges);

    private LanguageSettingsViewModel Language() => new(_settings, new LogSink().For<LanguageSettingsViewModel>());

    private void Capabilities(params string[] features) =>
        _silo.On(HttpMethod.Get, "api/v2/playback/capabilities", ScriptedSilo.Json(HttpStatusCode.OK,
            $$"""{ "revision": "1", "state": "available", "allowed": true, "installation_id": "i", "protocol_versions": [3], "features": [{{string.Join(",", features.Select(f => $"\"{f}\""))}}] }"""));

    [Fact]
    public async Task ThePlaybackPage_ShowsTheProfilesValues()
    {
        _settings.Set(SettingKeys.PreferredQuality, "\"original\"");
        _settings.Set(SettingKeys.MaxBitrateKbps, "8000");
        _settings.Set(SettingKeys.IntroSkipMode, "\"always\"");
        _settings.Set(SettingKeys.AutoSkipCredits, "true", "profile_device");
        _settings.Set(SettingKeys.SkipBackSeconds, "15");
        Capabilities();
        var page = Playback();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        // A stored "original" is Auto; a bitrate off the ladder reads as itself.
        Assert.Equal("auto", page.SelectedQuality?.Value);
        Assert.Equal(PlaybackSettingsViewModel.AutoQualityDescription, page.QualityDescription);
        Assert.Equal("8 Mbps", page.SelectedBitrate?.Label);
        Assert.Contains(page.BitrateChoices, c => c.Label == "No limit");
        Assert.Equal("Skip automatically", page.SelectedIntroSkip?.Label);
        Assert.True(page.AutoSkipCredits);
        Assert.True(page.AutoPlayNext);
        Assert.Equal("15 seconds", page.SelectedRewind?.Label);
        Assert.Equal("30 seconds", page.SelectedFastForward?.Label);
        Assert.False(page.ShowsBuffering);
        Assert.Empty(_settings.Writes);
    }

    [Fact]
    public void DirectPlayWithoutHardwareDecoding_IsThisPcsChoice_NotTheProfiles()
    {
        _device.DecodesInSoftware = true;
        var page = Playback();
        Assert.True(page.DecodesInSoftware);

        page.DecodesInSoftware = false;

        Assert.Equal([false], _device.Saved);
        Assert.Empty(_settings.Writes);
    }

    [Fact]
    public async Task OpenMpvConf_MakesTheFileFirst_ThenOpensIt()
    {
        var page = Playback();

        await page.OpenMpvConfigCommand.ExecuteAsync(null);

        Assert.True(File.Exists(MpvFolder.ConfigFile));
        Assert.Equal(["file " + MpvFolder.ConfigFile], _launcher.Opened);
        Assert.Equal("", page.MpvConfigMessage);
        Assert.Empty(_settings.Writes);
    }

    [Fact]
    public async Task AnOlderOpenThatEndsLast_DoesNotSpeakOverTheNewerOne()
    {
        using var hold = new ManualResetEventSlim();
        _launcher.HoldFiles = hold;
        _launcher.FailWhen = what => what.StartsWith("file ", StringComparison.Ordinal);
        var page = Playback();

        var older = page.OpenMpvConfigCommand.ExecuteAsync(null);
        await page.OpenMpvFolderCommand.ExecuteAsync(null);
        hold.Set();
        await older;

        Assert.Equal("", page.MpvConfigMessage);
    }

    [Fact]
    public async Task AnMpvConfThatDoesNotOpen_SaysWhereItIs()
    {
        _launcher.Fails = true;
        var page = Playback();

        await page.OpenMpvConfigCommand.ExecuteAsync(null);

        Assert.Equal($"Couldn't open mpv.conf. You'll find it in {MpvFolder.Directory}.", page.MpvConfigMessage);
        Assert.True(File.Exists(MpvFolder.ConfigFile));

        // The next try that works clears it.
        _launcher.Fails = false;
        await page.OpenMpvConfigCommand.ExecuteAsync(null);
        Assert.Equal("", page.MpvConfigMessage);
    }

    [Fact]
    public async Task AnMpvConfThatCannotBeMade_IsSaid()
    {
        // A file where the folder belongs.
        Directory.CreateDirectory(_data);
        await File.WriteAllTextAsync(Path.Combine(_data, "mpv"), "", TestContext.Current.CancellationToken);
        var page = Playback();

        await page.OpenMpvConfigCommand.ExecuteAsync(null);

        Assert.Equal($"Couldn't create mpv.conf in {MpvFolder.Directory}.", page.MpvConfigMessage);
        Assert.Empty(_launcher.Opened);
    }

    [Fact]
    public async Task AFolderInMpvConfsPlace_IsSaid()
    {
        Directory.CreateDirectory(MpvFolder.ConfigFile);
        var page = Playback();

        await page.OpenMpvConfigCommand.ExecuteAsync(null);

        Assert.Equal($"mpv.conf in {MpvFolder.Directory} is a folder. Rename or remove it, then try again.", page.MpvConfigMessage);
        Assert.Empty(_launcher.Opened);
    }

    [Theory]
    [InlineData(MpvConfigUse.NotUsed, "it sets player-operation-mode, which Granary needs to keep its own options", 0,
        "Your mpv.conf was not used: it sets player-operation-mode, which Granary needs to keep its own options. The log has the details.")]
    [InlineData(MpvConfigUse.StoppedDuringPlayback, "it keeps changing log-file", 0,
        "Granary stopped using your mpv.conf: it keeps changing log-file. The log has the details.")]
    [InlineData(MpvConfigUse.NotApplied, "", 0, "Couldn't apply your mpv.conf. Granary keeps playing with the one it started with. The log has the details.")]
    [InlineData(MpvConfigUse.Used, "", 2, "Granary kept 2 of its own options from your mpv.conf. The log names them.")]
    [InlineData(MpvConfigUse.Used, "", 0, "")]
    [InlineData(MpvConfigUse.None, "", 0, "")]
    public void HowYourMpvConfFaredThisRun_IsSaidUnderTheRow(MpvConfigUse use, string reason, int kept, string text)
    {
        _mpvStatus.Report(new MpvConfigState(use, reason, kept));

        var page = Playback();

        Assert.Equal(text, page.MpvConfigStatusText);
        Assert.Equal("", page.MpvConfigMessage);
    }

    [Fact]
    public async Task TheStatus_IsReadAgainWhenThePageLoads()
    {
        Capabilities();
        var page = Playback();
        Assert.Equal("", page.MpvConfigStatusText);
        // The player starts while Settings is open.
        _mpvStatus.Report(new MpvConfigState(MpvConfigUse.NotUsed, "mpv refused to start with it"));

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Your mpv.conf was not used: mpv refused to start with it. The log has the details.", page.MpvConfigStatusText);
    }

    [Fact]
    public void ATitleOpening_ShowsHowTheFileFared()
    {
        _mpvStatus.Report(new MpvConfigState(MpvConfigUse.NotUsed, "it sets player-operation-mode, which Granary needs to keep its own options"));
        using var page = Playback();
        _mpvChanges.Saved();

        // The title's load starts a new mpv with the file as it is now, which it uses.
        _mpvStatus.ReportTitleOpening();
        _mpvStatus.Report(new MpvConfigState(MpvConfigUse.Used, KeptOptions: 1));
        _mpvStatus.ReportTitleOpened();

        Assert.Equal("Granary kept 1 of its own options from your mpv.conf. The log names them.", page.MpvConfigStatusText);
    }

    [Fact]
    public void APageThatLeft_NoLongerFollowsTheFile()
    {
        var page = Playback();
        page.Dispose();
        _mpvChanges.Saved();

        _mpvStatus.ReportTitleOpening();
        _mpvStatus.Report(new MpvConfigState(MpvConfigUse.NotUsed, "mpv refused to start with it"));
        _mpvStatus.ReportTitleOpened();

        Assert.Equal("", page.MpvConfigStatusText);
    }

    [Fact]
    public void OpenLogs_ShowsGranarysLogFolder()
    {
        var page = Playback();

        page.OpenLogsCommand.Execute(null);

        Assert.Equal(["folder " + Granary.Core.Diagnostics.FileLoggerProvider.DefaultDirectory], _launcher.Opened);
        Assert.Equal("", page.MpvConfigMessage);
    }

    [Fact]
    public async Task OpenFolder_ShowsTheMpvFolder_MadeFirst()
    {
        var page = Playback();

        await page.OpenMpvFolderCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(MpvFolder.Directory));
        Assert.Equal(["folder " + MpvFolder.Directory], _launcher.Opened);
        Assert.False(File.Exists(MpvFolder.ConfigFile));
        Assert.Equal("", page.MpvConfigMessage);
    }

    [Fact]
    public async Task OpenFolder_WhenItCannotBeCreated_DoesNotLaunchIt()
    {
        Directory.CreateDirectory(_data);
        await File.WriteAllTextAsync(Path.Combine(_data, "mpv"), "", TestContext.Current.CancellationToken);
        var page = Playback();

        await page.OpenMpvFolderCommand.ExecuteAsync(null);

        Assert.Equal($"Couldn't open {MpvFolder.Directory}.", page.MpvConfigMessage);
        Assert.Empty(_launcher.Opened);
    }

    [Fact]
    public async Task OpenFolder_WhenTheLauncherFails_SaysWhereItIs_AndClearsTheMessageOnRetry()
    {
        _launcher.Fails = true;
        var page = Playback();

        await page.OpenMpvFolderCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(MpvFolder.Directory));
        Assert.Equal($"Couldn't open {MpvFolder.Directory}.", page.MpvConfigMessage);
        _launcher.Fails = false;
        await page.OpenMpvFolderCommand.ExecuteAsync(null);
        Assert.Equal(["folder " + MpvFolder.Directory], _launcher.Opened);
        Assert.Equal("", page.MpvConfigMessage);
    }

    [Fact]
    public void OpenLogs_WhenTheLogFolderFails_TriesTheDataFolder()
    {
        _launcher.FailWhen = what => what == "folder " + Granary.Core.Diagnostics.FileLoggerProvider.DefaultDirectory;
        var page = Playback();

        page.OpenLogsCommand.Execute(null);

        Assert.Equal(["folder " + Granary.Core.AppPaths.DataDirectory], _launcher.Opened);
        Assert.Equal("", page.MpvConfigMessage);
    }

    [Fact]
    public void OpenLogs_WhenBothFoldersFail_SaysWhereTheLogsAre()
    {
        _launcher.Fails = true;
        var page = Playback();

        page.OpenLogsCommand.Execute(null);

        Assert.Empty(_launcher.Opened);
        Assert.Equal($"Couldn't open {Granary.Core.Diagnostics.FileLoggerProvider.DefaultDirectory}.", page.MpvConfigMessage);
    }

    [Fact]
    public void TheDiscordStatus_IsThisPcsChoice_OffAtFirst()
    {
        var page = Playback();
        Assert.False(page.DiscordStatus);

        page.DiscordStatus = true;

        Assert.Equal([true], _device.SavedDiscordStatus);
        Assert.Empty(_settings.Writes);
    }

    [Fact]
    public void TheDiscordStatus_ShowsWhatThisPcHolds_WithoutSavingIt()
    {
        _device.DiscordStatus = true;

        var page = Playback();

        Assert.True(page.DiscordStatus);
        Assert.Empty(_device.SavedDiscordStatus);
    }


    [Fact]
    public void UpAndDownInThePlayer_IsThisPcsChoice_ControlsAndMenusFirst()
    {
        var page = Playback();
        Assert.Equal(["Controls and menus", "Volume"], page.UpDownChoices.Select(c => c.Label));
        Assert.Equal("controls", page.SelectedUpDown?.Value);

        page.SelectedUpDown = page.UpDownChoices[1];

        Assert.Equal([PlayerUpDown.Volume], _device.SavedUpDown);
        Assert.Empty(_settings.Writes);
    }

    [Fact]
    public void TheStillWatchingPrompt_IsThisPcsChoice_ThreeEpisodesAtFirst()
    {
        var page = Playback();
        Assert.Equal(["Unlimited", "2 episodes", "3 episodes", "4 episodes", "5 episodes", "Custom"], page.StillWatchingChoices.Select(c => c.Label));
        Assert.Equal("3", page.SelectedStillWatching?.Value);
        Assert.Empty(_device.SavedPassoutThreshold);

        page.SelectedStillWatching = page.StillWatchingChoices[0];

        Assert.Equal([0], _device.SavedPassoutThreshold);
        Assert.Empty(_settings.Writes);
    }

    [Fact]
    public void Custom_KeepsWhatHolds_UntilANumberIsTyped()
    {
        var page = Playback();
        Assert.False(page.IsStillWatchingCustom);

        page.SelectedStillWatching = page.StillWatchingChoices[^1];

        Assert.True(page.IsStillWatchingCustom);
        Assert.Equal(3, page.StillWatchingEpisodes);
        Assert.Empty(_device.SavedPassoutThreshold);
    }

    [Fact]
    public void Custom_AfterUnlimited_StartsAtTheDefault()
    {
        _device.PassoutThreshold = 0;
        var page = Playback();

        page.SelectedStillWatching = page.StillWatchingChoices[^1];

        Assert.Equal(AutoPlayStreak.DefaultThreshold, page.StillWatchingEpisodes);
        Assert.Equal([AutoPlayStreak.DefaultThreshold], _device.SavedPassoutThreshold);
    }

    [Fact]
    public void ACustomNumber_IsSaved_AndShowsAsCustomAtTheNextOpen()
    {
        var page = Playback();
        page.SelectedStillWatching = page.StillWatchingChoices[^1];

        page.StillWatchingEpisodes = 7;

        Assert.Equal([7], _device.SavedPassoutThreshold);
        _device.SavedPassoutThreshold.Clear();
        var again = Playback();
        Assert.Equal("Custom", again.SelectedStillWatching?.Label);
        Assert.True(again.IsStillWatchingCustom);
        Assert.Equal(7, again.StillWatchingEpisodes);
        Assert.Empty(_device.SavedPassoutThreshold);
    }

    [Theory]
    [InlineData(50, 20)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(6.6, 7)]
    public void ACustomNumberOutOfBounds_OrNotWhole_BecomesTheNearestAllowed(double typed, int kept)
    {
        var page = Playback();
        page.SelectedStillWatching = page.StillWatchingChoices[^1];

        page.StillWatchingEpisodes = typed;

        Assert.Equal(kept, page.StillWatchingEpisodes);
        Assert.Equal(kept, _device.PassoutThreshold);
    }

    [Fact]
    public void AClearedNumber_ShowsWhatHoldsAgain_AndSavesNothing()
    {
        _device.PassoutThreshold = 7;
        var page = Playback();

        page.StillWatchingEpisodes = double.NaN;

        Assert.Equal(7, page.StillWatchingEpisodes);
        Assert.Empty(_device.SavedPassoutThreshold);
    }

    [Fact]
    public void LeavingCustom_HidesTheNumber_AndSavesTheChoice()
    {
        _device.PassoutThreshold = 7;
        var page = Playback();

        page.SelectedStillWatching = page.StillWatchingChoices[0];

        Assert.False(page.IsStillWatchingCustom);
        Assert.Equal([0], _device.SavedPassoutThreshold);
    }

    [Fact]
    public void TheStillWatchingPromptSetToFive_ShowsAsSaved()
    {
        _device.PassoutThreshold = 5;

        Assert.Equal("5", Playback().SelectedStillWatching?.Value);
        Assert.Empty(_device.SavedPassoutThreshold);
    }

    [Fact]
    public void RewindOnResume_IsThisPcsChoice_NoneAtFirst()
    {
        var page = Playback();
        Assert.Equal(0, page.ResumeRewindSeconds);
        Assert.Empty(_device.SavedResumeRewindSeconds);

        page.ResumeRewindSeconds = 7;

        Assert.Equal([7], _device.SavedResumeRewindSeconds);
        Assert.Empty(_settings.Writes);
    }

    [Fact]
    public void RewindOnResume_ShowsWhatThisPcHolds_WithoutSavingIt()
    {
        _device.ResumeRewindSeconds = 12;

        Assert.Equal(12, Playback().ResumeRewindSeconds);
        Assert.Empty(_device.SavedResumeRewindSeconds);
    }

    [Theory]
    [InlineData(45, 30)]
    [InlineData(-3, 0)]
    [InlineData(6.6, 7)]
    public void ARewindOutOfBounds_OrNotWhole_BecomesTheNearestAllowed(double typed, int kept)
    {
        _device.ResumeRewindSeconds = 10;
        var page = Playback();

        page.ResumeRewindSeconds = typed;

        Assert.Equal(kept, page.ResumeRewindSeconds);
        Assert.Equal([kept], _device.SavedResumeRewindSeconds);
    }

    [Fact]
    public void AClearedRewind_ShowsWhatHoldsAgain_AndSavesNothing()
    {
        _device.ResumeRewindSeconds = 12;
        var page = Playback();

        page.ResumeRewindSeconds = double.NaN;

        Assert.Equal(12, page.ResumeRewindSeconds);
        Assert.Empty(_device.SavedResumeRewindSeconds);
    }

    [Fact]
    public void UpAndDownForTheVolume_ShowsAsSaved()
    {
        _device.UpDown = PlayerUpDown.Volume;

        Assert.Equal("volume", Playback().SelectedUpDown?.Value);
        Assert.Empty(_device.SavedUpDown);
    }

    [Fact]
    public async Task AChange_IsSavedAtOnce_ClearingADeviceValueThatShadowedIt()
    {
        _settings.Set(SettingKeys.AutoSkipCredits, "true", "profile_device");
        _settings.Set(SettingKeys.AutoSkipRecap, "false", "default");
        _settings.Set(SettingKeys.MaxBitrateKbps, "8000");
        Capabilities();
        var page = Playback();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        page.AutoSkipCredits = false;
        page.AutoSkipRecaps = true;
        page.SelectedBitrate = page.BitrateChoices.First(c => c.Label == "No limit");
        page.SelectedBitrate = page.BitrateChoices.First(c => c.Label == "20 Mbps");

        Assert.Equal(
            [
                "save playback.auto_skip_credits false clearing the device",
                "save playback.auto_skip_recap true",
                "reset playback.max_bitrate_kbps",
                "save playback.max_bitrate_kbps 20000",
            ],
            _settings.Writes);
    }

    [Fact]
    public async Task ASaveTheServerRefuses_PutsTheControlBack_AndSaysSo()
    {
        _settings.Set(SettingKeys.IntroSkipMode, "\"ask\"");
        Capabilities();
        var page = Playback();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        _settings.Refuses = true;

        page.SelectedIntroSkip = page.IntroSkipChoices[0];
        await Until(() => page.ErrorMessage.Length > 0);

        Assert.Equal("ask", page.SelectedIntroSkip?.Value);
        Assert.Equal("Failed to save playback setting", page.ErrorMessage);
    }

    [Fact]
    public async Task ThisConnection_ShowsItsLearnedLimitAndSpeed_AndTheLimitCanBeReset()
    {
        _settings.Set(SettingKeys.BufferingQualityMode, "\"always\"");
        Capabilities("buffering_quality_v1", "connection_speed_v1");
        string connection(string limit) => $$"""
            { "connection": "c1", "auto_budget_kbps": 26500, "learned_limit": {{limit}},
              "probe": { "kbps": 33800, "lower_bound": true, "measured_at": "2026-10-01T10:00:00.000Z", "transport": "wifi" } }
            """;
        _silo.On(HttpMethod.Get, "api/v2/playback/connection",
            ScriptedSilo.Json(HttpStatusCode.OK, connection("""{ "kbps": 44900, "observed_at": "2026-09-17T12:00:00.000Z", "expires_at": "2026-10-17T12:00:00.000Z" }""")),
            ScriptedSilo.Json(HttpStatusCode.OK, connection("null")));
        _silo.On(HttpMethod.Delete, "api/v2/playback/connection/learned-limit", ScriptedSilo.NoContent());
        var page = Playback();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.ShowsBuffering);
        Assert.True(page.ShowsLearnedLimit);
        Assert.Equal("Limited to 44 Mbps after buffering on Sep 17", page.LearnedLimitText);
        Assert.Equal("Faster than 33 Mbps · measured 2 hours ago on Wi-Fi", page.ConnectionSpeedText);
        Assert.Equal("Auto plays videos up to 26 Mbps without converting them.", page.BudgetText);

        await page.ResetLearnedLimitCommand.ExecuteAsync(null);
        Assert.False(page.ShowsLearnedLimit);
    }

    [Fact]
    public async Task SavesGoOutInOrder_AndARefusalOnlyTakesBackWhatIsStillShown()
    {
        _settings.Set(SettingKeys.IntroSkipMode, "\"ask\"");
        Capabilities();
        var page = Playback();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        _settings.Refuses = true;
        _settings.Gate = new TaskCompletionSource();

        page.SelectedIntroSkip = page.IntroSkipChoices[0];
        page.SelectedIntroSkip = page.IntroSkipChoices[2];
        Assert.Equal(["save playback.intro_skip_mode \"never\""], _settings.Writes);
        _settings.Gate.SetResult();
        await Until(() => _settings.Writes.Count == 2 && page.SelectedIntroSkip?.Value == "ask");

        // Never failed while Skip automatically showed, which then failed too: back to what the server holds.
        Assert.Equal("save playback.intro_skip_mode \"always\"", _settings.Writes[1]);
        Assert.Equal("ask", page.SelectedIntroSkip?.Value);
    }

    [Fact]
    public async Task ALanguageTheListDoesNotHave_JoinsIt()
    {
        _settings.Set(SettingKeys.AudioLanguage, "\"pt-AO\"");
        var page = Language();

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Same(page.SelectedSpokenLanguage, page.SpokenLanguageChoices.Single(c => c.Value == "pt-AO"));
        Assert.Equal("No preference", page.SpokenLanguageChoices[0].Label);
        Assert.Empty(_settings.Writes);
    }

    [Fact]
    public async Task ALanguageTheServerRefuses_GoesBack_InTheWebsWords()
    {
        _settings.Set(SettingKeys.AudioLanguage, "\"de\"");
        var page = Language();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        _settings.Refuses = true;

        page.SelectedSpokenLanguage = page.SpokenLanguageChoices.Single(c => c.Value == "fr");
        await Until(() => page.ErrorMessage.Length > 0);

        Assert.Equal("de", page.SelectedSpokenLanguage?.Value);
        Assert.Equal("Failed to save language setting", page.ErrorMessage);
    }

    private const string ProbePath = "api/v2/playback/bandwidth-probe";

    private static string ConnectionWithProbe(string? probe) =>
        $$"""{ "connection": "c1", "auto_budget_kbps": 26500, "learned_limit": null, "probe": {{probe ?? "null"}} }""";

    private async Task<PlaybackSettingsViewModel> ConnectionPageAsync(params Func<Task<HttpResponseMessage>>[] probeAnswers)
    {
        ConnectionSpeedTestTests.Capabilities(_silo);
        _silo.On(HttpMethod.Get, "api/v2/playback/connection",
            ScriptedSilo.Json(HttpStatusCode.OK, ConnectionWithProbe(null)),
            ScriptedSilo.Json(HttpStatusCode.OK, ConnectionWithProbe(
                """{ "kbps": 400000, "lower_bound": false, "measured_at": "2026-10-01T12:00:10.000Z", "transport": "ethernet" }""")));
        if (probeAnswers.Length > 0)
        {
            _silo.On(HttpMethod.Get, ProbePath, probeAnswers);
        }
        var page = Playback();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        return page;
    }

    [Fact]
    public async Task TestSpeed_ShowsTheNewReading()
    {
        var page = await ConnectionPageAsync(ProbeAnswers.Body(TimedProbeStream.Steady(_time, 400_000, 11_000)));
        _silo.On(HttpMethod.Post, "api/v2/playback/bandwidth-probe/results", ScriptedSilo.NoContent());
        Assert.Equal("Not measured yet", page.ConnectionSpeedText);
        Assert.True(page.ShowsTestSpeed);
        Assert.Equal("Test speed", page.TestSpeedText);

        var running = page.TestSpeedCommand.ExecuteAsync(null);
        await running;

        Assert.Equal("400 Mbps · measured just now on Ethernet", page.ConnectionSpeedText);
        Assert.Equal("", page.SpeedTestMessage);
        Assert.False(page.IsTestingSpeed);
        Assert.Contains("\"manual\":true", Assert.Single(_silo.SentTo(HttpMethod.Post, "api/v2/playback/bandwidth-probe/results")).Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestSpeed_SaysTesting_AndCannotBePressedAgainMeanwhile()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = await ConnectionPageAsync(() => answer.Task);

        var running = page.TestSpeedCommand.ExecuteAsync(null);

        Assert.True(page.IsTestingSpeed);
        Assert.Equal("Testing…", page.TestSpeedText);
        Assert.False(page.TestSpeedCommand.CanExecute(null));
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await running;
        Assert.Equal("Test speed", page.TestSpeedText);
        Assert.Equal("Couldn't measure the connection speed. Try again.", page.SpeedTestMessage);
    }

    [Fact]
    public async Task ARateLimitedTest_SaysWhen_ForThatLong()
    {
        var page = await ConnectionPageAsync(ProbeAnswers.RateLimited(TimeSpan.FromSeconds(12)));

        await page.TestSpeedCommand.ExecuteAsync(null);
        Assert.Equal("Try again in 12 seconds", page.SpeedTestMessage);

        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("Try again in 12 seconds", page.SpeedTestMessage);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => page.SpeedTestMessage.Length == 0);
    }

    [Fact]
    public async Task AResultTheServerDidNotKeep_SaysSo()
    {
        var page = await ConnectionPageAsync(ProbeAnswers.Body(TimedProbeStream.Steady(_time, 50_000, 11_000)));
        _silo.On(HttpMethod.Post, "api/v2/playback/bandwidth-probe/results", ScriptedSilo.Problem(500, "internal"));

        await page.TestSpeedCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't save the speed test. Try again.", page.SpeedTestMessage);
        Assert.Equal("Not measured yet", page.ConnectionSpeedText);
    }

    [Theory]
    [InlineData(NetworkTransports.Cellular, false, "Speed tests are skipped on cellular, and the Cellular quality settings apply.")]
    [InlineData(NetworkTransports.Wifi, true, "Speed tests are skipped on metered networks.")]
    public async Task OnACellularOrMeteredLink_TheRowSaysWhyThereIsNoTest(string transport, bool metered, string note)
    {
        _network.Current = new NetworkLink(transport, metered);

        var page = await ConnectionPageAsync();

        Assert.Equal(note, page.ConnectionSpeedText);
        Assert.False(page.ShowsTestSpeed);
        Assert.Equal("", page.BudgetText);
    }

    [Fact]
    public async Task ALinkThatTurnedMetered_HidesTheTest()
    {
        var page = await ConnectionPageAsync();
        _network.Current = new NetworkLink(NetworkTransports.Ethernet, true);

        await page.TestSpeedCommand.ExecuteAsync(null);

        Assert.Equal("Speed tests are skipped on metered networks.", page.ConnectionSpeedText);
        Assert.False(page.ShowsTestSpeed);
        Assert.Empty(_silo.SentTo(HttpMethod.Get, ProbePath));
    }

    [Fact]
    public async Task LeavingSettings_StopsTheTest_WithoutAMessage()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = await ConnectionPageAsync(() => answer.Task);

        var running = page.TestSpeedCommand.ExecuteAsync(null);
        page.StopSpeedTest();
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(TimedProbeStream.Steady(_time, 50_000, 11_000)) });
        await running;

        Assert.Equal("", page.SpeedTestMessage);
        Assert.False(page.IsTestingSpeed);
        Assert.Empty(_silo.SentTo(HttpMethod.Post, "api/v2/playback/bandwidth-probe/results"));
    }

    [Theory]
    [InlineData(800, "800 kbps")]
    [InlineData(4590, "4.5 Mbps")]
    [InlineData(26999, "26 Mbps")]
    public void ConnectionRates_AreRoundedDown(long kbps, string expected) =>
        Assert.Equal(expected, PlaybackSettingsViewModel.ConnectionRate(kbps));

    [Fact]
    public async Task TheSubtitlesPage_EditsTheLookAsADraft_AndSavesItWhole()
    {
        _settings.Set(SettingKeys.AudioLanguage, "\"de\"");
        _settings.Set(SettingKeys.SubtitleMode, "\"always\"");
        _settings.Set(SettingKeys.SubtitleAppearance, """{ "fontSize": "small", "fontColor": "#ffffff", "backgroundStyle": "box" }""", "profile_device");
        _settings.Set(SettingKeys.SubtitleSyncMs, "300");
        var page = Language();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("de", page.SelectedSpokenLanguage?.Value);
        Assert.Equal("None", page.SelectedSubtitleLanguage?.Label);
        Assert.Equal("Always on", page.SelectedSubtitleMode?.Label);
        Assert.Equal("Small", page.Look.SelectedFontSize?.Label);
        Assert.True(page.Look.IsBox);
        Assert.Equal("+300 ms", page.OffsetText);
        Assert.False(page.IsAppearanceChanged);

        page.Look.PickFontColor("#facc15");
        page.Look.SelectedPosition = page.Look.PositionChoices[2];
        Assert.True(page.IsAppearanceChanged);
        Assert.Single(page.Look.FontColorSwatches, s => s.IsSelected && s.Label == "Yellow");
        Assert.Empty(_settings.Writes);

        await page.SaveAppearanceCommand.ExecuteAsync(null);

        var saved = Assert.Single(_settings.Writes);
        Assert.StartsWith("save playback.subtitle_appearance {\"fontSize\":\"small\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"fontColor\":\"#facc15\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"position\":\"top\"", saved, StringComparison.Ordinal);
        Assert.EndsWith("clearing the device", saved, StringComparison.Ordinal);
        Assert.False(page.IsAppearanceChanged);
    }

    [Fact]
    public async Task DiscardAndReset_BringBackTheSavedLook_AndTheDefault()
    {
        _settings.Set(SettingKeys.SubtitleAppearance, """{ "fontSize": "xxlarge", "backgroundStyle": "none" }""");
        var page = Language();
        await page.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(page.IsAppearanceCustomized);

        page.Look.SelectedFontSize = page.Look.FontSizeChoices[0];
        page.DiscardAppearanceCommand.Execute(null);
        Assert.Equal("XX-Large", page.Look.SelectedFontSize?.Label);

        await page.ResetAppearanceCommand.ExecuteAsync(null);
        Assert.Equal(["reset playback.subtitle_appearance"], _settings.Writes);
        Assert.Equal("Large", page.Look.SelectedFontSize?.Label);
        Assert.Equal("Box", page.Look.SelectedBackgroundStyle?.Label);
        Assert.False(page.IsAppearanceCustomized);
    }

    [Fact]
    public async Task TheOffset_StepsBy100Ms_AndNoneClearsTheSubtitleLanguage()
    {
        _settings.Set(SettingKeys.SubtitleLanguage, "\"en\"");
        var page = Language();
        await page.LoadAsync(TestContext.Current.CancellationToken);

        await page.OffsetEarlierCommand.ExecuteAsync(null);
        await page.OffsetEarlierCommand.ExecuteAsync(null);
        page.SelectedSubtitleLanguage = page.SubtitleLanguageChoices[0];

        Assert.Equal("−200 ms", page.OffsetText);
        Assert.Equal(["save player.subtitle_sync_ms -100", "save player.subtitle_sync_ms -200", "reset playback.subtitle_language"], _settings.Writes);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
