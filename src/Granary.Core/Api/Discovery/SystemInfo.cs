namespace Granary.Core.Api.Discovery;

/// <summary><c>GET /api/v2/system/info</c>: public server and contract identity (docs/api-contract.md, 3.1).</summary>
public sealed record SystemInfo(long ApiMajor, string? ContractDigest, string? ServerVersion, SystemInfoLinks? Links);

public sealed record SystemInfoLinks(string? Capabilities, string? Identity, string? Openapi);

/// <summary><c>GET /api/v2/system/identity</c>: the deployment's stable, self-asserted id.</summary>
public sealed record ServerIdentity(string ServerId);

/// <summary>
/// <c>GET /api/v2/system/connections</c>: how a signed-in client may reach the server (docs/api-contract.md, 9.7).
/// <paramref name="LocalRoute"/> is absent when the API port does not answer TLS.
/// </summary>
public sealed record ServerConnections(string ServerId, ServerLocalRoute? LocalRoute);

/// <param name="SpkiSha256">Base64 SHA-256 of the SubjectPublicKeyInfo the API port presents over TLS.</param>
/// <param name="Url">The administrator's <c>server.local_url</c>; tried as HTTPS on its host and port, whatever its scheme.</param>
public sealed record ServerLocalRoute(string SpkiSha256, string? Url);

/// <summary><c>GET /api/v2/system/setup</c>.</summary>
public sealed record SetupStatus(bool NeedsSetup, bool WizardCompleted);

/// <summary><c>GET /api/v2/theme/branding</c>: public pre-login branding; Granary uses the server name.</summary>
public sealed record Branding(string ServerName, string? LoginSubtitle);
