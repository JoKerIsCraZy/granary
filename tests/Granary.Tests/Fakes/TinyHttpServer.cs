using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Granary.Tests.Fakes;

/// <summary>
/// A loopback HTTP/1.1 server on a raw socket (no URL ACLs needed). It records each request's
/// header block and answers with a fixed status and body.
/// </summary>
internal sealed class TinyHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly int _status;
    private readonly string _reason;
    private readonly byte[] _body;
    private readonly string _contentType;

    public TinyHttpServer(int status, string reason, byte[] body, string contentType)
    {
        _status = status;
        _reason = reason;
        _body = body;
        _contentType = contentType;
        _listener.Start();
        _loop = AcceptLoopAsync();
    }

    public ConcurrentQueue<string> RequestHeaders { get; } = new();

    public Uri Url(string path) => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{path}");

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                string head = await ReadHeadAsync(stream);
                RequestHeaders.Enqueue(head);
                bool isHead = head.StartsWith("HEAD ", StringComparison.Ordinal);
                string response =
                    $"HTTP/1.1 {_status} {_reason}\r\nContent-Type: {_contentType}\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                if (!isHead)
                {
                    await stream.WriteAsync(_body, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // The player may hang up early; that is fine for these tests.
            }
        }
    }

    private async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), _stop.Token);
            if (n == 0)
            {
                break;
            }
            read += n;
            string text = Encoding.ASCII.GetString(buffer, 0, read);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return text[..end];
            }
        }
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (ObjectDisposedException)
        {
        }
        _stop.Dispose();
    }
}
