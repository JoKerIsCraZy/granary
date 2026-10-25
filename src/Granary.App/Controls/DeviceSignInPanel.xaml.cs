using System.ComponentModel;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;

namespace Granary.App.Controls;

/// <summary>
/// Device (QR code) sign-in, framed for the sign-in card. The sign-in page calls <see cref="Start"/> with the server and
/// <see cref="Stop"/> when the user leaves the code; the panel also stops when it is unloaded, so a
/// page that is shown again must call <see cref="Start"/> again.
/// </summary>
public sealed partial class DeviceSignInPanel : UserControl
{
    // Large modules stay sharp when the image is scaled down to the card.
    private const int PixelsPerModule = 20;

    private readonly ILogger<DeviceSignInPanel> _logger;
    private int _qrVersion;

    public DeviceSignInPanel()
    {
        // Created, not resolved: the root provider would keep every transient disposable (view model
        // and flow) until the app exits. The panel owns both; Stop on Unloaded leaves nothing running.
        var flow = ActivatorUtilities.CreateInstance<DeviceSignInFlow>(App.Services);
        ViewModel = ActivatorUtilities.CreateInstance<DeviceSignInViewModel>(App.Services, flow);
        _logger = App.Services.GetRequiredService<ILogger<DeviceSignInPanel>>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => Stop();
    }

    public DeviceSignInViewModel ViewModel { get; }

    /// <summary>Shows a code for <paramref name="server"/> and signs in when it is approved; replaces a running sign-in.</summary>
    public void Start(ServerEntry server) => ViewModel.Start(server);

    /// <summary>Stops polling, withdraws the code on the server and clears the panel.</summary>
    public void Stop() => ViewModel.Stop();

    // Instance method because x:Bind function bindings call it on the control.
    public double QrOpacity(bool isOpened) => isOpened ? 0.4 : 1.0;

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DeviceSignInViewModel.QrPayload))
        {
            return;
        }
        int version = ++_qrVersion;
        string payload = ViewModel.QrPayload;
        if (payload.Length == 0)
        {
            QrImage.Source = null;
            return;
        }
        try
        {
            var bitmap = await CreateQrCodeAsync(payload);
            if (version == _qrVersion)
            {
                QrImage.Source = bitmap;
            }
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; the code and the address stay usable without the image.
            _logger.LogWarning(ex, "Could not render the sign-in QR code");
            QrImage.Source = null;
        }
    }

    private static async Task<BitmapImage> CreateQrCodeAsync(string payload)
    {
        byte[] png;
        using (var generator = new QRCodeGenerator())
        using (var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M))
        using (var renderer = new PngByteQRCode(data))
        {
            // QRCoder's quiet zone is the standard four modules.
            png = renderer.GetGraphic(PixelsPerModule, drawQuietZones: true);
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
