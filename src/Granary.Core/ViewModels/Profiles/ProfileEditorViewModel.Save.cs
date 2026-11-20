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
            bool deletesUpload = hadUpload && RemoveUploadedPicture && NewPicture is null && SelectedPreset is null;
            Profile saved;
            try
            {
                saved = Profile is { } profile
                    ? await _profiles.UpdateAsync(_target.Server, profile.Id, BuildUpdate(keepsUpload, deletesUpload), _target.Manager, cancellationToken)
                    : await _profiles.CreateAsync(_target.Server, BuildCreate(), _target.Manager, cancellationToken);
            }
            catch (Exception ex) when (ProfileWriteText.IsRefusal(ex, cancellationToken))
            {
                _logger.LogWarning("A profile could not be saved: {Failure}", ApiFailureText.Describe(ex));
                Status = ProfileWriteText.Describe(ex, "web.common.toast.profiles.saveFailed");
                return null;
            }
            string notice = "";
            if (NewPicture is { } picture)
            {
                try
                {
                    saved = await _profiles.UploadAvatarAsync(_target.Server, saved.Id, picture, PictureFileName, _target.Manager, cancellationToken);
                }
                catch (Exception ex) when (ex is AvatarRejectedException || ProfileWriteText.IsRefusal(ex, cancellationToken))
                {
                    _logger.LogWarning("A profile's picture could not be uploaded: {Failure}", ApiFailureText.Describe(ex));
                    notice = AppText.Get("web.common.toast.profiles.avatarUploadFailed");
                }
            }
            else if (deletesUpload)
            {
                try
                {
                    await _profiles.DeleteAvatarAsync(_target.Server, saved.Id, _target.Manager, cancellationToken);
                }
                catch (Exception ex) when (ProfileWriteText.IsRefusal(ex, cancellationToken))
                {
                    _logger.LogWarning("A profile's picture could not be removed: {Failure}", ApiFailureText.Describe(ex));
                    notice = AppText.Get("web.common.toast.profiles.avatarRemoveFailed");
                }
            }
            return new ProfileEditorSaved(saved, IsNew, ClearPin ? "" : Pin, ClearPin, notice);
        }
        finally
        {
            IsSaving = false;
        }
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
            MaxPlaybackQuality = ProfileAccess.WireValue(SelectedPlaybackCeiling.Value),
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
            MaxPlaybackQuality = ProfileAccess.WireValue(SelectedPlaybackCeiling.Value),
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
