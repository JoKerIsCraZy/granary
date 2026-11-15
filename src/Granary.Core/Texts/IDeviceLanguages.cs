using System.Globalization;

namespace Granary.Core.Texts;

/// <summary>Windows' languages, which "Match device" follows: as they were when Granary started, as its texts were chosen by them.</summary>
public interface IDeviceLanguages
{
    /// <summary>Windows' preferred languages, in the order the user put them; empty when they could not be read.</summary>
    IReadOnlyList<string> Preferred { get; }

    /// <summary>Windows' regional format, which dates and numbers follow unless a language of another was chosen.</summary>
    CultureInfo RegionalFormat { get; }
}
