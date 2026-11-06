using System.Buffers.Binary;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>
/// Small helpers for font files, so PDFs can use the Chinese, Japanese and Arabic fonts already on the computer:
/// takes one font out of a font collection (.ttc, which PDFsharp cannot read), and checks which characters a font has.
/// Only TrueType outlines are used; fonts with CFF outlines (most .otf and the Noto CJK collections) are skipped.
/// </summary>
internal static class FontFiles
{
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    /// <summary>The fonts in a file: one for a .ttf, several for a .ttc. Each comes back as a stand-alone .ttf.</summary>
    public static IEnumerable<byte[]> Fonts(byte[] file)
    {
        if (file.Length < 12) yield break;
        if (U32(file, 0) != 0x74746366) { yield return file; yield break; } // not "ttcf"
        var count = (int)Math.Min(U32(file, 8), 64);
        for (var i = 0; i < count; i++)
        {
            byte[]? font = null;
            try { font = Extract(file, (int)U32(file, 12 + 4 * i)); } catch (ArgumentOutOfRangeException) { }
            if (font is not null) yield return font;
        }
    }

    /// <summary>Copies the tables of the font whose table directory starts at <paramref name="at"/> into a new file.</summary>
    private static byte[] Extract(byte[] file, int at)
    {
        var n = U16(file, at + 4);
        var tables = Enumerable.Range(0, n).Select(i => at + 12 + 16 * i)
            .Select(r => (Tag: U32(file, r), Sum: U32(file, r + 4), Offset: (int)U32(file, r + 8), Length: (int)U32(file, r + 12))).ToList();
        var head = 12 + 16 * n;
        var size = head + tables.Sum(t => (t.Length + 3) & ~3);
        var output = new byte[size];
        Array.Copy(file, at, output, 0, 12);
        var pos = head;
        for (var i = 0; i < n; i++)
        {
            var t = tables[i];
            var r = 12 + 16 * i;
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(r), t.Tag);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(r + 4), t.Sum);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(r + 8), (uint)pos);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(r + 12), (uint)t.Length);
            Array.Copy(file, t.Offset, output, pos, t.Length);
            pos += (t.Length + 3) & ~3;
        }
        return output;
    }

    private static int? Table(byte[] font, string tag)
    {
        var want = BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(tag));
        var n = U16(font, 4);
        for (var i = 0; i < n; i++)
            if (U32(font, 12 + 16 * i) == want) return (int)U32(font, 12 + 16 * i + 8);
        return null;
    }

    /// <summary>True when the font has TrueType outlines and a glyph for every one of the characters.</summary>
    public static bool Covers(byte[] font, IEnumerable<int> characters)
    {
        try
        {
            if (Table(font, "glyf") is null || Table(font, "CFF ") is not null) return false;
            if (Table(font, "cmap") is not { } cmap) return false;
            // The Unicode BMP map (platform 3 encoding 1, or platform 0), format 4.
            int? sub = null;
            var count = U16(font, cmap + 2);
            for (var i = 0; i < count; i++)
            {
                var r = cmap + 4 + 8 * i;
                var (platform, encoding) = (U16(font, r), U16(font, r + 2));
                if ((platform == 3 && encoding == 1) || platform == 0)
                {
                    var off = cmap + (int)U32(font, r + 4);
                    if (U16(font, off) == 4) { sub = off; break; }
                }
            }
            return sub is { } s && characters.All(c => Glyph4(font, s, c) != 0);
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static int Glyph4(byte[] f, int s, int c)
    {
        if (c > 0xFFFF) return 0;
        var segs = U16(f, s + 6) / 2;
        var ends = s + 14;
        var starts = ends + 2 * segs + 2;
        var deltas = starts + 2 * segs;
        var ranges = deltas + 2 * segs;
        for (var i = 0; i < segs; i++)
        {
            if (U16(f, ends + 2 * i) < c) continue;
            var start = U16(f, starts + 2 * i);
            if (start > c) return 0;
            var delta = (short)U16(f, deltas + 2 * i);
            var range = U16(f, ranges + 2 * i);
            if (range == 0) return (c + delta) & 0xFFFF;
            var g = U16(f, ranges + 2 * i + range + 2 * (c - start));
            return g == 0 ? 0 : (g + delta) & 0xFFFF;
        }
        return 0;
    }

    /// <summary>
    /// Some fonts build a letter from a part that is itself built from parts (DejaVu Sans does this for Arabic qaf).
    /// PDFsharp only copies one level of parts into the PDF, so such letters lose their body. This rewrites those letters
    /// to point at the final parts directly. Fonts without such letters come back unchanged.
    /// </summary>
    public static byte[] FlattenNestedParts(byte[] font)
    {
        try
        {
            if (Table(font, "glyf") is null || Table(font, "loca") is null || Table(font, "head") is not { } head || Table(font, "maxp") is not { } maxp)
                return font;
            var glyfAt = Table(font, "glyf")!.Value; var locaAt = Table(font, "loca")!.Value;
            var longLoca = U16(font, head + 50) == 1;
            var count = U16(font, maxp + 4);
            var glyphs = new byte[count][];
            for (var g = 0; g < count; g++)
            {
                int start = longLoca ? (int)U32(font, locaAt + 4 * g) : 2 * U16(font, locaAt + 2 * g);
                int end = longLoca ? (int)U32(font, locaAt + 4 * g + 4) : 2 * U16(font, locaAt + 2 * g + 2);
                glyphs[g] = font.AsSpan(glyfAt + start, Math.Max(0, end - start)).ToArray();
            }
            bool IsComposite(int g) => g < count && glyphs[g].Length >= 10 && (short)U16(glyphs[g], 0) < 0;

            var changed = false;
            var output = new byte[count][];
            for (var g = 0; g < count; g++)
            {
                output[g] = glyphs[g];
                if (!IsComposite(g)) continue;
                var parts = Parts(glyphs[g]);
                if (parts is null || !parts.Any(p => IsComposite(p.Glyph))) continue;
                var flat = new List<(int Glyph, int Dx, int Dy, ushort Flags)>();
                var ok = true;
                void Add(int glyph, int dx, int dy, ushort flags, int depth)
                {
                    if (!IsComposite(glyph)) { flat.Add((glyph, dx, dy, flags)); return; }
                    var inner = depth < 8 ? Parts(glyphs[glyph]) : null;
                    if (inner is null) { ok = false; return; }
                    foreach (var p in inner) Add(p.Glyph, dx + p.Dx, dy + p.Dy, flags, depth + 1);
                }
                foreach (var p in parts) Add(p.Glyph, p.Dx, p.Dy, p.Flags, 0);
                if (!ok) continue;
                output[g] = Composite(glyphs[g].AsSpan(0, 10), flat);
                changed = true;
            }
            if (!changed) return font;

            // Rebuild glyf and a long loca, and say so in head.
            var glyf = new MemoryStream();
            var loca = new byte[4 * (count + 1)];
            for (var g = 0; g < count; g++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * g), (uint)glyf.Length);
                glyf.Write(output[g]);
                while (glyf.Length % 4 != 0) glyf.WriteByte(0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * count), (uint)glyf.Length);
            var tables = new Dictionary<uint, byte[]>();
            var n = U16(font, 4);
            for (var i = 0; i < n; i++)
            {
                var r = 12 + 16 * i;
                tables[U32(font, r)] = font.AsSpan((int)U32(font, r + 8), (int)U32(font, r + 12)).ToArray();
            }
            uint Tag(string t) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(t));
            tables[Tag("glyf")] = glyf.ToArray();
            tables[Tag("loca")] = loca;
            var headTable = tables[Tag("head")];
            BinaryPrimitives.WriteUInt16BigEndian(headTable.AsSpan(50), 1);
            return Write(U32(font, 0), tables);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException)
        {
            return font; // something unexpected in the file: use it as it is
        }
    }

    private const ushort Words = 0x1, XyValues = 0x2, RoundXy = 0x4, Scale = 0x8, More = 0x20, XyScale = 0x40, TwoByTwo = 0x80, MyMetrics = 0x200;

    /// <summary>The parts of a built-up letter, when each is only moved (not scaled or matched by points); otherwise null.</summary>
    private static List<(int Glyph, int Dx, int Dy, ushort Flags)>? Parts(byte[] g)
    {
        var parts = new List<(int, int, int, ushort)>();
        var at = 10;
        while (true)
        {
            var flags = U16(g, at); var glyph = U16(g, at + 2); at += 4;
            if ((flags & XyValues) == 0 || (flags & (Scale | XyScale | TwoByTwo)) != 0) return null;
            int dx, dy;
            if ((flags & Words) != 0) { dx = (short)U16(g, at); dy = (short)U16(g, at + 2); at += 4; }
            else { dx = (sbyte)g[at]; dy = (sbyte)g[at + 1]; at += 2; }
            parts.Add((glyph, dx, dy, flags));
            if ((flags & More) == 0) return parts;
        }
    }

    private static byte[] Composite(ReadOnlySpan<byte> header, List<(int Glyph, int Dx, int Dy, ushort Flags)> parts)
    {
        var ms = new MemoryStream();
        ms.Write(header);
        var b = new byte[8];
        for (var i = 0; i < parts.Count; i++)
        {
            var (glyph, dx, dy, old) = parts[i];
            var flags = (ushort)(Words | XyValues | (old & RoundXy) | (i < parts.Count - 1 ? More : 0) | (i == 0 ? old & MyMetrics : 0));
            BinaryPrimitives.WriteUInt16BigEndian(b, flags);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)glyph);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(4), (short)dx);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6), (short)dy);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    private static byte[] Write(uint version, Dictionary<uint, byte[]> tables)
    {
        var n = tables.Count;
        var head = 12 + 16 * n;
        var output = new byte[head + tables.Values.Sum(t => (t.Length + 3) & ~3)];
        BinaryPrimitives.WriteUInt32BigEndian(output, version);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)n);
        var pow = 1; var log = 0;
        while (pow * 2 <= n) { pow *= 2; log++; }
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), (ushort)(pow * 16));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), (ushort)(n * 16 - pow * 16));
        var pos = head; var r = 12;
        foreach (var (tag, data) in tables.OrderBy(t => t.Key))
        {
            uint sum = 0;
            for (var i = 0; i < data.Length; i += 4)
            {
                uint v = 0;
                for (var k = 0; k < 4; k++) v = (v << 8) | (i + k < data.Length ? data[i + k] : 0u);
                sum += v;
            }
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(r), tag);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(r + 4), sum);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(r + 8), (uint)pos);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(r + 12), (uint)data.Length);
            data.CopyTo(output, pos);
            pos += (data.Length + 3) & ~3; r += 16;
        }
        return output;
    }
}
