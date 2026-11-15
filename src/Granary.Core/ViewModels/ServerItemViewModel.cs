using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>One saved server in the server list. Immutable: the list is rebuilt when anything changes.</summary>
/// <param name="AccountName">The signed-in user, known only for the active server.</param>
public sealed record ServerItemViewModel(string Id, string Name, string Url, bool IsActive, string? AccountName)
{
    public bool CanSwitch => !IsActive;

    public bool HasAccount => AccountName is not null;

    public string AccountText => AccountName is null ? "" : AppText.Get("granary.servers.signedInAs", ("name", AccountName));

    /// <summary>"https://silo.example · Signed in as elena": the address, and the account on the active server.</summary>
    public string Details => AccountName is null ? Url : AppText.Get("granary.servers.details", ("address", Url), ("name", AccountName));

    public string SwitchLabel => AppText.Get("granary.servers.switchTo", ("name", Name));

    public string MoreOptionsLabel => AppText.Get("granary.servers.moreOptionsFor", ("name", Name));
}
