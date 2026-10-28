using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Core.Servers;

namespace Granary.Tests.Fakes;

/// <summary>
/// Wraps <see cref="FakeSessionManager"/> and also keeps the profile tokens passed to
/// <see cref="SelectProfileAsync"/>, which the shared fake only records as present or absent.
/// </summary>
internal sealed class ProfileSelectionRecorder(FakeSessionManager inner) : ISessionManager
{
    public FakeSessionManager Inner { get; } = inner;

    public List<string?> ProfileTokens { get; } = [];

    public SessionState State => Inner.State;

    public ServerEntry? ActiveServer => Inner.ActiveServer;

    public Account? Account => Inner.Account;

    public string? ProfileId => Inner.ProfileId;

    public event EventHandler? Changed
    {
        add => Inner.Changed += value;
        remove => Inner.Changed -= value;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Inner.InitializeAsync(cancellationToken);

    public Task ActivateServerAsync(string serverEntryId, CancellationToken cancellationToken = default) =>
        Inner.ActivateServerAsync(serverEntryId, cancellationToken);

    public Task<Account> SignInWithPasswordAsync(ServerEntry server, string username, string password, CancellationToken cancellationToken = default) =>
        Inner.SignInWithPasswordAsync(server, username, password, cancellationToken);

    public Task SignInWithTokensAsync(ServerEntry server, TokenPair tokens, CancellationToken cancellationToken = default) =>
        Inner.SignInWithTokensAsync(server, tokens, cancellationToken);

    /// <summary>When set, <see cref="SelectProfileAsync"/> waits for it, so a test can act while a selection runs.</summary>
    public Task? SelectGate { get; set; }

    /// <summary>The server each selection was made for.</summary>
    public List<string> ProfileServers { get; } = [];

    public Task<bool> ReopenServerAsync(string serverEntryId, string? serverId, CancellationToken cancellationToken = default) =>
        Inner.ReopenServerAsync(serverEntryId, serverId, cancellationToken);

    public async Task SelectProfileAsync(string serverEntryId, string profileId, string? profileToken, CancellationToken cancellationToken = default)
    {
        ProfileTokens.Add(profileToken);
        ProfileServers.Add(serverEntryId);
        if (SelectGate is { } gate)
        {
            await gate.WaitAsync(cancellationToken);
        }
        await Inner.SelectProfileAsync(serverEntryId, profileId, profileToken, cancellationToken);
    }

    public Task ClearProfileAsync(CancellationToken cancellationToken = default) => Inner.ClearProfileAsync(cancellationToken);

    public Task RefreshAccountAsync(CancellationToken cancellationToken = default) => Inner.RefreshAccountAsync(cancellationToken);

    public Task SignOutAsync(string? serverEntryId = null, CancellationToken cancellationToken = default) =>
        Inner.SignOutAsync(serverEntryId, cancellationToken);
}
