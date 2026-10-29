using System.IO.Compression;
using System.Xml;

namespace Granary.Core.Updates;

/// <summary>
/// The release notes a package carries: the <c>releaseNotes</c> of the nuspec at its root, which vpk pack fills from the
/// version's CHANGELOG.md section and Velopack's own ZipPackage reads. Read only from a package verified as signed.
/// </summary>
internal static class PackageNotes
{
    // A nuspec holds a few fields and the notes; nothing bigger is read.
    private const long NuspecLimit = 1024 * 1024;

    /// <summary>The notes in Markdown; null without, or longer than <paramref name="limit"/> characters.</summary>
    public static string? Read(Stream package, int limit)
    {
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/', StringComparison.Ordinal)
            && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length > NuspecLimit)
        {
            return null;
        }
        using var nuspec = entry.Open();
        using var reader = XmlReader.Create(nuspec, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = NuspecLimit,
        });
        // package > metadata > releaseNotes, whatever the nuspec's namespace.
        while (reader.Read())
        {
            if (reader is { NodeType: XmlNodeType.Element, Depth: 2, LocalName: "releaseNotes" })
            {
                string notes = reader.ReadElementContentAsString().Trim();
                return notes.Length > 0 && notes.Length <= limit ? notes : null;
            }
        }
        return null;
    }
}
