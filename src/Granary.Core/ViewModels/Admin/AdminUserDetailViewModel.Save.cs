using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// One of the account's cards, as the web's <c>EditableCard</c> saves it: its change goes at the version the account was
/// read at. Another administrator's change meanwhile (412) keeps the edits and offers Reload and review, which reads the
/// account again under them.
/// </summary>
public sealed partial class AdminEditCard : ObservableObject
{
    [ObservableProperty]
    public partial bool IsSaving { get; internal set; }

    [ObservableProperty]
    public partial string Error { get; internal set; } = "";

    [ObservableProperty]
    public partial bool HasConflict { get; internal set; }

    /// <summary>"Saved", for a moment after a save went through.</summary>
    [ObservableProperty]
    public partial string Saved { get; internal set; } = "";

    public string SaveText => AppText.Get(IsSaving ? "web.admin.users.detail.access.card.saving" : "common.save");

    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(SaveText));

    internal void Clear()
    {
        Error = Saved = "";
        HasConflict = false;
    }
}

public sealed partial class AdminUserDetailViewModel
{
    /// <summary>
    /// Sends <paramref name="change"/> for a card. True when it went through: the account is read again, so the next
    /// change carries its new version.
    /// </summary>
    private async Task<bool> SaveAsync(AdminEditCard card, AdminUserChange change, string? validation)
    {
        if (_actor is not { } actor || Account is not { } account || card.IsSaving || _version.Length == 0)
        {
            return false;
        }
        card.Clear();
        if (validation is not null)
        {
            card.Error = validation;
            return false;
        }
        card.IsSaving = true;
        try
        {
            await _users.UpdateAsync(actor, account.Id, _version, change);
            // The version just sent is spent: nothing more goes until the account is read again.
            _version = "";
            card.Saved = AppText.Get("web.admin.users.detail.access.card.saved");
            bool savedSignIn = ReferenceEquals(card, SignInCard);
            if (!await ReadAsync(keepSignIn: !savedSignIn, keepAccess: savedSignIn) && Account is not null)
            {
                // Saved, but not read again: Reload and review reads it before the next change.
                card.HasConflict = true;
                card.Error = AppText.Get("web.admin.users.detail.access.card.reloadFailed");
            }
            return true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not change an account");
            card.HasConflict = AdminFailures.IsChanged(ex);
            card.Error = card.HasConflict ? AppText.Get("web.admin.users.detail.access.card.conflict")
                : ex is SiloApiException { Status: 409, Code: ProblemCodes.Conflict } ? AppText.Get("granary.admin.users.nameTaken")
                : AdminFailures.Describe(ex, AppText.Get("web.admin.users.detail.access.card.saveFailed"));
            return false;
        }
        finally
        {
            card.IsSaving = false;
        }
    }

    /// <summary>Reload and review: the account read again, the card's edits kept on it.</summary>
    internal async Task ReviewAsync(AdminEditCard card)
    {
        // A review that could not read the account leaves Reload and review where it is.
        if (await ReadAsync(keepDrafts: true))
        {
            card.Clear();
        }
        else if (Account is not null)
        {
            card.Error = AppText.Get("web.admin.users.detail.access.card.reloadFailed");
        }
    }
}
