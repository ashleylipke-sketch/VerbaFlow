using System.Text;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>
/// Which writing system a character belongs to, and Arabic made ready for a PDF. PDFsharp draws characters one by one,
/// left to right, from one font: it does not join Arabic letters, write right to left, or switch to another font for
/// Chinese or Japanese. So Arabic letters are swapped for their joined forms (initial, middle, final), right-to-left
/// stretches are put in the order they are seen, and the PDF writer gives each stretch a font that has its characters.
/// Word does all this itself, so this is used for PDFs only.
/// </summary>
public static class Scripts
{
    public enum Kind { Neutral, Latin, Cjk, Arabic }

    public static bool IsArabic(int c) => c is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF
        or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF;

    public static bool IsCjk(int c) => c is >= 0x2E80 and <= 0x2FDF or >= 0x3000 and <= 0x30FF or >= 0x3100 and <= 0x31FF
        or >= 0x3200 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F
        or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x3FFFF;

    /// <summary>Japanese kana: a stretch with these is set in a Japanese font even in a Chinese document.</summary>
    public static bool IsKana(int c) => c is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F;

    public static Kind KindOf(int c) =>
        IsArabic(c) ? (char.IsLetter((char)c) ? Kind.Arabic : Kind.Neutral) // Arabic digits read left to right; punctuation goes either way
        : IsCjk(c) ? Kind.Cjk
        : char.IsLetter((char)Math.Min(c, 0xFFFF)) ? Kind.Latin : Kind.Neutral;

    /// <summary>True when the text has anything a plain Latin font cannot show properly.</summary>
    public static bool NeedsHelp(string text)
    {
        foreach (var ch in text) if (ch >= 0x0600 && (IsArabic(ch) || IsCjk(ch) || char.IsSurrogate(ch))) return true;
        return false;
    }

    /// <summary>True when the first letter is Arabic: the paragraph reads right to left.</summary>
    public static bool RightToLeft(string text)
    {
        foreach (var r in text.EnumerateRunes())
            switch (KindOf(r.Value)) { case Kind.Arabic: return true; case Kind.Latin or Kind.Cjk: return false; }
        return false;
    }

    // ---------- joining Arabic letters ----------

    // Isolated, final, initial, medial; a letter with two forms only joins to the letter before it.
    private static readonly Dictionary<int, int[]> Forms = new()
    {
        [0x0621] = [0xFE80], [0x0622] = [0xFE81, 0xFE82], [0x0623] = [0xFE83, 0xFE84], [0x0624] = [0xFE85, 0xFE86],
        [0x0625] = [0xFE87, 0xFE88], [0x0626] = [0xFE89, 0xFE8A, 0xFE8B, 0xFE8C], [0x0627] = [0xFE8D, 0xFE8E],
        [0x0628] = [0xFE8F, 0xFE90, 0xFE91, 0xFE92], [0x0629] = [0xFE93, 0xFE94], [0x062A] = [0xFE95, 0xFE96, 0xFE97, 0xFE98],
        [0x062B] = [0xFE99, 0xFE9A, 0xFE9B, 0xFE9C], [0x062C] = [0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0], [0x062D] = [0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4],
        [0x062E] = [0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8], [0x062F] = [0xFEA9, 0xFEAA], [0x0630] = [0xFEAB, 0xFEAC], [0x0631] = [0xFEAD, 0xFEAE],
        [0x0632] = [0xFEAF, 0xFEB0], [0x0633] = [0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4], [0x0634] = [0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8],
        [0x0635] = [0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC], [0x0636] = [0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0], [0x0637] = [0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4],
        [0x0638] = [0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8], [0x0639] = [0xFEC9, 0xFECA, 0xFECB, 0xFECC], [0x063A] = [0xFECD, 0xFECE, 0xFECF, 0xFED0],
        [0x0640] = [0x0640, 0x0640, 0x0640, 0x0640], [0x0641] = [0xFED1, 0xFED2, 0xFED3, 0xFED4], [0x0642] = [0xFED5, 0xFED6, 0xFED7, 0xFED8],
        [0x0643] = [0xFED9, 0xFEDA, 0xFEDB, 0xFEDC], [0x0644] = [0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0], [0x0645] = [0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4],
        [0x0646] = [0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8], [0x0647] = [0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC], [0x0648] = [0xFEED, 0xFEEE],
        [0x0649] = [0xFEEF, 0xFEF0], [0x064A] = [0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4],
        [0x067E] = [0xFB56, 0xFB57, 0xFB58, 0xFB59], [0x0686] = [0xFB7A, 0xFB7B, 0xFB7C, 0xFB7D], [0x0698] = [0xFB8A, 0xFB8B],
        [0x06A9] = [0xFB8E, 0xFB8F, 0xFB90, 0xFB91], [0x06AF] = [0xFB92, 0xFB93, 0xFB94, 0xFB95], [0x06CC] = [0xFBFC, 0xFBFD, 0xFBFE, 0xFBFF],
    };

