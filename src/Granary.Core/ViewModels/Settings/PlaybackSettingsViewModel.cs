using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback, as Silo's web has it (pages/settings/PlaybackSettings.tsx): quality, this connection, skipping,
/// the next episode and the seek intervals. Every choice is the profile's and is saved at once; a value this device
/// keeps for itself is cleared with it, so the control shows what was saved. A save the server refuses puts the
/// control back and says so.
/// </summary>
public sealed partial class PlaybackSettingsViewModel : ObservableObject
{
    public const string AutoQualityDescription = "Plays the original file whenever your device and connection can handle it.";
    public const string QualityDescriptionText = "The resolution your profile should request when playback begins.";

    private static readonly string[] Keys =
    [
        SettingKeys.PreferredQuality, SettingKeys.MaxBitrateKbps, SettingKeys.BufferingQualityMode, SettingKeys.IntroSkipMode,
        SettingKeys.AutoSkipRecap, SettingKeys.AutoSkipCredits, SettingKeys.AutoPlayNextPreview, SettingKeys.AutoPlayNext,
        SettingKeys.SkipBackSeconds, SettingKeys.SkipForwardSeconds,
    ];

    // The ladder of the web's bitrate select (lib/bitrateOptions.ts), within the setting's 100 to 200000 kbps.
    private static readonly int[] BitrateLadderKbps = [1500, 2000, 4000, 6000, 10000, 15000, 20000, 40000, 60000, 100000, 200000];

    private readonly IProfileSettings _settings;
    private readonly PlaybackApi _playback;
    private readonly PlaybackConnectionApi _connectionApi;
    private readonly ConnectionSpeedTest _speedTest;
    private readonly INetworkLink _network;
    private readonly ISessionManager _session;
    private readonly TimeProvider _time;
    private readonly ILogger<PlaybackSettingsViewModel> _logger;
    private readonly SettingSaves _saves;
    private readonly Dictionary<string, string?> _sources = [];

    // What the server holds for each control, as far as this page knows: a refused save goes back to it.
    private readonly Dictionary<string, object?> _confirmed = [];
    private bool _applying;

    public PlaybackSettingsViewModel(
        IProfileSettings settings, PlaybackApi playback, PlaybackConnectionApi connection, ConnectionSpeedTest speedTest, INetworkLink network,
        ISessionManager session, TimeProvider time, ILogger<PlaybackSettingsViewModel> logger)
    {
        _settings = settings;
        _playback = playback;
        _connectionApi = connection;
        _speedTest = speedTest;
        _network = network;
        _session = session;
        _time = time;
        _logger = logger;
        _saves = new SettingSaves(logger);
    }

    public IReadOnlyList<SettingChoice> QualityChoices { get; } =
    [
        new("auto", "Auto"), new("480p", "480p"), new("720p", "720p"), new("1080p", "1080p"), new("2160p", "2160p / 4K"),
    ];

    public IReadOnlyList<SettingChoice> BufferingChoices { get; } = [new("never", "Never"), new("ask", "Ask first"), new("always", "Automatically")];

    public IReadOnlyList<SettingChoice> IntroSkipChoices { get; } =
        [new("never", "Never"), new("ask", "Ask to skip"), new("always", "Skip automatically")];

    public IReadOnlyList<SettingChoice> SkipChoices { get; } =
        [.. new[] { 5, 10, 15, 30, 45, 60, 90 }.Select(s => new SettingChoice(SettingValues.Invariant(s), $"{s} seconds"))];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>What went wrong last, as the web's toasts say it; empty when nothing did.</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    [ObservableProperty]
    public partial SettingChoice? SelectedQuality { get; set; }

