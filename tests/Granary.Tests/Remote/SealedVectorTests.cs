using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Granary.Core.Remote;

namespace Granary.Tests.Remote;

/// <summary>
/// Level <c>sealed</c> of Silo's phone remote against the vectors Silo's own clients test with (<see cref="SealedVectors"/>),
/// byte for byte, and the refusal of every frame that is not the next one from the right side under the right key.
/// </summary>
public sealed class SealedVectorTests
{
    [Fact]
    public void TheKeyId_IsTheFirstEightHexDigitsOfTheKeysSha256()
    {
        string keyId = SiloCastSealed.KeyId(SealedVectors.Key);

        Assert.Equal(SealedVectors.KeyId, keyId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(SealedVectors.Key))[..8], keyId);
    }

    [Fact]
    public void TheSessionKeys_AreDerivedAsSilosClientsDeriveThem()
    {
        using var keys = VectorKeys();

        Assert.Equal(SealedVectors.EncKey, Convert.ToHexStringLower(keys.EncKey));
        Assert.Equal(SealedVectors.MacKey, Convert.ToHexStringLower(keys.MacKey));
        // The test's own derivation of the Roku app's IV key, checked against the vector before a test relies on it.
        Assert.Equal(SealedVectors.IvKey, Convert.ToHexStringLower(RokuIvKey(SealedVectors.Key, SealedVectors.PhoneNonce, SealedVectors.ReceiverNonce)));
    }

    [Fact]
    public void TheSessionKeys_AreForgottenWhenDisposed_AndNeverPrinted()
    {
        var keys = VectorKeys();
        ReadOnlySpan<byte> encKey = keys.EncKey;
        ReadOnlySpan<byte> macKey = keys.MacKey;

        Assert.DoesNotContain(SealedVectors.EncKey, keys.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SealedVectors.MacKey, keys.ToString(), StringComparison.OrdinalIgnoreCase);
        keys.Dispose();

        Assert.True(encKey.IndexOfAnyExcept((byte)0) < 0);
        Assert.True(macKey.IndexOfAnyExcept((byte)0) < 0);
        Assert.Throws<ObjectDisposedException>(() => SiloCastSealed.Seal(keys, SiloCastSealed.FromReceiver, 0, "{}"u8));
    }

    [Fact]
    public void TheKey_IsWrittenInBase32_AsThePairingLinkCarriesIt()
    {
        string text = Base32.Encode(SealedVectors.Key);

        Assert.Equal(SealedVectors.KeyInBase32, text);
        Assert.Equal(26, text.Length);
        Assert.DoesNotContain('=', text);
        Assert.Equal(SealedVectors.Key, Base32.Decode(text));
        Assert.Equal(SealedVectors.Key, Base32.Decode(text.ToLowerInvariant()));
        Assert.Null(Base32.Decode("AAAQ-EAYE"));
        Assert.Null(Base32.Decode(text + "A"));
    }

    [Fact]
    public void APhonesSealedHello_IsRead()
    {
        var hello = SiloCastSealed.ReadHello(Encoding.UTF8.GetBytes(SealedVectors.PhoneSealedHello));

        Assert.NotNull(hello);
        Assert.Equal(SealedVectors.KeyId, hello.KeyId);
        Assert.Equal(SealedVectors.PhoneNonce, hello.Nonce);
    }

    [Fact]
    public void TheReceiversSealedHello_CarriesItsNonceAsThePhonesReadIt()
    {
        string text = SiloCastSealed.HelloText(SealedVectors.KeyId, SealedVectors.ReceiverNonce);

        Assert.Equal(
            $$"""{"type":"sealed_hello","v":1,"keyId":"{{SealedVectors.KeyId}}","nonce":"{{SealedVectors.ReceiverNonceText}}"}""",
            text);
        var read = SiloCastSealed.ReadHello(Encoding.UTF8.GetBytes(text));
        Assert.NotNull(read);
        Assert.Equal(SealedVectors.ReceiverNonce, read.Nonce);
    }

    public static TheoryData<string> NotSealedHellos => new()
    {
        // A key id of one digit, and one that is not hex.
        """{"type":"sealed_hello","v":1,"keyId":"b","nonce":"EBESExQVFhcYGRobHB0eHw"}""",
        """{"type":"sealed_hello","v":1,"keyId":"be45cb2g","nonce":"EBESExQVFhcYGRobHB0eHw"}""",
        // A nonce of 3 bytes, one that is no base64url, and one that is no text.
        """{"type":"sealed_hello","v":1,"keyId":"be45cb26","nonce":"AAAA"}""",
        """{"type":"sealed_hello","v":1,"keyId":"be45cb26","nonce":"EBESExQVFhcYGRobHB0e!w"}""",
        """{"type":"sealed_hello","v":1,"keyId":"be45cb26","nonce":16}""",
        // A plain hello, and the plain close a receiver without the key answers.
        """{"type":"hello"}""",
        """{"type":"close","v":2,"close":{"reason":"unauthorized"}}""",
        "not json",
        "[]",
        // An object that names "type" twice.
        """{"type":"hello","type":"sealed_hello","v":1,"keyId":"be45cb26","nonce":"EBESExQVFhcYGRobHB0eHw"}""",
        // 2 KiB of spaces, alone and after a hello that would otherwise do: only a small frame is read.
        new string(' ', 2048),
        SealedVectors.PhoneSealedHello + new string(' ', 2048),
    };

    [Theory]
    [MemberData(nameof(NotSealedHellos))]
    public void AnythingElse_IsNotASealedHello(string text) => Assert.Null(SiloCastSealed.ReadHello(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void AReceiverFrame_IsSealedByteForByte_AsSilosRokuAppSealsIt()
    {
        using var keys = VectorKeys();
        byte[] iv = RokuIv(RokuIvKey(SealedVectors.Key, SealedVectors.PhoneNonce, SealedVectors.ReceiverNonce), SiloCastSealed.FromReceiver, 0);
        Assert.Equal(SealedVectors.ReceiverFrame[16..48], Convert.ToHexStringLower(iv));

        byte[] body = SiloCastSealed.Seal(keys, SiloCastSealed.FromReceiver, 0, Encoding.UTF8.GetBytes(SealedVectors.ReceiverPlaintext), iv);

        Assert.Equal(SealedVectors.ReceiverFrame, Convert.ToHexStringLower(body));
        Assert.Equal(SealedVectors.ReceiverPlaintext, Opened(keys, SiloCastSealed.FromReceiver, 0, body));
    }

    [Fact]
    public void APhoneFrame_Opens()
    {
        using var keys = VectorKeys();
        byte[] body = Convert.FromHexString(SealedVectors.PhoneFrame);

        Assert.Equal(SealedVectors.PhonePlaintext, Opened(keys, SiloCastSealed.FromPhone, SealedVectors.PhoneFrameSeq, body));
        // The same frame, sealed here with the phone's IV.
        byte[] again = SiloCastSealed.Seal(keys, SiloCastSealed.FromPhone, SealedVectors.PhoneFrameSeq,
            Encoding.UTF8.GetBytes(SealedVectors.PhonePlaintext), Convert.FromHexString(SealedVectors.PhoneFrameIv));
        Assert.Equal(SealedVectors.PhoneFrame, Convert.ToHexStringLower(again));
    }

    [Theory]
    [InlineData("a bit of the tag")]
    [InlineData("a bit of the IV")]
    [InlineData("a bit of the ciphertext")]
    [InlineData("a bit of the sequence number")]
    [InlineData("replayed")]
    [InlineData("reordered")]
    [InlineData("misdirected")]
    [InlineData("another key")]
    [InlineData("short")]
    [InlineData("one byte")]
    [InlineData("ragged")]
    public void ATamperedReplayedReorderedMisdirectedOrShortFrame_IsRefused(string change)
    {
        using var keys = VectorKeys();
        using var otherKeys = SiloCastSealed.DeriveSessionKeys(SealedVectors.OtherKey, SealedVectors.PhoneNonce, SealedVectors.ReceiverNonce);
        // Frame 0 from the phone, read where frame 0 is the one expected.
        byte[] body = SiloCastSealed.Seal(keys, SiloCastSealed.FromPhone, 0, Encoding.UTF8.GetBytes(SealedVectors.ReceiverPlaintext));
        Assert.NotNull(SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 0, body));

        byte[]? opened = change switch
        {
            "a bit of the tag" => SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 0, Flipped(body, body.Length - 1)),
            "a bit of the IV" => SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 0, Flipped(body, 8)),
            "a bit of the ciphertext" => SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 0, Flipped(body, 24)),
            "a bit of the sequence number" => SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 0, Flipped(body, 7)),
            // Frame 0 again where frame 1 is due.
            "replayed" => SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 1, body),
            // Frame 2 where frame 1 is due.
            "reordered" => SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 1,
                SiloCastSealed.Seal(keys, SiloCastSealed.FromPhone, 2, Encoding.UTF8.GetBytes(SealedVectors.ReceiverPlaintext))),
            "misdirected" => SiloCastSealed.Open(keys, SiloCastSealed.FromReceiver, 0, body),
            "another key" => SiloCastSealed.Open(otherKeys, SiloCastSealed.FromPhone, 0, body),
            // Sequence number, IV and tag without a block of ciphertext.
            "short" => SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 0, [.. body.AsSpan(0, 24), .. body.AsSpan(body.Length - 32)]),
            "one byte" => SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 0, [0x00]),
            // A ciphertext one byte longer than its blocks.
            "ragged" => SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, 0, [.. body.AsSpan(0, body.Length - 32), 0x00, .. body.AsSpan(body.Length - 32)]),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        Assert.Null(opened);
    }

    [Fact]
    public void EveryFrameGranarySeals_HasItsOwnIv()
    {
        using var keys = VectorKeys();
        byte[] plaintext = Encoding.UTF8.GetBytes(SealedVectors.ReceiverPlaintext);

        byte[] first = SiloCastSealed.Seal(keys, SiloCastSealed.FromReceiver, 0, plaintext);
        byte[] second = SiloCastSealed.Seal(keys, SiloCastSealed.FromReceiver, 0, plaintext);

        Assert.NotEqual(first.AsSpan(8, 16).ToArray(), second.AsSpan(8, 16).ToArray());
        Assert.Equal(SealedVectors.ReceiverPlaintext, Opened(keys, SiloCastSealed.FromReceiver, 0, first));
        Assert.Equal(SealedVectors.ReceiverPlaintext, Opened(keys, SiloCastSealed.FromReceiver, 0, second));
    }

    private static SealedSessionKeys VectorKeys() =>
        SiloCastSealed.DeriveSessionKeys(SealedVectors.Key, SealedVectors.PhoneNonce, SealedVectors.ReceiverNonce);

    private static string? Opened(SealedSessionKeys keys, byte direction, ulong seq, byte[] body) =>
        SiloCastSealed.Open(keys, direction, seq, body) is { } plain ? Encoding.UTF8.GetString(plain) : null;

    private static byte[] Flipped(byte[] body, int index)
    {
        byte[] copy = (byte[])body.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    // The Roku app's IV key, derived here as silo-roku's sealed.bs derives it; Granary itself never needs it.
    private static byte[] RokuIvKey(byte[] key, byte[] phoneNonce, byte[] receiverNonce)
    {
        byte[] salt = [.. phoneNonce, .. receiverNonce];
        byte[] info = [.. "silo-cast-sealed-1 iv"u8, 0x01];
        byte[] prk = HMACSHA256.HashData(salt, key);
        return HMACSHA256.HashData(prk, info)[..16];
    }

    // The Roku app's IV for a frame: AES-128-ECB of direction || seq, padded with zeros to one block (sealed.bs frameIv).
    private static byte[] RokuIv(byte[] ivKey, byte direction, ulong seq)
    {
        byte[] block = new byte[16];
        block[0] = direction;
        BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(1), seq);
        using var aes = Aes.Create();
        aes.Key = ivKey;
        return aes.EncryptEcb(block, PaddingMode.None);
    }
}
