namespace Granary.Tests.Remote;

/// <summary>
/// The numbers of Silo's sealed-frame vectors, from silo-roku's <c>tests/control/sealed.spec.bs</c> (made with Node's
/// crypto), which silo-android's <c>SiloCastSealedTest.kt</c> and silo-apple's <c>SiloControlSealedTests.swift</c>
/// repeat. Only the numbers are taken; each is a new copy, so no test can change another's.
/// </summary>
internal static class SealedVectors
{
    /// <summary>The pairing key of <c>keys()</c>.</summary>
    public static byte[] Key => Convert.FromHexString("000102030405060708090a0b0c0d0e0f");

    /// <summary>The phone's nonce of <c>keys()</c>.</summary>
    public static byte[] PhoneNonce => Convert.FromHexString("101112131415161718191a1b1c1d1e1f");

    /// <summary>The receiver's nonce of <c>keys()</c> (the vectors' <c>tvNonce</c>).</summary>
    public static byte[] ReceiverNonce => Convert.FromHexString("202122232425262728292a2b2c2d2e2f");

    /// <summary>The key of "refuses a tampered, replayed, reordered, or misdirected frame" that is not the pairing key.</summary>
    public static byte[] OtherKey => Convert.FromHexString("0f0e0d0c0b0a09080706050403020100");

    /// <summary>"names the key and derives the session keys": the key's id.</summary>
    public const string KeyId = "be45cb26";

    /// <summary>"names the key and derives the session keys": <c>encKey</c>.</summary>
    public const string EncKey = "239a09219f1c09a91e710d3c90e16a4d";

    /// <summary>"names the key and derives the session keys": <c>macKey</c>.</summary>
    public const string MacKey = "066768a42738a935b7ff4148306d0aaf4650844fbfa39191e41e9cdfea22fafe";

    /// <summary>"names the key and derives the session keys": <c>ivKey</c>, which only Silo's Roku app uses.</summary>
    public const string IvKey = "3816333626975d56fe75bbe953ab0834";

    /// <summary>"writes the key in base32 for the pairing link".</summary>
    public const string KeyInBase32 = "AAAQEAYEAUDAOCAJBIFQYDIOB4";

    /// <summary>"reads a peer's sealed_hello": the phone's opening frame, its key id in upper case.</summary>
    public const string PhoneSealedHello = """{"type":"sealed_hello","v":1,"keyId":"BE45CB26","nonce":"EBESExQVFhcYGRobHB0eHw"}""";

    /// <summary>
    /// The receiver's nonce as text, as silo-apple's <c>testChannelKeysFromTheTVsHelloAndCountsEachDirection</c> feeds it
    /// to its phone side.
    /// </summary>
    public const string ReceiverNonceText = "ICEiIyQlJicoKSorLC0uLw";

    /// <summary>"seals a Roku frame as the reference does": frame 0 from the receiver holds this text.</summary>
    public const string ReceiverPlaintext = """{"type":"ping","v":2}""";

    /// <summary>"seals a Roku frame as the reference does": the sealed body, with the IV the Roku app derives.</summary>
    public const string ReceiverFrame =
        "0000000000000000e3c9c8e329cba6d9317f6e0442a21dd98277e1fd6de898e0832b3578392e71044f28b35fe6b56267e20d49e56e19cf095275faead9fe27b5d6cd3e7cba162cf9d3e5dead6001a4eb0e596a8ac45b2011";

    /// <summary>"opens a phone frame with its own IV": the phone's frame holds this text.</summary>
    public const string PhonePlaintext = """{"type":"pong","v":2}""";

    /// <summary>"opens a phone frame with its own IV": the phone's frame is number 1, not 0.</summary>
    public const ulong PhoneFrameSeq = 1;

    /// <summary>"opens a phone frame with its own IV": the IV the phone chose.</summary>
    public const string PhoneFrameIv = "ffeeddccbbaa99887766554433221100";

    /// <summary>"opens a phone frame with its own IV": the sealed body.</summary>
    public const string PhoneFrame =
        "0000000000000001ffeeddccbbaa99887766554433221100d6cffafe6c10c438a7bb4a4418b0e3e7e1ba097b39d777647df0c04ad0d79684d56990d6c483611bdfc4acf0b7d16c06ff0c50c169f9739cd58a17445e0daeec";
}
