using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Granary.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace Granary.App.Views;

// The Look card's images and its file picker: the tile and the closed card's thumbnail show a chosen file, a pasted
// link, the saved poster or the collage, whichever the view model names.
public sealed partial class CollectionEditorPage
{
    private const int LookPosterWidth = 112;

    private static readonly string CollageGlyph = char.ConvertFromUtf32(0xF0E2);
    private static readonly string PictureGlyph = char.ConvertFromUtf32(0xE8B9);

    // The file types the server takes for a poster, with their media types for a file whose type Windows doesn't know.
    private static readonly Dictionary<string, string> PosterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
    };

    private int _previewShown;

    public Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"Look, Poster: a collage of its titles" for the closed card's button.</summary>
    public string LookName(string summary) => $"Look, {summary}";

    public string EmptyGlyph(bool collages) => collages ? CollageGlyph : PictureGlyph;

    public string EmptyLabel(bool collages) => collages ? "Collage" : "No poster";

    private void FollowLook() => ViewModel.PropertyChanged += OnLookChanged;

    private void OnLookChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.PosterPreviewImage) or nameof(ViewModel.PosterPreviewUrl):
                _ = ShowPosterPreviewAsync();
                break;
            case nameof(ViewModel.IsPastingLink) when ViewModel.IsPastingLink:
                // Paste a link…: the cursor waits in the link box.
                DispatcherQueue.TryEnqueue(() => PosterLinkBox.Focus(FocusState.Programmatic));
                break;
        }
    }

    // A chosen file shows from its bytes; a pasted link straight from where it points, as the web's tile shows it
    // (useArtworkPreview puts the link in an img); the saved poster and the collage through the app's artwork, which
    // knows the server.
    private async Task ShowPosterPreviewAsync()
    {
        int shown = ++_previewShown;
        ImageSource? source = null;
        try
        {
            if (ViewModel.PosterPreviewImage is { } file)
            {
                var bitmap = new BitmapImage { DecodePixelWidth = LookPosterWidth };
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(file.AsBuffer());
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                source = bitmap;
            }
            else if (ViewModel.PosterPreviewUrl is { } url)
            {
                source = ViewModel.StagedPoster?.Link is not null ? new BitmapImage(url) { DecodePixelWidth = LookPosterWidth } : Artwork.Load(url, LookPosterWidth);
            }
        }
        catch (Exception ex)
        {
            // A file Windows can't decode shows no preview; the server says on Save whether it takes it.
            Log(ex, "A poster preview could not be shown");
        }
        if (shown == _previewShown)
        {
            LookThumb.Source = source;
            LookTile.Source = source;
        }
    }

    // "Upload image…": a JPEG, PNG or WebP file, checked for type and size before it is read.
    private async void OnUploadPosterClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            // The types the server takes (CollectionsApi.PosterTypes).

            foreach (string extension in PosterExtensions.Keys)
            {
                picker.FileTypeFilter.Add(extension);
            }
            if (await picker.PickSingleFileAsync() is not { } picked)
            {
                return;
            }
            var file = await StorageFile.GetFileFromPathAsync(picked.Path);
            string type = PosterExtensions.TryGetValue(file.FileType, out var known) ? known : file.ContentType;
            var properties = await file.GetBasicPropertiesAsync();
            if (!ViewModel.CanTakePosterFile((long)properties.Size, type))
            {
                return;
            }
            var buffer = await FileIO.ReadBufferAsync(file);
            ViewModel.UsePosterFile(buffer.ToArray(), file.Name, type);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A file that can't be read leaves the poster as it was.
            Log(ex, "A poster file could not be read");
        }
    }

    // Enter uses the link, Escape closes the box, as on the web.
    private void OnPosterLinkKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.UsePosterLinkCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.UsePosterLinkCommand.Execute(null);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.CancelPosterLinkCommand.Execute(null);
        }
    }
}
