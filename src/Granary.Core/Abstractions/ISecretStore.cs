namespace Granary.Core.Abstractions;

/// <summary>
/// Persists secrets (tokens, profile tokens) per user and machine. Keys are free-form, for example
/// <c>server/{id}/refresh_token</c>. Values must never be logged.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
