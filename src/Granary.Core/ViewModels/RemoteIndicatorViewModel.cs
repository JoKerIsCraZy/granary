using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Remote;

namespace Granary.Core.ViewModels;

/// <summary>
/// What the PC shows while Silo's phone app controls it (design 5.9): "{name} connected" for four seconds when a phone
/// starts to control, as Silo's Roku app says it, and while one does a quiet indicator, "Remote control active", whose
/// "Disconnect Remote" lets every such phone go (the TV apps' texts). A phone that only watches (passive) shows nothing.
/// No standby screen: a PC is used for other things. The shell has one for the app; the player page makes its own for its
/// bar's icon and disposes it as it goes. Made and used on the UI thread.
/// </summary>
public sealed partial class RemoteIndicatorViewModel : ObservableObject, IDisposable
{
    /// <summary>The indicator's tooltip and name: the TV apps' "Remote control active".</summary>
    public const string ActiveText = "Remote control active";

    /// <summary>The indicator's one action: the TV apps' "Disconnect Remote".</summary>
    public const string DisconnectText = "Disconnect Remote";

    /// <summary>The notice when a phone starts to control, with its name: Silo's Roku app's "{name} connected".</summary>
    public const string ConnectedFormat = "{0} connected";

    /// <summary>How long the notice shows, as on Silo's Roku app.</summary>
    public static readonly TimeSpan NoticeShowsFor = TimeSpan.FromSeconds(4);

    private readonly RemoteReceiver _receiver;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ITimer? _noticeTimer;
    private int _notices;
    private bool _disposed;

    public RemoteIndicatorViewModel(RemoteReceiver receiver, IUiDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _receiver = receiver;
        _dispatcher = dispatcher;
        _time = time;
        _receiver.PhonesChanged += OnPhonesChanged;
        _receiver.PhoneConnected += OnPhoneConnected;
        ShowPhones();
    }

    /// <summary>A phone on Granary's server actively controls this PC.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    /// <summary>"Elena's phone connected" for <see cref="NoticeShowsFor"/>; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string NoticeText { get; private set; } = "";

    public bool HasNotice => NoticeText.Length > 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _receiver.PhonesChanged -= OnPhonesChanged;
        _receiver.PhoneConnected -= OnPhoneConnected;
        _noticeTimer?.Dispose();
        _noticeTimer = null;
    }

    // Every phone that controls, each with tv_disconnected; a phone that only watches stays. One that cannot be let go
    // keeps none of the others connected; the first failure is handed on once every phone was tried.
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        Exception? failure = null;
        foreach (var phone in _receiver.Phones.Where(phone => phone.IsActive).ToList())
        {
            try
            {
                await _receiver.DisconnectAsync(phone.Number);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure ??= ex;
            }
        }
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    // The receiver raises on the UI thread.
    private void OnPhonesChanged(object? sender, EventArgs e) => ShowPhones();

    private void OnPhoneConnected(object? sender, ConnectedPhone phone)
    {
        if (_disposed)
        {
            return;
        }
        NoticeText = string.Format(CultureInfo.InvariantCulture, ConnectedFormat, phone.Name);
        int notice = ++_notices;
        _noticeTimer?.Dispose();
        _noticeTimer = _time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            // A newer notice keeps its own time.
            if (notice == _notices)
            {
                NoticeText = "";
            }
        }), null, NoticeShowsFor, Timeout.InfiniteTimeSpan);
        ShowPhones();
    }

    private void ShowPhones()
    {
        if (!_disposed)
        {
            IsActive = _receiver.Phones.Any(phone => phone.IsActive);
        }
    }
}
