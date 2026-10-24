namespace Granary.Core.Api.Discovery;

/// <summary><c>GET /api/v2/system/info</c>: public server and contract identity (docs/api-contract.md, 3.1).</summary>
public sealed record SystemInfo(long ApiMajor, string? ContractDigest, string? ServerVersion, SystemInfoLinks? Links);

public sealed record SystemInfoLinks(string? Capabilities, string? Identity, string? Openapi);

/// <summary><c>GET /api/v2/system/identity</c>: the deployment's stable, self-asserted id.</summary>
public sealed record ServerIdentity(string ServerId);

/// <summary><c>GET /api/v2/system/setup</c>.</summary>
public sealed record SetupStatus(bool NeedsSetup, bool WizardCompleted);

/// <summary><c>GET /api/v2/theme/branding</c>: public pre-login branding; Granary uses the server name.</summary>
public sealed record Branding(string ServerName, string? LoginSubtitle);
