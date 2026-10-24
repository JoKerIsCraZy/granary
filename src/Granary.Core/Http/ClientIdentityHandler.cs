using Granary.Core.Api;
using Granary.Core.Auth;

namespace Granary.Core.Http;

/// <summary>
/// Adds the <c>X-Silo-Device-*</c> and <c>X-Silo-Client*</c> headers (docs/api-contract.md, 6.4) to
/// requests addressed to a Silo server, including the public sign-in calls. Never to other origins.
/// </summary>
public sealed class ClientIdentityHandler(ClientIdentity identity) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.GetServer() is not null)
        {
            // Sent once: a repeated X-Silo-Device-Id is a 422.
            Set(request, "X-Silo-Device-Id", identity.DeviceId);
            Set(request, "X-Silo-Device-Name", identity.DeviceName);
            Set(request, "X-Silo-Device-Platform", identity.DevicePlatform);
            Set(request, "X-Silo-Client", identity.ClientName);
            Set(request, "X-Silo-Client-Version", identity.ClientVersion);
            Set(request, "X-Silo-Client-Channel", identity.ClientChannel);
            Set(request, "X-Silo-Client-Family", identity.ClientFamily);
        }
        return base.SendAsync(request, cancellationToken);
    }

    private static void Set(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, HeaderSafe(value));
    }

    // Header values must be printable ASCII; a computer name may not be.
    internal static string HeaderSafe(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = c is >= ' ' and <= '~' ? c : '?';
            }
        });
}
