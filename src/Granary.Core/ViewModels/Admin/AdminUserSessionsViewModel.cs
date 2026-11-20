using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// An account's sign-ins, as the web's <c>LoginSessionsPanel</c> shows them to an administrator: the browsers and apps
/// signed in to it, a page of 50 at a time, each signed out on its own (asked first), or all of them with Sign out
/// everywhere. Its password and saved device settings stay. Offered where the server says <c>login_sessions</c>.
/// </summary>
public sealed partial class AdminUserSessionsViewModel(AdminUsersApi users, IConfirmation confirmation, TimeProvider time,
    ILogger<AdminUserSessionsViewModel> logger) : ObservableObject
{
    private AdminUser _account = new();
    private string? _cursor;
    // Counts the list's renewals: a page asked for before one (a sign-out everywhere, a fresh start) is dropped.
    private int _generation;
    // The account and profile this was opened as: everything it sends goes only as them.
    private AdminActor? _actor;

    public ObservableCollection<SignedInSessionRow> Sessions { get; } = [];

    [ObservableProperty]
    public partial LibraryLoadState State { get; private set; } = LibraryLoadState.Idle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; private set; }

    /// <summary>The viewer may sign the account out: the web's ownerOnly note otherwise.</summary>
    [ObservableProperty]
    public partial bool Manageable { get; private set; }

    [ObservableProperty]
    public partial string Notice { get; private set; } = "";

    [ObservableProperty]
    public partial bool NoticeIsError { get; private set; }

    public string Description => AppText.Get("web.settings.sessions.adminDescription", ("username", _account.Username));

    public bool IsEmpty => State == LibraryLoadState.Ready && Sessions.Count == 0;

    /// <summary>Reads the first page of the account's sign-ins.</summary>
    public Task OpenAsync(AdminActor? actor, AdminUser account, bool manageable)
    {
        _account = account;
        _actor = actor;
        Manageable = manageable;
        OnPropertyChanged(nameof(Description));
        return ReadFirstAsync();
    }

    /// <summary>Retry, as the account and profile the panel was opened as.</summary>
    [RelayCommand]
    private Task RetryAsync() => ReadFirstAsync();

    private async Task ReadFirstAsync()
    {
        _generation++;
        Sessions.Clear();
        _cursor = null;
        State = LibraryLoadState.Loading;
        await ReadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasMore))]
    private Task LoadMoreAsync() => ReadAsync();

    [RelayCommand]
    private async Task SignOutAsync(SignedInSessionRow? row)
    {
        if (row is null || _actor is not { } actor || !Manageable || row.IsBusy || !await confirmation.AskAsync(
            row.Name.Length > 0 ? AppText.Get("web.settings.sessions.confirm.deviceTitle", ("device", row.Name))
                : AppText.Get("web.settings.sessions.confirm.sessionTitle"),
            $"{AppText.Get("web.settings.sessions.confirm.adminClientDescription", ("username", _account.Username))} {AppText.Get("web.settings.sessions.confirm.note")}",
            AppText.Get("common.signOut")))
        {
            return;
        }
        row.IsBusy = true;
        try
        {
            await users.SignOutSessionAsync(actor, _account.Id, row.Id);
            Sessions.Remove(row);
            OnPropertyChanged(nameof(IsEmpty));
            Say(AppText.Get("web.settings.sessions.signedOut"), isError: false);
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not sign an account's session out");
            Say(AdminFailures.Describe(ex, AppText.Get("web.settings.sessions.signOutFailed")), isError: true);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutEverywhereAsync()
    {
        if (_actor is not { } actor || !Manageable || !await confirmation.AskAsync(
            AppText.Get("web.settings.sessions.confirm.allTitle", ("username", _account.Username)),
            AppText.Get("web.settings.sessions.confirm.note"),
            AppText.Get("web.settings.sessions.signOutEverywhere")))
        {
            return;
        }
        try
        {
            await users.SignOutEverywhereAsync(actor, _account.Id);
            _generation++;
            Sessions.Clear();
            HasMore = false;
            OnPropertyChanged(nameof(IsEmpty));
            Say(AppText.Get("web.settings.sessions.signedOutEverywhere", ("username", _account.Username)), isError: false);
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not sign an account out everywhere");
            Say(AdminFailures.Describe(ex, AppText.Get("web.settings.sessions.signOutFailed")), isError: true);
        }
    }

    private async Task ReadAsync()
    {
        if (_actor is not { } actor)
        {
            return;
        }
        int generation = _generation;
        try
        {
            var page = await users.ListLoginSessionsAsync(actor, _account.Id, _cursor);
            if (generation != _generation)
            {
                return;
            }
            var now = time.GetUtcNow();
            var known = Sessions.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in page.Items.Where(item => known.Add(item.Id)))
            {
                Sessions.Add(new SignedInSessionRow(item, now, time.LocalTimeZone));
            }
            string? next = page.Page is { HasMore: true, NextCursor: { Length: > 0 } cursor } && cursor != _cursor ? cursor : null;
            _cursor = next;
            HasMore = next is not null;
            State = LibraryLoadState.Ready;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            if (generation != _generation)
            {
                return;
            }
            logger.LogWarning(ex, "Could not read an account's sessions");
            if (Sessions.Count > 0)
            {
                // A later page failed: the ones shown stay, and Load more tries the same page again.
                State = LibraryLoadState.Ready;
                Say(AdminFailures.DescribeLoad(ex), isError: true);
            }
            else
            {
                State = LibraryLoadState.Error;
                HasMore = false;
            }
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Say(string text, bool isError)
    {
        Notice = text;
        NoticeIsError = isError;
    }
}
