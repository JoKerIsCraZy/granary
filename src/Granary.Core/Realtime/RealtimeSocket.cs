using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Granary.Core.Realtime;

/// <summary>One connection of a server socket that speaks JSON text frames: the playback control lane, the events stream.</summary>
public interface IRealtimeSocket : IAsyncDisposable
{
    /// <summary>The protocol the server chose; null before the connection opened.</summary>
    string? SubProtocol { get; }

    /// <summary>The code the server closed the connection with (4001: access changed); null while it is open.</summary>
    int? CloseStatus { get; }

    /// <summary>Opens the connection offering <paramref name="subProtocols"/> in order.</summary>
    Task ConnectAsync(Uri uri, IReadOnlyList<string> subProtocols, CancellationToken cancellationToken);

    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>The next text frame; null once the connection closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Says from the first bytes of a frame whether the rest of it is worth keeping.</summary>
public delegate bool FrameHeadFilter(ReadOnlySpan<byte> head);

/// <summary>
/// A socket over <see cref="ClientWebSocket"/>. Frames above <c>maxFrameBytes</c> close it: 64 KiB by default, the
/// playback control lane's limit. With <c>oversizeHeadBytes</c> the connection stays instead: the rest of such a frame is
/// read and dropped, and its first bytes are handed on, which is no whole JSON any more (the events stream, where one
/// frame may hold every running playback session). With <c>takesWhole</c>, a frame longer than that head is first
/// shown to it, and one it turns down is cut to its head the same way, however small: its payload is never kept.
/// </summary>
public sealed class WebSocketRealtimeSocket : IRealtimeSocket
{
    public const int DefaultMaxFrameBytes = 64 * 1024;

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly ClientWebSocket _socket = new();
    private readonly int _maxFrameBytes;
    private readonly int _oversizeHeadBytes;
    private readonly FrameHeadFilter? _takesWhole;

    public WebSocketRealtimeSocket(int maxFrameBytes = DefaultMaxFrameBytes, int oversizeHeadBytes = 0, FrameHeadFilter? takesWhole = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(oversizeHeadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(oversizeHeadBytes, maxFrameBytes);
        if (takesWhole is not null && oversizeHeadBytes == 0)
        {
            throw new ArgumentException("A frame can only be judged by a head that is kept.", nameof(takesWhole));
        }
        _maxFrameBytes = maxFrameBytes;
        _oversizeHeadBytes = oversizeHeadBytes;
        _takesWhole = takesWhole;
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public string? SubProtocol => _socket.SubProtocol;

    public int? CloseStatus => (int?)_socket.CloseStatus;

    public Task ConnectAsync(Uri uri, IReadOnlyList<string> subProtocols, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subProtocols);
        foreach (string protocol in subProtocols)
        {
            _socket.Options.AddSubProtocol(protocol);
        }
        return _socket.ConnectAsync(uri, cancellationToken);
    }

    public Task SendAsync(string text, CancellationToken cancellationToken) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    // A cancelled wait closes the connection as the protocol asks rather than aborting it, which a receive given the token
    // would do: the server hears the close, and the wait ends with it, or after two seconds without an answer.
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Off the thread that cancelled (the UI thread, as a playback stops), which must not wait on the socket.
        using var closing = cancellationToken.Register(() => _ = Task.Run(CloseAsync));
        var message = new ArrayBufferWriter<byte>();
        var chunk = new byte[4096];
        // The frame grew past the limit, or its head says it is not worth keeping: nothing more of it is kept.
        bool cut = false;
        bool judged = false;
        while (true)
        {
            var received = await _socket.ReceiveAsync(chunk, CancellationToken.None).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (!cut && message.WrittenCount + received.Count > _maxFrameBytes)
            {
                if (_oversizeHeadBytes == 0)
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, null, cancellationToken).ConfigureAwait(false);
                    return null;
                }
                cut = true;
                // The head may still lie in the chunk that crossed the limit.
                int missing = Math.Min(_oversizeHeadBytes - message.WrittenCount, received.Count);
                if (missing > 0)
                {
                    message.Write(chunk.AsSpan(0, missing));
                }
            }
            else if (!cut)
            {
                message.Write(chunk.AsSpan(0, received.Count));
                // Asked once, as soon as the head is there and more of the frame is still to come.
                if (!judged && _takesWhole is { } takesWhole && !received.EndOfMessage && message.WrittenCount >= _oversizeHeadBytes)
                {
                    judged = true;
                    cut = !takesWhole(message.WrittenSpan[.._oversizeHeadBytes]);
                }
            }
            if (received.EndOfMessage)
            {
                // Binary frames are not part of the protocol: an empty text stands for them and is ignored.
                if (received.MessageType != WebSocketMessageType.Text)
                {
                    return "";
                }
                var kept = cut ? message.WrittenSpan[..Math.Min(_oversizeHeadBytes, message.WrittenCount)] : message.WrittenSpan;
                return Encoding.UTF8.GetString(kept);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _socket.Dispose();
    }

    // Closing is best effort: a server that does not answer within two seconds is cut off.
    private async Task CloseAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection goes either way.
            }
        }
        _ = Task.Delay(CloseTimeout).ContinueWith(_ =>
        {
            if (_socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
            {
                _socket.Abort();
            }
        }, TaskScheduler.Default);
    }
}
