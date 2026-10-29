namespace Granary.Core.Updates;

/// <summary>
/// The full package the update feed offers: its version, file name and SHA-256 in hex. The feed is not signed, so none of
/// this counts until the release's signed SHA256SUMS agrees.
/// </summary>
public sealed record FeedPackage(string Version, string FileName, string Sha256);
