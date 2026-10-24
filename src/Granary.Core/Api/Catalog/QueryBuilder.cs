using System.Globalization;
using System.Text;
using Granary.Core.Api.Images;

namespace Granary.Core.Api.Catalog;

/// <summary>Builds a query string: names and values escaped, unset values left out, arrays as repeated keys.</summary>
internal sealed class QueryBuilder
{
    private readonly StringBuilder _query = new();

    public QueryBuilder Add(string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _query.Append(_query.Length == 0 ? '?' : '&')
                .Append(Uri.EscapeDataString(name))
                .Append('=')
                .Append(Uri.EscapeDataString(value));
        }
        return this;
    }

    public QueryBuilder Add(string name, int? value) => value is { } number ? Add(name, number.ToString(CultureInfo.InvariantCulture)) : this;

    /// <summary>Adds <c>name=true</c> when <paramref name="flag"/> is set; false is the server default and is left out.</summary>
    public QueryBuilder AddFlag(string name, bool flag) => flag ? Add(name, "true") : this;

    public QueryBuilder AddEach(string name, IEnumerable<string>? values)
    {
        foreach (string value in values ?? [])
        {
            Add(name, value);
        }
        return this;
    }

    public QueryBuilder Add(ImageParameters? images)
    {
        foreach (var (name, value) in images?.Values ?? [])
        {
            Add(name, value);
        }
        return this;
    }

    /// <summary>The query with its leading <c>?</c>, or empty.</summary>
    public override string ToString() => _query.ToString();
}
