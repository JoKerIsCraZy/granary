using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Core.Servers;

namespace Granary.Tests.Fakes;

/// <summary>Records what view models ask of the session; state is set directly by the test.</summary>
internal sealed class FakeSessionManager : ISessionManager
{
    public SessionState State { get; set; } = SessionState.NoServer;

    public ServerEntry? ActiveServer { get; set; }

    public Account? Account { get; set; }

    public string? ProfileId { get; set; }

    public List<string> Calls { get; } = [];

    /// <summary>When set, sign-in and profile calls throw it instead of succeeding.</summary>
    public Exception? FailWith { get; set; }

    public event EventHandler? Changed;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Record("initialize");

    public Task ActivateServerAsync(string serverEntryId, CancellationToken cancellationToken = default) =>
        Record($"activate:{serverEntryId}");

    public async Task<Account> SignInWithPasswordAsync(ServerEntry server, string username, string password, CancellationToken cancellationToken = default)
    {
        await Record($"password:{server.Id}:{username}");
        Account = new Account("1", username, "", "user", [], true, false, null);
        ActiveServer = server;
        State = SessionState.ProfileRequired;
        Changed?.Invoke(this, EventArgs.Empty);
        return Account;
    }

    public async Task SignInWithTokensAsync(ServerEntry server, TokenPair tokens, CancellationToken cancellationToken = default)
    {
        await Record($"tokens:{server.Id}:{tokens.User.Username}");
        Account = tokens.User;
        ActiveServer = server;
        State = SessionState.ProfileRequired;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The servers <see cref="ReopenServerAsync"/> finds signed in.</summary>
    public HashSet<string> SignedInServers { get; } = [];

    /// <summary>When set, answers <see cref="ReopenServerAsync"/> instead of <see cref="SignedInServers"/>, so a test can look on.</summary>
    public Func<string, bool>? ReopenAnswer { get; set; }

    public async Task<bool> ReopenServerAsync(string serverEntryId, string? serverId, CancellationToken cancellationToken = default)
    {
        await Record($"reopen:{serverEntryId}:{serverId ?? "unknown"}");
        return ReopenAnswer?.Invoke(serverEntryId) ?? SignedInServers.Contains(serverEntryId);
    }

    public async Task SelectProfileAsync(string serverEntryId, string profileId, string? profileToken, CancellationToken cancellationToken = default)
    {
        if (ActiveServer?.Id != serverEntryId)
        {
            throw new ActiveServerChangedException();
        }
        await Record($"profile:{profileId}:{(profileToken is null ? "no-token" : "token")}");
        ProfileId = profileId;
        State = SessionState.Ready;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task ClearProfileAsync(CancellationToken cancellationToken = default) => Record("clear-profile");

    public Task RefreshAccountAsync(CancellationToken cancellationToken = default) => Record("refresh-account");

    public Task SignOutAsync(string? serverEntryId = null, CancellationToken cancellationToken = default) =>
        Record($"sign-out:{serverEntryId ?? "active"}");

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private Task Record(string call)
    {
        Calls.Add(call);
        return FailWith is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}
