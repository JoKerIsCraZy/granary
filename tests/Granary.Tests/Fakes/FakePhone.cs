using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Granary.Tests.Fakes;

/// <summary>
/// The phone's side of Silo's phone remote at level <c>sealed</c>, as silo-android's <c>SiloCastDialer.kt</c> and
/// <c>SiloCastConnection.kt</c> and silo-roku's <c>scripts/silocontrol-phone.mjs</c> speak it. It seals and opens with its
/// own copy of the construction (.NET's <see cref="HKDF"/>, AES-CBC and HMAC), not with Granary's
/// <c>SiloCastSealed</c>: a mistake both sides shared would otherwise pass. Once keyed it reads in the background,
/// answers pings while <see cref="AnswersPings"/> is on, and keeps every other message for <see cref="ReadAsync"/>.
/// </summary>
internal sealed class FakePhone : IAsyncDisposable
{
    public const string DefaultDeviceId = "phone-1";
    public const string DefaultDeviceName = "Elena's phone";

    private const byte FromPhone = 0x01;
    private const byte FromReceiver = 0x02;

    // Long enough for any test; only a hang ends here.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static readonly string[] PhoneFeatures = ["close_reason", "resume_token", "replace_confirmation"];

    private readonly Stream _stream;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly List<string> _received = [];
    private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _encKey;
    private byte[]? _macKey;
    private ulong _sendSeq;
    private ulong _recvSeq;
    private byte[]? _lastSealed;
    private int _pingsAnswered;
    private int _openFailures;

    public FakePhone(Stream stream, byte[] key)
    {
        _stream = stream;
        _key = (byte[])key.Clone();
        Nonce = RandomNumberGenerator.GetBytes(16);
    }

    /// <summary>The phone answers each ping with a pong; on by default.</summary>
    public bool AnswersPings { get; set; } = true;

    /// <summary>This phone's nonce in its <c>sealed_hello</c>.</summary>
    public byte[] Nonce { get; }

    /// <summary>The text of the <c>sealed_hello</c> it sent last.</summary>
    public string? SentSealedHello { get; private set; }

    /// <summary>The receiver's answer to the <c>sealed_hello</c>, as text.</summary>
    public string? ReceivedSealedHello { get; private set; }

    /// <summary>Every sealed body it sent, in order.</summary>
    public List<byte[]> SentBodies { get; } = [];

    /// <summary>Every message it received so far, opened, pings included, in order.</summary>
    public IReadOnlyList<string> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    public int PingsAnswered => Volatile.Read(ref _pingsAnswered);

    /// <summary>Frames from the receiver that did not open: none, or the receiver sealed wrongly.</summary>
    public int OpenFailures => Volatile.Read(ref _openFailures);

    /// <summary>Completes once the receiver closed the connection.</summary>
    public Task Ended => _ended.Task;

    public static string KeyIdOf(byte[] key) => Convert.ToHexStringLower(SHA256.HashData(key))[..8];

