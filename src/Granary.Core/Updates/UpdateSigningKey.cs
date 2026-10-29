using System.Security.Cryptography;

namespace Granary.Core.Updates;

/// <summary>
/// The public key Granary's releases are signed with: ECDSA on P-256 over SHA-256, the signature in IEEE P1363 form
/// (docs/release.md, "Signed releases"). The app carries it in Updates/update-key.pub. Until scripts/new-update-key.ps1
/// writes a key there, that file is a placeholder and no update is trusted.
/// </summary>
public sealed class UpdateSigningKey
{
    /// <summary>A P-256 signature in IEEE P1363 form: r and s, 32 bytes each.</summary>
    internal const int SignatureLength = 64;

    private const string ResourceName = "Granary.Core.Updates.update-key.pub";

    // A P-256 SubjectPublicKeyInfo: the algorithm (id-ecPublicKey on prime256v1), then the 65-byte uncompressed point.
    private const int P256Length = 91;
    private static readonly byte[] P256Prefix = Convert.FromHexString("3059301306072A8648CE3D020106082A8648CE3D030107034200");

    private static readonly UpdateSigningKey None = new(null);

    private readonly byte[]? _publicKey;

    private UpdateSigningKey(byte[]? publicKey) => _publicKey = publicKey;

    /// <summary>The text of the key file the app carries.</summary>
    public static string EmbeddedText { get; } = ReadEmbedded();

    /// <summary>The key the app checks its updates with; not <see cref="IsPresent"/> while the file is a placeholder.</summary>
    public static UpdateSigningKey Embedded { get; } = Parse(EmbeddedText);

    public bool IsPresent => _publicKey is not null;

    /// <summary>
    /// A P-256 public key in PEM ("BEGIN PUBLIC KEY"). Anything else is no key, and text with a private key in it above
    /// all: that must never ship.
    /// </summary>
    public static UpdateSigningKey Parse(string? pem)
    {
        if (pem is null || pem.Contains("PRIVATE KEY", StringComparison.Ordinal) || !PemEncoding.TryFind(pem, out var fields)
            || !pem.AsSpan()[fields.Label].SequenceEqual("PUBLIC KEY"))
        {
            return None;
        }
        byte[] der = new byte[fields.DecodedDataLength];
        if (!Convert.TryFromBase64Chars(pem.AsSpan()[fields.Base64Data], der, out int written) || written != der.Length)
        {
            return None;
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(der, out int read);
            byte[] normal = key.ExportSubjectPublicKeyInfo();
            bool isP256 = read == der.Length && normal.Length == P256Length && normal.AsSpan().StartsWith(P256Prefix);
            return isP256 ? new UpdateSigningKey(normal) : None;
        }
        catch (CryptographicException)
        {
            return None;
        }
    }

    /// <summary>Whether <paramref name="signature"/> is this key's over exactly <paramref name="data"/>.</summary>
    internal bool Verifies(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (_publicKey is null || signature.Length != SignatureLength)
        {
            return false;
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(_publicKey, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string ReadEmbedded()
    {
        using var stream = typeof(UpdateSigningKey).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return "";
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
