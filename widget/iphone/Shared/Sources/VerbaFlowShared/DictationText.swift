import Foundation

/// One passage of the live text as VerbaFlow sends it, with spoken commands already applied.
public struct LivePara: Equatable, Sendable {
    public var text: String?
    public var newParagraph: Bool?
    public var list: String?
    public init(text: String?, newParagraph: Bool? = nil, list: String? = nil) {
        self.text = text; self.newParagraph = newParagraph; self.list = list
    }
}

/// How the heard words become text: paragraphs and list markers as the VerbaFlow item page shows them, the words already
/// heard before "Speak more", and a space where a person typing would leave one. The same rules as the browser Widget
/// (content.js) and the Widget for Windows (DictationText.cs).
public enum DictationText {
    /// The heard text as paragraphs, with "• " and "1. " list markers.
    public static func paragraphs(_ segments: [LivePara]) -> [String] {
        var paras: [(text: String, list: String?)] = []
        var first = true
        for s in segments {
            let pieces = (s.text ?? "").replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
            for (k, raw) in pieces.enumerated() {
                let p = raw.trimmingCharacters(in: .whitespaces)
                if first || k > 0 || s.newParagraph == true { paras.append((p, k == 0 ? s.list : nil)) }
                else { paras[paras.count - 1].text = (paras[paras.count - 1].text + " " + p).trimmingCharacters(in: .whitespaces) }
                first = false
            }
        }
        var n = 0
        var result: [String] = []
        for (text, list) in paras where !text.isEmpty {
            n = list == "number" ? n + 1 : 0
            result.append(list == "bullet" ? "• " + text : list == "number" ? "\(n). \(text)" : text)
        }
        return result
    }

    /// The heard text, paragraphs separated by an empty line. "new paragraph" said first breaks after the words before it.
    public static func heard(_ segments: [LivePara], startsWithBreak: Bool) -> String {
        let h = paragraphs(segments).joined(separator: "\n\n")
        return !h.isEmpty && startsWithBreak ? "\n\n" + h : h
    }

    /// Whether heard text joins straight on to what is before it (it starts with a space, a break or punctuation).
    public static func joinsOn(_ heard: String) -> Bool {
        guard let c = heard.first else { return false }
        return c.isWhitespace || ".,;:!?)]\"”’".contains(c)
    }

    /// The words already heard (from before "Speak more") followed by the newly heard ones.
    public static func join(_ before: String, _ heard: String) -> String {
        let p = before.trimmingTrailingWhitespace()
        if heard.isEmpty { return p.trimmingCharacters(in: .whitespacesAndNewlines) }
        if p.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return heard }
        return joinsOn(heard) || before.hasSuffix("\n") ? p + heard : p + " " + heard
    }

    /// A space between what is already in the box and the new words, as a person typing would leave one.
    public static func spaced(before: String?, _ text: String) -> String {
        guard let last = before?.last, !last.isWhitespace, !joinsOn(text) else { return text }
        return " " + text
    }

    public static func words(_ text: String) -> Int {
        text.split(whereSeparator: { $0.isWhitespace }).count
    }
}

extension String {
    func trimmingTrailingWhitespace() -> String {
        var s = Substring(self)
        while let c = s.last, c.isWhitespace { s = s.dropLast() }
        return String(s)
    }
}
