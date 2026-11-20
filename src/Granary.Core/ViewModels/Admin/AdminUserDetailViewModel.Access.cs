using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The Library access card, as the web's <c>LibraryAccessCard</c>: the access group, the libraries (the group's, or chosen
/// for the account), and the two permissions. Only the Owner changes an admin's; an admin has no group.
/// </summary>
public sealed partial class AdminUserDetailViewModel
{
    private IReadOnlyList<Api.Admin.AdminLibrary> _libraries = [];

    // Until the libraries are read, the account's own stay as they are: an empty list sent would take them all away.
    private bool _librariesRead;

    public AdminEditCard AccessCard { get; } = new();

    /// <summary>No group, then each group.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AdminGroupFilter> GroupChoices { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessChanges), nameof(InheritLibrariesText))]
    public partial AdminGroupFilter? SelectedGroup { get; set; }

    /// <summary>The account has libraries of its own instead of its group's (or the server default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessChanges), nameof(UsesGroupLibraries), nameof(HasNoLibraryWarning))]
    public partial bool ChoosesLibraries { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessChanges))]
    public partial bool MarkerEdit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessChanges))]
    public partial bool MetadataCuration { get; set; }

    /// <summary>The libraries to choose from, each ticked when the account has it.</summary>
    public ObservableCollection<AdminLibraryChoice> Libraries { get; } = [];

    /// <summary>The other choice of the libraries: the group's, or the server default without one.</summary>
    public bool UsesGroupLibraries
    {
        get => !ChoosesLibraries;
        set
        {
            if (value)
            {
                ChoosesLibraries = false;
            }
        }
    }

    public string InheritLibrariesText => AppText.Get(!IsAdminAccount && SelectedGroup is { Value: not AdminGroupFilter.None }
        ? "web.admin.users.detail.access.library.useGroupLibraries"
        : "web.admin.users.detail.access.library.useServerDefault");

    public bool IsAdminAccount => Account is { IsAdmin: true };

    /// <summary>The fields take changes: the viewer may change the account's access.</summary>
    public bool CanEditAccess => Header is { PolicyManageable: true } && Capabilities.Available;

    public bool CanPickGroup => CanEditAccess && !IsAdminAccount;

    /// <summary>The account's own libraries are none: it won't see any.</summary>
    public bool HasNoLibraryWarning => ChoosesLibraries && Libraries.All(library => !library.IsChecked);

    public bool HasAccessChanges => Account is { } account
        && (GroupOf(account) != SelectedValue || LibrariesChanged(account)
            || MarkerEdit != Has(account, AdminPermissions.MarkerEdit) || MetadataCuration != Has(account, AdminPermissions.MetadataCuration));

    /// <summary>The libraries can be chosen: they were read.</summary>
    public bool CanChooseLibraries => CanEditAccess && _librariesRead;

    [RelayCommand]
    private Task SaveAccessAsync()
    {
        if (Account is not { } account)
        {
            return Task.CompletedTask;
        }
        bool groupChanged = !account.IsAdmin && GroupOf(account) != SelectedValue;
        bool librariesChanged = LibrariesChanged(account);
        var permissions = account.Permissions
            .Where(permission => permission is not (AdminPermissions.MarkerEdit or AdminPermissions.MetadataCuration))
            .Concat(MarkerEdit ? [AdminPermissions.MarkerEdit] : [])
            .Concat(MetadataCuration ? [AdminPermissions.MetadataCuration] : [])
            .ToList();
        bool permissionsChanged = MarkerEdit != Has(account, AdminPermissions.MarkerEdit) || MetadataCuration != Has(account, AdminPermissions.MetadataCuration);
        var change = new AdminUserChange
        {
            SetsGroup = groupChanged,
            AccessGroupId = SelectedValue,
            SetsLibraries = librariesChanged,
            LibraryIds = ChoosesLibraries ? ChosenIds() : null,
            Permissions = permissionsChanged ? permissions : null,
        };
        return SaveAsync(AccessCard, change, validation: null);
    }

    [RelayCommand]
    private Task CancelAccessAsync() => CancelAsync(AccessCard, () => ResetAccess(null));

    [RelayCommand]
    private Task ReviewAccessAsync() => ReviewAsync(AccessCard);

    private string? SelectedValue => SelectedGroup is { Value: not AdminGroupFilter.None } group ? group.Value : null;

    private bool LibrariesChanged(AdminUser account) => _librariesRead
        && (ChoosesLibraries != account.LibraryIds is not null
            || (ChoosesLibraries && !ChosenIds().ToHashSet(StringComparer.Ordinal).SetEquals(account.LibraryIds ?? [])));

    private static string? GroupOf(AdminUser account) => account.IsAdmin ? null : account.AccessGroupId;

    private static bool Has(AdminUser account, string permission) => account.Permissions.Contains(permission);

    private List<string> ChosenIds() => [.. Libraries.Where(library => library.IsChecked).Select(library => library.Id)];

    // The parts edited on the account read before (null where untouched), to keep over a new read.
    private sealed record AccessEdits(bool GroupEdited, string? Group, bool LibrariesEdited, IReadOnlyList<string>? Libraries, bool? MarkerEdit,
        bool? MetadataCuration);

    // The account as the Library access card sends it; an admin's group stays as it was, as the card never sends one.
    private AdminUser SentAccess(AdminUser account) => account with
    {
        AccessGroupId = account.IsAdmin ? account.AccessGroupId : SelectedValue,
        LibraryIds = !_librariesRead ? account.LibraryIds : ChoosesLibraries ? ChosenIds() : null,
        Permissions = [.. account.Permissions.Where(permission => permission is not (AdminPermissions.MarkerEdit or AdminPermissions.MetadataCuration)),
            .. MarkerEdit ? [AdminPermissions.MarkerEdit] : Array.Empty<string>(),
            .. MetadataCuration ? [AdminPermissions.MetadataCuration] : Array.Empty<string>()],
    };

    private AccessEdits? AccessDraft(AdminUser? before) => before is null ? null : new AccessEdits(
        GroupOf(before) != SelectedValue, SelectedValue,
        LibrariesChanged(before), ChoosesLibraries ? ChosenIds() : null,
        MarkerEdit != Has(before, AdminPermissions.MarkerEdit) ? MarkerEdit : null,
        MetadataCuration != Has(before, AdminPermissions.MetadataCuration) ? MetadataCuration : null);

    private void ResetAccess(AccessEdits? edits)
    {
        if (Account is not { } account)
        {
            NotifyAccess();
            return;
        }
        var libraries = edits is { LibrariesEdited: true } ? edits.Libraries : account.LibraryIds;
        string? group = edits is { GroupEdited: true } ? edits.Group : account.AccessGroupId;
        GroupChoices = [new(AdminGroupFilter.None, AppText.Get("web.admin.users.detail.access.library.noGroup")),
            .. Groups.Select(known => new AdminGroupFilter(known.Id, known.Name)),
            // A group made after the list read its groups stays the account's, never quietly No group.
            .. account.AccessGroupId is { } own && Groups.All(known => known.Id != own)
                ? [new AdminGroupFilter(own, AppText.Get("web.admin.users.detail.header.groupIdFallback", ("id", own)))]
                : Array.Empty<AdminGroupFilter>()];
        SelectedGroup = GroupChoices.FirstOrDefault(choice => choice.Value == group) ?? GroupChoices[0];
        ChoosesLibraries = libraries is not null;
        MarkerEdit = edits?.MarkerEdit ?? Has(account, AdminPermissions.MarkerEdit);
        MetadataCuration = edits?.MetadataCuration ?? Has(account, AdminPermissions.MetadataCuration);
        ShowLibraries(libraries);
        NotifyAccess();
    }

    private void ShowLibraries(IReadOnlyList<string>? own)
    {
        foreach (var library in Libraries)
        {
            library.PropertyChanged -= OnLibraryTicked;
        }
        Libraries.Clear();
        own ??= [];
        foreach (var library in _libraries.OrderBy(library => library.SortOrder).ThenBy(library => library.Name, StringComparer.CurrentCulture))
        {
            var choice = new AdminLibraryChoice(library.Id, library.Name) { IsChecked = own.Contains(library.Id) };
            choice.PropertyChanged += OnLibraryTicked;
            Libraries.Add(choice);
        }
    }

    private void OnLibraryTicked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAccessChanges));
        OnPropertyChanged(nameof(HasNoLibraryWarning));
    }

    private void NotifyAccess()
    {
        OnPropertyChanged(nameof(HasAccessChanges));
        OnPropertyChanged(nameof(IsAdminAccount));
        OnPropertyChanged(nameof(CanEditAccess));
        OnPropertyChanged(nameof(CanPickGroup));
        OnPropertyChanged(nameof(CanChooseLibraries));
        OnPropertyChanged(nameof(InheritLibrariesText));
        OnPropertyChanged(nameof(HasNoLibraryWarning));
    }

    private async Task ReadLibrariesAsync()
    {
        if (_actor is not { } actor)
        {
            return;
        }
        try
        {
            _libraries = (await _admin.ListLibrariesAsync(actor)).Items;
            _librariesRead = true;
            if (Account is { } account)
            {
                ShowLibraries(account.LibraryIds);
            }
            NotifyAccess();
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            // The card shows the libraries it has; its other parts still work.
            _logger.LogWarning(ex, "Could not read the libraries for an account's access");
        }
    }
}

/// <summary>A library the account may be given, ticked when it has it.</summary>
public sealed partial class AdminLibraryChoice(string id, string name) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}
