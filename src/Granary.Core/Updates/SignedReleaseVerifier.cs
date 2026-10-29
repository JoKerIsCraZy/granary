using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Updates;

/// <summary>
/// Lets only signed releases update Granary (docs/release.md, "Signed releases"). A release carries SHA256SUMS, the SHA-256
/// of each file it publishes, and SHA256SUMS.sig, the signature over its exact bytes. Before anything is downloaded the
/// signature must hold and the manifest must list this version's full package under the hash the feed gives, which the
/// download is checked against; after, the downloaded file must hash to it. The version is bound by the file name the
/// manifest lists, so another release's signed manifest does not cover this one.
/// </summary>
public sealed partial class SignedReleaseVerifier(IReleaseFiles files, UpdateSigningKey key, ILogger<SignedReleaseVerifier> logger)
{
    public const string ManifestName = "SHA256SUMS";
    public const string SignatureName = "SHA256SUMS.sig";

    /// <summary>The most SHA256SUMS may hold: a release has a handful of files.</summary>
    public const int ManifestLimit = 64 * 1024;

    /// <summary>The most SHA256SUMS.sig may hold: one line of 88 base64 characters.</summary>
    public const int SignatureLimit = 1024;

    /// <summary>The longest release notes shown, in characters: a CHANGELOG.md section is far shorter.</summary>
    public const int NotesLimit = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private enum Fetched
    {
        Found,
        Missing,
        TooLarge,
        Unreachable,
    }

    private enum FileCheck
    {
        Missing,
        Matches,
        Differs,
        Unreadable,
    }

    /// <summary>
    /// Checks the release <paramref name="feed"/> offers, has <paramref name="download"/> fetch its full package into
    /// <paramref name="packagesFolder"/> only when the signed manifest vouches for it, and checks the file. A file under
    /// the package's name that is not the signed package is removed, whether it was there before or just came. Anything
    /// but <see cref="UpdateVerdict.Verified"/> means: apply nothing. A failed download is passed on as it is.
    /// </summary>
    public async Task<SignedDownload> DownloadAsync(Uri repository, string packageId, FeedPackage feed, string packagesFolder,
        Func<CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        string version = PlainVersion().IsMatch(feed.Version) ? feed.Version : "(not a plain version)";
        var result = await CheckAndDownloadAsync(repository, packageId, feed, packagesFolder, version, download, cancellationToken);
        if (result.Verdict == UpdateVerdict.Verified)
        {
            logger.LogInformation("Granary {Version} is signed, and the downloaded package is the signed one", version);
        }
        else
        {
            logger.LogWarning("Granary {Version} is not installed: {Reason}", version, Describe(result.Verdict));
        }
        return result;
    }

