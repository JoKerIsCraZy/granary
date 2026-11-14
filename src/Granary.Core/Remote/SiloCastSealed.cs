using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Granary.Core.Remote;

/// <summary>
/// Level <c>sealed</c> of Silo's phone remote, as silo-roku's <c>docs/silocontrol.md</c> section 9.2 and
/// <c>source/control/sealed.bs</c> define it and the phones build it (silo-android's <c>SiloCastSealed.kt</c>,
/// silo-apple's <c>SiloControlSealed.swift</c>).
/// <para>
/// Each side opens with one plain <c>sealed_hello</c> that names the pairing key by its id and carries a 16-byte nonce;
/// the phone sends first. The session keys come from the pairing key and both nonces. Every later frame body is
/// <c>seq (8, big-endian) || iv (16) || AES-128-CBC ciphertext || HMAC-SHA256 tag (32)</c>, numbered from 0 in each
/// direction, so a frame can be neither read, forged, replayed nor reordered without the key.
/// </para>
/// </summary>
public static class SiloCastSealed
{
    /// <summary>The direction byte of a frame the phone sealed.</summary>
    public const byte FromPhone = 0x01;

    /// <summary>The direction byte of a frame the receiver sealed (the vectors' <c>FROM_TV</c>).</summary>
    public const byte FromReceiver = 0x02;

    /// <summary>The pairing key's length.</summary>
    public const int KeyBytes = 16;

    /// <summary>The length of each side's nonce.</summary>
    public const int NonceBytes = 16;

    /// <summary>The type of the plain opening frame.</summary>
    public const string HelloType = "sealed_hello";

    private const int HelloVersion = 1;
    private const int KeyIdDigits = 8;
    private const int SeqBytes = 8;
    private const int IvBytes = 16;
    private const int BlockBytes = 16;
    private const int TagBytes = 32;

    // 16 bytes in base64url without padding.
    private const int NonceTextLength = 22;

    // A sealed_hello is one flat object; anything deeper is not one.
    private static readonly JsonDocumentOptions HelloReading = new() { MaxDepth = 4, AllowDuplicateProperties = false };

    private static ReadOnlySpan<byte> Label => "silo-cast-sealed-1"u8;

