namespace Granary.Core.Abstractions;

/// <summary>
/// What checking a signed update needs from outside (docs/release.md, "Signed releases"): a release's small files from where
/// releases are published, and the downloaded package on disk.
/// </summary>
public interface IReleaseFiles
{
    /// <summary>
    /// A file published with a release, read whole; null when the release has no such file. Throws
    /// <see cref="InvalidDataException"/> when it is larger than <paramref name="maxBytes"/>, and
    /// <see cref="HttpRequestException"/> or <see cref="TimeoutException"/> when it cannot be fetched.
    /// </summary>
    Task<byte[]?> GetAsync(Uri url, int maxBytes, CancellationToken cancellationToken);

    /// <summary>A file on disk, opened for reading; null when there is none. Throws <see cref="IOException"/> when it cannot be read.</summary>
    Stream? OpenRead(string path);

    /// <summary>Deletes a downloaded package that failed its check; false when it is still there.</summary>
    bool Remove(string path);
}
