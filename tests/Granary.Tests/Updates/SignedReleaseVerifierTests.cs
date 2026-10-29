using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Granary.Core.Abstractions;
using Granary.Core.Updates;
using Granary.Tests.Fakes;

namespace Granary.Tests.Updates;

/// <summary>
/// An update is downloaded only when its release's SHA256SUMS is signed with the key the app carries and lists the package
/// the feed offers, and kept only when the downloaded file is that package. Every key here is made for the test.
/// </summary>
public sealed class SignedReleaseVerifierTests : IDisposable
{
    private const string PackageId = "GranaryApp";
    private const string FullName = "GranaryApp-0.2.0-full.nupkg";
    private const string Packages = @"C:\Users\Elena\AppData\Local\GranaryApp\packages";
    private const string PackagePath = Packages + @"\" + FullName;
    private static readonly Uri Repository = new("https://github.com/elena/granary");
    private static readonly Uri ManifestUrl = new("https://github.com/elena/granary/releases/download/v0.2.0/SHA256SUMS");
    private static readonly Uri SignatureUrl = new("https://github.com/elena/granary/releases/download/v0.2.0/SHA256SUMS.sig");
    private static readonly byte[] Package = Encoding.UTF8.GetBytes("the 0.2.0 update package");

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeReleaseFiles _files = new();
    private readonly LogSink _logs = new();
    private byte[] _download = Package;
    private Exception? _downloadFails;
    private bool _lockedOnceDownloaded;
    private int _downloads;

    public void Dispose() => _key.Dispose();

