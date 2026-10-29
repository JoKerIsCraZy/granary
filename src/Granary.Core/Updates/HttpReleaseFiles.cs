using System.Net;
using Granary.Core.Abstractions;

namespace Granary.Core.Updates;

/// <summary>Release files over HTTPS from where releases are published, and the downloaded package on disk.</summary>
public sealed class HttpReleaseFiles(IHttpClientFactory clients, TimeProvider time) : IReleaseFiles
{
    /// <summary>
    /// The client for release files: nothing of this device or of any server's sign-in, redirects followed to the host that
    /// serves release downloads.
    /// </summary>
    public const string HttpClientName = "Releases";

    /// <summary>How long a release file may take, its body included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    public async Task<byte[]?> GetAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
    {
        // One bound for the request and the whole body: HttpClient's own timeout ends with the headers here, and a body
        // that trickles in would otherwise hold the check open.
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await ReadAsync(url, maxBytes, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The release file did not arrive in time.");
        }
    }

    public Stream? OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public bool Remove(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<byte[]?> ReadAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw TooLarge();
        }
        // Read one byte past the limit, so a body sent without a length is caught too.
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[maxBytes + 1];
        int read = 0;
        int count;
        while (read < buffer.Length && (count = await body.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
        {
            read += count;
        }
        return read > maxBytes ? throw TooLarge() : buffer[..read];
    }

    private static InvalidDataException TooLarge() => new("The release file is larger than a file of its kind can be.");
}
