using System.Net;
using System.Text;

namespace Granary.Tests.Fakes;

/// <summary>Answers requests with a scripted function and records what was sent.</summary>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    // Requests arrive on whatever thread the client sends from.
    private readonly Lock _gate = new();
    private readonly List<HttpRequestMessage> _requests = [];

    /// <summary>What was sent so far, as a copy.</summary>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public static FakeHttpMessageHandler Returning(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        }));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }
        return respond(request, cancellationToken);
    }
}
