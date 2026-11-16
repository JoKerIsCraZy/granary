using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>
/// A profile's avatar: its picture (<see cref="Image"/>) through the artwork cache, else its letter (<see cref="Initial"/>) on
/// the accent. When the picture shows and when the letter is <see cref="AvatarPicture"/>'s to say: a picture whose address
/// was signed anew stays until the new one is decoded, so the sidebar's reads every few minutes do not make it blink.
/// </summary>
public sealed partial class ProfileAvatar : UserControl
{
    public static readonly DependencyProperty ImageProperty =
        DependencyProperty.Register(nameof(Image), typeof(AvatarImage), typeof(ProfileAvatar),
            new PropertyMetadata(null, (sender, _) => ((ProfileAvatar)sender).ShowImage()));

    public static readonly DependencyProperty InitialProperty =
        DependencyProperty.Register(nameof(Initial), typeof(string), typeof(ProfileAvatar),
            new PropertyMetadata("", (sender, e) => ((ProfileAvatar)sender).InitialText.Text = e.NewValue as string ?? ""));

    // Decoded as wide as it is drawn; this when the control is given no width.
    private const int DefaultSize = 40;

    private readonly AvatarPicture _picture = new();
    private CancellationTokenSource? _loading;

    public ProfileAvatar() => InitializeComponent();

    /// <summary>The picture, and whether it is one of the server's own presets; null shows the letter.</summary>
    public AvatarImage? Image
    {
        get => (AvatarImage?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <summary>The letter shown without a picture.</summary>
    public string Initial
    {
        get => (string)GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    private int DecodeSize => double.IsNaN(Width) ? DefaultSize : (int)Math.Ceiling(Width);

    private void ShowImage()
    {
        var image = Image;
        int? load = _picture.Show(image?.Url);
        if (load is null && image is not null)
        {
            // That picture shows or is on its way already.
            return;
        }
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        if (load is { } number && image is not null)
        {
            _loading = new CancellationTokenSource();
            Load(number, image, _loading.Token);
        }
        Show();
    }

    // async void: whatever escapes would end the app over one picture.
    private async void Load(int load, AvatarImage image, CancellationToken cancellationToken)
    {
        ImageSource? source = null;
        try
        {
            source = await Artwork.PictureAsync(image.Url, DecodeSize, mayBeSvg: image.IsServerPreset, cancellationToken);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<ProfileAvatar>>().LogWarning("The profile's picture could not be shown: {Failure}", ex.GetType().Name);
        }
        // A load another one overtook, or one cancelled, changes nothing.
        if (source is not null ? _picture.Opened(load) : _picture.Failed(load))
        {
            PictureBrush.ImageSource = source;
            Show();
        }
    }

    private void Show()
    {
        bool picture = _picture.ShowsPicture;
        Picture.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
        Fallback.Visibility = picture ? Visibility.Collapsed : Visibility.Visible;
        if (!picture)
        {
            PictureBrush.ImageSource = null;
        }
    }
}
