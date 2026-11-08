using System.Globalization;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>
/// This device's audio delay (<c>player.audio_sync_ms</c>, docs/api-contract.md 6.6): the sound this many milliseconds
/// after the picture, negative before it, for Bluetooth headphones or an AV receiver that lag behind. As Silo's Android
/// app keeps it (AndroidPlayerSettingsStore): the profile's value on this device only, within ±5 s, 0 by default.
/// Settings and the player share this one: a change reaches what plays at once, as Android's SiloPlaybackService hands
/// it to its DelayAudioProcessor, and is saved for this device once the changes settle.
/// </summary>
/// <remarks>Used on the UI thread; <see cref="Changed"/> is raised there.</remarks>
public sealed class AudioDelay(SettingsApi settings, ISessionManager session, IUiDispatcher dispatcher, TimeProvider time, ILogger<AudioDelay> logger)
{
    public const string Key = "player.audio_sync_ms";

    /// <summary>The contract's limit either way, which Android's DelayAudioProcessor keeps to as well.</summary>
    public const int MaxMs = 5000;

    // A drag along the slider or a run of steps passes several values: the delay is saved once it settles.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(10);

    private ITimer? _settle;

    // A change made here that the server does not have yet, with the server entry and profile it was made for.
    private (string Server, string? Profile)? _unsaved;
    private Task _saving = Task.CompletedTask;

    // Counts the viewer's changes, so a read that answers after one does not put the value from before it back.
    private long _changes;

    /// <summary>The delay in milliseconds, positive for later sound; 0 until a value is read or set.</summary>
    public int Milliseconds { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Milliseconds"/> changed, for what plays and what shows it to follow.</summary>
    public event EventHandler? Changed;

    /// <summary>A stored value within the contract's range, in whole milliseconds; 0, the default, for anything but a number.</summary>
    public static int FromSetting(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double ms) && double.IsFinite(ms)
            ? (int)Math.Clamp(Math.Round(ms), -MaxMs, MaxMs)
            : 0;

    /// <summary>
    /// Reads this device's value from the server and takes it (<see cref="Take"/>), unless the viewer changed the delay or
    /// another profile took over while it was read.
    /// </summary>
    public async Task ReadAsync(CancellationToken cancellationToken)
    {
        var server = session.ActiveServer?.BaseUri ?? throw new InvalidOperationException("No server is signed in.");
        var owner = Owner;
        long changes = _changes;
        var values = await settings.GetEffectiveAsync(server, [Key], cancellationToken);
        if (Owner == owner && _changes == changes)
        {
            Take(FromSetting(values.Find(Key)));
        }
    }

    /// <summary>
    /// This device's value as the server holds it, read for a title or by Settings. A change made here that the server
    /// does not have yet is newer and stays, also one whose save failed: that one holds for the rest of the run, or until
    /// a later save goes through.
    /// </summary>
    public void Take(int milliseconds)
    {
        if (_unsaved is { } owner && owner == Owner)
        {
            return;
        }
        _unsaved = null;
        Show(Math.Clamp(milliseconds, -MaxMs, MaxMs));
    }

    /// <summary>The viewer's delay: what plays follows at once, and it is saved for this device once the changes settle.</summary>
    public void Set(int milliseconds)
    {
        int delay = Math.Clamp(milliseconds, -MaxMs, MaxMs);
        if (delay == Milliseconds)
        {
            return;
        }
        _unsaved = Owner;
        _changes++;
        Show(delay);
        _settle?.Dispose();
        _settle = time.CreateTimer(_ => dispatcher.Post(() => _ = SaveNowAsync()), null, Settle, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Saves a change at once instead of when it settles, as the app closes. Never fails: done once the latest change is
    /// saved, or its save failed and was logged; the choice then holds for this run, and the next change tries again.
    /// </summary>
    public Task SaveNowAsync()
    {
        _settle?.Dispose();
        _settle = null;
        if (_unsaved is not { } owner)
        {
            return _saving;
        }
        if (owner != Owner || session.ActiveServer is not { } server)
        {
            // Another server or profile acts by now: the change was the other one's.
            logger.LogInformation("The audio delay was not saved: the profile changed before it went out");
            _unsaved = null;
            return _saving;
        }
        return _saving = SaveAsync(_saving, owner, server.BaseUri, Milliseconds);
    }

    private (string Server, string? Profile)? Owner => session.ActiveServer is { } server ? (server.Id, session.ProfileId) : null;

    private void Show(int milliseconds)
    {
        if (milliseconds == Milliseconds)
        {
            return;
        }
        Milliseconds = milliseconds;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // After the save before it, so the latest value is the one the server keeps; not at all once another profile acts.
    private async Task SaveAsync(Task previous, (string Server, string? Profile) owner, Uri server, int delay)
    {
        await previous;
        if (Owner != owner)
        {
            logger.LogInformation("The audio delay was not saved: the profile changed before it went out");
            if (_unsaved == owner)
            {
                _unsaved = null;
            }
            return;
        }
        using var timeout = new CancellationTokenSource(SaveTimeout, time);
        using var document = JsonDocument.Parse(delay.ToString(CultureInfo.InvariantCulture));
        try
        {
            await settings.SetValueAsync(server, Key, document.RootElement, SettingScopes.ProfileDevice, timeout.Token);
            if (Milliseconds == delay && _unsaved == owner)
            {
                _unsaved = null;
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogWarning("Saving the audio delay failed: {Failure}", ApiFailureText.Describe(ex));
        }
        catch (Exception ex)
        {
            // A preference, not playback: whatever went wrong, the delay holds for this run.
            logger.LogError(ex, "Saving the audio delay failed unexpectedly");
        }
    }
}
