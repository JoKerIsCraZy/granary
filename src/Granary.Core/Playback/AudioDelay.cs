using System.Globalization;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api;
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
/// <remarks>
/// Used on the UI thread; <see cref="Changed"/> is raised there. A change belongs to the server and profile acting when it
/// was made, and is saved to that server as that profile only (<see cref="SiloRequestOptions.Profile"/>), also when another
/// one acts by the time it goes out: a server switched to keeps the one before signed in, so its change still arrives,
/// while a change for a profile its server no longer acts as is refused unsent. Switching profile saves a waiting change
/// first (<see cref="SidebarViewModel"/>).
/// </remarks>
public sealed class AudioDelay(SettingsApi settings, ISessionManager session, IUiDispatcher dispatcher, TimeProvider time, ILogger<AudioDelay> logger)
{
    public const string Key = "player.audio_sync_ms";

    /// <summary>The contract's limit either way, which Android's DelayAudioProcessor keeps to as well.</summary>
    public const int MaxMs = 5000;

    // A drag along the slider or a run of steps passes several values: the delay is saved once it settles.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(10);

    private ITimer? _settle;

    // The viewer's changes so far; a timer, a read or a save made for an earlier one does not act on a later one.
    private long _changes;

    // The change made here that its server does not have yet; null once it is saved, or cannot be.
    private Pending? _unsaved;

    // The change the latest save started for carries, so it is not sent twice; -1 after a failure, to try again.
    private long _sending = -1;

    // Each profile's saves, one after another, so the latest value is the one its server keeps. Another profile's never
    // waits for them: a save that hangs on one server does not hold back the profile acting now.
    private readonly Dictionary<Owner, Task> _chains = [];
    private Task _latest = Task.CompletedTask;

    // Whose value Milliseconds is: the profile it was read or changed for; null when none was acting.
    private Owner? _shownFor;

    /// <summary>The delay in milliseconds, positive for later sound; 0 until a value is read or set.</summary>
    public int Milliseconds { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Milliseconds"/> changed, for what plays and what shows it to follow.</summary>
    public event EventHandler? Changed;

    /// <summary>The latest save: done once it went through, or failed and was logged. Waiting for it starts none.</summary>
    public Task LatestSave => _latest;

    /// <summary>A stored value within the contract's range, in whole milliseconds; 0, the default, for anything but a number.</summary>
    public static int FromSetting(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double ms) && double.IsFinite(ms)
            ? (int)Math.Clamp(Math.Round(ms), -MaxMs, MaxMs)
            : 0;

    /// <summary>
    /// Reads this device's value from the server and takes it (<see cref="Take"/>), unless the viewer changed the delay or
    /// another profile took over while it was read. A server that does not know the setting refuses it (422).
    /// </summary>
    public async Task ReadAsync(CancellationToken cancellationToken)
    {
        var owner = Acting() ?? throw new InvalidOperationException("No profile is signed in.");
        long changes = _changes;
        var values = await settings.GetEffectiveAsync(owner.Server, [Key], cancellationToken);
        if (Acting() == owner && _changes == changes)
        {
            Take(FromSetting(values.Find(Key)));
        }
    }

    /// <summary>
    /// This device's value as the server holds it, read for a title or by Settings. A change made here for the same
    /// profile that the server does not have yet is newer and stays, also one whose save failed: that one holds for the
    /// rest of the run, or until a later save goes through.
    /// </summary>
    public void Take(int milliseconds)
    {
        if (_unsaved is { } pending && pending.Owner == Acting())
        {
            return;
        }
        _shownFor = Acting();
        Show(Math.Clamp(milliseconds, -MaxMs, MaxMs));
    }

    /// <summary>
    /// This device's value could not be read for a title: the one known stays when it is the acting profile's on this
    /// server, else the title plays with none rather than with another profile's.
    /// </summary>
    public void TakeUnread()
    {
        var acting = Acting();
        if (_shownFor == acting)
        {
            return;
        }
        _shownFor = acting;
        Show(0);
    }

    /// <summary>The viewer's delay: what plays follows at once, and it is saved for this device once the changes settle.</summary>
    public void Set(int milliseconds)
    {
        int delay = Math.Clamp(milliseconds, -MaxMs, MaxMs);
        if (delay == Milliseconds)
        {
            return;
        }
        if (_unsaved is { } other && other.Owner != Acting())
        {
            // A change another profile left waiting goes out now, before this one takes its place.
            _ = SaveNowAsync();
        }
        long change = ++_changes;
        _shownFor = Acting();
        _unsaved = _shownFor is { } owner ? new Pending(owner, delay, change) : null;
        Show(delay);
        _settle?.Dispose();
        _settle = time.CreateTimer(_ => dispatcher.Post(() =>
        {
            // A timer that was due as a later change came is that change's no longer.
            if (change == _changes)
            {
                _ = SaveNowAsync();
            }
        }), null, Settle, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Saves a waiting change at once instead of when it settles: the app closes, or the profile is about to change.
    /// Never fails: done once the waiting change is saved, or its save failed and was logged; with none waiting, once the
    /// acting profile's saves are.
    /// </summary>
    public Task SaveNowAsync()
    {
        _settle?.Dispose();
        _settle = null;
        if (_unsaved is not { } pending)
        {
            return Acting() is { } acting && _chains.TryGetValue(acting, out var saving) ? saving : Task.CompletedTask;
        }
        if (pending.Change != _sending)
        {
            _sending = pending.Change;
            var before = _chains.GetValueOrDefault(pending.Owner) ?? Task.CompletedTask;
            _latest = _chains[pending.Owner] = SaveAsync(before, pending);
        }
        return _chains[pending.Owner];
    }

    private Owner? Acting() =>
        session.ActiveServer is { } server && session.ProfileId is { Length: > 0 } profile ? new Owner(server.Id, server.BaseUri, profile) : null;

    private void Show(int milliseconds)
    {
        if (milliseconds == Milliseconds)
        {
            return;
        }
        Milliseconds = milliseconds;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // After the profile's save before it, so the latest value is the one its server keeps.
    private async Task SaveAsync(Task previous, Pending pending)
    {
        await previous;
        using var timeout = new CancellationTokenSource(SaveTimeout, time);
        using var document = JsonDocument.Parse(pending.Milliseconds.ToString(CultureInfo.InvariantCulture));
        try
        {
            await settings.SetValueAsProfileAsync(pending.Owner.Server, Key, document.RootElement, SettingScopes.ProfileDevice,
                pending.Owner.Profile, timeout.Token);
            Settled(pending);
        }
        catch (ProfileChangedException)
        {
            // Its server acts as another profile by now: the change was the one before's, and is not sent as this one's.
            logger.LogInformation("The audio delay was not saved: the profile changed before it went out");
            Settled(pending);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            logger.LogWarning("Saving the audio delay failed: {Failure}", ApiFailureText.Describe(ex));
            Failed(pending);
        }
        catch (Exception ex)
        {
            // A preference, not playback: whatever went wrong, the delay holds for this run.
            logger.LogError(ex, "Saving the audio delay failed unexpectedly");
            Failed(pending);
        }
    }

    private void Settled(Pending pending)
    {
        if (_unsaved == pending)
        {
            _unsaved = null;
        }
    }

    // The change stays this run's; the next save, a later change's or the close's, tries again.
    private void Failed(Pending pending)
    {
        if (_sending == pending.Change)
        {
            _sending = -1;
        }
    }

    // Whose a change is: the server it goes to, and the profile it is saved as.
    private sealed record Owner(string ServerId, Uri Server, string Profile);

    private sealed record Pending(Owner Owner, int Milliseconds, long Change);
}
