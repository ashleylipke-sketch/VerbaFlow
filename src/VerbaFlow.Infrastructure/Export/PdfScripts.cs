using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using System.Text;
using PdfSharp.Drawing;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>
/// Makes a MigraDoc document ready for Arabic, Chinese and Japanese before it is turned into a PDF. Paragraphs with only
/// Latin, Cyrillic and similar letters are left exactly as they are. In the others each stretch gets a font that has its
/// characters (found on the computer, see <see cref="PdfMinutesWriter"/>), Arabic letters are joined, and the paragraph
/// is broken into lines here, because MigraDoc only breaks lines at spaces (Chinese has none) and only lays text out
/// left to right (each Arabic line is then put in the order it is seen, and the paragraph is aligned right).
/// </summary>
public static class PdfScripts
{
    public const string ArabicFont = "VerbaFlow Script Arabic", ChineseFont = "VerbaFlow Script zh", JapaneseFont = "VerbaFlow Script ja";

    private sealed record Look(string? Name, double? Size, bool? Bold, bool? Italic, Color? Colour)
    {
        public Look Over(Look inner) => new(inner.Name ?? Name, inner.Size ?? Size, inner.Bold ?? Bold, inner.Italic ?? Italic, inner.Colour ?? Colour);
    }

    /// <param name="Glue">Stays on the line of the piece before it (a full stop split off a Latin word).</param>
    private sealed record Piece(string Text, Look Look, bool Space = false, bool Break = false, bool Glue = false);

    /// <param name="language">The document's language ("ja" sets Chinese characters in a Japanese font).</param>
    public static void Apply(Document doc, string language = "en")
    {
        var normal = doc.Styles[StyleNames.Normal]!.Font;
        var basic = new Look(normal.Name, normal.Size.Point > 0 ? normal.Size.Point : 10, false, false, null);
        var japanese = language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        foreach (Section section in doc.Sections)
        {
            var setup = section.PageSetup;
            var width = setup.PageWidth.Point - setup.LeftMargin.Point - setup.RightMargin.Point;
            if (width <= 0) width = 595 - 2 * 57;
            Walk(section.Elements, width, basic, japanese);
            foreach (var hf in new[] { section.Headers, section.Footers })
                foreach (var part in new[] { hf.Primary, hf.FirstPage, hf.EvenPage })
                    if (part is not null) Walk(part.Elements, width, basic, japanese);
        }
    }

    private static void Walk(DocumentElements elements, double width, Look basic, bool japanese)
    {
        for (var i = 0; i < elements.Count; i++)
            switch (elements[i])
            {
                case Paragraph p: Fix(p, width, basic, japanese); break;
                case Table t:
                    var pad = (t.LeftPadding.Point > 0 ? t.LeftPadding.Point : 3.4) + (t.RightPadding.Point > 0 ? t.RightPadding.Point : 3.4);
                    foreach (Row row in t.Rows)
                        for (var c = 0; c < row.Cells.Count; c++)
                        {
                            var cell = row.Cells[c];
                            var w = Enumerable.Range(c, 1 + cell.MergeRight).Where(k => k < t.Columns.Count).Sum(k => t.Columns[k].Width.Point) - pad;
                            Walk(cell.Elements, w > 0 ? w : width, basic, japanese);
                        }
                    break;
            }
    }

    private static Look LookOf(Font? f) => f is null ? new(null, null, null, null, null) : new(
        string.IsNullOrEmpty(f.Values.Name) ? null : f.Values.Name,
        f.Values.Size is { Point: > 0 } s ? s.Point : null,
        f.Values.Bold, f.Values.Italic,
        f.Values.Color is { } c && !c.IsEmpty ? c : null);

    private static void Fix(Paragraph p, double width, Look basic, bool japanese)
    {
        if (!Scripts.NeedsHelp(TextOf(p.Elements))) return;
        var look = basic.Over(LookOf(p.Format.Values.Font is null ? null : p.Format.Font));
        var elements = p.Elements.Cast<DocumentObject>().ToList();
        // A list point starts with its marker and a tab; those stay as they are, and the text after them is laid out here.
        var keep = elements.Count >= 2 && elements[0] is Text && elements[1] is Character { SymbolName: SymbolName.Tab } ? 2 : 0;
        var pieces = new List<Piece>();
        var simple = true;
        foreach (var e in elements.Skip(keep)) simple &= Flatten(e, look, pieces);
        var rtl = Scripts.RightToLeft(string.Concat(pieces.Select(x => x.Text)));
        if (!simple)
        {
            // Fields, pictures or tabs inside: each stretch of text is fixed where it stands, without new line breaks.
            for (var i = keep; i < elements.Count; i++)
                if (elements[i] is Text t && Scripts.NeedsHelp(t.Content)) Replace(p.Elements, i, t.Content, look, japanese);
                else FixInPlace(elements[i], look, japanese);
            return;
        }
        var fmt = p.Format;
        var available = width - fmt.LeftIndent.Point - fmt.RightIndent.Point
            - Math.Max(0, fmt.FirstLineIndent.Point);
        var lines = Lines(pieces, available * 0.97, japanese);
        while (p.Elements.Count > keep) p.Elements.RemoveObjectAt(keep);
        for (var l = 0; l < lines.Count; l++)
        {
            if (l > 0) p.AddLineBreak();
            Emit(p.Elements, lines[l], rtl, japanese);
        }
        if (rtl && (fmt.Values.Alignment is null || fmt.Alignment == ParagraphAlignment.Left)) fmt.Alignment = ParagraphAlignment.Right;
    }