    /// <summary>
    /// Whether a verified package is still the signed one, checked again just before it is applied. One that changed is
    /// removed.
    /// </summary>
    public bool IsUnchanged(SignedPackage package)
    {
        try
        {
            using var stream = files.OpenRead(package.Path);
            if (stream is null)
            {
                logger.LogWarning("The verified update package is gone; nothing is applied");
                return false;
            }
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) == package.Sha256)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The verified update package could not be read again ({Problem}); nothing is applied", UpdateFailures.KindOf(ex));
            return false;
        }
        logger.LogWarning("The update package changed since it was verified; nothing is applied");
        Discard(package.Path);
        return false;
    }

    private async Task<SignedDownload> CheckAndDownloadAsync(Uri repository, string packageId, FeedPackage feed, string packagesFolder,
        string version, Func<CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        if (!key.IsPresent)
        {
            return new(UpdateVerdict.NoKey);
        }
        if (!PlainVersion().IsMatch(feed.Version) || !PlainName().IsMatch(packageId))
        {
            return new(UpdateVerdict.InvalidRelease);
        }
        string fileName = $"{packageId}-{feed.Version}-full.nupkg";
        var release = new Uri($"{repository.AbsoluteUri.TrimEnd('/')}/releases/download/v{feed.Version}/");
        var (verdict, signedHash) = await ReadSignedHashAsync(release, fileName, version, cancellationToken);
        if (signedHash is null)
        {
            return new(verdict);
        }
        // Velopack checks its download against the feed's hash; only when that is the signed one may it download at all,
        // since it takes Update.exe out of the package as soon as the download is done.
        if (!string.Equals(feed.FileName, fileName, StringComparison.Ordinal) || !IsHash(feed.Sha256, signedHash))
        {
            return new(UpdateVerdict.FeedMismatch);
        }

        // Velopack takes a file already under the package's name for the download, so one left from before must be the
        // signed package too.
        string path = Path.Combine(packagesFolder, fileName);
        var before = await CheckFileAsync(path, signedHash, cancellationToken);
        if (before is not (FileCheck.Missing or FileCheck.Matches) && !Discard(path))
        {
            return new(before == FileCheck.Unreadable ? UpdateVerdict.PackageUnreadable : UpdateVerdict.HashMismatch);
        }

        await download(cancellationToken);
        switch (await CheckFileAsync(path, signedHash, cancellationToken))
        {
            case FileCheck.Matches:
                return new(UpdateVerdict.Verified, new SignedPackage(path, Convert.ToHexStringLower(signedHash)), ReadNotes(path));
            case FileCheck.Missing:
            case FileCheck.Unreadable:
                Discard(path);
                return new(UpdateVerdict.PackageUnreadable);
            default:
                Discard(path);
                return new(UpdateVerdict.HashMismatch);
        }
    }

    private async Task<FileCheck> CheckFileAsync(string path, byte[] signedHash, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = files.OpenRead(path);
            if (stream is null)
            {
                return FileCheck.Missing;
            }
            var actual = await SHA256.HashDataAsync(stream, cancellationToken);
            return CryptographicOperations.FixedTimeEquals(actual, signedHash) ? FileCheck.Matches : FileCheck.Differs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileCheck.Unreadable;
        }
    }

    /// <summary>The notes the verified package carries; none when they cannot be read, which does not undo the verdict.</summary>
    private string? ReadNotes(string path)
    {
        try
        {
            using var stream = files.OpenRead(path);
            return stream is null ? null : PackageNotes.Read(stream, NotesLimit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            logger.LogInformation("The update's notes could not be read from its package ({Problem})", UpdateFailures.KindOf(ex));
            return null;
        }
    }

    /// <summary>The hash the release's signed manifest gives <paramref name="fileName"/>; null with the reason when there is none.</summary>
    private async Task<(UpdateVerdict Verdict, byte[]? Hash)> ReadSignedHashAsync(Uri release, string fileName, string version,
        CancellationToken cancellationToken)
    {
        var (manifest, manifestFetched) = await FetchAsync(new Uri(release, ManifestName), ManifestLimit, version, cancellationToken);
        switch (manifestFetched)
        {
            case Fetched.Missing:
                return (UpdateVerdict.NoManifest, null);
            case Fetched.TooLarge:
                return (UpdateVerdict.MalformedManifest, null);
            case Fetched.Unreachable:
                return (UpdateVerdict.Unreachable, null);
        }
        var (signature, signatureFetched) = await FetchAsync(new Uri(release, SignatureName), SignatureLimit, version, cancellationToken);
        switch (signatureFetched)
        {
            case Fetched.Missing:
                return (UpdateVerdict.NoSignature, null);
            case Fetched.TooLarge:
                return (UpdateVerdict.BadSignature, null);
            case Fetched.Unreachable:
                return (UpdateVerdict.Unreachable, null);
        }

        if (!SignatureHolds(manifest!, signature!))
        {
            return (UpdateVerdict.BadSignature, null);
        }
        // Read only once the signature holds: until then the manifest is anyone's text.
        if (ReadManifest(manifest!) is not { } entries)
        {
            return (UpdateVerdict.MalformedManifest, null);
        }
        return entries.TryGetValue(fileName, out var hash) ? (UpdateVerdict.Verified, hash) : (UpdateVerdict.MissingEntry, null);
    }

    private async Task<(byte[]? Bytes, Fetched Outcome)> FetchAsync(Uri url, int limit, string version, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await files.GetAsync(url, limit, cancellationToken);
            return bytes is null ? (null, Fetched.Missing) : (bytes, Fetched.Found);
        }
        catch (InvalidDataException)
        {
            return (null, Fetched.TooLarge);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Could not fetch {File} of Granary {Version}: {Problem}", url.Segments[^1], version, UpdateFailures.KindOf(ex));
            return (null, Fetched.Unreachable);
        }
    }

    private bool SignatureHolds(byte[] manifest, byte[] signatureFile)
    {
        // One line of base64; a byte outside ASCII decodes to '?', which no base64 has.
        string text = Encoding.ASCII.GetString(signatureFile).Trim();
        Span<byte> signature = stackalloc byte[UpdateSigningKey.SignatureLength];
        return Convert.TryFromBase64String(text, signature, out int written) && written == signature.Length
            && key.Verifies(manifest, signature);
    }

    /// <summary>
    /// SHA256SUMS as sha256sum writes it: one "hash  name" (or "hash *name") per line, LF or CRLF. Null unless every line is
    /// one, with a plain file name listed once.
    /// </summary>
    private static Dictionary<string, byte[]>? ReadManifest(byte[] manifest)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(manifest);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length == 0)
            {
                continue;
            }
            var match = ManifestLine().Match(line);
            if (!match.Success || !entries.TryAdd(match.Groups["name"].Value, Convert.FromHexString(match.Groups["hash"].Value)))
            {
                return null;
            }
        }
        return entries.Count > 0 ? entries : null;
    }

    private static bool IsHash(string hex, byte[] hash) =>
        Sha256Hex().IsMatch(hex) && Convert.FromHexString(hex).AsSpan().SequenceEqual(hash);

    private bool Discard(string path)
    {
        if (files.Remove(path))
        {
            return true;
        }
        logger.LogWarning("An update package that is not the signed one could not be removed; it is not applied");
        return false;
    }

    private static string Describe(UpdateVerdict verdict) => verdict switch
    {
        UpdateVerdict.NoKey => "this build carries no update signing key",
        UpdateVerdict.InvalidRelease => "the update feed names no plain version",
        UpdateVerdict.Unreachable => "its SHA256SUMS or signature could not be fetched",
        UpdateVerdict.NoManifest => "its release has no SHA256SUMS",
        UpdateVerdict.NoSignature => "its release has no SHA256SUMS.sig",
        UpdateVerdict.BadSignature => "its SHA256SUMS is not signed with Granary's update key",
        UpdateVerdict.MalformedManifest => "its SHA256SUMS is not a list of checksums",
        UpdateVerdict.MissingEntry => "its SHA256SUMS does not list its full package",
        UpdateVerdict.FeedMismatch => "the update feed offers another package than its SHA256SUMS lists",
        UpdateVerdict.HashMismatch => "the package is not the one its SHA256SUMS lists",
        UpdateVerdict.PackageUnreadable => "the downloaded package could not be read",
        _ => verdict.ToString(),
    };

    // As the release workflow and the scripts take a version; [0-9], since \d takes any script's digits.
    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainVersion();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();

    [GeneratedRegex(@"\A[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"\A(?<hash>[0-9A-Fa-f]{64}) [ *](?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestLine();
}
