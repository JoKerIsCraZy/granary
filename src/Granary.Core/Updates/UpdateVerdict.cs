namespace Granary.Core.Updates;

/// <summary>How the check of an update ended. Only <see cref="Verified"/> lets it be applied.</summary>
public enum UpdateVerdict
{
    /// <summary>The release is signed with Granary's key, and the downloaded package is the one it lists.</summary>
    Verified,

    /// <summary>This build carries no update signing key, so no update can be trusted.</summary>
    NoKey,

    /// <summary>The feed names a version or package that is not a plain one; nothing was read.</summary>
    InvalidRelease,

    /// <summary>SHA256SUMS or its signature could not be fetched.</summary>
    Unreachable,

    NoManifest,
    NoSignature,

    /// <summary>The signature is not one, or it does not verify SHA256SUMS with Granary's key.</summary>
    BadSignature,

    /// <summary>The signed SHA256SUMS is not a plain list of checksums.</summary>
    MalformedManifest,

    /// <summary>The signed SHA256SUMS does not list this version's full package.</summary>
    MissingEntry,

    /// <summary>The feed offers another file, or another hash, than the signed one; nothing was downloaded.</summary>
    FeedMismatch,

    /// <summary>The downloaded package is not the signed one.</summary>
    HashMismatch,

    /// <summary>The downloaded package could not be read to check it.</summary>
    PackageUnreadable,
}
