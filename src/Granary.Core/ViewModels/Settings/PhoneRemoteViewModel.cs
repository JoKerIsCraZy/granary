using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Remote;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback → This PC → "Control Granary from your phone": the switch, kept on this PC, what the receiver does,
/// and the Connect a Phone panel with the code a phone scans. Silo's web has no wording for this; the texts follow Silo's
/// Roku app's Connect a Phone (silo-roku's <c>locale/en_US/strings.json</c>, <c>connect.*</c>) with "TV" as "PC", and the
/// ones marked new have no Silo source. They are Granary's own keys (<c>granary.settings.phoneRemote.*</c>), translated in
/// the terms of Silo's catalogs, with the names Silo's phone apps give Connect a Phone and Remote Control. The code holds the pairing key: it is read only while the panel is open and let
/// go when the panel closes, or when the receiver stops listening. The row follows the receiver between
/// <see cref="Attach"/> and <see cref="Detach"/> only, on the UI thread; <see cref="Dispose"/> detaches it too, as a second
/// net behind the page's own Detach.
/// </summary>
public sealed partial class PhoneRemoteViewModel : ObservableObject, IDisposable
{
    /// <summary>The switch: the Roku app's <c>connect.headline</c> "Control Silo from your phone", with Granary as what is controlled.</summary>
    public static string SwitchLabel => AppText.Get("granary.settings.phoneRemote.label");

    /// <summary>Under the switch: what a phone can do here, and the firewall's question beforehand. New (design 5.8).</summary>
    public static string DescriptionText => AppText.Get("granary.settings.phoneRemote.description");

    /// <summary>Under the description: what the network sees while it is on. New.</summary>
    public static string VisibilityText => AppText.Get("granary.settings.phoneRemote.visibility");

    /// <summary>The row's description: what a phone can do, then what the network sees, on a line of its own.</summary>
    public static string RowDescription => DescriptionText + "\n" + VisibilityText;

    /// <summary>The button and the panel's title: the Roku app's screen, which the phones' messages name.</summary>
    public static string ConnectAPhoneText => AppText.Get("granary.settings.phoneRemote.connectAPhone");

    /// <summary>The panel's close button.</summary>
    public static string DoneText => AppText.Get("common.done");

    /// <summary>The Roku app's <c>connect.none</c>.</summary>
    public static string NoPhoneText => AppText.Get("granary.settings.phoneRemote.noPhone");

    /// <summary>The Roku app's <c>connect.notListening</c>; <see cref="ReasonText"/> says why.</summary>
    public static string NotAvailableText => AppText.Get("granary.settings.phoneRemote.notAvailable");

    /// <summary>Why: no private IPv4 address. New.</summary>
    public static string NoLocalNetworkText => AppText.Get("granary.settings.phoneRemote.noLocalNetwork");

    /// <summary>Why: no port could be bound. New.</summary>
    public static string NoPortText => AppText.Get("granary.settings.phoneRemote.noPort");

    /// <summary>Why: no profile, a moment before the profile picker or after sign-out. New.</summary>
    public static string NotSignedInText => AppText.Get("granary.settings.phoneRemote.notSignedIn");

    /// <summary>Why: a step of the start failed (the key could not be read, Windows' advertisement threw). New, worded as the player's mpv.conf lines are.</summary>
    public static string FailedText => AppText.Get("granary.settings.phoneRemote.failed");

    /// <summary>The Roku app's <c>connect.resetPairing</c>, in the web's sentence case.</summary>
    public static string ResetPairingText => AppText.Get("granary.settings.phoneRemote.resetPairing");

    /// <summary>Under Reset pairing code. New.</summary>
    public static string ResetLineText => AppText.Get("granary.settings.phoneRemote.resetLine");

    /// <summary>The panel's firewall hint, always shown. New; the Roku app's <c>connect.fallback</c> starts the same way.</summary>
    public static string FirewallText => AppText.Get("granary.settings.phoneRemote.firewall");

    /// <summary>Shown when Windows did not take the advertisement. New.</summary>
    public static string NotAdvertisedText => AppText.Get("granary.settings.phoneRemote.notAdvertised");

