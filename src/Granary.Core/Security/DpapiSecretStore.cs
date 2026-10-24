using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Core.Security;

/// <summary>
/// Stores all secrets in one file encrypted with DPAPI for the current Windows user. The file lives
/// in local (non-roaming) app data, so tokens never leave this machine and account. A file that can no
/// longer be decrypted (another Windows user, a reset profile, corruption) is set aside as
/// <c>.unreadable-&lt;time&gt;</c> and the store starts empty, so the user can sign in again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string filePath, ILogger<DpapiSecretStore>? logger = null) : ISecretStore
{
    // Binds the ciphertext to this purpose, so other DPAPI blobs of the same user cannot be swapped in.
    private static readonly byte[] Entropy = "Granary.SecretStore.v1"u8.ToArray();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string DefaultPath =>
        Path.Combine(AppPaths.DataDirectory, "secrets.bin");

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secrets = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return secrets.GetValueOrDefault(key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        UpdateAsync(secrets => secrets[key] = value, cancellationToken);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        UpdateAsync(secrets => secrets.Remove(key), cancellationToken);

    private async Task UpdateAsync(Action<Dictionary<string, string>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secrets = await LoadAsync(cancellationToken).ConfigureAwait(false);
            change(secrets);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(secrets, SiloJsonContext.Default.DictionaryStringString);
            byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            string temp = filePath + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);
                // On disk before the rename: a torn file would sign every server out.
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        byte[] cipher = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            return SetAside(ex);
        }
        try
        {
            var secrets = JsonSerializer.Deserialize(plain, SiloJsonContext.Default.DictionaryStringString);
            return new Dictionary<string, string>(secrets ?? [], StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            return SetAside(ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private Dictionary<string, string> SetAside(Exception reason)
    {
        (logger ?? NullLogger<DpapiSecretStore>.Instance).LogError(
            "The secret store cannot be read ({Reason}); setting it aside, every server needs a new sign-in", reason.GetType().Name);
        File.Move(filePath, $"{filePath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmssfff}", overwrite: true);
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
