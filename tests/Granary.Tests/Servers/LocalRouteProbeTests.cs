using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Granary.Core.Servers;
using Granary.Tests.Fakes;

namespace Granary.Tests.Servers;

/// <summary>
/// The probe against a real TLS listener with a self-signed certificate made for no name, as the server's API port
/// presents it: only the pinned key and the same identity make a route.
/// </summary>
public sealed class LocalRouteProbeTests : IAsyncDisposable
{
    private static readonly Uri Saved = new("https://silo.example/");

    private readonly X509Certificate2 _certificate = SelfSigned();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly LogSink _logs = new();
    private readonly Task _serving;
    private int _requests;

    public LocalRouteProbeTests()
    {
        _listener.Start();
        _serving = ServeAsync();
    }

    private Uri Candidate => new($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    private string Pin => LocalRoute.SpkiSha256(_certificate);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePinnedKey_AndTheSameIdentity_MakeARoute_ThatKeepsTheirTrust()
    {
        var probe = new LocalRouteProbe(_logs.For<LocalRouteProbe>());

        using var route = await probe.ProbeAsync(Saved, Candidate, Pin, "server-1", Cancellation);

        Assert.NotNull(route);
        Assert.Equal(Candidate.GetLeftPart(UriPartial.Authority), route.Origin.GetLeftPart(UriPartial.Authority));
        // The route's own client trusts the same key for what follows.
        using var request = new HttpRequestMessage(HttpMethod.Get, route.Rewrite(new Uri("https://silo.example/api/v2/system/identity")));
        using var response = await route.Client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnotherKey_IsNoRoute()
    {
        var probe = new LocalRouteProbe(_logs.For<LocalRouteProbe>());
        string otherPin = LocalRoute.SpkiSha256(SelfSigned());

        Assert.Null(await probe.ProbeAsync(Saved, Candidate, otherPin, "server-1", Cancellation));
        Assert.Equal(0, _requests);
    }

    [Fact]
    public async Task TheKey_WithAnotherIdentity_IsNoRoute()
    {
        var probe = new LocalRouteProbe(_logs.For<LocalRouteProbe>());

        Assert.Null(await probe.ProbeAsync(Saved, Candidate, Pin, "server-2", Cancellation));
    }

    [Fact]
    public async Task NobodyThere_IsNoRoute()
    {
        var probe = new LocalRouteProbe(_logs.For<LocalRouteProbe>());
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var nobody = new Uri($"https://127.0.0.1:{((IPEndPoint)closed.LocalEndpoint).Port}/");
        closed.Stop();

        Assert.Null(await probe.ProbeAsync(Saved, nobody, Pin, "server-1", Cancellation));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _serving;
        _certificate.Dispose();
    }

    // The server makes its certificate per process, for no name; only its key is stable.
    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=silo", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel needs a key it can use for a server, which an ephemeral one is not.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    // Answers every request on a connection with the identity of "server-1".
    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = AnswerAsync(client);
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(_certificate);
                var buffer = new byte[4096];
                var received = new StringBuilder();
                while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await tls.ReadAsync(buffer);
                    if (read == 0)
                    {
                        return;
                    }
                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                Interlocked.Increment(ref _requests);
                const string body = """{"server_id":"server-1"}""";
                string answer = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(answer));
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                // A client that refused the key hangs up during the handshake.
            }
        }
    }
}
