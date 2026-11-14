using System.Buffers.Binary;

namespace Granary.Tests.Fakes;

/// <summary>
/// How wide a line of text is in one of the app's fonts, from the font file's own advance widths (its <c>cmap</c> and
/// <c>hmtx</c>), kerning left out: enough to tell whether a card's line fits its width without the app.
/// </summary>
internal sealed class FontWidths
{
    private readonly int _unitsPerEm;
    private readonly ushort[] _advances;
    private readonly Dictionary<int, int> _glyphs = [];

    private FontWidths(byte[] font)
    {
        var tables = new Dictionary<string, int>(StringComparer.Ordinal);
        int count = U16(font, 4);
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + 16 * i;
            tables[System.Text.Encoding.ASCII.GetString(font, entry, 4)] = (int)U32(font, entry + 8);
        }
        _unitsPerEm = U16(font, tables["head"] + 18);
        int metrics = U16(font, tables["hhea"] + 34);
        _advances = new ushort[metrics];
        for (int i = 0; i < metrics; i++)
        {
            _advances[i] = (ushort)U16(font, tables["hmtx"] + 4 * i);
        }
        ReadCharacterMap(font, tables["cmap"]);
    }

    /// <summary>The font file at <paramref name="relativePath"/> in the repository.</summary>
    public static FontWidths Load(string relativePath) => new(File.ReadAllBytes(Path.Combine(RepositoryFiles.Root, relativePath)));

    /// <summary>The width of <paramref name="text"/> at <paramref name="size"/> pixels, letters <paramref name="spacing"/> thousandths of an em apart.</summary>
    public double Width(string text, double size, double spacing = 0) =>
        text.Sum(c => Advance(c) * size / _unitsPerEm + spacing * size / 1000);

    private int Advance(char c)
    {
        int glyph = _glyphs.GetValueOrDefault(c);
        return _advances[Math.Min(glyph, _advances.Length - 1)];
    }

    // The first format 4 subtable: the Basic Multilingual Plane, which every text here is in.
    private void ReadCharacterMap(byte[] font, int cmap)
    {
        int subtables = U16(font, cmap + 2);
        for (int i = 0; i < subtables; i++)
        {
            int at = cmap + (int)U32(font, cmap + 8 + 8 * i);
            if (U16(font, at) != 4)
            {
                continue;
            }
            int segments = U16(font, at + 6) / 2;
            int ends = at + 14;
            int starts = ends + 2 * segments + 2;
            int deltas = starts + 2 * segments;
            int offsets = deltas + 2 * segments;
            for (int s = 0; s < segments; s++)
            {
                int end = U16(font, ends + 2 * s);
                int start = U16(font, starts + 2 * s);
                short delta = (short)U16(font, deltas + 2 * s);
                int offset = U16(font, offsets + 2 * s);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph = offset == 0 ? (c + delta) & 0xFFFF : U16(font, offsets + 2 * s + offset + 2 * (c - start));
                    _glyphs[c] = offset != 0 && glyph != 0 ? (glyph + delta) & 0xFFFF : glyph;
                }
            }
            return;
        }
    }

    private static int U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at, 2));

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));
}
