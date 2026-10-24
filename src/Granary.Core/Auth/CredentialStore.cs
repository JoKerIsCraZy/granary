using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Auth;

/// <summary>
/// Keeps each server's credentials as one JSON record in the <see cref="ISecretStore"/> and caches them
/// in memory, because the authentication handler reads them on every request.
/// </summary>
public sealed class CredentialStore(ISecretStore secrets, ILogger<CredentialStore> logger) : ICredentialStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, SessionCredentials?> _cache = new(StringComparer.Ordinal);

    public event EventHandler<string>? Changed;

    public async Task<SessionCredentials?> GetAsync(string serverEntryId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAsync(serverEntryId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string serverEntryId, SessionCredentials credentials, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync(serverEntryId, credentials, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, serverEntryId);
    }

    public async Task<SessionCredentials?> UpdateAsync(string serverEntryId, Func<SessionCredentials, SessionCredentials> update, CancellationToken cancellationToken = default)
    {
        SessionCredentials? current;
        SessionCredentials? next;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = await LoadAsync(serverEntryId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return null;
            }
            next = update(current);
            if (next == current)
            {
                return current;
            }
            await SaveAsync(serverEntryId, next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, serverEntryId);
        return next;
    }

    public async Task RemoveAsync(string serverEntryId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await secrets.RemoveAsync(Key(serverEntryId), cancellationToken).ConfigureAwait(false);
            _cache[serverEntryId] = null;
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, serverEntryId);
    }

    private async Task<SessionCredentials?> LoadAsync(string serverEntryId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(serverEntryId, out var cached))
        {
            return cached;
        }
        string? json = await secrets.GetAsync(Key(serverEntryId), cancellationToken).ConfigureAwait(false);
        SessionCredentials? credentials = null;
        if (json is not null)
        {
            try
            {
                credentials = JsonSerializer.Deserialize(json, CredentialJsonContext.Default.SessionCredentials);
            }
            catch (JsonException)
            {
                // Never log the record itself: it holds the tokens.
                logger.LogWarning("Stored credentials for server {ServerEntryId} are unreadable; treating it as signed out", serverEntryId);
            }
        }
        _cache[serverEntryId] = credentials;
        return credentials;
    }

    private async Task SaveAsync(string serverEntryId, SessionCredentials credentials, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(credentials, CredentialJsonContext.Default.SessionCredentials);
        await secrets.SetAsync(Key(serverEntryId), json, cancellationToken).ConfigureAwait(false);
        _cache[serverEntryId] = credentials;
    }

    private static string Key(string serverEntryId) => $"server/{serverEntryId}/session";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionCredentials))]
internal sealed partial class CredentialJsonContext : JsonSerializerContext;
