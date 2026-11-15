using System.ComponentModel;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;

namespace Granary.App.Controls;

/// <summary>
/// Connect a Phone: the code Silo's phone app scans, drawn from <see cref="PhoneRemoteViewModel.PairingLink"/> while the
/// panel is open, as <see cref="DeviceSignInPanel"/> draws its code. The link holds the pairing key: it is drawn and kept
/// nowhere else, never written to a file, a log or the clipboard, and the picture goes with <see cref="Close"/>.
/// </summary>
public sealed partial class ConnectPhonePanel : UserControl
{
    // Large modules stay sharp when the image is scaled down to its square.
    private const int PixelsPerModule = 20;

    private readonly ILogger<ConnectPhonePanel> _logger;
    private int _qrVersion;
    private bool _closed;

    public ConnectPhonePanel(PhoneRemoteViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        _logger = App.Services.GetRequiredService<ILogger<ConnectPhonePanel>>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        DrawCode();
    }

    public PhoneRemoteViewModel ViewModel { get; }

    public string QrName => "Code to connect a phone. Scan it with your phone's camera.";

    public string NotAdvertisedText => PhoneRemoteViewModel.NotAdvertisedText;

    public string ResetText => PhoneRemoteViewModel.ResetPairingText;

    public string ResetLineText => PhoneRemoteViewModel.ResetLineText;

    public string ResetQuestionTitle => PhoneRemoteViewModel.ResetQuestionTitle;

    public string ResetQuestionText => PhoneRemoteViewModel.ResetQuestionText;

    public string ResetAction => PhoneRemoteViewModel.ResetAction;

    public string FirewallText => PhoneRemoteViewModel.FirewallText;

    /// <summary>The dialog closed: the panel follows the row no more, and the code's picture goes.</summary>
    public void Close()
    {
        _closed = true;
        _qrVersion++;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ResetQuestion.Hide();
        QrImage.Source = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhoneRemoteViewModel.PairingLink))
        {
            DrawCode();
        }
    }

    private async void DrawCode()
    {
        int version = ++_qrVersion;
        string? link = ViewModel.PairingLink;
        if (_closed || string.IsNullOrEmpty(link))
        {
            QrImage.Source = null;
            return;
        }
        try
        {
            var bitmap = await CreateQrCodeAsync(link);
            if (version == _qrVersion && !_closed)
            {
                QrImage.Source = bitmap;
            }
        }
        catch (Exception ex)
        {
            // An async void method must not throw. Only the type: the message could carry what the code holds.
            _logger.LogWarning("Could not draw the code to connect a phone ({Failure})", ex.GetType().Name);
            QrImage.Source = null;
        }
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        ResetQuestion.Hide();
        try
        {
            await ViewModel.ResetPairingAnsweredAsync();
        }
        catch (Exception ex)
        {
            // The receiver says how a reset went itself; this keeps anything else from closing the app.
            _logger.LogWarning("Reset Pairing Code failed ({Failure})", ex.GetType().Name);
        }
    }

    private void OnResetCancelClick(object sender, RoutedEventArgs e) => ResetQuestion.Hide();

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

        try
        {
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
        finally
        {
            // The picture holds the key as much as the link does: the bytes are not left behind for the collector.
            Array.Clear(png);
        }
    }
}