    [Fact]
    public async Task ASignedRelease_WhosePackageIsListed_IsDownloadedAndVerified()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));

        var result = await DownloadAsync(Create());

        Assert.Equal(UpdateVerdict.Verified, result.Verdict);
        Assert.Equal(new SignedPackage(PackagePath, Hex(Package)), result.Package);
        Assert.Equal([ManifestUrl, SignatureUrl], _files.Read);
        Assert.Equal(1, _downloads);
        Assert.Empty(_files.Removed);
    }

    [Fact]
    public async Task AManifestChangedAfterSigning_IsNotTrusted_AndNothingIsDownloaded()
    {
        var swapped = Encoding.UTF8.GetBytes("something else");
        string signature = Sign(Manifest());
        // Another package's hash put in after the release was signed, and the feed and the download changed to match it.
        Publish(Manifest(content: swapped), signature);
        _download = swapped;

        var result = await DownloadAsync(Create(), Feed(content: swapped));

        Assert.Equal(UpdateVerdict.BadSignature, result.Verdict);
        Assert.Null(result.Package);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task ASignatureByAnotherKey_IsNotTrusted()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();
        Publish(manifest, Sign(manifest, other));

        Assert.Equal(UpdateVerdict.BadSignature, (await DownloadAsync(Create())).Verdict);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task AManifestThatDoesNotListThePackage_IsNotTrusted()
    {
        var manifest = Manifest(fullName: "GranaryApp-0.2.0-delta.nupkg");
        Publish(manifest, Sign(manifest));

        Assert.Equal(UpdateVerdict.MissingEntry, (await DownloadAsync(Create())).Verdict);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task ADownloadThatDiffersFromTheSignedHash_IsRemoved()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));
        _download = Encoding.UTF8.GetBytes("a package swapped on the way");

        var result = await DownloadAsync(Create());

        Assert.Equal(UpdateVerdict.HashMismatch, result.Verdict);
        Assert.Null(result.Package);
        Assert.Equal([PackagePath], _files.Removed);
    }

    [Fact]
    public async Task ADownloadThatCannotBeRead_IsRemoved()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));
        _lockedOnceDownloaded = true;

        Assert.Equal(UpdateVerdict.PackageUnreadable, (await DownloadAsync(Create())).Verdict);
        Assert.Equal([PackagePath], _files.Removed);
    }

    [Fact]
    public async Task APackageThatCannotBeRemoved_IsStillNotTrusted_AndTheLogSaysSo()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));
        _download = Encoding.UTF8.GetBytes("a package swapped on the way");
        _files.CanRemove = false;

        Assert.Equal(UpdateVerdict.HashMismatch, (await DownloadAsync(Create())).Verdict);
        Assert.Contains("could not be removed", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADownloadThatFails_IsPassedOn_AndNothingIsChecked()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));
        _downloadFails = new HttpRequestException("offline");

        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(Create()));

        // Only the look for a file left from before.
        Assert.Equal([PackagePath], _files.Opened);
        Assert.Empty(_files.Removed);
    }

    [Fact]
    public async Task ASignedPackageAlreadyInThePackagesFolder_IsKept()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));
        _files.Disk[PackagePath] = Package;

        var result = await DownloadAsync(Create());

        Assert.Equal(UpdateVerdict.Verified, result.Verdict);
        Assert.Empty(_files.Removed);
    }

    [Fact]
    public async Task AnotherFileUnderThePackagesName_IsRemovedBeforeTheDownload()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));
        _files.Disk[PackagePath] = Encoding.UTF8.GetBytes("left from before, not the signed package");
        _files.BeforeDownload = () => Assert.False(_files.Disk.ContainsKey(PackagePath));

        var result = await DownloadAsync(Create());

        Assert.Equal(UpdateVerdict.Verified, result.Verdict);
        Assert.Equal([PackagePath], _files.Removed);
        Assert.Equal(1, _downloads);
    }

    [Fact]
    public async Task AnotherFileThatCannotBeRemoved_StopsTheUpdateBeforeTheDownload()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));
        _files.Disk[PackagePath] = Encoding.UTF8.GetBytes("left from before, not the signed package");
        _files.CanRemove = false;

        Assert.Equal(UpdateVerdict.HashMismatch, (await DownloadAsync(Create())).Verdict);
        Assert.Equal(0, _downloads);
        Assert.Contains("could not be removed", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFeedPackageOtherThanTheSignedOne_IsNotDownloaded()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));

        Assert.Equal(UpdateVerdict.FeedMismatch, (await DownloadAsync(Create(), Feed(content: Encoding.UTF8.GetBytes("another")))).Verdict);
        Assert.Equal(UpdateVerdict.FeedMismatch, (await DownloadAsync(Create(), Feed() with { FileName = "GranaryApp-0.1.0-full.nupkg" })).Verdict);
        Assert.Equal(UpdateVerdict.FeedMismatch, (await DownloadAsync(Create(), Feed() with { FileName = @"..\" + FullName })).Verdict);
        Assert.Equal(UpdateVerdict.FeedMismatch,
            (await DownloadAsync(Create(), Feed() with { FileName = "https://objects.example/" + FullName })).Verdict);
        Assert.Equal(UpdateVerdict.FeedMismatch, (await DownloadAsync(Create(), Feed() with { Sha256 = "" })).Verdict);
        Assert.Equal(UpdateVerdict.FeedMismatch, (await DownloadAsync(Create(), Feed() with { Sha256 = "not hex" })).Verdict);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task AFeedHashInCapitals_IsTheSameHash()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));

        var result = await DownloadAsync(Create(), Feed() with { Sha256 = Hex(Package).ToUpperInvariant() });

        Assert.Equal(UpdateVerdict.Verified, result.Verdict);
    }

    [Fact]
    public async Task AReleaseWithoutItsSignature_IsNotTrusted()
    {
        Publish(Manifest(), signature: null);

        Assert.Equal(UpdateVerdict.NoSignature, (await DownloadAsync(Create())).Verdict);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task AReleaseWithoutAManifest_IsNotTrusted()
    {
        Assert.Equal(UpdateVerdict.NoManifest, (await DownloadAsync(Create())).Verdict);
        Assert.Equal([ManifestUrl], _files.Read);
        Assert.Equal(0, _downloads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all!")]
    [InlineData("AAECAwQFBgcICQ==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    public async Task ASignatureThatIsGarbage_IsNotTrusted(string signature)
    {
        Publish(Manifest(), signature);

        Assert.Equal(UpdateVerdict.BadSignature, (await DownloadAsync(Create())).Verdict);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public async Task ASignatureOfTheWrongLength_IsNotTrusted(int length)
    {
        var manifest = Manifest();
        byte[] real = Convert.FromBase64String(Sign(manifest));
        byte[] signature = new byte[length];
        real.AsSpan(0, Math.Min(length, real.Length)).CopyTo(signature);
        Publish(manifest, Convert.ToBase64String(signature));

        Assert.Equal(UpdateVerdict.BadSignature, (await DownloadAsync(Create())).Verdict);
    }

    [Fact]
    public async Task AnotherVersionsSignedManifest_DoesNotCoverThisRelease()
    {
        // A real, signed 0.1.0 manifest put into the 0.2.0 release, its package offered as 0.2.0's.
        var older = Manifest(fullName: "GranaryApp-0.1.0-full.nupkg");
        Publish(older, Sign(older));

        Assert.Equal(UpdateVerdict.MissingEntry, (await DownloadAsync(Create())).Verdict);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task TheSignatureCoversTheExactBytes_LineEndingsIncluded()
    {
        var windows = Manifest(newline: "\r\n");
        Publish(windows, Sign(windows));
        Assert.Equal(UpdateVerdict.Verified, (await DownloadAsync(Create())).Verdict);

        // The same lines with other line endings are other bytes.
        Publish(Manifest(newline: "\n"), Sign(windows));
        Assert.Equal(UpdateVerdict.BadSignature, (await DownloadAsync(Create())).Verdict);
    }

    [Fact]
    public async Task ABuildWithoutAKey_TrustsNoUpdate_AndReadsNothing()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));

        var verifier = Create("PLACEHOLDER: no update signing key yet.");

        Assert.False(UpdateSigningKey.Parse("PLACEHOLDER: no update signing key yet.").IsPresent);
        Assert.Equal(UpdateVerdict.NoKey, (await DownloadAsync(verifier)).Verdict);
        Assert.Empty(_files.Read);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task AKeyOnAnotherCurve_IsNoKey()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var manifest = Manifest();
        Publish(manifest, Sign(manifest, other));

        Assert.Equal(UpdateVerdict.NoKey, (await DownloadAsync(Create(other.ExportSubjectPublicKeyInfoPem()))).Verdict);
    }

    [Fact]
    public void APrivateKey_IsNeverTakenForThePublicOne()
    {
        Assert.False(UpdateSigningKey.Parse(_key.ExportPkcs8PrivateKeyPem()).IsPresent);
        Assert.False(UpdateSigningKey.Parse(_key.ExportECPrivateKeyPem()).IsPresent);
        Assert.False(UpdateSigningKey.Parse(_key.ExportSubjectPublicKeyInfoPem() + "\n" + _key.ExportPkcs8PrivateKeyPem()).IsPresent);
        Assert.True(UpdateSigningKey.Parse(_key.ExportSubjectPublicKeyInfoPem()).IsPresent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v0.2.0")]
    [InlineData("0.2.0/../../other/releases/download/v9.9.9")]
    [InlineData("0.2.0?x=1")]
    [InlineData("0.2.0\n")]
    [InlineData("0.2")]
    public async Task AVersionThatIsNotAPlainOne_IsRefusedBeforeAnyRead(string version)
    {
        Assert.Equal(UpdateVerdict.InvalidRelease, (await DownloadAsync(Create(), Feed() with { Version = version })).Verdict);
        Assert.Empty(_files.Read);
    }

    [Fact]
    public async Task AReleaseThatCannotBeReached_SaysSo_WithoutTheAddressInTheLog()
    {
        _files.Unreachable = new HttpRequestException("offline at https://objects.example/x?token=abc123");

        Assert.Equal(UpdateVerdict.Unreachable, (await DownloadAsync(Create())).Verdict);
        Assert.Contains("0.2.0", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeout_IsUnreachable_ButClosingGranaryStopsTheCheck()
    {
        _files.Unreachable = new TimeoutException("too slow");
        Assert.Equal(UpdateVerdict.Unreachable, (await DownloadAsync(Create())).Verdict);

        _files.Unreachable = new TaskCanceledException("timed out");
        Assert.Equal(UpdateVerdict.Unreachable, (await DownloadAsync(Create())).Verdict);

        using var closing = new CancellationTokenSource();
        await closing.CancelAsync();
        _files.Unreachable = new OperationCanceledException(closing.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create().DownloadAsync(Repository, PackageId, Feed(), Packages, DownloadPackage, closing.Token));
    }

    [Fact]
    public async Task EveryVerdictButVerified_IsLoggedWithItsReason()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();
        Publish(manifest, Sign(manifest, other));

        await DownloadAsync(Create());

        Assert.Contains("Granary 0.2.0 is not installed", _logs.All, StringComparison.Ordinal);
        Assert.Contains("not signed with Granary's update key", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheManifestAndSignature_AreReadWithASizeLimit()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));

        await DownloadAsync(Create());

        Assert.Equal([SignedReleaseVerifier.ManifestLimit, SignedReleaseVerifier.SignatureLimit], _files.Limits);
    }

    [Fact]
    public async Task AManifestOrSignatureOverTheLimit_IsNotTrusted()
    {
        Publish(new byte[SignedReleaseVerifier.ManifestLimit + 1], "AAAA");
        Assert.Equal(UpdateVerdict.MalformedManifest, (await DownloadAsync(Create())).Verdict);

        var manifest = Manifest();
        _files.Files[ManifestUrl] = manifest;
        _files.Files[SignatureUrl] = new byte[SignedReleaseVerifier.SignatureLimit + 1];
        Assert.Equal(UpdateVerdict.BadSignature, (await DownloadAsync(Create())).Verdict);
    }

    [Theory]
    [InlineData("not a hash line\n")]
    [InlineData("0123  GranaryApp-0.2.0-full.nupkg\n")]
    [InlineData("{0}  GranaryApp-0.2.0-full.nupkg\n{0}  GranaryApp-0.2.0-full.nupkg\n")]
    [InlineData("{0}  ../GranaryApp-0.2.0-full.nupkg\n")]
    [InlineData("{0}  GranaryApp-0.2.0-full.nupkg\nwhatever\n")]
    [InlineData("\n")]
    public async Task ASignedManifestThatIsMalformed_IsNotTrusted(string text)
    {
        var manifest = Encoding.UTF8.GetBytes(string.Format(System.Globalization.CultureInfo.InvariantCulture, text, Hex(Package)));
        Publish(manifest, Sign(manifest));

        Assert.Equal(UpdateVerdict.MalformedManifest, (await DownloadAsync(Create())).Verdict);
    }

    [Fact]
    public async Task ASignedManifestThatIsNotUtf8_IsNotTrusted()
    {
        byte[] line = Encoding.ASCII.GetBytes($"{Hex(Package)}  {FullName}\n");
        // 0xC3 0x28 is no UTF-8 sequence.
        byte[] manifest = [.. line, 0xC3, 0x28, (byte)'\n'];
        Publish(manifest, Sign(manifest));

        Assert.Equal(UpdateVerdict.MalformedManifest, (await DownloadAsync(Create())).Verdict);
    }

    [Fact]
    public async Task ASignedManifestInCapitals_AndBinaryMode_StillMatches()
    {
        var manifest = Encoding.UTF8.GetBytes($"{Hex(Package).ToUpperInvariant()} *{FullName}\n");
        Publish(manifest, Sign(manifest));

        Assert.Equal(UpdateVerdict.Verified, (await DownloadAsync(Create())).Verdict);
    }

    [Fact]
    public async Task TheNotesShown_AreTheOnesTheSignedPackageCarries()
    {
        byte[] package = Nupkg("### Fixed\n- Posters load faster.");
        var manifest = Manifest(content: package);
        Publish(manifest, Sign(manifest));
        _download = package;

        var result = await DownloadAsync(Create(), Feed(content: package));

        Assert.Equal(UpdateVerdict.Verified, result.Verdict);
        Assert.Equal("### Fixed\n- Posters load faster.", result.Notes);
    }

    [Fact]
    public async Task APackageWithoutNotes_OrNotesTooLong_ShowsNone()
    {
        foreach (byte[] package in new[] { Nupkg(null), Nupkg(new string('x', SignedReleaseVerifier.NotesLimit + 1)), Package })
        {
            var manifest = Manifest(content: package);
            Publish(manifest, Sign(manifest));
            _download = package;

            var result = await DownloadAsync(Create(), Feed(content: package));

            Assert.Equal(UpdateVerdict.Verified, result.Verdict);
            Assert.Null(result.Notes);
        }
    }

    [Fact]
    public async Task ASignedPackage_IsCheckedOnceMoreBeforeItIsApplied()
    {
        var manifest = Manifest();
        Publish(manifest, Sign(manifest));
        var verifier = Create();
        var package = (await DownloadAsync(verifier)).Package!;

        Assert.True(verifier.IsUnchanged(package));

        _files.Disk[PackagePath] = Encoding.UTF8.GetBytes("swapped after the check");
        Assert.False(verifier.IsUnchanged(package));
        Assert.Equal([PackagePath], _files.Removed);
        Assert.Contains("changed since it was verified", _logs.All, StringComparison.Ordinal);

        Assert.False(verifier.IsUnchanged(package));
    }

    [Fact]
    public void TheKeyTheAppCarries_IsAPlaceholderOrAP256PublicKey()
    {
        // Once update-key.pub holds the real key, this should require UpdateSigningKey.Embedded.IsPresent.
        string text = UpdateSigningKey.EmbeddedText;

        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        Assert.True(UpdateSigningKey.Embedded.IsPresent || text.Contains("PLACEHOLDER", StringComparison.Ordinal), text);
    }

    private static string Hex(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] Manifest(string newline = "\n", string fullName = FullName, byte[]? content = null) =>
        Encoding.UTF8.GetBytes(
            $"{Hex(Encoding.UTF8.GetBytes("setup"))}  GranaryApp-win-Setup.exe{newline}" +
            $"{Hex(content ?? Package)}  {fullName}{newline}" +
            $"{Hex(Encoding.UTF8.GetBytes("portable"))}  GranaryApp-win-Portable.zip{newline}");

    /// <summary>A package as vpk pack writes one: the nuspec at its root, with the release's notes in it.</summary>
    private static byte[] Nupkg(string? notes)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var nuspec = new StreamWriter(zip.CreateEntry("GranaryApp.nuspec").Open()))
            {
                string release = notes is null ? "" : $"<releaseNotes>{System.Security.SecurityElement.Escape(notes)}</releaseNotes>";
                nuspec.Write($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
                      <metadata><id>GranaryApp</id><version>0.2.0</version>{release}</metadata>
                    </package>
                    """);
            }
            using var app = new StreamWriter(zip.CreateEntry("lib/app/Granary.exe").Open());
            app.Write("the app");
        }
        return buffer.ToArray();
    }

    private static FeedPackage Feed(byte[]? content = null) => new("0.2.0", FullName, Hex(content ?? Package));

    private string Sign(byte[] data, ECDsa? with = null) =>
        Convert.ToBase64String((with ?? _key).SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private void Publish(byte[] manifest, string? signature)
    {
        _files.Files[ManifestUrl] = manifest;
        if (signature is null)
        {
            _files.Files.Remove(SignatureUrl);
        }
        else
        {
            _files.Files[SignatureUrl] = Encoding.ASCII.GetBytes(signature + "\n");
        }
    }

    private SignedReleaseVerifier Create(string? publicKey = null) =>
        new(_files, UpdateSigningKey.Parse(publicKey ?? _key.ExportSubjectPublicKeyInfoPem()), _logs.For<SignedReleaseVerifier>());

    private Task<SignedDownload> DownloadAsync(SignedReleaseVerifier verifier, FeedPackage? feed = null) =>
        verifier.DownloadAsync(Repository, PackageId, feed ?? Feed(), Packages, DownloadPackage, TestContext.Current.CancellationToken);

    // As Velopack does: nothing when the package is already there, else the download lands under the package's name.
    private Task DownloadPackage(CancellationToken cancellationToken)
    {
        _files.BeforeDownload?.Invoke();
        _downloads++;
        if (_downloadFails is { } failure)
        {
            throw failure;
        }
        _files.Disk.TryAdd(PackagePath, _download);
        if (_lockedOnceDownloaded)
        {
            _files.Locked.Add(PackagePath);
        }
        return Task.CompletedTask;
    }

    private sealed class FakeReleaseFiles : IReleaseFiles
    {
        public Dictionary<Uri, byte[]> Files { get; } = [];

        /// <summary>The packages folder: file paths and their bytes.</summary>
        public Dictionary<string, byte[]> Disk { get; } = [];

        /// <summary>Files that exist but cannot be read.</summary>
        public HashSet<string> Locked { get; } = [];

        public Exception? Unreachable { get; set; }

        public bool CanRemove { get; set; } = true;

        public Action? BeforeDownload { get; set; }

        public List<Uri> Read { get; } = [];

        public List<int> Limits { get; } = [];

        public List<string> Opened { get; } = [];

        public List<string> Removed { get; } = [];

        public Task<byte[]?> GetAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
        {
            Read.Add(url);
            Limits.Add(maxBytes);
            if (Unreachable is { } problem)
            {
                throw problem;
            }
            if (!Files.TryGetValue(url, out var bytes))
            {
                return Task.FromResult<byte[]?>(null);
            }
            return bytes.Length > maxBytes ? throw new InvalidDataException("too large") : Task.FromResult<byte[]?>(bytes);
        }

        public Stream? OpenRead(string path)
        {
            Opened.Add(path);
            if (Locked.Contains(path))
            {
                throw new IOException("locked");
            }
            return Disk.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, writable: false) : null;
        }

        public bool Remove(string path)
        {
            Removed.Add(path);
            if (!CanRemove)
            {
                return false;
            }
            Disk.Remove(path);
            Locked.Remove(path);
            return true;
        }
    }
}
