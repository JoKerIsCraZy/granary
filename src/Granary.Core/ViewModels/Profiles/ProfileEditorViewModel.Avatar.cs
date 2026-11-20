using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Profiles;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Profiles;

// The picture: a preset, the profile's own uploaded image, or a new image, as the web's editor keeps them apart.
public sealed partial class ProfileEditorViewModel
{
    private string _pictureName = "";

    // The picture went through on a save whose update then did not: the next save leaves it as it is.
    private bool _pictureSaved;

    private void PictureSaved()
    {
        _pictureSaved = true;
        NewPicture = null;
        RemoveUploadedPicture = false;
    }

    public IReadOnlyList<ProfileAvatarStyle> Styles { get; }

    /// <summary>The style whose pictures show.</summary>
    [ObservableProperty]
    public partial ProfileAvatarStyle ActiveStyle { get; set; }

    partial void OnActiveStyleChanged(ProfileAvatarStyle value)
    {
        _presetBatch = 0;
        ShowPresets();
    }

    private int _presetBatch;

    /// <summary>The batch of pictures on show.</summary>
    public ObservableCollection<ProfileAvatarPreset> Presets { get; } = [];

    /// <summary>"Showing 18 options · Geometric, technical, high-contrast patterns".</summary>
    [ObservableProperty]
    public partial string PresetsShowing { get; private set; } = "";

    /// <summary>The preset chosen; null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(PreviewLine))]
    public partial ProfileAvatarPreset? SelectedPreset { get; private set; }

    /// <summary>The profile's uploaded picture is to go (another picture, or Delete image, was chosen).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(PreviewLine), nameof(HasUploadedPicture))]
    public partial bool RemoveUploadedPicture { get; private set; }

    /// <summary>An image chosen from a file, to upload after the save; null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewLine), nameof(HasNewPicture))]
    public partial byte[]? NewPicture { get; private set; }

    public bool HasNewPicture => NewPicture is not null;

    [ObservableProperty]
    public partial string PictureError { get; private set; } = "";

    /// <summary>Custom upload shows: the server keeps uploads.</summary>
    public bool ShowsUpload => _target.Household.AvatarUploadEnabled;

    /// <summary>"Custom uploads are unavailable" shows in its place.</summary>
    public bool ShowsUploadUnavailable => !ShowsUpload;

    /// <summary>The profile's uploaded picture, while it stays.</summary>
    public bool HasUploadedPicture => UploadedPicture is not null && !RemoveUploadedPicture;

    private Uri? UploadedPicture => Profile is { AvatarSource: "upload", AvatarUrl: { } url } ? ProfileItemViewModel.ResolveAvatarUrl(url, _target.Server) : null;

    /// <summary>The preview's picture, a new image aside (the view draws that from <see cref="NewPicture"/>); null shows the initial.</summary>
    public AvatarImage? Preview => SelectedPreset is { } preset ? new AvatarImage(preset.PreviewUrl, true)
        : HasUploadedPicture ? new AvatarImage(UploadedPicture!, false)
        : null;

    /// <summary>The preview's name: the typed one, else "Preview".</summary>
    public string PreviewName => Name.Trim().Length > 0 ? Name.Trim() : AppText.Get("web.profiles.editor.preview");

    public string PreviewInitial => ProfileItemViewModel.InitialsOf(Name.Trim().Length > 0 ? Name : Profile?.Name ?? "?") is { Length: > 0 } initial
        ? initial[..1] : "?";

    /// <summary>What the preview shows: Custom upload selected, the preset's name, Custom upload, or Initials fallback.</summary>
    public string PreviewLine => NewPicture is not null ? AppText.Get("web.profiles.editor.customUploadSelected")
        : SelectedPreset is { } preset ? preset.Label
        : HasUploadedPicture ? AppText.Get("web.profiles.editor.customUpload")
        : AppText.Get("web.profiles.editor.initialsFallback");

    private void InitializeAvatar()
    {
        SelectedPreset = ProfileAvatarPresets.Resolve(Profile?.Avatar, _target.Server);
        string style = ProfileAvatarPresets.StyleOf(SelectedPreset?.Id) ?? Styles[0].Id;
        ActiveStyle = Styles.FirstOrDefault(s => s.Id == style) ?? Styles[0];
    }

    private void ShowPresets()
    {
        Presets.Clear();
        foreach (var preset in ProfileAvatarPresets.Batch(ActiveStyle.Id, _presetBatch))
        {
            Presets.Add(preset);
        }
        PresetsShowing = AppText.Get("web.profiles.editor.presetsShowing", ("count", Presets.Count),
            ("count_text", AppText.Number(Presets.Count)), ("summary", ActiveStyle.Summary));
    }

    /// <summary>More options: another batch of the style's pictures.</summary>
    [RelayCommand]
    private void MorePresets()
    {
        _presetBatch++;
        ShowPresets();
    }

    /// <summary>Picks a preset, or puts it back when it was picked; either way an uploaded picture goes, as on the web.</summary>
    public void SelectPreset(ProfileAvatarPreset? preset)
    {
        if (preset is null)
        {
            return;
        }
        _pictureSaved = false;
        RemoveUploadedPicture = true;
        SelectedPreset = SelectedPreset?.Id == preset.Id ? null : preset;
    }

    /// <summary>
    /// Takes an image from a file to upload with the save. One the server would refuse (too large, not a JPEG, PNG or WebP)
    /// is not kept, and the editor says why.
    /// </summary>
    public bool ChoosePicture(byte[] image, string fileName)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            AvatarUpload.Check(image);
        }
        catch (AvatarRejectedException ex)
        {
            RejectPicture(ex.Problem);
            return false;
        }
        PictureError = "";
        _pictureName = string.IsNullOrWhiteSpace(fileName) ? "avatar" : Path.GetFileName(fileName);
        _pictureSaved = false;
        NewPicture = image;
        RemoveUploadedPicture = true;
        return true;
    }

    /// <summary>Says why a file was not taken; the view calls it for a file too large to read.</summary>
    public void RejectPicture(AvatarProblem problem) => PictureError = AppText.Get(problem == AvatarProblem.TooLarge
        ? "granary.profiles.editor.pictureTooLarge" : "granary.profiles.editor.pictureNotAnImage");

    /// <summary>Remove selected file: the new image goes; the uploaded one stays unless a preset replaced it.</summary>
    public void RemoveNewPicture()
    {
        NewPicture = null;
        PictureError = "";
        if (UploadedPicture is not null && SelectedPreset is null)
        {
            RemoveUploadedPicture = false;
        }
    }

    /// <summary>Delete image: the profile's uploaded picture goes with the save.</summary>
    public void DeleteUploadedPicture()
    {
        _pictureSaved = false;
        NewPicture = null;
        RemoveUploadedPicture = true;
    }

    // The update's avatar: left out while the uploaded picture stays or goes through its own call, as the web's handleSubmit.
    // A new image is left out too: it replaces the picture through its own call, and one that fails leaves the old picture, not
    // none. So is a picture already saved by a try that failed after it.
    private Change<string> AvatarChange(bool keepsUpload, bool deletesUpload) =>
        keepsUpload || deletesUpload || NewPicture is not null || _pictureSaved ? Change<string>.Keep
        : SelectedPreset is { } preset ? Change<string>.To(ProfileAvatarPresets.Reference(preset.Id))
        : Change<string>.Clear;

    private string PictureFileName => _pictureName.Length > 0 ? _pictureName : "avatar";
}
