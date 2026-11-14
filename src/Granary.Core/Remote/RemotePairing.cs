using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Remote;

/// <summary>
/// The receiver's id and its pairing key: one key per PC, kept until Reset Pairing Code, as Silo's Roku app keeps its own
/// (silo-roku's <c>docs/silocontrol.md</c> section 9.2). The key lives only in the <see cref="ISecretStore"/>, which
/// encrypts it for the Windows user, and in memory; the id is not a secret and lives apart from it in
/// <c>phone-remote.json</c>. Kept apart, a lost secret store means a new key under the same id, so a phone says its
/// pairing code has changed instead of keeping a dead receiver beside a new one. Nothing here logs a value.
/// </summary>
public sealed class RemotePairing
{
    /// <summary>Where the key is kept in the secret store, in base64.</summary>
    public const string SecretKey = "phone-remote/pairing-key";

    private readonly ISecretStore _secrets;
    private readonly string _idFilePath;
    private readonly ILogger<RemotePairing> _logger;

    // Load, create and reset one at a time, so two callers never draw two keys.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PairingMaterial? _material;

    public RemotePairing(ISecretStore secrets, string idFilePath, ILogger<RemotePairing> logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrWhiteSpace(idFilePath);
        ArgumentNullException.ThrowIfNull(logger);
        _secrets = secrets;
        _idFilePath = Path.GetFullPath(idFilePath);
        _logger = logger;
    }

    /// <summary><c>phone-remote.json</c> in the data folder: a second Granary on its own folder has its own id and key.</summary>
    public static string DefaultIdFilePath => Path.Combine(AppPaths.DataDirectory, "phone-remote.json");

    /// <summary>Raised after <see cref="ResetKeyAsync"/> drew a new key, so the receiver starts again under it.</summary>
    public event EventHandler? KeyChanged;

