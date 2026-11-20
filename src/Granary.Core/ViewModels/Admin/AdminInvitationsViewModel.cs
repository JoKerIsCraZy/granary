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
/// may still have happened, so the history is read again before another. Without the server's public URL nothing new
/// can be made: Invite someone and the new links wait, as on the web.
/// </summary>
public sealed partial class AdminInvitationsViewModel(ISessionManager session, AdminInvitationsApi invitations, IConfirmation confirmation,
    ILogger<AdminInvitationsViewModel> logger) : ObservableObject
{
    private string? _cursor;
    // The account and profile the tab was opened as: everything it sends goes only as them.
    private AdminActor? _actor;
    // The cursors this history was read with: one named again would go round in a circle.
    private readonly HashSet<string> _cursors = new(StringComparer.Ordinal);
    private bool _viewerIsOwner;
    // The tab's reads while it shows; Deactivate cancels them.
    private CancellationTokenSource _load = new();
    // The history as it is read now; a reload replaces it, so a page of the one before is dropped.
    private CancellationTokenSource _history = new();

    public ObservableCollection<AdminInvitationRow> Rows { get; } = [];

    /// <summary>Loading, Ready, Error (the capabilities failed) or Unavailable (not configured).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInvite))]
    public partial LibraryLoadState State { get; private set; } = LibraryLoadState.Idle;

    public AdminInvitationCapabilities Capabilities { get; private set; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    [NotifyPropertyChangedFor(nameof(IsFirstPageLoading))]
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

    /// <summary>The server has no public URL, which every link is built on: nothing new can be made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInvite))]
    public partial bool PublicUrlMissing { get; private set; }

    public bool CanInvite => State == LibraryLoadState.Ready && !PublicUrlMissing;

    public bool IsEmpty => State == LibraryLoadState.Ready && !IsHistoryLoading && HistoryError.Length == 0 && Rows.Count == 0;

    public bool HasRows => Rows.Count > 0;

    /// <summary>"Loading invitations...": the first page is on its way.</summary>
    public bool IsFirstPageLoading => IsHistoryLoading && Rows.Count == 0;

    /// <summary>The account and profile this was opened as; its dialogs act as them too.</summary>
    public AdminActor? Actor => _actor;

    public bool ViewerIsOwner => _viewerIsOwner;

    /// <summary>The web's notice without its link to General settings, a page Granary does not have.</summary>
    public string PublicUrlMissingText => AdminText.WithoutLink("web.admin.settings.invitations.publicUrlMissing");

    /// <summary>The web's failure without its button, which the tab shows on its own.</summary>
    public string CapabilitiesFailedText => AdminText.WithoutTag("web.admin.settings.invitations.capabilitiesFailed", "button");

    public string ReloadCapabilitiesText => AdminText.TagText("web.admin.settings.invitations.capabilitiesFailed", "button");

    /// <summary>Reads what the server offers, then the first page of the history.</summary>
    public async Task OpenAsync(bool viewerIsOwner)
    {
        _viewerIsOwner = viewerIsOwner;
        _actor = AdminActors.Of(session);
        _load.Cancel();
        _load = new CancellationTokenSource();
        await ReadCapabilitiesAsync(_load.Token);
    }

    /// <summary>The tab leaves: its reads stop and a new link shown is forgotten.</summary>
    public void Deactivate()
    {
        _load.Cancel();
        _history.Cancel();
        Resent = null;
    }

    /// <summary>"Reload capabilities", after they failed.</summary>
    [RelayCommand]
    private Task ReloadCapabilitiesAsync() => ReadCapabilitiesAsync(_load.Token);

    /// <summary>Reload history: the first page again, and the rows' actions free once it is read.</summary>
    [RelayCommand]
    public async Task RestartAsync()
    {
        _history.Cancel();
        _history.Dispose();
        var history = _history = CancellationTokenSource.CreateLinkedTokenSource(_load.Token);
        Rows.Clear();
        OnPropertyChanged(nameof(HasRows));
        _cursors.Clear();
        _cursor = null;
        HasMore = false;
        if (await ReadPageAsync(history.Token))
        {
            IsBlocked = false;
            ActionError = "";
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => ReadPageAsync(_history.Token);

    private bool CanLoadMore() => HasMore && !IsHistoryLoading;

    /// <summary>
    /// A new link for the invitation, emailed or not, for its dialog to show at once. The rows stay held until the history is
    /// read again (<see cref="RestartAsync"/>), which the dialog does when it closes.
    /// </summary>
    public async Task<bool> ResendAsync(AdminInvitationRow row, string delivery)
    {
        if (_actor is not { } actor || IsBlocked || PublicUrlMissing)
        {
            return false;
        }
        IsBlocked = true;
        Resent = null;
        ActionError = "";
        try
        {
            var resent = await invitations.ResendAsync(actor, row.Id, delivery);
            if (_load.IsCancellationRequested)
            {
                // The tab left meanwhile: the link is not shown, and the next visit reads the history afresh.
                return false;
            }
            Resent = resent;
            return true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
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
        if (row is null || _actor is not { } actor || IsBlocked || !await confirmation.AskAsync(
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
            await invitations.RevokeAsync(actor, row.Id);
            IsBlocked = false;
            await RestartAsync();
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not revoke an invitation");
            ActionError = AdminFailures.Describe(ex, AppText.Get("web.admin.settings.invitations.revokeFailed"));
            IsBlocked = false;
        }
    }

    private async Task ReadCapabilitiesAsync(CancellationToken cancellationToken)
    {
        if (_actor is not { } actor)
        {
            return;
        }
        State = LibraryLoadState.Loading;
        try
        {
            var capabilities = await invitations.GetCapabilitiesAsync(actor, cancellationToken);
            bool publicUrlMissing = capabilities.Available && await IsPublicUrlMissingAsync(actor, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            Capabilities = capabilities;
            OnPropertyChanged(nameof(Capabilities));
            PublicUrlMissing = publicUrlMissing;
            State = capabilities.Available ? LibraryLoadState.Ready : LibraryLoadState.Unavailable;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            if (cancellationToken.IsCancellationRequested)
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

    // The web blocks nothing on settings it could not read.
    private async Task<bool> IsPublicUrlMissingAsync(AdminActor actor, CancellationToken cancellationToken)
    {
        try
        {
            return await invitations.IsPublicUrlMissingAsync(actor, cancellationToken);
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex) && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Could not read whether the server has its public URL");
            return false;
        }
    }

    // One page, appended unless the history was read again or the tab left meanwhile.
    private async Task<bool> ReadPageAsync(CancellationToken cancellationToken)
    {
        if (_actor is not { } actor || cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        IsHistoryLoading = true;
        HistoryError = "";
        try
        {
            var page = await invitations.ListPageAsync(actor, _cursor, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            // A cursor the server named before fails the read, as the web's repeatedCursor.
            if (page.Page is { HasMore: true, NextCursor: { } repeated } && (repeated == _cursor || _cursors.Contains(repeated)))
            {
                throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server's invitation pages go round in a circle.");
            }
            if (_cursor is not null)
            {
                _cursors.Add(_cursor);
            }
            foreach (var invitation in page.Items)
            {
                Rows.Add(AdminInvitationRow.From(invitation, _viewerIsOwner, Capabilities.EmailDelivery, PublicUrlMissing));
            }
            _cursor = page.Page is { HasMore: true, NextCursor: { } next } ? next : null;
            HasMore = _cursor is not null;
            return true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            logger.LogWarning(ex, "Could not read the invitation history");
            HistoryError = AppText.Get("web.admin.settings.invitations.historyFailed", ("error", AdminFailures.DescribeLoad(ex)));
            HasMore = false;
            return false;
        }
        finally
        {
            // A read dropped for a newer one leaves the newer one's state alone.
            if (!cancellationToken.IsCancellationRequested || _history.Token == cancellationToken || _load.IsCancellationRequested)
            {
                IsHistoryLoading = false;
            }
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(IsFirstPageLoading));
        }
    }
}
