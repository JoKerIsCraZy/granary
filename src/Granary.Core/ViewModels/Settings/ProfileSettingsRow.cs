using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Profiles;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Profiles;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// A profile in Settings → Profiles, as the web's row: its avatar, its name with the Current, Primary, Kids and PIN badges,
/// the line of its limits, and Edit and Delete, Delete held back with the reason the web gives.
/// </summary>
public sealed partial class ProfileSettingsRow : ObservableObject
{
    public ProfileSettingsRow(Profile profile, Uri serverBase, bool isCurrent, string? deleteGuard)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;
        Item = ProfileItemViewModel.From(profile, serverBase);
        IsCurrent = isCurrent;
        DeleteGuard = deleteGuard ?? "";
        AccessSummary = ProfileAccess.Summary(profile);
    }

    /// <summary>The profile as the server sent it, for the editor.</summary>
    public Profile Profile { get; }

    public ProfileItemViewModel Item { get; }

    public string Id => Profile.Id;

    public string Name => Profile.Name;

    /// <summary>The picture; null shows <see cref="Initial"/>.</summary>
    public AvatarImage? Avatar => Item.AvatarUrl is { } url ? new AvatarImage(url, Item.AvatarIsServerPreset) : null;

    /// <summary>The web's fallback: the name's first letter, upper-case.</summary>
    public string Initial => Name.Length == 0 ? "?" : StringInfo.GetNextTextElement(Name).ToUpperInvariant();

    /// <summary>The profile that acts now.</summary>
    public bool IsCurrent { get; }

    public bool IsPrimary => Profile.IsPrimary;

    public bool IsKids => Profile.IsChild;

    public bool HasPin => Profile.HasPin;

    public string AccessSummary { get; }

    /// <summary>Why Delete is held back ("At least one profile is required."); empty when it is not.</summary>
    public string DeleteGuard { get; }

    public bool CanDelete => DeleteGuard.Length == 0;

    /// <summary>Delete or Edit runs for this profile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    /// <summary>"Delete Elena", for a screen reader, which hears the button without the row.</summary>
    public string DeleteName => AppText.Get("granary.settings.profiles.deleteName", ("name", Name));

    /// <summary>"Edit Elena".</summary>
    public string EditName => AppText.Get("granary.settings.profiles.editName", ("name", Name));

    /// <summary>
    /// Why the web holds Delete back (<c>getDeleteGuardReason</c>): the last profile, the primary one, and the one acting now,
    /// in that order; null when it may go.
    /// </summary>
    public static string? GuardOf(Profile profile, int householdSize, string? actingProfileId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (householdSize <= 1)
        {
            return AppText.Get("web.settings.profiles.deleteGuard.lastProfile");
        }
        if (profile.IsPrimary)
        {
            return AppText.Get("web.settings.profiles.deleteGuard.primary");
        }
        return profile.Id == actingProfileId ? AppText.Get("web.settings.profiles.deleteGuard.active") : null;
    }

    public override string ToString() => Name;
}
