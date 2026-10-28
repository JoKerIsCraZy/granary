using System.Collections.Concurrent;
using System.Threading.Channels;
using Granary.Core.Api.Playback;
using Granary.Core.Realtime;

namespace Granary.Tests.Fakes;

/// <summary>A server socket the test talks through: it pushes the server's frames and reads what the app sent.</summary>
internal sealed class FakeRealtimeSocket : IRealtimeSocket
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();

    public Uri? Uri { get; private set; }

    public IReadOnlyList<string> Offered { get; private set; } = [];

    /// <summary>The protocol the server picks; the control protocol unless the test says otherwise.</summary>
    public string? Picks { get; set; } = PlaybackControlApi.Protocol;

    public string? SubProtocol { get; private set; }

    public int? CloseStatus { get; private set; }

    public ConcurrentQueue<string> Sent { get; } = new();

    public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Disposed { get; private set; }

    public Task ConnectAsync(Uri uri, IReadOnlyList<string> subProtocols, CancellationToken cancellationToken)
    {
        Uri = uri;
        Offered = subProtocols;
        SubProtocol = Picks;
        Connected.TrySetResult();
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken cancellationToken)
    {
        Sent.Enqueue(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken) => await _incoming.Reader.ReadAsync(cancellationToken);

    /// <summary>A frame from the server.</summary>
    public void Push(string text) => _incoming.Writer.TryWrite(text);

    /// <summary>The server closes the connection, with <paramref name="status"/> as its code.</summary>
    public void Close(int? status = 1000)
    {
        CloseStatus = status;
        _incoming.Writer.TryWrite(null);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public static string Command(string sessionId, string commandId, string name, string payload = "{}", string? issuedBy = "admin") =>
        $$"""{ "type": "command", "command_id": "{{commandId}}", "session_id": "{{sessionId}}", "name": "{{name}}", "payload": {{payload}}{{(issuedBy is null ? "" : $$""", "issued_by": { "kind": "{{issuedBy}}" }""")}} }""";
}