    private static string TextOf(ParagraphElements elements) => string.Concat(elements.Cast<DocumentObject>().Select(e => e switch
    {
        Text t => t.Content,
        FormattedText f => TextOf(f.Elements),
        _ => "",
    }));

    /// <summary>Text and formatting in reading order; false when something other than text, spaces and line breaks is inside.</summary>
    private static bool Flatten(DocumentObject e, Look look, List<Piece> into)
    {
        switch (e)
        {
            case Text t:
                Split(t.Content, look, into);
                return true;
            case FormattedText f:
                var inner = look.Over(LookOf(f.Font));
                var ok = true;
                foreach (DocumentObject x in f.Elements) ok &= Flatten(x, inner, into);
                return ok;
            case Character { SymbolName: SymbolName.Blank or SymbolName.Em or SymbolName.En or SymbolName.Em4 } c:
                for (var i = 0; i < Math.Max(1, c.Count); i++) into.Add(new(" ", look, Space: true));
                return true;
            case Character { SymbolName: SymbolName.LineBreak }:
                into.Add(new("", look, Break: true));
                return true;
            case Character { Char: var ch } c when ch != '\0':
                Split(new string(ch, Math.Max(1, c.Count)), look, into);
                return true;
            default:
                return false;
        }
    }

    // Chinese and Japanese closing punctuation never starts a line.
    private const string NoLineStart = "、。，．！？：；）」』】〕〉》’”・ー～ぁぃぅぇぉっゃゅょァィゥェォッャュョ%)]},.!?:;";

    /// <summary>Words, single Chinese or Japanese characters, and spaces.</summary>
    private static void Split(string text, Look look, List<Piece> into)
    {
        var word = new System.Text.StringBuilder();
        void Flush()
        {
            if (word.Length == 0) return;
            var w = word.ToString();
            word.Clear();
            // Punctuation after a Latin word is its own piece, so in right-to-left text it ends up on the word's left.
            var end = w.Length;
            while (end > 0 && ".,;:!?".Contains(w[end - 1])) end--;
            if (end > 0 && end < w.Length && w[..end].Any(c => c < 0x0590 && char.IsLetter(c)) && !w.Any(c => Scripts.IsArabic(c)))
            {
                into.Add(new(w[..end], look));
                into.Add(new(w[end..], look, Glue: true));
            }
            else into.Add(new(w, look));
        }
        foreach (var r in text.EnumerateRunes())
        {
            var s = r.ToString();
            if (r.Value == '\n') { Flush(); into.Add(new("", look, Break: true)); }
            else if (Rune.IsWhiteSpace(r)) { Flush(); into.Add(new(" ", look, Space: true)); }
            else if (Scripts.IsCjk(r.Value))
            {
                if (NoLineStart.Contains(s) && word.Length == 0 && into.Count > 0 && !into[^1].Space && !into[^1].Break)
                    into[^1] = into[^1] with { Text = into[^1].Text + s };
                else { Flush(); into.Add(new(s, look)); }
            }
            else if (word.Length == 0 && NoLineStart.Contains(s) && into.Count > 0 && !into[^1].Space && !into[^1].Break && into[^1].Text.Length > 0 && Scripts.IsCjk(into[^1].Text[^1]))
                into[^1] = into[^1] with { Text = into[^1].Text + s }; // a full stop after a Chinese character
            else word.Append(s);
        }
        Flush();
    }

    /// <summary>Arabic joined with the forms the Arabic font on this computer has.</summary>
    private static string Shape(string text) => Scripts.Shape(text, PdfMinutesWriter.ArabicFontHas);

    private static string FontFor(string text, bool japanese)
    {
        var arabic = false; var cjk = false; var kana = false;
        foreach (var r in text.EnumerateRunes())
        {
            arabic |= Scripts.IsArabic(r.Value);
            cjk |= Scripts.IsCjk(r.Value);
            kana |= Scripts.IsKana(r.Value);
        }
        return arabic ? ArabicFont : cjk ? (japanese || kana ? JapaneseFont : ChineseFont) : "";
    }

