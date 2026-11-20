using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Granary.Core.Api.Profiles;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Granary.App.Controls;

/// <summary>The profile editor's form (<see cref="ProfileEditorViewModel"/>); <see cref="ShowAsync"/> shows it as the web's dialog.</summary>
public sealed partial class ProfileEditorView : UserControl
{
    private const int PreviewDecodeSize = 96;

    // Set while the view puts the view model's choices into its boxes, so that is not taken for a pick.
    private bool _syncing;

    public ProfileEditorView(ProfileEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ShowChoices();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public ProfileEditorViewModel ViewModel { get; }

    /// <summary>
    /// Shows the editor in a dialog with Cancel and Save profile; Save keeps it open until the save went through, as the web's
    /// dialog does. What was saved, or null when it was cancelled. <paramref name="close"/> closes it from outside, as when the
    /// profile it writes as is no longer the one in use, so the dialog never outlives its editor.
    /// </summary>
    public static async Task<ProfileEditorSaved?> ShowAsync(ProfileEditorViewModel editor, XamlRoot root, CancellationToken close)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var view = new ProfileEditorView(editor);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = editor.Title,
            Content = new ScrollViewer { Content = view, Padding = new Thickness(0, 0, 16, 0), MaxHeight = 640 },
            PrimaryButtonText = AppText.Get("web.profiles.editor.save"),
            CloseButtonText = AppText.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;
        AutomationProperties.SetHelpText(dialog, AppText.Get("web.profiles.editor.description"));
        ProfileEditorSaved? saved = null;
        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                sender.IsPrimaryButtonEnabled = false;
                sender.PrimaryButtonText = AppText.Get("web.profiles.editor.saving");
                saved = await editor.SaveAsync();
                args.Cancel = saved is null;
            }
            catch (Exception ex)
            {
                // A handler of an event: nothing may escape. The dialog stays, with what it had.
                Log(ex, "The profile editor could not save");
                args.Cancel = true;
            }
            finally
            {
                sender.IsPrimaryButtonEnabled = true;
                sender.PrimaryButtonText = AppText.Get("web.profiles.editor.save");
                deferral.Complete();
            }
        };
        dialog.Opened += (_, _) => view.NameBox.Focus(FocusState.Programmatic);
        _ = editor.LoadLibrariesAsync();
        using var closing = close.Register(dialog.Hide);
        try
        {
            if (close.IsCancellationRequested)
            {
                return null;
            }
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // Another dialog is open: nothing was changed.
            Log(ex, "The profile editor could not be shown");
            return null;
        }
        return saved;
    }

    // Static because x:Bind function bindings in the item template call them on the type. A preset is the server's drawing or
    // DiceBear's, the pictures that may be drawn as SVG.
    public static AvatarImage PresetImage(Uri url) => new(url, true);

    private void ShowChoices()
    {
        _syncing = true;
        try
        {
            StyleBox.SelectedItem = ViewModel.ActiveStyle;
            RatingBox.SelectedItem = ViewModel.SelectedContentRating;
            AgeBox.SelectedItem = ViewModel.SelectedAdvisoryAge;
            QualityBox.SelectedItem = ViewModel.SelectedPlaybackCeiling;
            PresetGrid.SelectedItem = ViewModel.SelectedPreset;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProfileEditorViewModel.ActiveStyle) or nameof(ProfileEditorViewModel.SelectedContentRating)
                or nameof(ProfileEditorViewModel.SelectedAdvisoryAge) or nameof(ProfileEditorViewModel.SelectedPlaybackCeiling)
                or nameof(ProfileEditorViewModel.SelectedPreset):
                // The Kids preset and More options change them too; and a new batch is shown before its choice is.
                DispatcherQueue.TryEnqueue(ShowChoices);
                break;
            case nameof(ProfileEditorViewModel.Pin) when PinBox.Password != ViewModel.Pin:
                PinBox.Password = ViewModel.Pin;
                break;
            case nameof(ProfileEditorViewModel.NewPicture):
                _ = ShowNewPictureAsync();
                break;
        }
    }

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ComboBox { SelectedItem: { } picked } box)
        {
            return;
        }
        switch (picked)
        {
            case ProfileAvatarStyle style when ReferenceEquals(box, StyleBox):
                ViewModel.ActiveStyle = style;
                break;
            case ProfileChoice<string> rating:
                ViewModel.SelectedContentRating = rating;
                break;
            case ProfileChoice<int?> age:
                ViewModel.SelectedAdvisoryAge = age;
                break;
            case ProfileChoice<PlaybackCeiling> ceiling:
                ViewModel.SelectedPlaybackCeiling = ceiling;
                break;
        }
    }

    // A press picks the preset, or puts it back when it was the one picked; the grid then shows the view model's choice.
    private void OnPresetClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProfileAvatarPreset preset)
        {
            ViewModel.SelectPreset(preset);
            DispatcherQueue.TryEnqueue(ShowChoices);
        }
    }

    // Each picture is announced by its name.
    private void OnPresetContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is ProfileAvatarPreset preset)
        {
            AutomationProperties.SetName(args.ItemContainer, preset.Label);
        }
    }

    // The PIN goes from the box into the view model, which keeps four digits.
    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Pin != PinBox.Password)
        {
            ViewModel.Pin = PinBox.Password;
        }
    }

    private void OnPinToggleClick(object sender, RoutedEventArgs e) => ViewModel.TogglePinRemoval();

    // Choose image: a JPEG, PNG or WebP file, its size checked before it is read.
    private async void OnChoosePictureClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            foreach (string extension in AvatarUpload.FileTypes)
            {
                picker.FileTypeFilter.Add(extension);
            }
            if (await picker.PickSingleFileAsync() is not { } picked)
            {
                return;
            }
            var file = await StorageFile.GetFileFromPathAsync(picked.Path);
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > AvatarUpload.MaxBytes)
            {
                ViewModel.RejectPicture(AvatarProblem.TooLarge);
                return;
            }
            var buffer = await FileIO.ReadBufferAsync(file);
            ViewModel.ChoosePicture(buffer.ToArray(), file.Name);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A file that cannot be read leaves the picture as it was.
            Log(ex, "A profile picture could not be read");
        }
    }

    private void OnRemoveNewPictureClick(object sender, RoutedEventArgs e) => ViewModel.RemoveNewPicture();

    private void OnDeleteUploadedPictureClick(object sender, RoutedEventArgs e) => ViewModel.DeleteUploadedPicture();

    private async Task ShowNewPictureAsync()
    {
        try
        {
            if (ViewModel.NewPicture is not { } image)
            {
                NewPictureCircle.Visibility = Visibility.Collapsed;
                NewPictureBrush.ImageSource = null;
                return;
            }
            var bitmap = new BitmapImage { DecodePixelWidth = PreviewDecodeSize, DecodePixelType = DecodePixelType.Logical };
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(image.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            if (ReferenceEquals(ViewModel.NewPicture, image))
            {
                NewPictureBrush.ImageSource = bitmap;
                NewPictureCircle.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            // An image the decoder does not take shows no preview; the server still decides.
            Log(ex, "A profile picture could not be previewed");
        }
    }

    // The kind of failure only: a message can name the file's path or the profile.
    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<ProfileEditorView>>().LogWarning("{Message} ({Failure})", message, ex.GetType().Name);
}
