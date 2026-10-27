namespace Granary.Core.ViewModels;

/// <summary>One saved server in the server list. Immutable: the list is rebuilt when anything changes.</summary>
/// <param name="AccountName">The signed-in user, known only for the active server.</param>
public sealed record ServerItemViewModel(string Id, string Name, string Url, bool IsActive, string? AccountName)
{
    public bool CanSwitch => !IsActive;

    public bool HasAccount => AccountName is not null;

    public string AccountText => AccountName is null ? "" : $"Signed in as {AccountName}";

    /// <summary>"https://silo.example · Signed in as elena": the address, and the account on the active server.</summary>
    public string Details => HasAccount ? $"{Url} · {AccountText}" : Url;

    public string SwitchLabel => $"Switch to {Name}";

    public string MoreOptionsLabel => $"More options for {Name}";
}