    private static readonly XGraphics Measure = XGraphics.CreateMeasureContext(new XSize(4000, 4000), XGraphicsUnit.Point, XPageDirection.Downwards);
    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string, double, bool, bool), XFont> FontCache = [];

    private static double WidthOf(string text, Look look, bool japanese)
    {
        if (text.Length == 0) return 0;
        var font = FontFor(text, japanese);
        var name = font.Length > 0 ? font : look.Name ?? PdfMinutesWriter.Font;
        var key = (name, look.Size ?? 10, look.Bold == true, look.Italic == true);
        lock (Gate)
        {
            if (!FontCache.TryGetValue(key, out var x))
                FontCache[key] = x = new XFont(name, key.Item2, (key.Item3 ? XFontStyleEx.Bold : 0) | (key.Item4 ? XFontStyleEx.Italic : 0));
            return Measure.MeasureString(Shape(text), x).Width;
        }
    }

    /// <summary>Fills each line with as many pieces as fit, breaking at spaces and between Chinese or Japanese characters.</summary>
    private static List<List<Piece>> Lines(List<Piece> pieces, double width, bool japanese)
    {
        var lines = new List<List<Piece>>();
        var line = new List<Piece>();
        double used = 0;
        void End() { while (line.Count > 0 && line[^1].Space) line.RemoveAt(line.Count - 1); lines.Add(line); line = []; used = 0; }
        foreach (var piece in pieces)
        {
            if (piece.Break) { End(); continue; }
            if (piece.Space && line.Count == 0) continue; // no space at the start of a line
            var w = WidthOf(piece.Text, piece.Look, japanese);
            if (!piece.Space && !piece.Glue && line.Count > 0 && used + w > width) End();
            line.Add(piece);
            used += w;
        }
        End();
        return lines;
    }

    /// <summary>Adds one line in the order it is seen, with the right font for each stretch.</summary>
    private static void Emit(ParagraphElements into, List<Piece> line, bool rtl, bool japanese)
    {
        var texts = line.Select(x => Shape(x.Text)).ToList();
        var drawn = Scripts.Visual(texts, rtl, out var order);
        FormattedText? current = null;
        (string Font, Look Look)? currentKey = null;
        for (var n = 0; n < order.Count; n++)
        {
            var i = order[n];
            var text = drawn[n];
            // A space takes the font of what it stands next to, so stretches are not cut up by spaces.
            var font = line[i].Space && currentKey is { } k ? k.Font : FontFor(line[i].Text, japanese);
            var key = (font, line[i].Look);
            if (current is null || currentKey != key)
            {
                current = into.AddFormattedText();
                Dress(current, line[i].Look, font);
                currentKey = key;
            }
            current.AddText(text);
        }
    }

    private static void Dress(FormattedText t, Look look, string font)
    {
        if (font.Length > 0) t.Font.Name = font;
        else if (look.Name is not null) t.Font.Name = look.Name;
        if (look.Size is { } s) t.Font.Size = Unit.FromPoint(s);
        if (look.Bold is { } b) t.Font.Bold = b;
        if (look.Italic is { } i) t.Font.Italic = i;
        if (look.Colour is { } c) t.Font.Color = c;
    }

    /// <summary>Where fields or pictures sit in a paragraph, each piece of text is fixed in place (fonts, joining, order).</summary>
    private static void FixInPlace(DocumentObject e, Look look, bool japanese)
    {
        if (e is not FormattedText f) return;
        var inner = look.Over(LookOf(f.Font));
        var list = f.Elements.Cast<DocumentObject>().ToList();
        for (var i = 0; i < list.Count; i++)
            if (list[i] is Text t && Scripts.NeedsHelp(t.Content)) Replace(f.Elements, i, t.Content, inner, japanese);
            else FixInPlace(list[i], inner, japanese);
    }

    private static void Replace(ParagraphElements elements, int at, string text, Look look, bool japanese)
    {
        var pieces = new List<Piece>();
        Split(text, look, pieces);
        var replacement = new FormattedText();
        Emit(replacement.Elements, pieces, Scripts.RightToLeft(text), japanese);
        elements.RemoveObjectAt(at);
        elements.InsertObject(at, replacement);
    }

    /// <summary>Draws one line of text straight onto a PDF page (the signature line), with the same fonts, joining and order.</summary>
    public static void DrawLine(XGraphics gfx, string text, double size, XBrush brush, XRect rect)
    {
        var look = new Look(PdfMinutesWriter.Font, size, false, false, null);
        var pieces = new List<Piece>();
        Split(text, look, pieces);
        var rtl = Scripts.RightToLeft(text);
        var texts = pieces.Select(x => Shape(x.Text)).ToList();
        var visual = Scripts.Visual(texts, rtl, out var order);
        var drawn = order.Select((i, n) => (Text: visual[n], Font: FontFor(pieces[i].Text, false))).ToList();
        XFont FontOf(string f) => new(f.Length > 0 ? f : PdfMinutesWriter.Font, size);
        var widths = drawn.Select(d => gfx.MeasureString(d.Text, FontOf(d.Font)).Width).ToList();
        var x = rtl ? rect.Right - widths.Sum() : rect.Left;
        for (var i = 0; i < drawn.Count; i++)
        {
            gfx.DrawString(drawn[i].Text, FontOf(drawn[i].Font), brush, new XRect(x, rect.Top, widths[i] + 1, rect.Height), XStringFormats.CenterLeft);
            x += widths[i];
        }
    }
}
