using System.Net.Http.Headers;

namespace Granary.Core.Api;

/// <summary>
/// A read of a file from byte <paramref name="From"/> to its end (<c>Range: bytes=From-</c>), for a transfer that carries on
/// where it stopped. <paramref name="IfRange"/> is the validator the bytes on disk came with (a strong ETag, or a
/// <c>Last-Modified</c> date in its HTTP form): a server whose file changed since answers 200 with the whole new file instead
/// of the rest of the old one. Offsets are <see cref="long"/>, so files over 4 GB resume as well.
/// </summary>
public sealed record ByteRange(long From, string? IfRange)
{
    /// <summary>
    /// <paramref name="value"/> is what <c>If-Range</c> takes: one strong entity tag or one HTTP date. A weak or a wildcard
    /// tag, a list or anything else is not, and bytes kept with it cannot be resumed.
    /// </summary>
    public static bool IsValidator(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && RangeConditionHeaderValue.TryParse(value, out var condition)
        && (condition.EntityTag is null || (!condition.EntityTag.IsWeak && condition.EntityTag.Tag != "*"));

    /// <summary>Puts this range on a request's headers.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="From"/> is negative.</exception>
    /// <exception cref="ArgumentException"><see cref="IfRange"/> is not a validator (<see cref="IsValidator"/>).</exception>
    internal void ApplyTo(HttpRequestHeaders headers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(From);
        if (IfRange is not null && !IsValidator(IfRange))
        {
            throw new ArgumentException("If-Range takes one strong entity tag or one HTTP date.", "range");
        }
        headers.Range = new RangeHeaderValue(From, null);
        if (IfRange is not null)
        {
            headers.IfRange = RangeConditionHeaderValue.Parse(IfRange);
        }
    }
}
