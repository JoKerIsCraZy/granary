using System.Buffers.Binary;

namespace Granary.Core.Remote;

/// <summary>
/// The framing of Silo's phone remote, as Silo's clients share it (silo-roku's <c>protocol.bs</c>, silo-android's
/// <c>PairingFrame.kt</c>): a 4-byte big-endian unsigned length, then that many bytes. At level <c>sealed</c> the length
/// counts the sealed body.
/// </summary>
public static class SiloCastFrame
{
    /// <summary>The largest body Granary reads or writes: the Roku-type receiver's own limit (<c>MAX_FRAME_BYTES</c>).</summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>
    /// The largest first frame, the only one read from a peer that has not shown the key: a <c>sealed_hello</c> is under
    /// 100 bytes.
    /// </summary>
    public const int MaxFirstFrameBytes = 1024;

    private const int PrefixBytes = 4;

    /// <summary>The frame of <paramref name="body"/>: its length, then the body.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxFrameBytes)
        {
            throw new FrameTooLargeException(body.Length, MaxFrameBytes);
        }
        byte[] frame = new byte[PrefixBytes + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame.AsSpan(PrefixBytes));
        return frame;
    }

    /// <summary>
    /// The next frame's body, or null when the peer closed between frames. A length over <paramref name="maxBytes"/>
    /// throws <see cref="FrameTooLargeException"/> before any of its body is read; a peer that closes inside a frame
    /// throws <see cref="EndOfStreamException"/>.
    /// </summary>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, MaxFrameBytes);

        byte[] prefix = new byte[PrefixBytes];
        int read = await stream.ReadAtLeastAsync(prefix, PrefixBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }
        if (read < PrefixBytes)
        {
            throw new EndOfStreamException("The peer closed inside a frame's length.");
        }

        // Unsigned, so a length with the high bit set is refused instead of going negative.
        uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length > maxBytes)
        {
            throw new FrameTooLargeException(length, maxBytes);
        }
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return body;
    }
}

/// <summary>A frame announced or had a length over the limit for where it was: the connection ends.</summary>
public sealed class FrameTooLargeException(long length, int limit)
    : IOException($"A frame of {length} bytes is over the limit of {limit}.")
{
    /// <summary>The length the frame announced or had.</summary>
    public long Length { get; } = length;

    /// <summary>The limit it was over.</summary>
    public int Limit { get; } = limit;
}