    /// <summary>
    /// The receiver's id and key: made the first time, read from then on, and the same for the rest of the run. A key the
    /// store does not have, or has in a form that is not 16 bytes, is drawn anew; so is an id file that cannot be read.
    /// </summary>
    /// <exception cref="IOException">The id file exists but the disk did not give it, or the secret store's file failed.</exception>
    /// <exception cref="UnauthorizedAccessException">Windows refused the id file or the secret store's file.</exception>
    /// <exception cref="CryptographicException">Windows could not encrypt a new key for the secret store.</exception>
    public async Task<PairingMaterial> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_material is null)
            {
                string receiverId = await LoadOrCreateIdAsync(cancellationToken).ConfigureAwait(false);
                byte[]? key = ReadKey(await _secrets.GetAsync(SecretKey, cancellationToken).ConfigureAwait(false));
                _material = key is null
                    ? await CreateKeyAsync(receiverId, cancellationToken).ConfigureAwait(false)
                    : new PairingMaterial(receiverId, key);
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            return _material;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reset Pairing Code: a new key under the same id. Every phone has to scan the new code; this is the only way to take
    /// a phone's access away, since all phones share the one key.
    /// </summary>
    public async Task<PairingMaterial> ResetKeyAsync(CancellationToken cancellationToken)
    {
        PairingMaterial material;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string receiverId = _material?.ReceiverId ?? await LoadOrCreateIdAsync(cancellationToken).ConfigureAwait(false);
            material = await CreateKeyAsync(receiverId, cancellationToken).ConfigureAwait(false);
            _material = material;
        }
        finally
        {
            _gate.Release();
        }
        _logger.LogInformation("The phone remote's pairing code was reset");
        NotifyKeyChanged();
        return material;
    }

    // Each listener by itself, after the key changed: one that throws must neither undo the reset for the caller nor keep
    // the news from the others. Only the exception's type is logged.
    private void NotifyKeyChanged()
    {
        if (KeyChanged is not { } changed)
        {
            return;
        }
        foreach (var listener in changed.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                listener(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError("A listener of the pairing code's reset failed ({Error})", ex.GetType().Name);
            }
        }
    }

    private async Task<PairingMaterial> CreateKeyAsync(string receiverId, CancellationToken cancellationToken)
    {
        byte[] key = RandomNumberGenerator.GetBytes(SiloCastSealed.KeyBytes);
        try
        {
            await _secrets.SetAsync(SecretKey, Convert.ToBase64String(key), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("The phone remote has a new pairing key");
            return new PairingMaterial(receiverId, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // The stored key, or null when there is none or it is not 16 bytes in base64.
    private byte[]? ReadKey(string? stored)
    {
        if (stored is null)
        {
            return null;
        }
        Span<byte> key = stackalloc byte[SiloCastSealed.KeyBytes];
        try
        {
            if (Convert.TryFromBase64String(stored, key, out int written) && written == SiloCastSealed.KeyBytes)
            {
                return key.ToArray();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        _logger.LogWarning("The phone remote's pairing key could not be read; a new one is made, and every phone scans the new code");
        return null;
    }

    private async Task<string> LoadOrCreateIdAsync(CancellationToken cancellationToken)
    {
        if (await ReadIdAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }
        string receiverId = Guid.NewGuid().ToString("D");
        await WriteIdAsync(receiverId, cancellationToken).ConfigureAwait(false);
        return receiverId;
    }

    // The saved id, or null when there is no file or it holds no id. A disk that fails to read throws: a passing lock
    // must not replace the id every phone keeps the key under.
    private async Task<string?> ReadIdAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_idFilePath))
        {
            return null;
        }
        string text = await File.ReadAllTextAsync(_idFilePath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (JsonSerializer.Deserialize(text, RemotePairingJsonContext.Default.PhoneRemoteFile)?.ReceiverId is { } id
                && Guid.TryParseExact(id, "D", out _))
            {
                return id;
            }
        }
        catch (JsonException)
        {
            // Not JSON, or not the file's shape; the content is not logged.
        }
        _logger.LogWarning("This PC's phone remote id could not be read; a new one is made, and every phone scans the new code");
        return null;
    }

    // Written whole beside the file and moved over it, as the other per-PC files are. A write that fails keeps the id for
    // this run only.
    private async Task WriteIdAsync(string receiverId, CancellationToken cancellationToken)
    {
        string temp = $"{_idFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_idFilePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, new PhoneRemoteFile(receiverId), RemotePairingJsonContext.Default.PhoneRemoteFile,
                    cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, _idFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("This PC's phone remote id could not be saved ({Error}); phones have to scan the code again after the next start",
                ex.GetType().Name);
            try
            {
                File.Delete(temp);
            }
            catch (Exception deleteFailure) when (deleteFailure is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("A half-written phone remote id file stays behind ({Error})", deleteFailure.GetType().Name);
            }
        }
    }
}

/// <summary>
/// The receiver's id and its pairing key, as the receiver uses them. <see cref="ToString"/> shows the receiver's id and
/// nothing else; the key is only ever read through <see cref="Key"/>.
/// </summary>
public sealed class PairingMaterial
{
    private readonly byte[] _key;

    public PairingMaterial(string receiverId, byte[] key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverId);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != SiloCastSealed.KeyBytes)
        {
            throw new ArgumentException("A pairing key is 16 bytes.", nameof(key));
        }
        ReceiverId = receiverId;
        _key = (byte[])key.Clone();
        KeyId = SiloCastSealed.KeyId(_key);
    }

    /// <summary>The receiver's stable id, under which every phone keeps the key.</summary>
    public string ReceiverId { get; }

    /// <summary>The key's public id: TXT <c>kid</c> and <c>sealed_hello.keyId</c>.</summary>
    public string KeyId { get; }

    /// <summary>The 16-byte pairing key.</summary>
    public ReadOnlySpan<byte> Key => _key;

    public override string ToString() => ReceiverId;
}

/// <summary><c>phone-remote.json</c>: <c>{ "receiver_id": "…" }</c>, and nothing secret.</summary>
internal sealed record PhoneRemoteFile(string? ReceiverId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(PhoneRemoteFile))]
internal sealed partial class RemotePairingJsonContext : JsonSerializerContext;