    /// <summary>The key's id: the first 8 hex digits of its SHA-256, as TXT <c>kid</c> and <c>sealed_hello.keyId</c> carry it.</summary>
    public static string KeyId(ReadOnlySpan<byte> key)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(key, hash);
        return Convert.ToHexStringLower(hash[..(KeyIdDigits / 2)]);
    }

    /// <summary>
    /// The session's keys: HKDF-SHA256 with one output block each, the salt <c>phoneNonce || receiverNonce</c> and the
    /// info the label, a space and the key's purpose. Granary sends random IVs, so it needs no IV key.
    /// </summary>
    public static SealedSessionKeys DeriveSessionKeys(ReadOnlySpan<byte> key, ReadOnlySpan<byte> phoneNonce, ReadOnlySpan<byte> receiverNonce)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException("A pairing key is 16 bytes.", nameof(key));
        }
        if (phoneNonce.Length != NonceBytes || receiverNonce.Length != NonceBytes)
        {
            throw new ArgumentException("A nonce is 16 bytes.");
        }

        Span<byte> salt = stackalloc byte[NonceBytes * 2];
        phoneNonce.CopyTo(salt);
        receiverNonce.CopyTo(salt[NonceBytes..]);
        Span<byte> prk = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(salt, key, prk);
        byte[] encryption = Expand(prk, "enc"u8);
        byte[] authentication = Expand(prk, "mac"u8);
        CryptographicOperations.ZeroMemory(prk);
        byte[] encKey = encryption[..KeyBytes];
        CryptographicOperations.ZeroMemory(encryption);
        return new SealedSessionKeys(encKey, authentication);
    }

    /// <summary>One frame body sealed with a fresh random IV: a sender may choose any IV that cannot be predicted.</summary>
    public static byte[] Seal(SealedSessionKeys keys, byte direction, ulong seq, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> iv = stackalloc byte[IvBytes];
        RandomNumberGenerator.Fill(iv);
        return Seal(keys, direction, seq, plaintext, iv);
    }

    /// <summary>One frame body sealed with the IV given, so the vectors can be met byte for byte.</summary>
    internal static byte[] Seal(SealedSessionKeys keys, byte direction, ulong seq, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (iv.Length != IvBytes)
        {
            throw new ArgumentException("An IV is 16 bytes.", nameof(iv));
        }

        // PKCS#7 always adds padding: a whole block when the text fills its last one.
        int cipherLength = ((plaintext.Length / BlockBytes) + 1) * BlockBytes;
        byte[] body = new byte[SeqBytes + IvBytes + cipherLength + TagBytes];
        BinaryPrimitives.WriteUInt64BigEndian(body, seq);
        iv.CopyTo(body.AsSpan(SeqBytes));
        using var inUse = keys.Enter();
        using (var aes = Aes.Create())
        {
            aes.SetKey(inUse.EncKey);
            aes.EncryptCbc(plaintext, iv, body.AsSpan(SeqBytes + IvBytes, cipherLength), PaddingMode.PKCS7);
        }
        Tag(inUse.MacKey, direction, body.AsSpan(0, body.Length - TagBytes), body.AsSpan(body.Length - TagBytes));
        return body;
    }

    /// <summary>
    /// The text of a body sealed in <paramref name="direction"/> that has to be frame <paramref name="expectedSeq"/>, or
    /// null for a frame that is short, tampered with, replayed, reordered, sealed by the other side or under another key,
    /// or that holds no text. The tag is checked, in constant time, before anything is decrypted.
    /// </summary>
    public static byte[]? Open(SealedSessionKeys keys, byte direction, ulong expectedSeq, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(keys);
        using var inUse = keys.Enter();
        int cipherLength = body.Length - SeqBytes - IvBytes - TagBytes;
        if (cipherLength < BlockBytes || cipherLength % BlockBytes != 0)
        {
            return null;
        }

        ReadOnlySpan<byte> signed = body[..^TagBytes];
        Span<byte> tag = stackalloc byte[TagBytes];
        Tag(inUse.MacKey, direction, signed, tag);
        if (!CryptographicOperations.FixedTimeEquals(tag, body[^TagBytes..]))
        {
            return null;
        }
        if (BinaryPrimitives.ReadUInt64BigEndian(signed) != expectedSeq)
        {
            return null;
        }

        byte[] plaintext;
        try
        {
            using var aes = Aes.Create();
            aes.SetKey(inUse.EncKey);
            plaintext = aes.DecryptCbc(signed[(SeqBytes + IvBytes)..], signed.Slice(SeqBytes, IvBytes), PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            // Padding that does not check out, under a tag that does: only a broken sender makes that.
            return null;
        }
        // A frame of padding alone is no message; silo-roku's open refuses it too.
        return plaintext.Length == 0 ? null : plaintext;
    }

    /// <summary>The plain opening frame's text: <c>{"type":"sealed_hello","v":1,"keyId":…,"nonce":…}</c>, the nonce in base64url.</summary>
    public static string HelloText(string keyId, ReadOnlySpan<byte> nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        if (nonce.Length != NonceBytes)
        {
            throw new ArgumentException("A nonce is 16 bytes.", nameof(nonce));
        }

        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", HelloType);
            writer.WriteNumber("v", HelloVersion);
            writer.WriteString("keyId", keyId);
            writer.WriteString("nonce", Base64Url.EncodeToString(nonce));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// A peer's <c>sealed_hello</c>, with its key id in lower case, or null for anything else: a frame over
    /// <see cref="SiloCastFrame.MaxFirstFrameBytes"/>, not JSON, deeper than one object, with a member twice, another
    /// type, a key id that is not 8 hex digits or a nonce that is not 16 bytes in base64url.
    /// </summary>
    public static SealedHello? ReadHello(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > SiloCastFrame.MaxFirstFrameBytes)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), HelloReading);
            return ReadHello(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SealedHello? ReadHello(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object || Text(frame, "type") != HelloType)
        {
            return null;
        }
        if (Text(frame, "keyId") is not { Length: KeyIdDigits } keyId || !keyId.All(char.IsAsciiHexDigit))
        {
            return null;
        }
        if (Text(frame, "nonce") is not { } nonceText || Nonce(nonceText) is not { } nonce)
        {
            return null;
        }
        return new SealedHello(keyId.ToLowerInvariant(), nonce);
    }

    // 16 bytes as the phones write them: 22 characters of the base64url alphabet, no padding, no white space, and no
    // bits after the last byte, so one nonce has one spelling.
    private static byte[]? Nonce(string text)
    {
        if (text.Length != NonceTextLength || !text.All(IsBase64UrlDigit))
        {
            return null;
        }
        byte[] nonce = new byte[NonceBytes];
        // The status form, which refuses bits after the last byte without throwing.
        var status = Base64Url.DecodeFromChars(text, nonce, out int consumed, out int written);
        return status == OperationStatus.Done && consumed == text.Length && written == NonceBytes
            && Base64Url.EncodeToString(nonce) == text
                ? nonce
                : null;
    }

    private static bool IsBase64UrlDigit(char character) => char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

    private static string? Text(JsonElement frame, string name) =>
        frame.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // HKDF-Expand's first block: HMAC(prk, label || " " || purpose || 0x01).
    private static byte[] Expand(ReadOnlySpan<byte> prk, ReadOnlySpan<byte> purpose)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, prk);
        hmac.AppendData(Label);
        hmac.AppendData(" "u8);
        hmac.AppendData(purpose);
        hmac.AppendData([0x01]);
        return hmac.GetHashAndReset();
    }

    // HMAC(macKey, label || direction || seq || iv || ciphertext); signed is the last three.
    private static void Tag(ReadOnlySpan<byte> macKey, byte direction, ReadOnlySpan<byte> signed, Span<byte> tag)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        hmac.AppendData(Label);
        hmac.AppendData([direction]);
        hmac.AppendData(signed);
        hmac.GetHashAndReset(tag);
    }
}

