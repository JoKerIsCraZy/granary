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
    /// The material as it is now, or null before it was first loaded. Every handshake reads it at that moment, so a code
    /// reset counts at once, whether or not the news of it reached anyone.
    /// </summary>
    public PairingMaterial? Current => Volatile.Read(ref _material);

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
            var current = Volatile.Read(ref _material);
            if (current is null)
            {
                string receiverId = await LoadOrCreateIdAsync(cancellationToken).ConfigureAwait(false);
                byte[]? key = ReadKey(await _secrets.GetAsync(SecretKey, cancellationToken).ConfigureAwait(false));
                current = key is null
                    ? await CreateKeyAsync(receiverId, cancellationToken).ConfigureAwait(false)
                    : new PairingMaterial(receiverId, key);
                Volatile.Write(ref _material, current);
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            return current;
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
        PairingMaterial? replaced;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            replaced = Volatile.Read(ref _material);
            string receiverId = replaced?.ReceiverId ?? await LoadOrCreateIdAsync(cancellationToken).ConfigureAwait(false);
            material = await CreateKeyAsync(receiverId, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _material, material);
        }
        finally
        {
            _gate.Release();
        }
        // The old key is forgotten: zeroed once no handshake still reads it.
        replaced?.Dispose();
        _logger.LogInformation("The phone remote's pairing code was reset");
        NotifyKeyChanged();
        return material;
    }

    // Each listener by itself, after the key changed: one that throws must neither undo the reset for the caller nor keep
    // the news from the others. The receiver does not rely on this news alone: every handshake and frame reads the
    // material as it is (Current). The failure is logged by its kind and the listener's type, never a value.
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
                _logger.LogError("A listener of the pairing code's reset failed ({Error}, in {Listener})", RemoteLog.Failure(ex),
                    listener.Method.DeclaringType?.Name ?? "an unknown type");
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
/// nothing else. One material is one key: whether the PC's key is still the one a session was keyed under is asked of
/// the material itself (<see cref="Same"/>), never of its 32-bit id. A material that was replaced is forgotten: its key
/// is zeroed once no handshake reads it any more (<see cref="UseKey"/>), and nothing reads it after.
/// </summary>
public sealed class PairingMaterial : IDisposable
{
    private readonly byte[] _key;
    private readonly Lock _gate = new();
    private int _inUse;
    private bool _disposed;
    private bool _zeroed;

    public PairingMaterial(string receiverId, byte[] key)
        : this(receiverId, key, keyId: null)
    {
    }

    /// <summary>With a key id given rather than computed: for a test of two keys whose ids collide.</summary>
    internal PairingMaterial(string receiverId, byte[] key, string? keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverId);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != SiloCastSealed.KeyBytes)
        {
            throw new ArgumentException("A pairing key is 16 bytes.", nameof(key));
        }
        ReceiverId = receiverId;
        _key = (byte[])key.Clone();
        KeyId = keyId ?? SiloCastSealed.KeyId(_key);
    }

    /// <summary>The receiver's stable id, under which every phone keeps the key.</summary>
    public string ReceiverId { get; }

    /// <summary>The key's public id: TXT <c>kid</c> and <c>sealed_hello.keyId</c>. It names the key; it does not tell keys apart.</summary>
    public string KeyId { get; }

    /// <summary>The 16-byte pairing key, for a reader that holds it only for a moment; Granary's own readers use <see cref="UseKey"/>.</summary>
    internal ReadOnlySpan<byte> Key
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _key;
            }
        }
    }

    /// <summary>The key was replaced and is zeroed.</summary>
    internal bool IsForgotten
    {
        get
        {
            lock (_gate)
            {
                return _zeroed;
            }
        }
    }

    /// <summary>Whether two materials are one key: the same material, not merely the same id.</summary>
    public static bool Same(PairingMaterial? left, PairingMaterial? right) => left is not null && ReferenceEquals(left, right);

    /// <summary>Holds the key for one handshake or one pairing link; it stays whole until disposed. Throws once forgotten.</summary>
    internal KeyInUse UseKey()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inUse++;
        }
        return new KeyInUse(this);
    }

    /// <summary>Forgets the key: nothing new may read it, and it is zeroed once the last reader is done.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_inUse == 0)
            {
                Zero();
            }
        }
    }

    public override string ToString() => ReceiverId;

    private void Leave()
    {
        lock (_gate)
        {
            _inUse--;
            if (_disposed && _inUse == 0)
            {
                Zero();
            }
        }
    }

    private void Zero()
    {
        CryptographicOperations.ZeroMemory(_key);
        _zeroed = true;
    }

    /// <summary>The key held for one reader.</summary>
    internal readonly ref struct KeyInUse
    {
        private readonly PairingMaterial _material;

        internal KeyInUse(PairingMaterial material) => _material = material;

        public ReadOnlySpan<byte> Key => _material._key;

        public void Dispose() => _material.Leave();
    }
}

/// <summary><c>phone-remote.json</c>: <c>{ "receiver_id": "…" }</c>, and nothing secret.</summary>
internal sealed record PhoneRemoteFile(string? ReceiverId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(PhoneRemoteFile))]
internal sealed partial class RemotePairingJsonContext : JsonSerializerContext;
