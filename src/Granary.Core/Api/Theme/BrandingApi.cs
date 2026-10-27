namespace Granary.Core.Api.Theme;

/// <summary><c>GET /api/v2/theme/branding</c>: how the server presents itself, before anyone signs in.</summary>
public sealed record ServerBranding
{
    public string? ServerName { get; init; }

    public string? LoginSubtitle { get; init; }

    /// <summary>The admin's accent, as stored: free text, of which only <c>#rgb</c> and <c>#rrggbb</c> are honoured.</summary>
    public string? AccentColor { get; init; }

    public string? WordmarkUrl { get; init; }

    public string? MarkUrl { get; init; }
}

/// <summary>The server's branding (docs/api-contract.md): public, so it is read without a sign-in.</summary>
public sealed class BrandingApi(SiloHttp http)
{
    public Task<ServerBranding> GetAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/theme/branding", SiloAuth.Anonymous, SiloJsonContext.Default.ServerBranding, cancellationToken);
}
