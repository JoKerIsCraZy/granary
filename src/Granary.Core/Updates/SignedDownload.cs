namespace Granary.Core.Updates;

/// <summary>A downloaded full package that is the one its release signed: where it lies, and its SHA-256 in hex.</summary>
public sealed record SignedPackage(string Path, string Sha256);

/// <summary>
/// How a signed download ended. Only a <see cref="UpdateVerdict.Verified"/> one has a <see cref="Package"/>, and its
/// <see cref="Notes"/> are the ones the signed package carries; null without.
/// </summary>
public sealed record SignedDownload(UpdateVerdict Verdict, SignedPackage? Package = null, string? Notes = null);
