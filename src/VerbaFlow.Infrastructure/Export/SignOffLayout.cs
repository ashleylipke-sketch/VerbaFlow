using System.Text.RegularExpressions;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>What goes under a letter: the closing, the signature picture (or null), the name and the job title.</summary>
public sealed record LetterSignOff(string? Closing, byte[]? Picture, string Name, string? Title);

/// <summary>
/// Fits a person's sign-off to a dictated letter. When the dictation already ends with a closing ("Yours faithfully",
/// "Kind regards"...), that closing is kept and the short lines said after it (usually the name) give way to the
/// sign-off's name and title, so nothing is printed twice. The signature picture is only put on approved letters.
/// </summary>
public static partial class SignOffLayout
{
    [GeneratedRegex(@"^(yours\s+(sincerely|faithfully|truly)|(kind|kindest|best|warm|warmest|with\s+kind)\s+regards|regards|best\s+wishes|(met\s+)?vriendelike\s+groete|groete|die\s+uwe)\s*[,.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingLine();

    public static bool IsClosing(string paragraph) => ClosingLine().IsMatch(paragraph.Trim());

    public sealed record Fitted(IReadOnlyList<string> Paragraphs, IReadOnlyList<string?>? Markers, LetterSignOff? SignOff, string Note);

    public static Fitted Fit(IReadOnlyList<string> paragraphs, IReadOnlyList<string?>? markers, SignOff? s, byte[]? picture, bool approved)
    {
        if (s is null) return new(paragraphs, markers, null, "none (the author has no sign-off switched on)");
        var usePicture = approved && picture is not null;
        var pictureNote = picture is null ? "no signature picture" : usePicture ? "with signature picture" : "signature picture only once approved";
        for (var c = paragraphs.Count - 1; c >= Math.Max(0, paragraphs.Count - 3); c--)
        {
            if (!IsClosing(paragraphs[c])) continue;
            var after = paragraphs.Skip(c + 1).ToList();
            if (after.Any(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8))
                return new(paragraphs, markers, null, "not added (the letter has more text after its closing)");
            return new(paragraphs.Take(c).ToList(), markers?.Take(c).ToList(),
                new LetterSignOff(paragraphs[c].Trim(), usePicture ? picture : null, s.Name, s.Title),
                $"added under the dictated closing, {pictureNote}");
        }
        return new(paragraphs, markers,
            new LetterSignOff(s.Closing.Length == 0 ? null : s.Closing, usePicture ? picture : null, s.Name, s.Title), $"added, {pictureNote}");
    }
}
