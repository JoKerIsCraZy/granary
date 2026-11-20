using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// Admin → Users → Invite Codes, as the web's <c>InviteCodesTab</c> and <c>InviteCodeList</c>: the codes a page of 50 at a
/// time, whether public signups are on (codes work only then), Create Code, and a row's switch, Add Uses and Delete (asked
/// first). What an action did is said in a line above the table, where the web shows a toast. Codes are secrets: they are
/// shown and copied, never logged.
/// </summary>
public sealed partial class AdminInviteCodesViewModel(ISessionManager session, AdminInviteCodesApi codes, AdminUsersApi users,
    IConfirmation confirmation, ILogger<AdminInviteCodesViewModel> logger) : ObservableObject
{
    private string? _cursor;
    private AdminActor? _actor;
    // The tab's reads while it shows; Deactivate cancels them. Writes are never cancelled: one sent may have happened.
    private CancellationTokenSource _load = new();
    // The list as it is read now; a restart replaces it, so a page of the one before is dropped.
    private CancellationTokenSource _list = new();
    // Counts the list's reads: only the latest one's answer and end are taken.
    private int _reads;

    public ObservableCollection<AdminInviteCodeRow> Rows { get; } = [];

    /// <summary>The account and profile the tab was opened as.</summary>
    public AdminActor? Actor => _actor;

    /// <summary>"Loading invite codes...": the first page is on its way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsFirstLoad))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool LoadFailed { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; private set; }

    /// <summary>Public signups: null until read, which shows neither note.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignupsOnShown), nameof(SignupsOffShown))]
    public partial bool? SignupsEnabled { get; private set; }

    public bool SignupsOnShown => SignupsEnabled == true;

    public bool SignupsOffShown => SignupsEnabled == false;

    /// <summary>What the last action did ("Invite code created"), or why it failed; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; private set; } = "";

    [ObservableProperty]
    public partial bool MessageIsError { get; private set; }

    public bool HasMessage => Message.Length > 0;

    /// <summary>"Loading invite codes...": the first page is on its way.</summary>
    public bool IsFirstLoad => IsLoading && Rows.Count == 0;

    /// <summary>"No invite codes yet. Create one to get started.", once read.</summary>
    public bool IsEmpty => !IsLoading && !LoadFailed && Rows.Count == 0;

    // The Create Invite Code form.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewCode))]
    public partial string NewCode { get; set; } = "";

    public bool HasNewCode => NewCode.Trim().Length > 0;

    [ObservableProperty]
    public partial string NewLabel { get; set; } = "";

    [ObservableProperty]
    public partial string NewMaxUses { get; set; } = "10";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateText))]
    public partial bool IsCreating { get; private set; }

    [ObservableProperty]
    public partial string FormError { get; private set; } = "";

    public string CreateText => AppText.Get(IsCreating ? "web.admin.settings.inviteCodes.form.creating" : "web.admin.settings.inviteCodes.form.create");

    // The Top Up form.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopUpDescription))]
    public partial AdminInviteCodeRow? TopUpRow { get; private set; }

    [ObservableProperty]
    public partial string AdditionalUses { get; set; } = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopUpText))]
    public partial bool IsToppingUp { get; private set; }

    public string TopUpText => AppText.Get(IsToppingUp ? "web.admin.settings.inviteCodes.topUpForm.adding" : "web.admin.settings.inviteCodes.topUpForm.submit");

    public string TopUpDescription => TopUpRow is { } row
        ? AppText.Get("web.admin.settings.inviteCodes.topUp.description", ("code", row.Code), ("used", row.UseCount.ToString(CultureInfo.InvariantCulture)),
            ("max", row.MaxUses.ToString(CultureInfo.InvariantCulture)))
        : "";

    /// <summary>The web's failure without its button, which the tab shows on its own.</summary>
    public string LoadFailedText => AdminText.WithoutTag("web.admin.settings.inviteCodes.loadFailed", "button");

    public string ReloadText => AdminText.TagText("web.admin.settings.inviteCodes.loadFailed", "button");

    /// <summary>Reads whether signups are on and the first page of codes.</summary>
    public async Task OpenAsync()
    {
        _actor = AdminActors.Of(session);
        _load.Cancel();
        _load = new CancellationTokenSource();
        Message = "";
        var token = _load.Token;
        await Task.WhenAll(ReadSignupsAsync(token), RestartAsync());
    }

    /// <summary>The tab leaves: its reads stop.</summary>
    public void Deactivate()
    {
        _load.Cancel();
        TopUpRow = null;
        NewCode = NewLabel = "";
    }

    /// <summary>The first page again, as the web's list after a change.</summary>
    [RelayCommand]
    public async Task RestartAsync()
    {
        _list.Cancel();
        _list.Dispose();
        _list = CancellationTokenSource.CreateLinkedTokenSource(_load.Token);
        _reads++;
        IsLoading = false;
        Rows.Clear();
        _cursor = null;
        HasMore = false;
        await ReadPageAsync(_list.Token);
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => ReadPageAsync(_list.Token);

    private bool CanLoadMore() => HasMore && !IsLoading;

    /// <summary>Starts the Create Invite Code form: a fresh 16-character code, no label, ten uses, as the web's.</summary>
    public void StartCreate()
    {
        NewCode = RandomNumberGenerator.GetHexString(16);
        NewLabel = FormError = "";
        NewMaxUses = "10";
    }

    /// <summary>Makes the code; true when it was made and the form may close.</summary>
    public async Task<bool> CreateAsync()
    {
        if (_actor is not { } actor || IsCreating)
        {
            return false;
        }
        if (!int.TryParse(NewMaxUses.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int max) || max <= 0)
        {
            FormError = AppText.Get("web.admin.settings.inviteCodes.form.maxUsesInvalid");
            return false;
        }
        string code = NewCode.Trim().ToUpperInvariant();
        // The code is required, as the web's form field: Create stays off without one.
        if (code.Length == 0)
        {
            return false;
        }
        IsCreating = true;
        FormError = "";
        try
        {
            await codes.CreateAsync(actor, new AdminInviteCodeDraft { Code = code, Label = NewLabel, MaxUses = max });
            Say("web.admin.settings.hooks.inviteCodes.created");
            await RestartAsync();
            return true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not create an invite code");
            FormError = AdminFailures.Describe(ex, AppText.Get("web.admin.settings.hooks.inviteCodes.createFailed"));
            return false;
        }
        finally
        {
            IsCreating = false;
        }
    }

    /// <summary>A row's switch: turns its code on or off, then reads the list again.</summary>
    public async Task ToggleAsync(AdminInviteCodeRow row)
    {
        if (_actor is not { } actor)
        {
            return;
        }
        try
        {
            await codes.SetEnabledAsync(actor, row.Id, !row.Enabled);
            Message = "";
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not change an invite code");
            Fail(AdminFailures.Describe(ex, AppText.Get("web.admin.settings.hooks.inviteCodes.updateFailed")));
        }
        await RestartAsync();
    }

    /// <summary>Starts Add Uses for a row: one more use, as the web's form.</summary>
    public void StartTopUp(AdminInviteCodeRow row)
    {
        TopUpRow = row;
        AdditionalUses = "1";
        FormError = "";
    }

    /// <summary>Adds the uses; true when the form may close. A failure may still have counted, so the list is read again either way.</summary>
    public async Task<bool> TopUpAsync()
    {
        if (_actor is not { } actor || TopUpRow is not { } row || IsToppingUp)
        {
            return false;
        }
        if (!int.TryParse(AdditionalUses.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
        {
            FormError = AppText.Get("web.admin.settings.inviteCodes.topUpForm.invalid");
            return false;
        }
        IsToppingUp = true;
        FormError = "";
        bool done = false;
        try
        {
            await codes.TopUpAsync(actor, row.Id, amount);
            Say("web.admin.settings.hooks.inviteCodes.toppedUp");
            done = true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not top up an invite code");
            FormError = AppText.Get("web.admin.settings.hooks.inviteCodes.topUpFailed", ("detail",
                AdminFailures.Describe(ex, AppText.Get("web.admin.settings.hooks.inviteCodes.topUpUnconfirmed"))));
        }
        finally
        {
            IsToppingUp = false;
        }
        await RestartAsync();
        if (done)
        {
            TopUpRow = null;
        }
        return done;
    }

    /// <summary>Delete, asked first as the web's: the code no longer signs anyone up.</summary>
    public async Task DeleteAsync(AdminInviteCodeRow row)
    {
        if (_actor is not { } actor || !await confirmation.AskAsync(AppText.Get("web.admin.settings.inviteCodes.delete.title"),
            AppText.Get("web.admin.settings.inviteCodes.delete.description", ("code", row.Code)),
            AppText.Get("web.admin.settings.inviteCodes.delete.confirm")))
        {
            return;
        }
        try
        {
            await codes.DeleteAsync(actor, row.Id);
            Say("web.admin.settings.hooks.inviteCodes.deleted");
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not delete an invite code");
            Fail(AdminFailures.Describe(ex, AppText.Get("web.admin.settings.hooks.inviteCodes.deleteFailed")));
        }
        await RestartAsync();
    }

    /// <summary>What Copy did, said as the web's toast.</summary>
    public void Copied(bool copied)
    {
        if (copied)
        {
            Say("web.admin.settings.inviteCodes.copied");
        }
        else
        {
            Fail(AppText.Get("web.admin.settings.inviteCodes.copyFailed"));
        }
    }

    private void Say(string key)
    {
        Message = AppText.Get(key);
        MessageIsError = false;
    }

    private void Fail(string text)
    {
        Message = text;
        MessageIsError = true;
    }

    private async Task ReadSignupsAsync(CancellationToken cancellationToken)
    {
        if (_actor is not { } actor)
        {
            return;
        }
        try
        {
            var switches = await users.GetServerSwitchesAsync(actor, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                SignupsEnabled = switches.SignupsEnabled;
            }
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            // Neither note shows, as on the web while the settings are unknown.
            logger.LogWarning(ex, "Could not read whether public signups are on");
        }
    }

    private async Task ReadPageAsync(CancellationToken cancellationToken)
    {
        if (_actor is not { } actor || IsLoading)
        {
            return;
        }
        int read = _reads;
        IsLoading = true;
        LoadFailed = false;
        try
        {
            var page = await codes.ListPageAsync(actor, _cursor, cancellationToken);
            if (cancellationToken.IsCancellationRequested || read != _reads)
            {
                return;
            }
            foreach (var code in page.Items)
            {
                Rows.Add(new AdminInviteCodeRow(code));
            }
            _cursor = page.Page?.NextCursor;
            HasMore = page.Page is { HasMore: true };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The tab left.
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex) && read == _reads)
        {
            logger.LogWarning(ex, "Could not read the invite codes");
            LoadFailed = true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            // A newer read took over.
            logger.LogDebug(ex, "An earlier read of the invite codes ended");
        }
        finally
        {
            if (read == _reads)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }
}

/// <summary>One invite code as the web's table shows it: the code, its label or a dash, "3 / 10" (red once used up), the switch and badge, the date.</summary>
public sealed record AdminInviteCodeRow(AdminInviteCode Source)
{
    /// <summary>Shown in the Label column for a code without a label, as the web's <c>NO_LABEL</c>.</summary>
    private const string NoLabel = "-";

    public string Id => Source.Id;

    public string Code => Source.Code;

    public int UseCount => Source.UseCount;

    public int MaxUses => Source.MaxUses;

    public bool Enabled => Source.Enabled;

    public bool HasLabel => Source.Label.Length > 0;

    public string LabelText => HasLabel ? Source.Label : NoLabel;

    public string UsageText => AppText.Get("web.admin.settings.inviteCodes.usageCount", ("used", Source.UseCount), ("max", Source.MaxUses));

    public bool IsUsedUp => Source.UseCount >= Source.MaxUses;

    public string StatusText => AppText.Get(Source.Enabled ? "web.admin.settings.inviteCodes.status.active" : "web.admin.settings.inviteCodes.status.disabled");

    public string CreatedText => DateText.Date(Source.CreatedAt.LocalDateTime);

    public string TopUpName => AppText.Get("web.admin.settings.inviteCodes.topUpLabel", ("code", Source.Code));

    public string DeleteName => AppText.Get("web.admin.settings.inviteCodes.deleteLabel", ("code", Source.Code));

    public override string ToString() => $"AdminInviteCodeRow {{ Id = {Id} }}";
}
