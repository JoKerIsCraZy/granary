using System.Net;
using System.Net.Sockets;

namespace Granary.Tests.Fakes;

/// <summary>
/// An HttpListener on a free port for a test's real WebSocket. The port is as the system hands it out, which knows nothing of
/// what http.sys has reserved, and another test can take it in between; so a few tries, as the media proxy has.
/// </summary>
internal static class LoopbackHttpListener
{
    private const int MaxTries = 5;

    public static (HttpListener Listener, Uri Address) Start()
    {
        for (int tries = 1; ; tries++)
        {
            var address = new Uri($"http://localhost:{FreePort()}/");
            var listener = new HttpListener();
            listener.Prefixes.Add(address.AbsoluteUri);
            try
            {
                listener.Start();
                return (listener, address);
            }
            catch (HttpListenerException) when (tries < MaxTries)
            {
                listener.Close();
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
