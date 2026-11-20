namespace Granary.Core.Api.Profiles;

/// <summary>Why a picture was not sent as a profile's avatar.</summary>
public enum AvatarProblem
{
    /// <summary>Larger than <see cref="AvatarUpload.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>Not a JPEG, PNG or WebP image, an empty file among them.</summary>
    NotAnImage,
}

/// <summary>A picture refused before it was sent (<see cref="AvatarUpload.Check"/>).</summary>
public sealed class AvatarRejectedException(AvatarProblem problem)
    : Exception(problem == AvatarProblem.TooLarge ? "The picture is larger than a profile's avatar may be." : "The picture is not a JPEG, PNG or WebP image.")
{
    public AvatarProblem Problem { get; } = problem;
}

/// <summary>
/// What the server takes as a profile's uploaded picture (silo-server <c>uploadProfileAvatar</c>): JPEG, PNG or WebP, at most
/// 10 MiB. The type comes from the bytes, as the server decodes them, never from a file's name.
/// </summary>
public static class AvatarUpload
{
    /// <summary>The largest picture the server takes for a profile (10 MiB).</summary>
    public const int MaxBytes = 10 << 20;

    /// <summary>The file types the picker offers.</summary>
    public static IReadOnlyList<string> FileTypes { get; } = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>The image's media type, from its first bytes.</summary>
    /// <exception cref="AvatarRejectedException">The picture is empty, too large, or not one of the three types.</exception>
    public static string Check(ReadOnlySpan<byte> image)
    {
        if (image.Length > MaxBytes)
        {
            throw new AvatarRejectedException(AvatarProblem.TooLarge);
        }
        return TypeOf(image) ?? throw new AvatarRejectedException(AvatarProblem.NotAnImage);
    }

    private static string? TypeOf(ReadOnlySpan<byte> image)
    {
        if (image.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }
        if (image.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }
        if (image.Length >= 12 && image.StartsWith("RIFF"u8) && image[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }
}