    // Lam followed by an alef is written as one sign: isolated, final.
    private static readonly Dictionary<int, int[]> LamAlef = new()
    {
        [0x0622] = [0xFEF5, 0xFEF6], [0x0623] = [0xFEF7, 0xFEF8], [0x0625] = [0xFEF9, 0xFEFA], [0x0627] = [0xFEFB, 0xFEFC],
    };

    /// <summary>Vowel marks and the like: they sit on a letter and do not break the joining.</summary>
    private static bool Mark(int c) => c is >= 0x064B and <= 0x065F or 0x0670 or >= 0x06D6 and <= 0x06ED;

    private static bool JoinsBoth(int c) => Forms.TryGetValue(c, out var f) && f.Length == 4;
    private static bool Joins(int c) => Forms.TryGetValue(c, out var f) && f.Length >= 2;

    /// <summary>The Arabic letters of the text in their joined forms (still in reading order). <paramref name="has"/> says
    /// whether the font has a form; a missing one is left as the plain letter, which looks unjoined but readable.</summary>
    public static string Shape(string text, Func<int, bool>? has = null)
    {
        has ??= _ => true;
        if (!text.Any(c => c is >= '؀' and <= 'ۿ')) return text;
        var cs = text.Select(c => (int)c).ToArray();
        int Prev(int i) { for (var j = i - 1; j >= 0; j--) if (!Mark(cs[j])) return cs[j]; return 0; }
        int Next(int i) { for (var j = i + 1; j < cs.Length; j++) if (!Mark(cs[j])) return cs[j]; return 0; }
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < cs.Length; i++)
        {
            var c = cs[i];
            var before = JoinsBoth(Prev(i));
            if (c == 0x0644 && i + 1 < cs.Length && LamAlef.TryGetValue(cs[i + 1], out var lig))
            {
                if (has(lig[before ? 1 : 0])) { sb.Append((char)lig[before ? 1 : 0]); i++; continue; }
            }
            if (!Forms.TryGetValue(c, out var forms)) { sb.Append((char)c); continue; }
            var after = forms.Length == 4 && Joins(Next(i));
            var form = (before, after) switch { (true, true) => 3, (true, false) => 1, (false, true) => 2, _ => 0 };
            var chosen = forms[Math.Min(form, forms.Length - 1)];
            sb.Append((char)(has(chosen) ? chosen : c));
        }
        return sb.ToString();
    }

    private static readonly Dictionary<char, char> Mirror = new()
    {
        ['('] = ')', [')'] = '(', ['['] = ']', [']'] = '[', ['{'] = '}', ['}'] = '{', ['<'] = '>', ['>'] = '<', ['«'] = '»', ['»'] = '«',
    };

    /// <summary>A number inside right-to-left text: its digits stay in order, the punctuation around it changes sides.</summary>
    public static string NumberInRightToLeft(string text)
    {
        var start = 0; var end = text.Length;
        while (start < end && !char.IsLetterOrDigit(text[start])) start++;
        while (end > start && !char.IsLetterOrDigit(text[end - 1])) end--;
        return Backwards(text[end..]) + text[start..end] + Backwards(text[..start]);
    }

    /// <summary>A right-to-left stretch written backwards (brackets turned round), so drawn left to right it reads correctly.</summary>
    public static string Backwards(string text)
    {
        var sb = new StringBuilder(text.Length);
        // Keep surrogate pairs and a letter's marks together.
        var pieces = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var list = new List<string>();
        while (pieces.MoveNext()) list.Add((string)pieces.Current);
        for (var i = list.Count - 1; i >= 0; i--)
            sb.Append(list[i].Length == 1 && Mirror.TryGetValue(list[i][0], out var m) ? m.ToString() : list[i]);
        return sb.ToString();
    }

    /// <summary>
    /// The order in which pieces of one line are drawn left to right. Each piece is a word, a single Chinese or Japanese
    /// character, or a space. Neighbouring pieces going the same way stay together; in a right-to-left line the groups
    /// are drawn from the right, and Arabic words are written backwards. Numbers and Latin words keep their own order.
    /// </summary>
    /// <summary>Each piece as drawn: Arabic written backwards, numbers in a right-to-left stretch with their punctuation turned round.</summary>
    public static List<string> Visual(IReadOnlyList<string> pieces, bool rightToLeft, out List<int> order)
    {
        order = VisualOrder(pieces, rightToLeft, out var backwards, out var rtlRun);
        return order.Select(i => backwards[i] ? Backwards(pieces[i]) : rtlRun[i] ? NumberInRightToLeft(pieces[i]) : pieces[i]).ToList();
    }

    public static List<int> VisualOrder(IReadOnlyList<string> pieces, bool rightToLeft, out bool[] backwards) =>
        VisualOrder(pieces, rightToLeft, out backwards, out _);

    private static List<int> VisualOrder(IReadOnlyList<string> pieces, bool rightToLeft, out bool[] backwards, out bool[] rtlRun)
    {
        var dir = pieces.Select(Direction).ToArray(); // 1 = right to left, -1 = left to right, 0 = either
        for (var i = 0; i < dir.Length; i++)
        {
            if (dir[i] != 0) continue;
            var before = Enumerable.Range(0, i).Reverse().Select(j => dir[j]).FirstOrDefault(d => d != 0);
            var after = dir.Skip(i + 1).FirstOrDefault(d => d != 0);
            dir[i] = before != 0 && before == after ? before : rightToLeft ? 1 : -1;
        }
        // Group runs going the same way.
        var runs = new List<(int Start, int End, int Dir)>();
        for (var i = 0; i < dir.Length;)
        {
            var j = i;
            while (j + 1 < dir.Length && dir[j + 1] == dir[i]) j++;
            runs.Add((i, j, dir[i]));
            i = j + 1;
        }
        if (rightToLeft) runs.Reverse();
        var order = new List<int>();
        backwards = new bool[pieces.Count];
        rtlRun = new bool[pieces.Count];
        foreach (var (start, end, d) in runs)
            if (d == 1)
                for (var k = end; k >= start; k--) { order.Add(k); rtlRun[k] = true; backwards[k] = !pieces[k].Any(char.IsDigit); } // a number keeps its digits in order
            else
                for (var k = start; k <= end; k++) order.Add(k);
        return order;
    }

    private static int Direction(string piece)
    {
        foreach (var r in piece.EnumerateRunes())
            switch (KindOf(r.Value)) { case Kind.Arabic: return 1; case Kind.Latin or Kind.Cjk: return -1; }
        return 0;
    }
}

