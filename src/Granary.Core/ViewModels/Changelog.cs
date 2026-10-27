using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Granary.Core.ViewModels;

/// <summary>Changes of one kind within a release: "Added", "Fixed"; an empty title for a list under no heading.</summary>
public sealed record ChangelogGroup(string Title, IReadOnlyList<string> Items)
{
    public bool HasTitle => Title.Length > 0;
}

/// <summary>A release in the changelog: a version and its day, or "Unreleased".</summary>
public sealed record ChangelogRelease(string Version, string? Date, IReadOnlyList<ChangelogGroup> Groups)
{
    /// <summary>"0.2.0 · Oct 27, 2026", "Unreleased".</summary>
    public string Heading => Date is { } date ? $"{Version} · {date}" : Version;
}

/// <summary>
/// Reads a changelog as <see href="https://keepachangelog.com">Keep a Changelog</see> writes it (Granary's CHANGELOG.md, and a
/// release's notes): <c>## [0.2.0] - 2026-10-27</c> starts a release, <c>### Added</c> a group, <c>- </c> an item, an
/// indented line continues it. Links, bold and code read as their plain words; anything else is left out.
/// </summary>
public static partial class Changelog
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    public static IReadOnlyList<ChangelogRelease> Parse(string? markdown)
    {
        var releases = new List<ChangelogRelease>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return releases;
        }
        string? version = null;
        string? date = null;
        var groups = new List<ChangelogGroup>();
        string? groupTitle = null;
        var items = new List<string>();
        StringBuilder? item = null;

        void EndItem()
        {
            if (item is not null)
            {
                items.Add(Plain(item.ToString()));
                item = null;
            }
        }

        void EndGroup()
        {
            EndItem();
            if (items.Count > 0)
            {
                groups.Add(new ChangelogGroup(groupTitle ?? "", [.. items]));
            }
            items.Clear();
            groupTitle = null;
        }

        void EndRelease()
        {
            EndGroup();
            // Notes without a release heading still are one release.
            if (version is not null || groups.Count > 0)
            {
                releases.Add(new ChangelogRelease(version ?? "", date, [.. groups]));
            }
            groups.Clear();
            version = null;
            date = null;
        }

        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                EndRelease();
                (version, date) = ReleaseHeading(line[3..]);
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                EndGroup();
                groupTitle = Plain(line[4..]);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                EndItem();
                item = new StringBuilder(line[2..].Trim());
            }
            else if (item is not null && line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Length > 0)
            {
                item.Append(' ').Append(line.Trim());
            }
            else
            {
                EndItem();
            }
        }
        EndRelease();
        return releases;
    }

    // "[0.2.0] - 2026-10-27" → ("0.2.0", "Oct 27, 2026"); "[Unreleased]" → ("Unreleased", null).
    private static (string Version, string? Date) ReleaseHeading(string heading)
    {
        string[] parts = heading.Split(" - ", 2, StringSplitOptions.TrimEntries);
        // A linked heading, [0.2.0](https://...), reads as its words before the brackets go.
        string version = Plain(parts[0]).Trim('[', ']');
        string? date = parts.Length > 1
            ? DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", English, DateTimeStyles.None, out var day) ? day.ToString("MMM d, yyyy", English) : parts[1]
            : null;
        return (version, date);
    }

    // Markdown's links, bold and code as their words.
    private static string Plain(string text)
    {
        text = LinkPattern().Replace(text, "$1");
        return text.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal).Trim();
    }

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkPattern();
}
