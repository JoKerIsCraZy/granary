using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// Admin → Users → Invitations, as the web's <c>InvitationsTab</c>: what the server offers, the history a page of 50 at a
/// time, a new link for an invitation (shown once), and Revoke, asked first. A resend or revoke runs alone; a failed one
/// may still have happened, so the history is read again before another.
/// </summary>
public sealed partial class AdminInvitationsViewModel(ISessionManager session, AdminInvitationsApi invitations, IConfirmation confirmation,
    ILogger<AdminInvitationsViewModel> logger) : ObservableObject
{
    private string? _cursor;
    private bool _viewerIsOwner;
    private CancellationTokenSource? _load;

    public ObservableCollection<AdminInvitationRow> Rows { get; } = [];

    /// <summary>Loading, Ready, Error (the capabilities failed) or Unavailable (not configured).</summary>
    [ObservableProperty]
    public partial LibraryLoadState State { get; private set; } = LibraryLoadState.Idle;

    public AdminInvitationCapabilities Capabilities { get; private set; } = new();

    [ObservableProperty]
    public partial bool IsHistoryLoading { get; private set; }

    /// <summary>"Could not load invitation history. …", with Reload history.</summary>
    [ObservableProperty]
    public partial string HistoryError { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; private set; }

    /// <summary>A resend or revoke runs, or failed: the rows' actions wait until the history is read again.</summary>
    [ObservableProperty]
    public partial bool IsBlocked { get; private set; }

    /// <summary>What a revoke or resend failure says, above the history.</summary>
    [ObservableProperty]
    public partial string ActionError { get; private set; } = "";

    /// <summary>The result of a new link, for its dialog; null when none.</summary>
    [ObservableProperty]
    public partial InvitationDelivery? Resent { get; private set; }

    public bool IsEmpty => State == LibraryLoadState.Ready && !IsHistoryLoading && HistoryError.Length == 0 && Rows.Count == 0;

    public bool ViewerIsOwner => _viewerIsOwner;

    /// <summary>Reads what the server offers, then the first page of the history.</summary>
    public async Task OpenAsync(bool viewerIsOwner)
    {
        _viewerIsOwner = viewerIsOwner;
        if (session.ActiveServer is not { } server)
        {
            return;
        }
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        State = LibraryLoadState.Loading;
        try
        {
            Capabilities = await invitations.GetCapabilitiesAsync(server.BaseUri, load.Token);
            OnPropertyChanged(nameof(Capabilities));
            State = Capabilities.Available ? LibraryLoadState.Ready : LibraryLoadState.Unavailable;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (load.Token.IsCancellationRequested)
            {
                return;
            }
            logger.LogWarning(ex, "Could not read what invitations offer");
            State = LibraryLoadState.Error;
            return;
        }
        if (State == LibraryLoadState.Ready)
        {
            await RestartAsync();
        }
    }

    public void Deactivate() => _load?.Cancel();

    /// <summary>Reload history: the first page again, and the rows' actions free once it is read.</summary>
    [RelayCommand]
    public async Task RestartAsync()
    {
        Rows.Clear();
        _cursor = null;
        HasMore = false;
        if (await ReadPageAsync())
        {
            IsBlocked = false;
            ActionError = "";
        }
    }

    [RelayCommand(CanExecute = nameof(HasMore))]
    private Task LoadMoreAsync() => ReadPageAsync();

    /// <summary>A new link for the invitation, emailed or not; its dialog shows the answer.</summary>
    public async Task<bool> ResendAsync(AdminInvitationRow row, string delivery)
    {
        if (session.ActiveServer is not { } server || IsBlocked)
        {
            return false;
        }
        IsBlocked = true;
        Resent = null;
        ActionError = "";
        try
        {
            Resent = await invitations.ResendAsync(server.BaseUri, row.Id, delivery);
            IsBlocked = false;
            await RestartAsync();
            return true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            // It may still have made a new link: the history is read again before another.
            logger.LogWarning(ex, "Could not resend an invitation");
            ActionError = $"{AdminFailures.Describe(ex, AppText.Get("web.admin.settings.invitations.resendFailed"))} {AppText.Get("web.admin.settings.invitations.resend.uncertain")}";
            return false;
        }
    }

    /// <summary>Forgets the new link once its dialog closed.</summary>
    public void ForgetResent() => Resent = null;

    [RelayCommand]
    private async Task RevokeAsync(AdminInvitationRow? row)
    {
        if (row is null || session.ActiveServer is not { } server || IsBlocked || !await confirmation.AskAsync(
            AppText.Get("web.admin.settings.invitations.revoke.title"),
            row.Invitation.IsLink
                ? AppText.Get("web.admin.settings.invitations.revoke.linkDescription")
                : AppText.Get("web.admin.settings.invitations.revoke.emailDescription", ("email", row.Invitation.Email)),
            AppText.Get("web.admin.settings.invitations.revoke.confirm")))
        {
            return;
        }
        IsBlocked = true;
        ActionError = "";
        try
        {
            await invitations.RevokeAsync(server.BaseUri, row.Id);
            IsBlocked = false;
            await RestartAsync();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not revoke an invitation");
            ActionError = AdminFailures.Describe(ex, AppText.Get("web.admin.settings.invitations.revokeFailed"));
            IsBlocked = false;
        }
    }

    // One page, appended; a cursor the server named before fails the read, as the web's repeatedCursor.
    private async Task<bool> ReadPageAsync()
    {
        if (session.ActiveServer is not { } server)
        {
            return false;
        }
        IsHistoryLoading = true;
        HistoryError = "";
        try
        {
            var page = await invitations.ListPageAsync(server.BaseUri, _cursor);
            foreach (var invitation in page.Items)
            {
                Rows.Add(AdminInvitationRow.From(invitation, _viewerIsOwner, Capabilities.EmailDelivery));
            }
            _cursor = page.Page is { HasMore: true, NextCursor: { } next } ? next : null;
            HasMore = _cursor is not null;
            return true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not read the invitation history");
            HistoryError = AppText.Get("web.admin.settings.invitations.historyFailed", ("error", LibraryErrors.Describe(ex)));
            HasMore = false;
            return false;
        }
        finally
        {
            IsHistoryLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }
}