    [ObservableProperty]
    public partial string QualityDescription { get; private set; } = AutoQualityDescription;

    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> BitrateChoices { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice? SelectedBitrate { get; set; }

    /// <summary>The server knows "Lower quality when buffering".</summary>
    [ObservableProperty]
    public partial bool ShowsBuffering { get; private set; }

    [ObservableProperty]
    public partial SettingChoice? SelectedBuffering { get; set; }

    /// <summary>"Limited to 44 Mbps after buffering on Oct 17".</summary>
    [ObservableProperty]
    public partial string LearnedLimitText { get; private set; } = "";

    [ObservableProperty]
    public partial bool ShowsLearnedLimit { get; private set; }

    [ObservableProperty]
    public partial bool ShowsConnection { get; private set; }

    /// <summary>"Faster than 33 Mbps · measured 20 hours ago on Wi-Fi", or "Not measured yet".</summary>
    [ObservableProperty]
    public partial string ConnectionSpeedText { get; private set; } = "";

    /// <summary>"Auto plays videos up to 26 Mbps without converting them."; empty without a budget.</summary>
    [ObservableProperty]
    public partial string BudgetText { get; private set; } = "";

    /// <summary>Test speed is offered: not on a cellular or metered link, where the description says why instead.</summary>
    [ObservableProperty]
    public partial bool ShowsTestSpeed { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestSpeedText))]
    [NotifyCanExecuteChangedFor(nameof(TestSpeedCommand))]
    public partial bool IsTestingSpeed { get; private set; }

    public string TestSpeedText => IsTestingSpeed ? "Testing…" : "Test speed";

    /// <summary>Why the last test did not measure, as the web says it; empty otherwise. Some messages hide themselves again.</summary>
    [ObservableProperty]
    public partial string SpeedTestMessage { get; private set; } = "";

    [ObservableProperty]
    public partial bool ShowsIntroSkip { get; private set; } = true;

    [ObservableProperty]
    public partial SettingChoice? SelectedIntroSkip { get; set; }

    [ObservableProperty]
    public partial bool AutoSkipRecaps { get; set; }

    [ObservableProperty]
    public partial bool AutoSkipCredits { get; set; }

    [ObservableProperty]
    public partial bool StartNextAtPreview { get; set; }

    [ObservableProperty]
    public partial bool AutoPlayNext { get; set; } = true;

    [ObservableProperty]
    public partial SettingChoice? SelectedRewind { get; set; }

    [ObservableProperty]
    public partial SettingChoice? SelectedFastForward { get; set; }

    private PlaybackConnectionState? _connection;

    // Replaces the speed on a link that is not tested.
    private string? _meteredNote;
    private CancellationTokenSource? _speedTestStop;
    private int _messageVersion;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            var values = await _settings.ReadAsync(Keys, cancellationToken);
            Apply(values);
            await LoadConnectionAsync(cancellationToken);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken) || ex is InvalidOperationException)
        {
            _logger.LogInformation("The playback settings could not be read: {Failure}", ApiFailureText.Describe(ex));
            ErrorMessage = "Could not load the playback settings. " + ApiFailureText.Describe(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ResetLearnedLimitAsync()
    {
        if (_session.ActiveServer?.BaseUri is not { } server)
        {
            return;
        }
        try
        {
            await _connectionApi.ResetLearnedLimitAsync(server);
            _connection = await _connectionApi.GetAsync(server);
            ShowConnection();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("The learned limit could not be reset: {Failure}", ApiFailureText.Describe(ex));
            ErrorMessage = "Couldn't reset the limit. Try again.";
        }
    }

    /// <summary>
    /// The accurate test (Settings → Test speed): the server keeps its result as this connection's reading, and the row
    /// shows it. A press while one runs joins it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestSpeed))]
    private async Task TestSpeedAsync()
    {
        if (_session.ActiveServer?.BaseUri is not { } server)
        {
            return;
        }
        IsTestingSpeed = true;
        Say("");
        _speedTestStop?.Dispose();
        _speedTestStop = new CancellationTokenSource();
        try
        {
            var outcome = await _speedTest.RunAsync(server, _speedTestStop.Token);
            switch (outcome.Status)
            {
                case SpeedTestStatus.Measured:
                    await ReadConnectionAgainAsync(server);
                    break;
                case SpeedTestStatus.Metered:
                    // The link turned metered: the row says why instead.
                    _meteredNote = MeteredNote(_network.Current) ?? MeteredText;
                    ShowConnection();
                    break;
                case SpeedTestStatus.RateLimited:
                    int seconds = outcome.RetryAfterSeconds;
                    Say(string.Create(CultureInfo.InvariantCulture, $"Try again in {seconds} {(seconds == 1 ? "second" : "seconds")}"),
                        TimeSpan.FromSeconds(seconds));
                    break;
                case SpeedTestStatus.NotSaved:
                    Say("Couldn't save the speed test. Try again.");
                    break;
                case SpeedTestStatus.Failed:
                    Say("Couldn't measure the connection speed. Try again.");
                    break;
            }
        }
        finally
        {
            IsTestingSpeed = false;
        }
    }

    private bool CanTestSpeed() => !IsTestingSpeed;

    /// <summary>Stops a test this page started: leaving Settings is the way to playback, which a test would slow.</summary>
    public void StopSpeedTest() => _speedTestStop?.Cancel();

    private async Task ReadConnectionAgainAsync(Uri server)
    {
        try
        {
            _connection = await _connectionApi.GetAsync(server);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            // The server kept the result; the next look shows it.
            _logger.LogInformation("This connection's playback state could not be read again: {Failure}", ApiFailureText.Describe(ex));
        }
        ShowConnection();
    }

    // "Try again in N seconds" is only true for those N seconds.
    private void Say(string text, TimeSpan? forTime = null)
    {
        int version = ++_messageVersion;
        SpeedTestMessage = text;
        if (forTime is { } delay)
        {
            _ = HideMessageAsync(version, delay);
        }
    }

    private async Task HideMessageAsync(int version, TimeSpan delay)
    {
        await Task.Delay(delay, _time);
        if (version == _messageVersion)
        {
            SpeedTestMessage = "";
        }
    }

    private const string MeteredText = "Speed tests are skipped on metered networks.";

    // A link paid for by the byte is not tested, and a reading taken on another link does not describe it.
    private static string? MeteredNote(NetworkLink link) =>
        link.Transport == NetworkTransports.Cellular ? "Speed tests are skipped on cellular, and the Cellular quality settings apply."
        : link.IsMetered ? MeteredText
        : null;

    private void Apply(IReadOnlyDictionary<string, EffectiveSetting> values)
    {
        _applying = true;
        try
        {
            foreach (var (key, value) in values)
            {
                _sources[key] = value.Source;
            }
            EffectiveSetting? Get(string key) => values.GetValueOrDefault(key);
            // A stored "original" means Auto: the contract keeps it as a deprecated value for the same thing.
            string quality = SettingValues.Text(Get(SettingKeys.PreferredQuality))?.ToLowerInvariant() is { } q && q != "original" ? q : "auto";
            SelectedQuality = QualityChoices.FirstOrDefault(c => c.Value == quality) ?? QualityChoices[0];
            QualityDescription = SelectedQuality.Value == "auto" ? AutoQualityDescription : QualityDescriptionText;
            ApplyBitrate(SettingValues.Number(Get(SettingKeys.MaxBitrateKbps)) is { } kbps ? (int)kbps : null);
            ShowsBuffering = values.ContainsKey(SettingKeys.BufferingQualityMode);
            SelectedBuffering = Choice(BufferingChoices, SettingValues.Text(Get(SettingKeys.BufferingQualityMode)), "always");
            ShowsIntroSkip = values.ContainsKey(SettingKeys.IntroSkipMode);
            SelectedIntroSkip = Choice(IntroSkipChoices, SettingValues.Text(Get(SettingKeys.IntroSkipMode)), "ask");
            AutoSkipRecaps = SettingValues.Bool(Get(SettingKeys.AutoSkipRecap)) ?? false;
            AutoSkipCredits = SettingValues.Bool(Get(SettingKeys.AutoSkipCredits)) ?? false;
            StartNextAtPreview = SettingValues.Bool(Get(SettingKeys.AutoPlayNextPreview)) ?? false;
            AutoPlayNext = SettingValues.Bool(Get(SettingKeys.AutoPlayNext)) ?? true;
            SelectedRewind = Choice(SkipChoices, Seconds(Get(SettingKeys.SkipBackSeconds)), "10");
            SelectedFastForward = Choice(SkipChoices, Seconds(Get(SettingKeys.SkipForwardSeconds)), "30");
            _confirmed[SettingKeys.PreferredQuality] = SelectedQuality;
            _confirmed[SettingKeys.MaxBitrateKbps] = SelectedBitrate;
            _confirmed[SettingKeys.BufferingQualityMode] = SelectedBuffering;
            _confirmed[SettingKeys.IntroSkipMode] = SelectedIntroSkip;
            _confirmed[SettingKeys.AutoSkipRecap] = AutoSkipRecaps;
            _confirmed[SettingKeys.AutoSkipCredits] = AutoSkipCredits;
            _confirmed[SettingKeys.AutoPlayNextPreview] = StartNextAtPreview;
            _confirmed[SettingKeys.AutoPlayNext] = AutoPlayNext;
            _confirmed[SettingKeys.SkipBackSeconds] = SelectedRewind;
            _confirmed[SettingKeys.SkipForwardSeconds] = SelectedFastForward;
        }
        finally
        {
            _applying = false;
        }

        static string? Seconds(EffectiveSetting? setting) => SettingValues.Number(setting) is { } s ? SettingValues.Invariant(s) : null;
    }

    private static SettingChoice Choice(IReadOnlyList<SettingChoice> choices, string? value, string fallback) =>
        choices.FirstOrDefault(c => c.Value == value) ?? choices.First(c => c.Value == fallback);

    // "No limit", the ladder, and a stored value off the ladder (another client's), so it reads as itself.
    private void ApplyBitrate(int? kbps)
    {
        var choices = new List<SettingChoice> { new("", "No limit") };
        choices.AddRange(BitrateLadderKbps.Select(k => new SettingChoice(SettingValues.Invariant(k), Rate(k))));
        if (kbps is { } stored && !BitrateLadderKbps.Contains(stored))
        {
            choices.Add(new SettingChoice(SettingValues.Invariant(stored), Rate(stored)));
            choices = [choices[0], .. choices.Skip(1).OrderBy(c => int.Parse(c.Value, CultureInfo.InvariantCulture))];
        }
        BitrateChoices = choices;
        SelectedBitrate = choices.First(c => c.Value == (kbps is { } k2 ? SettingValues.Invariant(k2) : ""));
    }

    // "8 Mbps", "1.5 Mbps", "800 kbps" (web: formatBitrateKbps).
    private static string Rate(int kbps) => kbps >= 1000
        ? (kbps % 1000 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{kbps / 1000} Mbps")
            : string.Create(CultureInfo.InvariantCulture, $"{kbps / 1000.0:0.0} Mbps"))
        : string.Create(CultureInfo.InvariantCulture, $"{kbps} kbps");

    private async Task LoadConnectionAsync(CancellationToken cancellationToken)
    {
        if (_session.ActiveServer?.BaseUri is not { } server)
        {
            return;
        }
        _meteredNote = MeteredNote(_network.Current);
        try
        {
            var capabilities = await _playback.GetCapabilitiesAsync(server, cancellationToken);
            bool learns = capabilities.HasFeature(PlaybackFeatures.BufferingQualityV1);
            ShowsConnection = capabilities.HasFeature(PlaybackFeatures.ConnectionSpeedV1);
            _connection = learns ? await _connectionApi.GetAsync(server, cancellationToken) : null;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            // The rest of the page does not need it.
            _logger.LogInformation("This connection's playback state could not be read: {Failure}", ApiFailureText.Describe(ex));
            ShowsConnection = false;
            _connection = null;
        }
        ShowConnection();
    }

    // Never sends no learned limit: then neither the limit nor the budget it is part of applies.
    private void ShowConnection()
    {
        bool ignoresLimit = SelectedBuffering?.Value == "never";
        var limit = _connection?.LearnedLimit;
        ShowsLearnedLimit = ShowsBuffering && !ignoresLimit && limit is not null;
        LearnedLimitText = limit is null
            ? ""
            : $"Limited to {ConnectionRate(limit.Kbps)} after buffering on {limit.ObservedAt.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture)}";
        ConnectionSpeedText = _meteredNote ?? SpeedText(_connection);
        ShowsTestSpeed = _meteredNote is null;
        long? budget = ignoresLimit && limit is not null ? null : _connection?.AutoBudgetKbps;
        BudgetText = budget is { } kbps && _meteredNote is null ? $"Auto plays videos up to {ConnectionRate(kbps)} without converting them." : "";
    }

    // The highest reading kept for this connection: a test, or else the best playback throughput, a lower bound.
    private string SpeedText(PlaybackConnectionState? state)
    {
        if (state?.Probe is { } probe)
        {
            string rate = probe.LowerBound ? $"Faster than {ConnectionRate(probe.Kbps)}" : ConnectionRate(probe.Kbps);
            string transport = probe.Transport switch
            {
                "wifi" => "Wi-Fi",
                "ethernet" => "Ethernet",
                "cellular" => "cellular",
                _ => "this connection",
            };
            return $"{rate} · measured {Ago(probe.MeasuredAt)} on {transport}";
        }
        if (state?.PlaybackSample is { } sample)
        {
            return $"Faster than {ConnectionRate(sample.Kbps)} · seen during playback {Ago(sample.MeasuredAt)}";
        }
        return "Not measured yet";
    }

    // "just now", "3 minutes ago", "2 hours ago", "4 days ago".
    private string Ago(DateTimeOffset at)
    {
        int minutes = Math.Max(0, (int)(_time.GetUtcNow() - at).TotalMinutes);
        if (minutes < 1)
        {
            return "just now";
        }
        var (count, unit) = minutes < 60 ? (minutes, "minute") : minutes < 24 * 60 ? (minutes / 60, "hour") : (minutes / (24 * 60), "day");
        return string.Create(CultureInfo.InvariantCulture, $"{count} {unit}{(count == 1 ? "" : "s")} ago");
    }

    // Rounded down, as every Silo client shows a speed or a limit: one decimal below 10 Mbps, whole Mbps above.
    internal static string ConnectionRate(long kbps)
    {
        long value = Math.Max(0, kbps);
        return value < 1000 ? string.Create(CultureInfo.InvariantCulture, $"{value} kbps")
            : value < 10_000 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(value / 100.0) / 10:0.0} Mbps")
            : string.Create(CultureInfo.InvariantCulture, $"{value / 1000} Mbps");
    }

    partial void OnSelectedQualityChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            QualityDescription = value.Value == "auto" ? AutoQualityDescription : QualityDescriptionText;
            Save(SettingKeys.PreferredQuality, value, () => SelectedQuality, v => SelectedQuality = v, SettingValues.Json(value.Value),
                "Failed to save preferred quality");
        }
    }

    // "No limit" clears the rows rather than storing a number, so no cap stays the absence of a value everywhere.
    partial void OnSelectedBitrateChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Save(SettingKeys.MaxBitrateKbps, value, () => SelectedBitrate, v => SelectedBitrate = v,
                value.Value.Length == 0 ? null : SettingValues.Json(int.Parse(value.Value, CultureInfo.InvariantCulture)), "Failed to save maximum bitrate");
        }
    }

    // Never ignores the learned limit, so the limit and Auto's budget follow the choice, also when it goes back.
    partial void OnSelectedBufferingChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Save(SettingKeys.BufferingQualityMode, value, () => SelectedBuffering, v => SelectedBuffering = v, SettingValues.Json(value.Value));
        }
        if (!_applying)
        {
            ShowConnection();
        }
    }

    partial void OnSelectedIntroSkipChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Save(SettingKeys.IntroSkipMode, value, () => SelectedIntroSkip, v => SelectedIntroSkip = v, SettingValues.Json(value.Value));
        }
    }

    partial void OnAutoSkipRecapsChanged(bool value) =>
        Save(SettingKeys.AutoSkipRecap, value, () => AutoSkipRecaps, v => AutoSkipRecaps = v, SettingValues.Json(value));

    partial void OnAutoSkipCreditsChanged(bool value) =>
        Save(SettingKeys.AutoSkipCredits, value, () => AutoSkipCredits, v => AutoSkipCredits = v, SettingValues.Json(value));

    partial void OnStartNextAtPreviewChanged(bool value) =>
        Save(SettingKeys.AutoPlayNextPreview, value, () => StartNextAtPreview, v => StartNextAtPreview = v, SettingValues.Json(value));

    partial void OnAutoPlayNextChanged(bool value) =>
        Save(SettingKeys.AutoPlayNext, value, () => AutoPlayNext, v => AutoPlayNext = v, SettingValues.Json(value));

    partial void OnSelectedRewindChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Save(SettingKeys.SkipBackSeconds, value, () => SelectedRewind, v => SelectedRewind = v,
                SettingValues.Json(int.Parse(value.Value, CultureInfo.InvariantCulture)), "Failed to save rewind interval for video");
        }
    }

    partial void OnSelectedFastForwardChanged(SettingChoice? value)
    {
        if (value is not null)
        {
            Save(SettingKeys.SkipForwardSeconds, value, () => SelectedFastForward, v => SelectedFastForward = v,
                SettingValues.Json(int.Parse(value.Value, CultureInfo.InvariantCulture)), "Failed to save fast-forward interval for video");
        }
    }

    // Saved after the saves before it; a refusal puts the control back to what the server holds, unless the viewer
    // has moved it on since. A null value is a reset.
    private void Save<T>(string key, T value, Func<T> shown, Action<T> show, JsonElement? json, string failure = "Failed to save playback setting")
    {
        if (_applying)
        {
            return;
        }
        bool clearDevice = _sources.GetValueOrDefault(key) == SettingScopes.ProfileDevice;
        ErrorMessage = "";
        _ = _saves.RunAsync(
            () => json is { } stored ? _settings.SaveAsync(key, stored, clearDevice, CancellationToken.None) : _settings.ResetAsync(key, CancellationToken.None),
            saved =>
            {
                if (saved)
                {
                    _confirmed[key] = value;
                    _sources[key] = SettingScopes.Profile;
                    return;
                }
                ErrorMessage = failure;
                if (EqualityComparer<T>.Default.Equals(shown(), value) && _confirmed.GetValueOrDefault(key) is T confirmed)
                {
                    _applying = true;
                    try
                    {
                        show(confirmed);
                        QualityDescription = SelectedQuality?.Value is null or "auto" ? AutoQualityDescription : QualityDescriptionText;
                        ShowConnection();
                    }
                    finally
                    {
                        _applying = false;
                    }
                }
            });
    }
}
