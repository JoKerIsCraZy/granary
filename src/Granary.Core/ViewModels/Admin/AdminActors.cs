using Granary.Core.Api.Admin;
using Granary.Core.Auth;

namespace Granary.Core.ViewModels.Admin;

/// <summary>Takes the <see cref="AdminActor"/> a page or dialog acts as, when it opens.</summary>
public static class AdminActors
{
    /// <summary>The signed-in account and the profile acting on the active server; null while there is none.</summary>
    public static AdminActor? Of(ISessionManager session) =>
        session is { ActiveServer: { } server, Account: { Id: { Length: > 0 } accountId }, ProfileId: { Length: > 0 } profileId }
            ? new AdminActor(server.BaseUri, accountId, profileId)
            : null;
}
