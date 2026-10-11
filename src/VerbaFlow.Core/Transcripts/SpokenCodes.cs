using System.Text;

namespace VerbaFlow.Core.Transcripts;

/// <summary>
/// Turns a saved-text code, typed ("legal 1,1") or as the speech service wrote it ("legal 1.1.", "Legal one point one",
/// "legal 1, 1"), into one comparable form ("legal 1.1"). Numbers may be digits or English words up to ninety-nine;
/// "point", "dot" or "comma" (and a written . or ,) between two numbers become the dot.
/// </summary>
public static class SpokenCodes
{
    private static readonly Dictionary<string, int> Units = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };
    private static readonly Dictionary<string, int> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };
    private static readonly HashSet<string> Separators = ["point", "dot", "comma", "punt", "komma"];

    private enum Kind { Word, Number, Separator }
    private readonly record struct Atom(Kind Kind, string Text);

    /// <summary>The comparable form of the given words, or "" when there is nothing but punctuation.</summary>
    public static string Key(IEnumerable<string> tokens) => Build(Atoms(tokens));

    public static string Key(string text) => Key(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>True when the token holds a word or number, not only punctuation or a spoken "point"/"comma".</summary>
    public static bool HasContent(string token) => Atoms([token]).Any(a => a.Kind != Kind.Separator);

    private static List<Atom> Atoms(IEnumerable<string> tokens)
    {
        var raw = new List<Atom>();
        foreach (var token in tokens)
        {
            var t = token.ToLowerInvariant();
            for (var i = 0; i < t.Length;)
            {
                if (char.IsLetter(t[i]))
                {
                    var j = i;
                    while (j < t.Length && (char.IsLetter(t[j]) || (t[j] is '\'' or '’' && j + 1 < t.Length && char.IsLetter(t[j + 1])))) j++;
                    raw.Add(new(Kind.Word, t[i..j]));
                    i = j;
                }
                else if (char.IsDigit(t[i]))
                {
                    var j = i;
                    while (j < t.Length && char.IsDigit(t[j])) j++;
                    raw.Add(new(Kind.Number, t[i..j]));
                    i = j;
                }
                else
                {
                    if (t[i] is '.' or ',') raw.Add(new(Kind.Separator, "."));
                    i++;
                }
            }
        }
        // Spoken numbers and separators.
        var atoms = new List<Atom>();
        for (var i = 0; i < raw.Count; i++)
        {
            var a = raw[i];
            if (a.Kind != Kind.Word) { atoms.Add(a); continue; }
            if (Tens.TryGetValue(a.Text, out var ten))
            {
                if (i + 1 < raw.Count && raw[i + 1].Kind == Kind.Word && Units.TryGetValue(raw[i + 1].Text, out var u) && u is > 0 and < 10)
                { atoms.Add(new(Kind.Number, (ten + u).ToString())); i++; }
                else atoms.Add(new(Kind.Number, ten.ToString()));
            }
            else if (Units.TryGetValue(a.Text, out var unit)) atoms.Add(new(Kind.Number, unit.ToString()));
            else if (Separators.Contains(a.Text)) atoms.Add(new(Kind.Separator, "."));
            // "two point oh"
            else if (a.Text is "oh" or "o" && atoms.Count >= 2 && atoms[^1].Kind == Kind.Separator && atoms[^2].Kind == Kind.Number) atoms.Add(new(Kind.Number, "0"));
            else atoms.Add(a);
        }
        return atoms;
    }

    private static string Build(List<Atom> atoms)
    {
        var parts = new List<string>();
        var number = new StringBuilder();
        var pendingSeparator = false;
        foreach (var a in atoms)
        {
            switch (a.Kind)
            {
                case Kind.Number:
                    if (number.Length > 0 && pendingSeparator) number.Append('.').Append(a.Text);
                    else { Flush(); number.Append(a.Text); }
                    pendingSeparator = false;
                    break;
                case Kind.Separator:
                    pendingSeparator = number.Length > 0;
                    break;
                default:
                    Flush(); pendingSeparator = false;
                    parts.Add(a.Text);
                    break;
            }
        }
        Flush();
        return string.Join(' ', parts);

        void Flush()
        {
            if (number.Length > 0) { parts.Add(number.ToString()); number.Clear(); }
        }
    }
}