/// <summary>
/// Word does its own joining and right-to-left layout, but only for paragraphs marked as right to left. This marks every
/// paragraph whose first letter is Arabic (and its runs), so it reads from the right in Word.
/// </summary>
public static class DocxScripts
{
    public static DocumentFormat.OpenXml.OpenXmlElement Mark(DocumentFormat.OpenXml.OpenXmlElement e)
    {
        var paragraphs = e is DocumentFormat.OpenXml.Wordprocessing.Paragraph p
            ? [p] : e.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().ToList();
        foreach (var para in paragraphs)
        {
            if (!Scripts.RightToLeft(para.InnerText)) continue;
            var props = para.ParagraphProperties ??= new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties();
            props.BiDi = new DocumentFormat.OpenXml.Wordprocessing.BiDi();
            // In a right-to-left paragraph Word reads "left" as the start of the line, which is the right-hand side.
            if (props.Justification?.Val?.Value == DocumentFormat.OpenXml.Wordprocessing.JustificationValues.Right)
                props.Justification.Val = DocumentFormat.OpenXml.Wordprocessing.JustificationValues.Left;
            foreach (var run in para.Descendants<DocumentFormat.OpenXml.Wordprocessing.Run>())
                (run.RunProperties ??= new DocumentFormat.OpenXml.Wordprocessing.RunProperties()).RightToLeftText = new DocumentFormat.OpenXml.Wordprocessing.RightToLeftText();
        }
        return e;
    }
}