/// <summary>A peer's <c>sealed_hello</c>: the id of the key it holds and its nonce.</summary>
public sealed record SealedHello(string KeyId, byte[] Nonce);

/// <summary>
/// One session's encryption and authentication keys. They are zeroed when it is disposed, and nothing prints them: the
/// type has no members that show them outside Granary.
/// <para>
/// A connection may end on one thread while it seals or opens a frame on another. A seal or open in progress therefore
/// holds the keys (<see cref="Enter"/>); <see cref="Dispose"/> refuses every new use at once and zeroes the keys when the
/// last use in progress has ended, so no frame is ever sealed or opened under keys zeroed half way. Dispose never waits.
/// </para>
/// </summary>
public sealed class SealedSessionKeys : IDisposable
{
    private readonly byte[] _encKey;
    private readonly byte[] _macKey;
    private readonly Lock _gate = new();
    private int _inUse;
    private bool _disposed;

    internal SealedSessionKeys(byte[] encKey, byte[] macKey)
    {
        _encKey = encKey;
        _macKey = macKey;
    }

    /// <summary>The AES-128 key, 16 bytes. For tests; a seal or open reads it through <see cref="Enter"/>.</summary>
    internal ReadOnlySpan<byte> EncKey
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _encKey;
            }
        }
    }

    /// <summary>The HMAC-SHA256 key, 32 bytes. For tests; a seal or open reads it through <see cref="Enter"/>.</summary>
    internal ReadOnlySpan<byte> MacKey
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _macKey;
            }
        }
    }

    /// <summary>Holds the keys for one seal or open; they stay whole until it is disposed. Throws once the keys are disposed.</summary>
    internal InUse Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inUse++;
        }
        return new InUse(this);
    }

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
        CryptographicOperations.ZeroMemory(_encKey);
        CryptographicOperations.ZeroMemory(_macKey);
    }

    /// <summary>The keys held for one seal or open.</summary>
    internal readonly ref struct InUse
    {
        private readonly SealedSessionKeys _keys;

        internal InUse(SealedSessionKeys keys) => _keys = keys;

        public ReadOnlySpan<byte> EncKey => _keys._encKey;

        public ReadOnlySpan<byte> MacKey => _keys._macKey;

        public void Dispose() => _keys.Leave();
    }
}
