using System.Text;
using System.Text.RegularExpressions;

namespace Granary.Core.Artwork;

/// <summary>What <see cref="AvatarSvg.Check"/> found.</summary>
public enum AvatarSvgCheck
{
    /// <summary>The server did not label it SVG: a bitmap, for the bitmap decoder.</summary>
    NotSvg,

    /// <summary>A small, self-contained SVG drawing: it may be drawn.</summary>
    Drawable,

    /// <summary>Labelled SVG, but not drawn: the avatar shows its letter.</summary>
    Refused,
}

/// <summary>
/// Whether one of the server's own preset avatars (<c>/profile-avatars/&lt;id&gt;.svg</c>) may be drawn as SVG. Only what the
/// server labels <c>image/svg+xml</c> counts, never bytes that merely look like markup. Such markup is drawn only when it is
/// small (<see cref="MaxBytes"/>), UTF-8, has <c>&lt;svg&gt;</c> as its root (after an optional <c>&lt;?xml?&gt;</c>
/// declaration), declares no doctype or entities, and refers to nothing outside itself: every <c>href</c> and <c>url()</c>
/// points at a <c>#</c> fragment, and there is no script, foreign object or <c>@import</c>. Anything else is refused.
/// </summary>
/// <remarks>The server's presets are a few shapes with attribute paint (web/public/profile-avatars), well inside this.</remarks>
public static partial class AvatarSvg
{
    /// <summary>The largest avatar drawing taken; the presets are well under a kilobyte.</summary>
    public const int MaxBytes = 256 * 1024;

    private const string SvgMediaType = "image/svg+xml";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static AvatarSvgCheck Check(ArtworkBytes image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!string.Equals(image.MediaType, SvgMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return AvatarSvgCheck.NotSvg;
        }
        if (image.Length > MaxBytes || Markup(image) is not { } markup)
        {
            return AvatarSvgCheck.Refused;
        }
        return OpensWithSvg(markup) && !HasForbidden(markup) ? AvatarSvgCheck.Drawable : AvatarSvgCheck.Refused;
    }

    private static bool HasForbidden(string markup)
    {
        try
        {
            return Forbidden().IsMatch(markup);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    // The bytes as text; null when they are not UTF-8.
    private static string? Markup(ArtworkBytes image)
    {
        var bytes = new byte[image.Length];
        using (var stream = image.OpenRead())
        {
            stream.ReadExactly(bytes);
        }
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool OpensWithSvg(string markup)
    {
        var text = markup.AsSpan().TrimStart('﻿').TrimStart();
        if (text.StartsWith("<?xml", StringComparison.Ordinal))
        {
            int end = text.IndexOf("?>", StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }
            text = text[(end + 2)..].TrimStart();
        }
        return text.StartsWith("<svg", StringComparison.Ordinal) && text.Length > 4 && (char.IsWhiteSpace(text[4]) || text[4] is '>' or '/');
    }

    // A doctype or entity declaration, a reference that leaves the drawing, or a way to run or embed other content.
    [GeneratedRegex("""<!DOCTYPE|<!ENTITY|<script|<foreignObject|@import|href\s*=\s*(?!["']#)|url\(\s*(?!["']?\s*#)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex Forbidden();
}
