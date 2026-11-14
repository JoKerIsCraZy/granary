using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Remote;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback → This PC → "Control Granary from your phone": the switch, kept on this PC, what the receiver does,
/// and the Connect a Phone panel with the code a phone scans. Silo's web has no wording for this; the texts follow Silo's
/// Roku app's Connect a Phone (silo-roku's <c>locale/en_US/strings.json</c>, <c>connect.*</c>) with "TV" as "PC", and the
/// ones marked new have no Silo source. The code holds the pairing key: it is read only while the panel is open and let
/// go when the panel closes. The row follows the receiver between <see cref="Attach"/> and <see cref="Detach"/> only, on
/// the UI thread.
/// </summary>
public sealed partial class PhoneRemoteViewModel : ObservableObject
{
    /// <summary>The switch: the Roku app's <c>connect.headline</c> "Control Silo from your phone", with Granary as what is controlled.</summary>
    public const string SwitchLabel = "Control Granary from your phone";

    /// <summary>Under the switch. New.</summary>
    public const string DescriptionText =
        "Silo's phone app on this network can control what plays on this PC and start titles here. Windows may ask to let Granary through its firewall.";

    /// <summary>Under the description: what the network sees while it is on. New.</summary>
    public const string VisibilityText = "While this is on, devices on this network can see this PC's name and your server's address.";

    /// <summary>The button and the panel's title: the Roku app's screen, which the phones' messages name.</summary>
    public const string ConnectAPhoneText = "Connect a Phone";

    /// <summary>The panel's close button. New.</summary>
    public const string DoneText = "Done";

    /// <summary>The Roku app's <c>connect.none</c>.</summary>
    public const string NoPhoneText = "No phone is connected.";

    /// <summary>The Roku app's <c>connect.phones</c>, with the names of the phones that actively control.</summary>
    public const string ConnectedFormat = "Connected: {0}";

    /// <summary>The Roku app's <c>connect.notListening</c>; <see cref="ReasonText"/> says why.</summary>
    public const string NotAvailableText = "Remote control isn't available right now.";

    /// <summary>Why: no private IPv4 address. New.</summary>
    public const string NoLocalNetworkText = "This PC is not on a local network.";

    /// <summary>Why: no port could be bound. New.</summary>
    public const string NoPortText = "Windows did not give Granary a port.";

    /// <summary>Why: no profile, a moment before the profile picker or after sign-out. New.</summary>
    public const string NotSignedInText = "No profile is signed in on this PC.";

    /// <summary>Why: a step of the start failed (the key could not be read, Windows' advertisement threw). New, worded as the player's mpv.conf lines are.</summary>
    public const string FailedText = "Granary couldn't start it. The log has the details.";

    /// <summary>The panel's instruction, with this PC's name. New: the Roku app's "Scan this code in the Silo app" would mislead, as the Android app has no scanner of its own.</summary>
    public const string ScanFormat = "Scan this code with your phone's camera and open it in Silo. Then choose {0} under Remote Control.";

    /// <summary>The panel's hint, with the server's address as the viewer typed it. New.</summary>
    public const string ServerHintFormat = "The phone has to be on this network and signed in to {0}.";

    /// <summary>The Roku app's <c>connect.resetPairing</c>.</summary>
    public const string ResetPairingText = "Reset Pairing Code";

    /// <summary>Under Reset Pairing Code. New.</summary>
    public const string ResetLineText = "Every phone has to scan the new code.";

    /// <summary>The panel's firewall hint, always shown. New; the Roku app's <c>connect.fallback</c> starts the same way.</summary>
    public const string FirewallText =
        "Phone can't connect? Allow Granary under Windows Security → Firewall & network protection → Allow an app through firewall.";

    /// <summary>Shown when Windows did not take the advertisement. New.</summary>
    public const string NotAdvertisedText = "Phones can connect through this code only: Windows did not advertise this PC.";

    /// <summary>The question before a reset. New.</summary>
    public const string ResetQuestionTitle = "Reset Pairing Code?";

    /// <summary>New.</summary>
    public const string ResetQuestionText = "Every phone has to scan the new code. Phones that are connected now are disconnected.";

    /// <summary>New.</summary>
    public const string ResetAction = "Reset";

    /// <summary>After a reset that could not let a phone go (<see cref="PairingResetOutcome.PhonesNotDropped"/>). New.</summary>
    public const string PhonesNotDroppedText =
        "The new code is in place, but a phone could not be disconnected. It can no longer control this PC.";

    /// <summary>After a reset whose new key could not be saved (<see cref="PairingResetOutcome.Failed"/>). New.</summary>
    public const string ResetFailedText = "Couldn't reset the pairing code. The old code still works.";

    private readonly RemoteReceiver _receiver;
    private readonly IPlaybackDevicePreferences _preferences;
    private readonly IConfirmation _confirmation;
    private readonly IUiDispatcher _dispatcher;
    private bool _attached;

    public PhoneRemoteViewModel(RemoteReceiver receiver, IPlaybackDevicePreferences preferences, IConfirmation confirmation,
        IUiDispatcher dispatcher, ClientIdentity client)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(client);
        _receiver = receiver;
        _preferences = preferences;
        _confirmation = confirmation;
        _dispatcher = dispatcher;
        ScanText = string.Format(CultureInfo.InvariantCulture, ScanFormat, client.DeviceName);
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

    /// <summary>How the last Reset Pairing Code went when it did not go as planned; empty otherwise.</summary>
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

    // Asked first: every phone has to scan again. The code shown changes once the receiver runs under the new key.
    [RelayCommand]
    private async Task ResetPairingAsync()
    {
        if (!await _confirmation.AskAsync(ResetQuestionTitle, ResetQuestionText, ResetAction))
        {
            return;
        }
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
        PhonesText = names.Count == 0 ? NoPhoneText : string.Format(CultureInfo.InvariantCulture, ConnectedFormat, string.Join(", ", names));
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
        ServerHint = status.ServerAddress is { Length: > 0 } server ? string.Format(CultureInfo.InvariantCulture, ServerHintFormat, server) : "";
        ShowsNotAdvertised = listening && !status.IsAdvertised;
        if (IsPanelOpen)
        {
            PairingLink = _receiver.PairingLink;
        }
    }
}
