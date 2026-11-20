namespace Granary.Core.Api.Admin;

/// <summary>
/// The server, account and profile an administrator's page or dialog was opened as. Every call it makes is bound to them
/// (<see cref="SiloRequestOptions.Account"/>, <see cref="SiloRequestOptions.Profile"/>): once another account or profile
/// acts on the server it is refused unsent (<see cref="ProfileChangedException"/>), so nothing done on the page lands as
/// someone else, as the web's captured admin authority.
/// </summary>
public sealed record AdminActor(Uri Server, string AccountId, string ProfileId);
