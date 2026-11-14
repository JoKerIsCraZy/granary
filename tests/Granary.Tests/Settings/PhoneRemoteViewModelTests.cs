using Granary.Core.Remote;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;
using Granary.Tests.Remote;

namespace Granary.Tests.Settings;

/// <summary>
/// Settings → Playback → This PC → "Control Granary from your phone" and its Connect a Phone panel, over a receiver that
/// runs on fakes (<see cref="RemoteHarness"/>): what the row says, the code shown only while the panel is open, and Reset
/// Pairing Code.
/// </summary>
public sealed class PhoneRemoteViewModelTests : IAsyncDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly RemoteHarness _harness = new(lifetime: true);
    private readonly FakeConfirmation _confirmation = new();
    private readonly PhoneRemoteViewModel _viewModel;

    public PhoneRemoteViewModelTests()
    {
        _viewModel = new PhoneRemoteViewModel(_harness.Receiver, _harness.Preferences, _confirmation, _harness.Dispatcher, RemoteHarness.Client);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private RemoteReceiver Receiver => _harness.Receiver;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    // The row as the settings page shows it: attached, the receiver following its triggers, the PC on its network.
    private async Task ShownAsync(bool signedIn = true, bool onNetwork = true)
    {
        if (onNetwork)
        {
            _harness.Addresses.Set(RemoteHarness.Lan);
        }
        if (signedIn)
        {
            _harness.SignIn();
        }
        Receiver.Start();
        await OnUiAsync(_viewModel.Attach);
        await SettleAsync();
    }

    private async Task SwitchOnAsync()
    {
        await OnUiAsync(() => _viewModel.IsOn = true);
        await SettleAsync();
    }

    // What the UI thread was given has run, the receiver has looked at it, and what it raised has reached the view model.
    private async Task SettleAsync()
    {
        await _harness.SettleUiAsync();
        await Receiver.SettledAsync().WaitAsync(HangGuard, Token);
        await _harness.SettleUiAsync();
    }

    private Task OnUiAsync(Action action) => _harness.Dispatcher.InvokeAsync(() =>
    {
        action();
        return true;
    });

    private T Read<T>(Func<PhoneRemoteViewModel, T> read) => _harness.Dispatcher.Read(() => read(_viewModel));

    [Fact]
    public async Task TheSwitch_IsOffByDefault_AndTurningItOn_SavesItOnThisPc()
    {
        await ShownAsync();
        Assert.False(Read(vm => vm.IsOn));
        Assert.Equal(("", false), Read(vm => (vm.StatusText, vm.CanConnectAPhone)));

        await SwitchOnAsync();

        Assert.Equal([true], _harness.Preferences.SavedPhoneRemote);
        Assert.Equal(RemoteReceiverState.Listening, Receiver.Status.State);
    }

    [Fact]
    public async Task WhileListening_ItSaysNoPhoneIsConnected_AndOffersConnectAPhone()
    {
        await ShownAsync();

        await SwitchOnAsync();

        Assert.Equal(("No phone is connected.", "", true), Read(vm => (vm.StatusText, vm.ReasonText, vm.CanConnectAPhone)));
    }

    [Fact]
    public async Task ConnectedPhones_AreNamed()
    {
        await ShownAsync();
        await SwitchOnAsync();

        var (phone, _, _) = await _harness.ConnectThroughListenerAsync();
        Assert.Equal("hello", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
        await phone.HelloAsync(RemoteHarness.ServerId, RemoteHarness.ServerIdentity);
        await phone.NextAsync("state");
        await SettleAsync();

        Assert.Equal(("Connected: Elena's phone", "Connected: Elena's phone"), Read(vm => (vm.StatusText, vm.PhonesText)));
    }

    [Theory]
    [InlineData(RemoteReceiverState.NoLocalNetwork, "This PC is not on a local network.")]
    [InlineData(RemoteReceiverState.NoPort, "Windows did not give Granary a port.")]
    [InlineData(RemoteReceiverState.NotSignedIn, "No profile is signed in on this PC.")]
    [InlineData(RemoteReceiverState.Failed, "Granary couldn't start it. The log has the details.")]
    public async Task WhenItCannotRun_ItSaysWhy(RemoteReceiverState state, string reason)
    {
        _harness.Listener.NoPortFree = state == RemoteReceiverState.NoPort;
        _harness.Advertiser.Throws = state == RemoteReceiverState.Failed ? new InvalidOperationException("Windows refused.") : null;
        await ShownAsync(signedIn: state != RemoteReceiverState.NotSignedIn, onNetwork: state != RemoteReceiverState.NoLocalNetwork);

        await SwitchOnAsync();

        Assert.Equal(state, Receiver.Status.State);
        Assert.Equal(("Remote control isn't available right now.", reason, false),
            Read(vm => (vm.StatusText, vm.ReasonText, vm.CanConnectAPhone)));
    }

    [Fact]
    public async Task TheLink_IsReadOnlyWhileThePanelIsOpen()
    {
        await ShownAsync();
        await SwitchOnAsync();
        Assert.Null(Read(vm => vm.PairingLink));

        await OnUiAsync(() => _viewModel.OpenPanelCommand.Execute(null));
        Assert.True(Read(vm => vm.IsPanelOpen));
        Assert.Equal(Receiver.PairingLink, Read(vm => vm.PairingLink));
        Assert.NotNull(Read(vm => vm.PairingLink));

        await OnUiAsync(() => _viewModel.ClosePanelCommand.Execute(null));
        Assert.Equal((false, (string?)null), Read(vm => (vm.IsPanelOpen, vm.PairingLink)));
    }

    [Fact]
    public async Task ThePanel_ShowsTheAddressAndThePort_AndTheServersAddressAsAHint()
    {
        await ShownAsync();
        await SwitchOnAsync();

        await OnUiAsync(() => _viewModel.OpenPanelCommand.Execute(null));

        Assert.Equal("192.168.1.20:52817", Read(vm => vm.AddressText));
        Assert.Equal("The phone has to be on this network and signed in to https://silo.example.", Read(vm => vm.ServerHint));
        Assert.Equal("Scan this code with your phone's camera and open it in Silo. Then choose Elena's PC under Remote Control.",
            Read(vm => vm.ScanText));
        Assert.False(Read(vm => vm.ShowsNotAdvertised));
    }

    [Fact]
    public async Task ThePanel_FollowsANewAddressAndANewKey()
    {
        await ShownAsync();
        await SwitchOnAsync();
        await OnUiAsync(() => _viewModel.OpenPanelCommand.Execute(null));
        string first = Read(vm => vm.PairingLink)!;

        _harness.Addresses.Set(new LanAddress(System.Net.IPAddress.Parse("192.168.1.21"), 7));
        await SettleAsync();

        Assert.Equal("192.168.1.21:52817", Read(vm => vm.AddressText));
        string moved = Read(vm => vm.PairingLink)!;
        Assert.NotEqual(first, moved);
        Assert.Equal(Receiver.PairingLink, moved);

        // A new key under the same address and port: only the code changes.
        await _harness.PairingStore.ResetKeyAsync(Token);
        await SettleAsync();

        string renewed = Read(vm => vm.PairingLink)!;
        Assert.NotEqual(moved, renewed);
        Assert.Equal(Receiver.PairingLink, renewed);
        Assert.Equal("192.168.1.21:52817", Read(vm => vm.AddressText));
    }

    [Fact]
    public async Task WhenWindowsDidNotAdvertise_ThePanelSaysPhonesConnectThroughTheCodeOnly()
    {
        _harness.Advertiser.Accepts = false;
        await ShownAsync();
        await SwitchOnAsync();

        await OnUiAsync(() => _viewModel.OpenPanelCommand.Execute(null));

        Assert.True(Read(vm => vm.ShowsNotAdvertised));
        Assert.Equal("Phones can connect through this code only: Windows did not advertise this PC.", PhoneRemoteViewModel.NotAdvertisedText);
        Assert.NotNull(Read(vm => vm.PairingLink));
    }

    [Fact]
    public async Task ResetPairingCode_AsksFirst_ThenShowsANewCode()
    {
        await ShownAsync();
        await SwitchOnAsync();
        await OnUiAsync(() => _viewModel.OpenPanelCommand.Execute(null));
        string before = Read(vm => vm.PairingLink)!;
        string keyId = _harness.PairingStore.Current!.KeyId;

        await _harness.Dispatcher.RunAsync(() => _viewModel.ResetPairingCommand.ExecuteAsync(null));
        await SettleAsync();

        var asked = Assert.Single(_confirmation.Asked);
        Assert.Equal(("Reset Pairing Code?", "Every phone has to scan the new code. Phones that are connected now are disconnected.", "Reset"),
            asked);
        Assert.NotEqual(keyId, _harness.PairingStore.Current!.KeyId);
        string after = Read(vm => vm.PairingLink)!;
        Assert.NotEqual(before, after);
        Assert.Equal(Receiver.PairingLink, after);
        Assert.Equal("", Read(vm => vm.ResetMessage));
    }

    [Fact]
    public async Task ARefusedReset_ChangesNothing()
    {
        _confirmation.Answer = false;
        await ShownAsync();
        await SwitchOnAsync();
        await OnUiAsync(() => _viewModel.OpenPanelCommand.Execute(null));
        string before = Read(vm => vm.PairingLink)!;
        string keyId = _harness.PairingStore.Current!.KeyId;

        await _harness.Dispatcher.RunAsync(() => _viewModel.ResetPairingCommand.ExecuteAsync(null));
        await SettleAsync();

        Assert.Single(_confirmation.Asked);
        Assert.Equal(keyId, _harness.PairingStore.Current!.KeyId);
        Assert.Equal(before, Read(vm => vm.PairingLink));
    }

    [Fact]
    public async Task AResetThatCannotBeSaved_SaysTheOldCodeStillWorks()
    {
        await ShownAsync();
        await SwitchOnAsync();
        await OnUiAsync(() => _viewModel.OpenPanelCommand.Execute(null));
        string before = Read(vm => vm.PairingLink)!;
        _harness.Secrets.FailWrites = new IOException("The disk is full.");

        await _harness.Dispatcher.RunAsync(() => _viewModel.ResetPairingCommand.ExecuteAsync(null));
        await SettleAsync();

        Assert.Equal("Couldn't reset the pairing code. The old code still works.", Read(vm => vm.ResetMessage));
        Assert.Equal(before, Read(vm => vm.PairingLink));
    }

    [Theory]
    [InlineData(PairingResetOutcome.Reset, "")]
    [InlineData(PairingResetOutcome.PhonesNotDropped,
        "The new code is in place, but a phone could not be disconnected. It can no longer control this PC.")]
    [InlineData(PairingResetOutcome.Failed, "Couldn't reset the pairing code. The old code still works.")]
    public void EveryOutcomeOfAReset_IsWordedPlainly(PairingResetOutcome outcome, string text) =>
        Assert.Equal(text, PhoneRemoteViewModel.ResetText(outcome));

    [Fact]
    public async Task SwitchingOff_ClosesThePanel_AndForgetsTheLink()
    {
        await ShownAsync();
        await SwitchOnAsync();
        await OnUiAsync(() => _viewModel.OpenPanelCommand.Execute(null));
        Assert.NotNull(Read(vm => vm.PairingLink));

        await OnUiAsync(() => _viewModel.IsOn = false);
        await SettleAsync();

        Assert.Equal((false, (string?)null, false, ""), Read(vm => (vm.IsPanelOpen, vm.PairingLink, vm.CanConnectAPhone, vm.StatusText)));
        Assert.Equal(RemoteReceiverState.Off, Receiver.Status.State);
    }

    [Fact]
    public async Task AfterDetach_NothingOfTheReceiverIsFollowed()
    {
        await ShownAsync();
        await SwitchOnAsync();
        await OnUiAsync(_viewModel.Detach);

        _harness.Addresses.Set(null);
        await SettleAsync();

        Assert.Equal(RemoteReceiverState.NoLocalNetwork, Receiver.Status.State);
        Assert.Equal(("No phone is connected.", true), Read(vm => (vm.StatusText, vm.CanConnectAPhone)));
    }

    [Fact]
    public async Task AttachingAgain_ShowsTheReceiverAsItIsNow()
    {
        await ShownAsync();
        await SwitchOnAsync();
        await OnUiAsync(_viewModel.Detach);
        _harness.Addresses.Set(null);
        await SettleAsync();

        await OnUiAsync(_viewModel.Attach);

        Assert.Equal("Remote control isn't available right now.", Read(vm => vm.StatusText));
    }
}
