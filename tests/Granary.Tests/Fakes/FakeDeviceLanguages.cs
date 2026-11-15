using System.Globalization;
using Granary.Core.Texts;

namespace Granary.Tests.Fakes;

/// <summary>Windows' languages and regional format, as a test sets them.</summary>
internal sealed class FakeDeviceLanguages(IReadOnlyList<string> preferred, string regionalFormat) : IDeviceLanguages
{
    public IReadOnlyList<string> Preferred { get; } = preferred;

    public CultureInfo RegionalFormat { get; } = CultureInfo.GetCultureInfo(regionalFormat);
}
