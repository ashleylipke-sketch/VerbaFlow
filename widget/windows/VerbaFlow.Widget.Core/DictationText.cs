using System.Text;
using System.Text.RegularExpressions;

namespace VerbaFlow.Widget;

/// <summary>One passage of the live text as VerbaFlow sends it, with spoken commands already applied.</summary>
public sealed record LivePara(string? Text, bool? NewParagraph, string? List);

/// <summary>Where a new paragraph leaves an empty line in the program the text goes into.</summary>
public enum ParagraphGap { Automatic, EmptyLine, NewLineOnly }

/// <summary>
/// How the heard words become text: paragraphs and list markers as the VerbaFlow item page shows them, the words already
/// in the panel, and a space where a person typing would leave one. The same rules as the browser Widget (content.js).
/// </summary>
public static partial class DictationText
{
    /// <summary>The heard text as paragraphs, with "• " and "1. " list markers.</summary>
    public static List<string> Paragraphs(IEnumerable<LivePara> segments)
    {
        var paras = new List<(string Text, string? List)>();
        var first = true;
        foreach (var s in segments)
        {
            var pieces = (s.Text ?? "").Replace("\r\n", "\n").Split('\n');
            for (var k = 0; k < pieces.Length; k++)
            {
                var p = pieces[k].Trim();
                if (first || k > 0 || s.NewParagraph == true) paras.Add((p, k == 0 ? s.List : null));
                else paras[^1] = ((paras[^1].Text + " " + p).Trim(), paras[^1].List);
                first = false;
            }
        }
        var n = 0;
        var result = new List<string>();
        foreach (var (text, list) in paras)
        {
            if (text.Length == 0) continue;
            n = list == "number" ? n + 1 : 0;
            result.Add(list == "bullet" ? "• " + text : list == "number" ? $"{n}. {text}" : text);
        }
        return result;
    }

    /// <summary>The heard text, paragraphs separated by an empty line. "new paragraph" said first breaks after the words before it.</summary>
    public static string Heard(IEnumerable<LivePara> segments, bool startsWithBreak)
    {
        var h = string.Join("\n\n", Paragraphs(segments));
        return h.Length > 0 && startsWithBreak ? "\n\n" + h : h;
    }

    /// <summary>Whether heard text joins straight on to what is before it (it starts with a space, a break or punctuation).</summary>
    public static bool JoinsOn(string heard) => heard.Length > 0 && (char.IsWhiteSpace(heard[0]) || ".,;:!?)]\"”’".Contains(heard[0]));

    /// <summary>The words already in the panel (from before "Speak more") followed by the newly heard ones.</summary>
    public static string Join(string before, string heard)
    {
        var p = before.TrimEnd();
        if (heard.Length == 0) return p.Trim();
        if (p.Trim().Length == 0) return heard;
        return JoinsOn(heard) || before.EndsWith('\n') ? p + heard : p + " " + heard;
    }

    /// <summary>A space between what was already in the box and the new words, as a person typing would leave one.</summary>
    public static string Spaced(string? before, string text) =>
        !string.IsNullOrEmpty(before) && !char.IsWhiteSpace(before[^1]) && !JoinsOn(text) ? " " + text : text;

    public static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// The text as it is pasted: Windows line ends, and paragraphs separated by an empty line or by a single line end.
    /// Word puts its own space after a paragraph, so an empty line there would look like two; most email and chat
    /// boxes put none, so an empty line is left there, as people do when typing.
    /// </summary>
    public static string ForPasting(string text, bool emptyLineBetweenParagraphs)
    {
        var t = text.Replace("\r\n", "\n").TrimEnd();
        var sb = new StringBuilder();
        // "new paragraph" said first: the words start a new paragraph after what is already in the box.
        if (t.StartsWith('\n')) sb.Append(emptyLineBetweenParagraphs ? "\r\n\r\n" : "\r\n");
        var paras = ParagraphBreak().Split(t.Trim('\n'));
        for (var i = 0; i < paras.Length; i++)
        {
            if (i > 0) sb.Append(emptyLineBetweenParagraphs ? "\r\n\r\n" : "\r\n");
            sb.Append(paras[i].Replace("\n", "\r\n"));
        }
        return sb.ToString();
    }

    /// <summary>Programs that space their own paragraphs (Word), where Automatic leaves no empty line.</summary>
    public static bool EmptyLineFor(ParagraphGap gap, string program) => gap switch
    {
        ParagraphGap.EmptyLine => true,
        ParagraphGap.NewLineOnly => false,
        _ => !program.Equals("winword.exe", StringComparison.OrdinalIgnoreCase),
    };

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex ParagraphBreak();
}
