using Granary.Core.Remote;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// What the PC shows while a phone controls it (design 5.9): the short "connected" notice, the quiet indicator, and its
/// Disconnect remote, over a receiver that serves fake phones in memory.
/// </summary>
public sealed class RemoteIndicatorViewModelTests : IAsyncDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly RemoteHarness _harness = new();
    private readonly RemoteIndicatorViewModel _indicator;

    public RemoteIndicatorViewModelTests() =>
        _indicator = _harness.Dispatcher.Read(() => new RemoteIndicatorViewModel(_harness.Receiver, _harness.Dispatcher, _harness.Time));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _harness.Dispatcher.InvokeAsync(() =>
        {
            _indicator.Dispose();
            return true;
        });
        await _harness.DisposeAsync();
    }

    private T Read<T>(Func<RemoteIndicatorViewModel, T> read) => _harness.Dispatcher.Read(() => read(_indicator));

    // A phone said hello and what the receiver raised for it has reached the indicator.
    private async Task<(FakePhone Phone, Task Serving)> PhoneAsync(string deviceId = FakePhone.DefaultDeviceId,
        string deviceName = FakePhone.DefaultDeviceName, bool passive = false)
    {
        var (phone, serving) = await _harness.HelloAsync(deviceId: deviceId, deviceName: deviceName, passive: passive);
        await phone.NextAsync("state");
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");
        await _harness.SettleUiAsync();
        return (phone, serving);
    }

    [Fact]
    public void WithoutAPhone_NothingShows()
    {
        Assert.Equal((false, false, ""), Read(vm => (vm.IsActive, vm.HasNotice, vm.NoticeText)));
    }

    [Fact]
    public async Task APhoneThatControls_ShowsTheIndicator_AndIsNamedForFourSeconds()
    {
        await PhoneAsync();

        Assert.Equal((true, true, "Elena's phone connected"), Read(vm => (vm.IsActive, vm.HasNotice, vm.NoticeText)));

        _harness.Time.Advance(RemoteIndicatorViewModel.NoticeShowsFor - TimeSpan.FromMilliseconds(1));
        await _harness.SettleUiAsync();
        Assert.True(Read(vm => vm.HasNotice));
        _harness.Time.Advance(TimeSpan.FromMilliseconds(1));
        await _harness.SettleUiAsync();
        Assert.Equal((true, false, ""), Read(vm => (vm.IsActive, vm.HasNotice, vm.NoticeText)));
    }

    [Fact]
    public async Task APassivePhone_ShowsNothing()
    {
        await PhoneAsync(passive: true);

        Assert.Equal((false, false), Read(vm => (vm.IsActive, vm.HasNotice)));
    }

    [Fact]
    public async Task ASecondPhone_GetsItsOwnFourSeconds()
    {
        await PhoneAsync();
        _harness.Time.Advance(TimeSpan.FromSeconds(3));
        await _harness.SettleUiAsync();

        await PhoneAsync(deviceId: "phone-2", deviceName: "Elena's tablet");
        _harness.Time.Advance(TimeSpan.FromSeconds(2));
        await _harness.SettleUiAsync();

        Assert.Equal("Elena's tablet connected", Read(vm => vm.NoticeText));
    }

    [Fact]
    public async Task DisconnectRemote_LetsEveryPhoneThatControlsGo()
    {
        var (first, firstServing) = await PhoneAsync();
        var (second, secondServing) = await PhoneAsync(deviceId: "phone-2", deviceName: "Elena's tablet");

        await _harness.Dispatcher.RunAsync(() => _indicator.DisconnectCommand.ExecuteAsync(null));

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"tv_disconnected"}}""", await first.NextAsync("close"));
        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"tv_disconnected"}}""", await second.NextAsync("close"));
        await Task.WhenAll(firstServing, secondServing).WaitAsync(HangGuard, Token);
        await _harness.SettleUiAsync();
        Assert.False(Read(vm => vm.IsActive));
    }

    [Fact]
    public async Task AfterDispose_NoPhoneIsFollowed()
    {
        await _harness.Dispatcher.InvokeAsync(() =>
        {
            _indicator.Dispose();
            return true;
        });

        await PhoneAsync();

        Assert.Equal((false, false), Read(vm => (vm.IsActive, vm.HasNotice)));
    }

    [Fact]
    public void ItsTexts_AreSilosTvApps()
    {
        Assert.Equal("Remote control active", RemoteIndicatorViewModel.ActiveText);
        Assert.Equal("Disconnect remote", RemoteIndicatorViewModel.DisconnectText);
        Assert.Equal("Elena's phone connected", RemoteIndicatorViewModel.ConnectedNotice("Elena's phone"));
    }

    [Fact]
    public void ItsTexts_InTheLanguageOnScreen_AreSilosAppsTranslations()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal("Fernbedienung aktiv", RemoteIndicatorViewModel.ActiveText);
        Assert.Equal("Fernbedienung trennen", RemoteIndicatorViewModel.DisconnectText);
        Assert.Equal("Elena's phone verbunden", RemoteIndicatorViewModel.ConnectedNotice("Elena's phone"));
    }
}
