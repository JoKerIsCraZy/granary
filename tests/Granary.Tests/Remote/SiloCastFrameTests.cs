using System.Buffers.Binary;
using Granary.Core.Remote;

namespace Granary.Tests.Remote;

/// <summary>
/// The framing of Silo's phone remote: a 4-byte big-endian length, then the body. A length over the cap ends the
/// connection before a byte of its body is read.
/// </summary>
public sealed class SiloCastFrameTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void AFrame_IsItsLengthInFourBigEndianBytes_ThenItsBody()
    {
        byte[] frame = SiloCastFrame.Encode("""{"type":"ping","v":2}"""u8);

        Assert.Equal(new byte[] { 0, 0, 0, 21 }, frame[..4]);
        Assert.Equal("""{"type":"ping","v":2}"""u8.ToArray(), frame[4..]);
    }

    [Fact]
    public async Task AFrame_ArrivesWholeFromSplitChunks()
    {
        using var stream = new OneByteStream([.. SiloCastFrame.Encode("split across reads"u8)]);

        Assert.Equal("split across reads"u8.ToArray(), await SiloCastFrame.ReadAsync(stream, SiloCastFrame.MaxFrameBytes, Token));
    }

    [Fact]
    public async Task TwoFramesInOneChunk_AreReadOneByOne()
    {
        using var stream = new MemoryStream([.. SiloCastFrame.Encode("first"u8), .. SiloCastFrame.Encode(""u8), .. SiloCastFrame.Encode("third"u8)]);

        Assert.Equal("first"u8.ToArray(), await SiloCastFrame.ReadAsync(stream, SiloCastFrame.MaxFrameBytes, Token));
        Assert.Empty((await SiloCastFrame.ReadAsync(stream, SiloCastFrame.MaxFrameBytes, Token))!);
        Assert.Equal("third"u8.ToArray(), await SiloCastFrame.ReadAsync(stream, SiloCastFrame.MaxFrameBytes, Token));
        Assert.Null(await SiloCastFrame.ReadAsync(stream, SiloCastFrame.MaxFrameBytes, Token));
    }

    [Theory]
    [InlineData(SiloCastFrame.MaxFrameBytes)]
    [InlineData(SiloCastFrame.MaxFirstFrameBytes)]
    public async Task AFrameOfExactlyTheLimit_IsRead(int limit)
    {
        using var stream = new MemoryStream(SiloCastFrame.Encode(new byte[limit]));

        Assert.Equal(limit, (await SiloCastFrame.ReadAsync(stream, limit, Token))!.Length);
    }

    [Theory]
    [InlineData(65_537u, SiloCastFrame.MaxFrameBytes)]
    [InlineData(1_025u, SiloCastFrame.MaxFirstFrameBytes)]
    // The phones' own limit, 1 MiB, is over Granary's.
    [InlineData(1_048_576u, SiloCastFrame.MaxFrameBytes)]
    // The high bit, which would be negative if the length were read as signed.
    [InlineData(0x8000_0000u, SiloCastFrame.MaxFrameBytes)]
    [InlineData(0xFFFF_FFFFu, SiloCastFrame.MaxFrameBytes)]
    public async Task ALengthOverTheLimit_IsRefusedBeforeItsBodyIsRead(uint length, int limit)
    {
        byte[] wire = new byte[4 + 2048];
        BinaryPrimitives.WriteUInt32BigEndian(wire, length);
        using var stream = new MemoryStream(wire);

        var refused = await Assert.ThrowsAsync<FrameTooLargeException>(async () => await SiloCastFrame.ReadAsync(stream, limit, Token));

        Assert.Equal((long)length, refused.Length);
        Assert.Equal(limit, refused.Limit);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void ABodyOverTheLimit_IsNotEncoded() =>
        Assert.Throws<FrameTooLargeException>(() => SiloCastFrame.Encode(new byte[SiloCastFrame.MaxFrameBytes + 1]));

    [Fact]
    public async Task APeerThatClosesBetweenFrames_GivesNull()
    {
        using var stream = new MemoryStream(SiloCastFrame.Encode("last"u8));

        Assert.Equal("last"u8.ToArray(), await SiloCastFrame.ReadAsync(stream, SiloCastFrame.MaxFrameBytes, Token));
        Assert.Null(await SiloCastFrame.ReadAsync(stream, SiloCastFrame.MaxFrameBytes, Token));
    }

    [Fact]
    public async Task APeerThatClosesInsideAFrame_Throws()
    {
        using var insideTheLength = new MemoryStream([0, 0]);
        using var insideTheBody = new MemoryStream([0, 0, 0, 5, 1, 2]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await SiloCastFrame.ReadAsync(insideTheLength, SiloCastFrame.MaxFrameBytes, Token));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await SiloCastFrame.ReadAsync(insideTheBody, SiloCastFrame.MaxFrameBytes, Token));
    }

    /// <summary>A stream that hands out one byte per read, as a slow network may.</summary>
    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(1, count), cancellationToken);

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(1, buffer.Length)]);
    }
}
