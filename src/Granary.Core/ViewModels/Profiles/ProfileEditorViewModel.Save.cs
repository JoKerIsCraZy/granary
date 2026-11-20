using Granary.Core.Api;
using Granary.Core.Api.Profiles;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Profiles;

// Save: the profile as the web's buildProfileRequestFromDraft and buildProfileUpdateFromDraft send it, then the picture.
public sealed partial class ProfileEditorViewModel
{
    /// <summary>
    /// Saves the profile, then its new picture or the removal of its uploaded one. Null when nothing was saved: the editor
    /// then says why. A picture that does not go through leaves the save as it is, with a notice, as on the web.
    /// </summary>
    public async Task<ProfileEditorSaved?> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (IsSaving || !Validate())
        {
            return null;
        }
        IsSaving = true;
        try
        {
            bool hadUpload = Profile is { AvatarSource: "upload" };
            bool keepsUpload = hadUpload && !RemoveUploadedPicture && NewPicture is null && SelectedPreset is null;
            bool deletesUpload = hadUpload && !_pictureSaved && RemoveUploadedPicture && NewPicture is null && SelectedPreset is null;
            // What is sent, and the PIN said back, as they are now: an edit while the save runs changes neither.
            string pin = ClearPin ? "" : Pin;
            bool clearsPin = ClearPin;
            var update = Profile is null ? null : BuildUpdate(keepsUpload, deletesUpload);
            var create = Profile is null ? BuildCreate() : null;
            // A PIN change ends the profile's proofs, the one a picture would go with among them: the picture goes first then.
            bool pictureFirst = update is not null && (clearsPin || pin.Length > 0) && (NewPicture is not null || deletesUpload);
            string notice = "";
            Profile? pictured = null;
            if (pictureFirst)
            {
                (pictured, notice) = await SavePictureAsync(Profile!.Id, deletesUpload, cancellationToken);
            }
            Profile saved;
            try
            {
                saved = update is not null
                    ? await _profiles.UpdateAsync(_target.Server, Profile!.Id, update, _target.Manager, cancellationToken)
                    : await _profiles.CreateAsync(_target.Server, create!, _target.Manager, cancellationToken);
            }
            catch (Exception ex) when (ProfileWriteText.IsRefusal(ex, cancellationToken))
            {
                _logger.LogWarning("A profile could not be saved: {Failure}", ApiFailureText.Describe(ex));
                Status = ProfileWriteText.Describe(ex, "web.common.toast.profiles.saveFailed");
                if (pictureFirst && notice.Length == 0)
                {
                    // The picture went through before: said, and not sent again with the next try.
                    Status += " · " + AppText.Get(deletesUpload ? "web.common.toast.profiles.avatarRemoved" : "web.common.toast.profiles.avatarUpdated");
                    PictureSaved();
                }
                return null;
            }
            if (!pictureFirst)
            {
                (pictured, notice) = await SavePictureAsync(saved.Id, deletesUpload, cancellationToken);
                saved = pictured ?? saved;
            }
            return new ProfileEditorSaved(saved, IsNew, pin, clearsPin, notice);
        }
        finally
        {
            IsSaving = false;
        }
    }

    // The new picture, or the removal of the uploaded one: the profile as the upload left it (null when nothing came back),
    // and why it did not go through, as the web's toast; the save stays either way.
    private async Task<(Profile? Profile, string Notice)> SavePictureAsync(string profileId, bool deletesUpload, CancellationToken cancellationToken)
    {
        if (NewPicture is { } picture)
        {
            try
            {
                return (await _profiles.UploadAvatarAsync(_target.Server, profileId, picture, PictureFileName, _target.Manager, cancellationToken), "");
            }
            catch (Exception ex) when (ex is AvatarRejectedException || ProfileWriteText.IsRefusal(ex, cancellationToken))
            {
                _logger.LogWarning("A profile's picture could not be uploaded: {Failure}", ApiFailureText.Describe(ex));
                return (null, AppText.Get("web.common.toast.profiles.avatarUploadFailed"));
            }
        }
        if (deletesUpload)
        {
            try
            {
                await _profiles.DeleteAvatarAsync(_target.Server, profileId, _target.Manager, cancellationToken);
            }
            catch (Exception ex) when (ProfileWriteText.IsRefusal(ex, cancellationToken))
            {
                _logger.LogWarning("A profile's picture could not be removed: {Failure}", ApiFailureText.Describe(ex));
                return (null, AppText.Get("web.common.toast.profiles.avatarRemoveFailed"));
            }
        }
        return (null, "");
    }

    private ProfileCreate BuildCreate()
    {
        var household = _target.Household;
        int? age = household.MaxAdvisoryAgeSupported ? SelectedAdvisoryAge.Value : null;
        return new ProfileCreate(Name.Trim())
        {
            Avatar = SelectedPreset is { } preset ? ProfileAvatarPresets.Reference(preset.Id) : null,
            Pin = Pin.Length > 0 ? Pin : null,
            IsChild = IsChild,
            MaxContentRating = SelectedContentRating.Value.Length > 0 ? SelectedContentRating.Value : null,
            MaxAdvisoryAge = age,
            RequireAdvisoryAge = age is not null && household.RequireAdvisoryAgeSupported && RequireAdvisoryAge ? true : null,
            RequestsAllowed = household.RequestsAllowedSupported && !RequestsAllowed ? false : null,
            MaxPlaybackQuality = PlaybackQualityToSend(),
            LibraryRestrictionsEnabled = ShowsLibraryChoices,
            AllowedLibraryIds = AllowedLibraryIds(),
        };
    }

    private ProfileUpdate BuildUpdate(bool keepsUpload, bool deletesUpload)
    {
        var household = _target.Household;
        int? age = SelectedAdvisoryAge.Value;
        return new ProfileUpdate(Name.Trim())
        {
            Avatar = AvatarChange(keepsUpload, deletesUpload),
            Pin = ClearPin ? Change<string>.Clear : Pin.Length > 0 ? Change<string>.To(Pin) : Change<string>.Keep,
            IsChild = IsChild,
            MaxContentRating = SelectedContentRating.Value.Length > 0 ? SelectedContentRating.Value : null,
            MaxAdvisoryAge = !household.MaxAdvisoryAgeSupported ? Change<int>.Keep : age is { } limit ? Change<int>.To(limit) : Change<int>.Clear,
            // Cleared with the limit, so a stored flag does not come back with the next one.
            RequireAdvisoryAge = household.MaxAdvisoryAgeSupported && household.RequireAdvisoryAgeSupported ? age is not null && RequireAdvisoryAge : null,
            RequestsAllowed = household.RequestsAllowedSupported ? RequestsAllowed : null,
            MaxPlaybackQuality = PlaybackQualityToSend(),
            LibraryRestrictionsEnabled = ShowsLibraryChoices,
            AllowedLibraryIds = AllowedLibraryIds(),
        };
    }
}

/// <summary>How Settings → Profiles and the editor word a household write that did not go through.</summary>
public static class ProfileWriteText
{
    /// <summary>A failure the page reports instead of crashing: the server's answer, the network, or a profile switch meanwhile.</summary>
    public static bool IsRefusal(Exception ex, CancellationToken cancellationToken) =>
        ex is ProfileChangedException || (LibraryErrors.IsExpected(ex) && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested));

    /// <summary>
    /// A refusal the server explains by who asks or how busy it is, and a network failure, in the library's words
    /// (<see cref="LibraryErrors.Describe"/>); anything else, as a name taken meanwhile, in the web's toast.
    /// </summary>
    public static string Describe(Exception ex, string toastKey) => ex switch
    {
        ProfileChangedException => AppText.Get(toastKey),
        SiloApiException { Status: 401 or 403 or 404 or 429 or 503 } => LibraryErrors.Describe(ex),
        SiloApiException => AppText.Get(toastKey),
        _ => LibraryErrors.Describe(ex),
    };
}