    /// <summary>The plain opening frame, under this phone's key unless another key id is given.</summary>
    public Task SendSealedHelloAsync(string? keyId = null)
    {
        string text = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "sealed_hello",
            ["v"] = 1,
            ["keyId"] = keyId ?? KeyIdOf(_key),
            ["nonce"] = Convert.ToBase64String(Nonce).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        });
        SentSealedHello = text;
        return SendPlainAsync(text);
    }

    /// <summary>Sends <paramref name="text"/> unsealed, as the first frame or a phone that dials plain does.</summary>
    public Task SendPlainAsync(string text) => WriteFrameAsync(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Reads the receiver's answer to the <c>sealed_hello</c>: true when it is one under this phone's key, which keys the
    /// session and starts the background reader; false for anything else (a plain close, the end).
    /// </summary>
    public async Task<bool> ReadSealedHelloAsync()
    {
        string? text = await ReadPlainAsync();
        ReceivedSealedHello = text;
        if (text is null)
        {
            return false;
        }
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.GetProperty("type").GetString() != "sealed_hello" || root.GetProperty("keyId").GetString() != KeyIdOf(_key))
        {
            return false;
        }
        string nonceText = root.GetProperty("nonce").GetString()!.Replace('-', '+').Replace('_', '/');
        byte[] receiverNonce = Convert.FromBase64String(nonceText.PadRight(nonceText.Length + ((4 - (nonceText.Length % 4)) % 4), '='));
        byte[] salt = [.. Nonce, .. receiverNonce];
        _encKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 16, salt, "silo-cast-sealed-1 enc"u8.ToArray());
        _macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt, "silo-cast-sealed-1 mac"u8.ToArray());
        _ = Task.Run(ReadLoopAsync);
        return true;
    }

    /// <summary>Sends the <c>sealed_hello</c> and reads the answer.</summary>
    public async Task<bool> PairAsync()
    {
        await SendSealedHelloAsync();
        return await ReadSealedHelloAsync();
    }

    /// <summary>The next plain frame's text, or null at the end of the connection. Only before the session is keyed.</summary>
    public async Task<string?> ReadPlainAsync()
    {
        byte[]? body = await ReadFrameAsync().WaitAsync(HangGuard);
        return body is null ? null : Encoding.UTF8.GetString(body);
    }

    /// <summary>Seals <paramref name="json"/> as the phone's next frame.</summary>
    public async Task SendAsync(string json)
    {
        await _writeGate.WaitAsync();
        try
        {
            byte[] body = Seal(_sendSeq++, Encoding.UTF8.GetBytes(json));
            _lastSealed = body;
            SentBodies.Add(body);
            await WriteFrameLockedAsync(body);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Writes <paramref name="body"/> as one frame, as it is.</summary>
    public Task SendRawAsync(byte[] body) => WriteFrameAsync(body);

    /// <summary>Writes only a frame's length, as a peer that announces a frame does.</summary>
    public async Task SendLengthAsync(uint length)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, length);
        await _stream.WriteAsync(prefix);
    }

    /// <summary>Sends the last sealed frame again, with its own sequence number.</summary>
    public Task ReplaySealedFrameAsync() => WriteFrameAsync(_lastSealed ?? throw new InvalidOperationException("Nothing was sent yet."));

    /// <summary>Sends a ping under the number after the next one, so one number is skipped.</summary>
    public async Task SkipASequenceNumberAsync()
    {
        Interlocked.Increment(ref _sendSeq);
        await SendAsync("""{"type":"ping","v":2}""");
    }

    /// <summary>The phone's <c>hello</c>, as the real phone sent it: the payload, then <c>"v"</c>.</summary>
    public Task HelloAsync(string serverId, string? serverIdentity, string deviceId = DefaultDeviceId, string deviceName = DefaultDeviceName,
        bool passive = false, IReadOnlyList<string>? features = null)
    {
        var hello = new Dictionary<string, object?>
        {
            ["role"] = "phone",
            ["deviceName"] = deviceName,
            ["deviceId"] = deviceId,
            ["serverId"] = serverId,
            ["serverName"] = "Example Server",
            ["supportedVersions"] = new[] { 2 },
            ["features"] = features ?? PhoneFeatures,
        };
        if (serverIdentity is not null)
        {
            hello["serverIdentity"] = serverIdentity;
        }
        if (passive)
        {
            hello["passive"] = true;
        }
        return SendAsync(JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = "hello", ["hello"] = hello, ["v"] = 2 }));
    }

    /// <summary>The next message from the receiver, opened, of any type; null once the connection ended.</summary>
    public async Task<string?> ReadAsync()
    {
        using var guard = new CancellationTokenSource(HangGuard);
        try
        {
            return await _messages.Reader.WaitToReadAsync(guard.Token) && _messages.Reader.TryRead(out var message) ? message : null;
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            throw new TimeoutException("The receiver sent nothing.");
        }
    }

    /// <summary>The next message of <paramref name="type"/>, skipping others; throws once the connection ended.</summary>
    public async Task<string> NextAsync(string type)
    {
        while (true)
        {
            string message = await ReadAsync() ?? throw new InvalidOperationException($"The connection ended before a {type}.");
            if (TypeOf(message) == type)
            {
                return message;
            }
        }
    }

    /// <summary>The type of a message's text.</summary>
    public static string? TypeOf(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _ended.TrySetResult();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await ReadFrameAsync() is { } body)
            {
                if (Open(_recvSeq, body) is not { } text)
                {
                    Interlocked.Increment(ref _openFailures);
                    break;
                }
                _recvSeq++;
                lock (_received)
                {
                    _received.Add(text);
                }
                if (TypeOf(text) == "ping" && AnswersPings)
                {
                    Interlocked.Increment(ref _pingsAnswered);
                    await SendAsync("""{"type":"pong","v":2}""");
                }
                _messages.Writer.TryWrite(text);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection ended.
        }
        finally
        {
            _messages.Writer.TryComplete();
            _ended.TrySetResult();
        }
    }

    private async Task<byte[]?> ReadFrameAsync()
    {
        byte[] prefix = new byte[4];
        try
        {
            int read = await _stream.ReadAtLeastAsync(prefix, 4, throwOnEndOfStream: false);
            if (read < 4)
            {
                _ended.TrySetResult();
                return null;
            }
            byte[] body = new byte[BinaryPrimitives.ReadUInt32BigEndian(prefix)];
            await _stream.ReadExactlyAsync(body);
            return body;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _ended.TrySetResult();
            return null;
        }
    }

    private async Task WriteFrameAsync(byte[] body)
    {
        await _writeGate.WaitAsync();
        try
        {
            await WriteFrameLockedAsync(body);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteFrameLockedAsync(byte[] body)
    {
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await _stream.WriteAsync(frame);
    }

    // seq (8, big-endian) || iv (16) || AES-128-CBC || HMAC-SHA256(macKey, label || 0x01 || seq || iv || ciphertext)
    private byte[] Seal(ulong seq, byte[] plaintext)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = _encKey ?? throw new InvalidOperationException("Not keyed yet.");
        byte[] ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
        byte[] seqBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqBytes, seq);
        byte[] signed = [.. seqBytes, .. iv, .. ciphertext];
        return [.. signed, .. Tag(FromPhone, signed)];
    }

    private string? Open(ulong seq, byte[] body)
    {
        if (body.Length < 8 + 16 + 16 + 32)
        {
            return null;
        }
        byte[] signed = body[..^32];
        if (!CryptographicOperations.FixedTimeEquals(Tag(FromReceiver, signed), body.AsSpan(body.Length - 32))
            || BinaryPrimitives.ReadUInt64BigEndian(signed) != seq)
        {
            return null;
        }
        using var aes = Aes.Create();
        aes.Key = _encKey!;
        return Encoding.UTF8.GetString(aes.DecryptCbc(signed[24..], signed[8..24], PaddingMode.PKCS7));
    }

    private byte[] Tag(byte direction, byte[] signed)
    {
        byte[] message = [.. "silo-cast-sealed-1"u8, direction, .. signed];
        return HMACSHA256.HashData(_macKey!, message);
    }
}