    /// <summary>The question before a reset. New.</summary>
    public static string ResetQuestionTitle => AppText.Get("granary.settings.phoneRemote.resetQuestion");

    /// <summary>New.</summary>
    public static string ResetQuestionText => AppText.Get("granary.settings.phoneRemote.resetQuestionText");

    /// <summary>The answer that resets: the web's Reset of its settings.</summary>
    public static string ResetAction => AppText.Get("web.settings.resetButton.label");

    /// <summary>After a reset that could not let a phone go (<see cref="PairingResetOutcome.PhonesNotDropped"/>). New.</summary>
    public static string PhonesNotDroppedText => AppText.Get("granary.settings.phoneRemote.phonesNotDropped");

    /// <summary>After a reset whose new key could not be saved (<see cref="PairingResetOutcome.Failed"/>). New.</summary>
    public static string ResetFailedText => AppText.Get("granary.settings.phoneRemote.resetFailed");

    private readonly RemoteReceiver _receiver;
    private readonly IPlaybackDevicePreferences _preferences;
    private readonly IUiDispatcher _dispatcher;
    private bool _attached;

    public PhoneRemoteViewModel(RemoteReceiver receiver, IPlaybackDevicePreferences preferences, IUiDispatcher dispatcher, ClientIdentity client)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(client);
        _receiver = receiver;
        _preferences = preferences;
        _dispatcher = dispatcher;
        // New: the Roku app's "Scan this code in the Silo app" would mislead, as the Android app has no scanner of its own.
        ScanText = AppText.Get("granary.settings.phoneRemote.scan", ("name", client.DeviceName));
        IsOn = preferences.PhoneRemote;
    }

    /// <summary>The switch (<see cref="IPlaybackDevicePreferences.PhoneRemote"/>), off by default.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; }

    /// <summary>The row's line: the connected phones while it listens, that it is not available, or nothing while it is off.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>Why it is not available; empty otherwise.</summary>
    [ObservableProperty]
    public partial string ReasonText { get; private set; } = "";

    /// <summary>It listens, so Connect a Phone shows.</summary>
    [ObservableProperty]
    public partial bool CanConnectAPhone { get; private set; }

    [ObservableProperty]
    public partial bool IsPanelOpen { get; private set; }

    /// <summary>The link the code shows, with the key; only while the panel is open, null otherwise. Never logged, saved or copied.</summary>
    [ObservableProperty]
    public partial string? PairingLink { get; private set; }

    /// <summary>"192.168.1.20:52817" while it listens.</summary>
    [ObservableProperty]
    public partial string AddressText { get; private set; } = "";

    /// <summary>The phone must be signed in to the same server address.</summary>
    [ObservableProperty]
    public partial string ServerHint { get; private set; } = "";

    /// <summary>The phones that actively control, or that none does.</summary>
    [ObservableProperty]
    public partial string PhonesText { get; private set; } = NoPhoneText;

    /// <summary>Windows did not advertise this PC: phones find it through the code only.</summary>
    [ObservableProperty]
    public partial bool ShowsNotAdvertised { get; private set; }

    /// <summary>How the last Reset pairing code went when it did not go as planned; empty otherwise.</summary>
    [ObservableProperty]
    public partial string ResetMessage { get; private set; } = "";

    /// <summary>The panel's instruction, with this PC's name.</summary>
    public string ScanText { get; }

    /// <summary>The settings page is shown: the row follows the receiver from now on. On the UI thread.</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }
        _attached = true;
        _receiver.StatusChanged += OnReceiverChanged;
        _receiver.PhonesChanged += OnReceiverChanged;
        IsOn = _preferences.PhoneRemote;
        Show();
    }

    /// <summary>The settings page is gone: the row follows nothing, and an open panel lets the code go. On the UI thread.</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }
        _attached = false;
        _receiver.StatusChanged -= OnReceiverChanged;
        _receiver.PhonesChanged -= OnReceiverChanged;
        ClosePanel();
    }

    /// <summary>The page that showed the row is gone: as <see cref="Detach"/>. On the UI thread.</summary>
    public void Dispose() => Detach();

    /// <summary>
    /// The phones that actively control, as the Roku app's <c>connect.phones</c> lists them, in a list as the language
    /// writes one ("Connected: Elena's phone and Elena's tablet"); its <c>connect.none</c> without one.
    /// </summary>
    internal static string PhonesLine(IReadOnlyList<string> names) => names.Count == 0
        ? NoPhoneText
        : AppText.Get("granary.settings.phoneRemote.connected", ("names", TextList.And(names)));

    /// <summary>What a reset's outcome says; empty when it went as planned.</summary>
    internal static string ResetText(PairingResetOutcome outcome) => outcome switch
    {
        PairingResetOutcome.PhonesNotDropped => PhonesNotDroppedText,
        PairingResetOutcome.Failed => ResetFailedText,
        _ => "",
    };

    partial void OnIsOnChanged(bool value)
    {
        if (!value)
        {
            ClosePanel();
        }
        if (value != _preferences.PhoneRemote)
        {
            // Never fails: a save that does not work is logged, and the choice holds for this run. The receiver follows it.
            _ = _preferences.SetPhoneRemoteAsync(value);
        }
    }

    [RelayCommand]
    private void OpenPanel()
    {
        if (!CanConnectAPhone)
        {
            return;
        }
        ResetMessage = "";
        IsPanelOpen = true;
        Show();
    }

    [RelayCommand]
    private void ClosePanel()
    {
        IsPanelOpen = false;
        PairingLink = null;
        ResetMessage = "";
    }

    /// <summary>
    /// Reset Pairing Code once the viewer said yes where the question was asked: every phone has to scan again, so the
    /// Connect a Phone panel asks first itself, with <see cref="ResetQuestionTitle"/>, <see cref="ResetQuestionText"/> and
    /// <see cref="ResetAction"/>, in a flyout, since no second dialog can open over the panel's. The code shown changes once
    /// the receiver runs under the new key. On the UI thread.
    /// </summary>
    public async Task ResetPairingAnsweredAsync()
    {
        var outcome = await _receiver.ResetPairingAsync();
        OnUiThread(() =>
        {
            ResetMessage = ResetText(outcome);
            if (_attached)
            {
                Show();
            }
        });
    }

    // The receiver raises on the UI thread; a change that comes after Detach is not followed.
    private void OnReceiverChanged(object? sender, EventArgs e) => OnUiThread(() =>
    {
        if (_attached)
        {
            Show();
        }
    });

    private void OnUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    private void Show()
    {
        var status = _receiver.Status;
        bool listening = status.State == RemoteReceiverState.Listening;
        // As silo-roku's AppSceneControl.bs lists them: the phones that are authorized and not passive.
        var names = _receiver.Phones.Where(phone => phone.IsActive).Select(phone => phone.Name).ToList();
        PhonesText = PhonesLine(names);
        StatusText = status.State switch
        {
            RemoteReceiverState.Off => "",
            RemoteReceiverState.Listening => PhonesText,
            _ => NotAvailableText,
        };
        ReasonText = status.State switch
        {
            RemoteReceiverState.NoLocalNetwork => NoLocalNetworkText,
            RemoteReceiverState.NoPort => NoPortText,
            RemoteReceiverState.NotSignedIn => NotSignedInText,
            RemoteReceiverState.Failed => FailedText,
            _ => "",
        };
        CanConnectAPhone = listening;
        AddressText = listening && status.Address is { } address
            ? string.Create(CultureInfo.InvariantCulture, $"{address}:{status.Port}")
            : "";
        ServerHint = status.ServerAddress is { Length: > 0 } server
            ? AppText.Get("granary.settings.phoneRemote.serverHint", ("server", server))
            : "";
        ShowsNotAdvertised = listening && !status.IsAdvertised;
        if (!IsPanelOpen)
        {
            return;
        }
        // A receiver that stopped listening closes the panel: the code must not come back by itself when it listens again.
        if (listening)
        {
            PairingLink = _receiver.PairingLink;
        }
        else
        {
            ClosePanel();
        }
    }
}
