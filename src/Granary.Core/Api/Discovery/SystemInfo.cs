namespace Granary.Core.Api.Discovery;

/// <summary><c>GET /api/v2/system/info</c>: public server and contract identity (docs/api-contract.md, 3.1).</summary>
public sealed record SystemInfo(long ApiMajor, string? ContractDigest, string? ServerVersion, SystemInfoLinks? Links);

public sealed record SystemInfoLinks(string? Capabilities, string? Identity, string? Openapi);

/// <summary><c>GET /api/v2/system/identity</c>: the deployment's stable, self-asserted id.</summary>
public sealed record ServerIdentity(string ServerId);
