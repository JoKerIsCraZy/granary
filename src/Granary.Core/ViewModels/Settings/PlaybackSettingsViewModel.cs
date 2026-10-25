using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback, as Silo's web has it (pages/settings/PlaybackSettings.tsx): quality, this connection, skipping,
/// the next episode and the seek intervals. Every choice is the profile's and is saved at once; a value this device
/// keeps for itself is cleared with it, so the control shows what was saved. A save the server refuses puts the
/// control back and says so.
/// </summary>
public sealed partial class PlaybackSettingsViewModel(
    IProfileSettings settings, PlaybackApi playback, PlaybackConnectionApi connection, ISessionManager session, TimeProvider time,
    ILogger<PlaybackSettingsViewModel> logger) : ObservableObject
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

    private readonly Dictionary<string, string?> _sources = [];
    private bool _applying;

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

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            var values = await settings.ReadAsync(Keys, cancellationToken);
            Apply(values);
            await LoadConnectionAsync(cancellationToken);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogInformation("The playback settings could not be read: {Failure}", ApiFailureText.Describe(ex));
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
        if (session.ActiveServer?.BaseUri is not { } server)
        {
            return;
        }
        try
        {
            await connection.ResetLearnedLimitAsync(server);
            _connection = await connection.GetAsync(server);
            ShowConnection();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogInformation("The learned limit could not be reset: {Failure}", ApiFailureText.Describe(ex));
            ErrorMessage = "Couldn't reset the limit. Try again.";
        }
    }

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
        if (session.ActiveServer?.BaseUri is not { } server)
        {
            return;
        }
        try
        {
            var capabilities = await playback.GetCapabilitiesAsync(server, cancellationToken);
            bool learns = capabilities.HasFeature("buffering_quality_v1");
            ShowsConnection = capabilities.HasFeature("connection_speed_v1");
            _connection = learns ? await connection.GetAsync(server, cancellationToken) : null;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            // The rest of the page does not need it.
            logger.LogInformation("This connection's playback state could not be read: {Failure}", ApiFailureText.Describe(ex));
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
        ConnectionSpeedText = SpeedText(_connection);
        long? budget = ignoresLimit && limit is not null ? null : _connection?.AutoBudgetKbps;
        BudgetText = budget is { } kbps ? $"Auto plays videos up to {ConnectionRate(kbps)} without converting them." : "";
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
        int minutes = Math.Max(0, (int)(time.GetUtcNow() - at).TotalMinutes);
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

    partial void OnSelectedQualityChanged(SettingChoice? oldValue, SettingChoice? newValue)
    {
        if (newValue is not null)
        {
            QualityDescription = newValue.Value == "auto" ? AutoQualityDescription : QualityDescriptionText;
        }
        Save(SettingKeys.PreferredQuality, newValue is null ? null : SettingValues.Json(newValue.Value), () => SelectedQuality = oldValue,
            "Failed to save preferred quality");
    }

    // "No limit" clears the rows rather than storing a number, so no cap stays the absence of a value everywhere.
    partial void OnSelectedBitrateChanged(SettingChoice? oldValue, SettingChoice? newValue)
    {
        if (_applying || newValue is null)
        {
            return;
        }
        if (newValue.Value.Length == 0)
        {
            _ = WriteAsync(SettingKeys.MaxBitrateKbps, () => settings.ResetAsync(SettingKeys.MaxBitrateKbps, CancellationToken.None),
                () => SelectedBitrate = oldValue, "Failed to save maximum bitrate");
            return;
        }
        Save(SettingKeys.MaxBitrateKbps, SettingValues.Json(int.Parse(newValue.Value, CultureInfo.InvariantCulture)), () => SelectedBitrate = oldValue,
            "Failed to save maximum bitrate");
    }

    partial void OnSelectedBufferingChanged(SettingChoice? oldValue, SettingChoice? newValue)
    {
        Save(SettingKeys.BufferingQualityMode, newValue is null ? null : SettingValues.Json(newValue.Value), () => SelectedBuffering = oldValue);
        if (!_applying)
        {
            ShowConnection();
        }
    }

    partial void OnSelectedIntroSkipChanged(SettingChoice? oldValue, SettingChoice? newValue) =>
        Save(SettingKeys.IntroSkipMode, newValue is null ? null : SettingValues.Json(newValue.Value), () => SelectedIntroSkip = oldValue);

    partial void OnAutoSkipRecapsChanged(bool value) => Save(SettingKeys.AutoSkipRecap, SettingValues.Json(value), () => AutoSkipRecaps = !value);

    partial void OnAutoSkipCreditsChanged(bool value) => Save(SettingKeys.AutoSkipCredits, SettingValues.Json(value), () => AutoSkipCredits = !value);

    partial void OnStartNextAtPreviewChanged(bool value) =>
        Save(SettingKeys.AutoPlayNextPreview, SettingValues.Json(value), () => StartNextAtPreview = !value);

    partial void OnAutoPlayNextChanged(bool value) => Save(SettingKeys.AutoPlayNext, SettingValues.Json(value), () => AutoPlayNext = !value);

    partial void OnSelectedRewindChanged(SettingChoice? oldValue, SettingChoice? newValue) =>
        Save(SettingKeys.SkipBackSeconds, newValue is null ? null : SettingValues.Json(int.Parse(newValue.Value, CultureInfo.InvariantCulture)),
            () => SelectedRewind = oldValue, "Failed to save rewind interval for video");

    partial void OnSelectedFastForwardChanged(SettingChoice? oldValue, SettingChoice? newValue) =>
        Save(SettingKeys.SkipForwardSeconds, newValue is null ? null : SettingValues.Json(int.Parse(newValue.Value, CultureInfo.InvariantCulture)),
            () => SelectedFastForward = oldValue, "Failed to save fast-forward interval for video");

    private void Save(string key, JsonElement? value, Action revert, string failure = "Failed to save playback setting")
    {
        if (_applying || value is not { } json)
        {
            return;
        }
        bool clearDevice = _sources.GetValueOrDefault(key) == SettingScopes.ProfileDevice;
        _ = WriteAsync(key, () => settings.SaveAsync(key, json, clearDevice, CancellationToken.None), revert, failure);
    }

    private async Task WriteAsync(string key, Func<Task> write, Action revert, string failure)
    {
        ErrorMessage = "";
        try
        {
            await write();
            _sources[key] = SettingScopes.Profile;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogInformation("Saving {Key} failed: {Failure}", key, ApiFailureText.Describe(ex));
            ErrorMessage = failure;
            _applying = true;
            try
            {
                revert();
            }
            finally
            {
                _applying = false;
            }
        }
    }
}
