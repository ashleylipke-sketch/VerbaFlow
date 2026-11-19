import Foundation

/// What a spoken command asks the Widget to do (the same as the Widget for Windows' VoiceCommands.cs).
public enum VoiceAction: Equatable, Sendable {
    case dictate, switchTo(String), open(String), key(VoiceKey), goToCell(String)
    case accept, speakMore, fixGrammar, undoFix, cancel
}

/// A key the person can press by voice.
public enum VoiceKey: Equatable, Sendable {
    case nextCell, previousCell, down, up, right, left, enter, tab, escape, backspace, delete, newDocument
}

/// Hands-free use: the commands said to the Widget. Commands start with the wake word "VerbaFlow" ("VerbaFlow, switch
/// to Outlook"); while the words are being checked, "accept", "speak more", "fix grammar", "undo fix" and "cancel" work
/// without it. While dictating, "stop dictating" at the end finishes. English only.
public enum VoiceCommands {
    public static let wakeForms = ["verbaflow", "verba flow", "verb a flow", "verbal flow", "verba flo", "hey verbaflow", "hey verba flow",
                                   "ok verbaflow", "ok verba flow"]
    public static let dictatePhrases: Set<String> = ["dictate", "start dictating", "start dictation", "take dictation", "dictation"]
    public static let switchWords = ["switch to", "go to", "show me", "show"]
    public static let openWords = ["open", "start", "launch"]
    public static let keyPhrases: [String: VoiceKey] = [
        "next cell": .nextCell, "previous cell": .previousCell, "last cell": .previousCell,
        "cell below": .down, "next row": .down, "down": .down, "move down": .down,
        "cell above": .up, "up": .up, "move up": .up,
        "cell to the right": .right, "cell right": .right, "right": .right, "move right": .right,
        "cell to the left": .left, "cell left": .left, "left": .left, "move left": .left,
        "press enter": .enter, "press return": .enter, "press tab": .tab,
        "press escape": .escape, "press backspace": .backspace, "press delete": .delete,
        "new document": .newDocument, "new blank document": .newDocument, "blank document": .newDocument,
        "open a new document": .newDocument, "open new document": .newDocument,
        "open a blank document": .newDocument, "open blank document": .newDocument,
        "new file": .newDocument, "new email": .newDocument, "new spreadsheet": .newDocument,
    ]
    public static let checkPhrases: [String: VoiceAction] = [
        "accept": .accept, "accept it": .accept, "insert it": .accept, "put it in": .accept,
        "speak more": .speakMore, "add more": .speakMore, "carry on": .speakMore, "keep going": .speakMore,
        "fix grammar": .fixGrammar, "fix the grammar": .fixGrammar,
        "undo fix": .undoFix, "undo the fix": .undoFix,
        "cancel": .cancel, "throw it away": .cancel, "discard": .cancel,
    ]
    public static let stopPhrases = ["stop dictating", "stop dictation", "finish dictating", "finish dictation", "end dictation", "stop listening"]

    public static let listeningHint = "Say \u{201C}stop dictating\u{201D} when you have finished"
    public static let checkHint = "Say \u{201C}accept\u{201D}, \u{201C}speak more\u{201D}, \u{201C}fix grammar\u{201D} or \u{201C}cancel\u{201D}"

    /// What was heard, as a command, or nil (see the type's notes).
    public static func parse(_ heard: String, checking: Bool) -> VoiceAction? {
        var t = plain(heard)
        var woke = false
        for w in wakeForms.sorted(by: { $0.count > $1.count }) where t == w || t.hasPrefix(w + " ") {
            t = String(t.dropFirst(w.count)).trimmingCharacters(in: .whitespaces)
            woke = true
            break
        }
        if checking, let c = checkPhrases[t] { return c }
        guard woke, !t.isEmpty, !checking else { return nil }
        if dictatePhrases.contains(t) { return .dictate }
        if let k = keyPhrases[t] { return .key(k) }
        if t.hasPrefix("go to ") {
            var rest = String(t.dropFirst(6))
            if rest.hasPrefix("cell ") { rest = String(rest.dropFirst(5)) }
            if let cell = cellOf(rest) { return .goToCell(cell) }
        }
        for s in switchWords.sorted(by: { $0.count > $1.count }) where t.hasPrefix(s + " ") {
            let name = String(t.dropFirst(s.count + 1)).trimmingCharacters(in: .whitespaces)
            if !name.isEmpty { return .switchTo(name) }
        }
        for o in openWords where t.hasPrefix(o + " ") {
            let name = String(t.dropFirst(o.count + 1)).trimmingCharacters(in: .whitespaces)
            if !name.isEmpty { return .open(name) }
        }
        return nil
    }

    /// The newest command in a running transcript (the Mac's recogniser writes everything it hears): from the last
    /// wake word on, or, while checking, the last words alone.
    public static func latest(in transcript: String, checking: Bool) -> VoiceAction? {
        let t = plain(transcript)
        let words = t.split(separator: " ").map(String.init)
        var best: Int?
        for i in words.indices {
            let rest = words[i...].joined(separator: " ")
            if wakeForms.contains(where: { rest == $0 || rest.hasPrefix($0 + " ") }) { best = i }
        }
        if let b = best, let c = parse(words[b...].joined(separator: " "), checking: checking) { return c }
        if checking {
            for n in [3, 2, 1] where words.count >= n {
                if let c = checkPhrases[words.suffix(n).joined(separator: " ")] { return c }
            }
        }
        return nil
    }

    /// The text without "stop dictating" at the end, or nil when it does not end with it.
    public static func withoutStop(_ text: String) -> String? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        for p in stopPhrases {
            let pattern = "\\b" + p.split(separator: " ").joined(separator: "[\\s,.]+") + "[\\s,.;:!?]*$"
            guard let r = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]),
                  let m = r.firstMatch(in: trimmed, range: NSRange(trimmed.startIndex..., in: trimmed)),
                  let range = Range(m.range, in: trimmed) else { continue }
            var out = String(trimmed[..<range.lowerBound])
            while let c = out.last, c.isWhitespace || c == "," || c == ";" || c == ":" { out.removeLast() }
            return out
        }
        return nil
    }

    private static let letterNames: [String: Character] = [
        "a": "A", "ay": "A", "b": "B", "bee": "B", "be": "B", "c": "C", "see": "C", "sea": "C", "d": "D", "dee": "D",
        "e": "E", "f": "F", "ef": "F", "g": "G", "gee": "G", "h": "H", "aitch": "H", "i": "I", "j": "J", "jay": "J",
        "k": "K", "kay": "K", "l": "L", "el": "L", "m": "M", "em": "M", "n": "N", "en": "N", "o": "O", "oh": "O",
        "p": "P", "pee": "P", "q": "Q", "queue": "Q", "cue": "Q", "r": "R", "are": "R", "s": "S", "ess": "S",
        "t": "T", "tee": "T", "tea": "T", "u": "U", "you": "U", "v": "V", "vee": "V", "w": "W", "x": "X", "ex": "X",
        "y": "Y", "why": "Y", "z": "Z", "zed": "Z", "zee": "Z",
    ]
    private static let units = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
                                "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"]
    private static let tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"]

    /// A cell address from what was heard ("b5", "b 5", "bee five", "aa twelve"), upper case, or nil.
    public static func cellOf(_ heard: String) -> String? {
        let parts = plain(heard).split(separator: " ").map(String.init)
        guard let first = parts.first else { return nil }
        if parts.count == 1, let r = try? NSRegularExpression(pattern: "^([a-z]{1,3})(\\d{1,7})$"),
           let m = r.firstMatch(in: first, range: NSRange(first.startIndex..., in: first)),
           let l = Range(m.range(at: 1), in: first), let d = Range(m.range(at: 2), in: first) {
            return first[l].uppercased() + first[d]
        }
        var letters = ""
        var i = 0
        while i < parts.count, letters.count < 3, let l = letterNames[parts[i]] { letters.append(l); i += 1 }
        guard !letters.isEmpty, i < parts.count, let n = number(Array(parts[i...])), n > 0, n <= 1_048_576 else { return nil }
        return letters + String(n)
    }

    private static func number(_ words: [String]) -> Int? {
        let w = words.filter { $0 != "and" }
        if w.isEmpty { return nil }
        if w.allSatisfy({ $0.allSatisfy(\.isNumber) }) { return Int(w.joined()) }
        var total = 0, current = 0
        for x in w {
            if let u = units.firstIndex(of: x) { current += u }
            else if let t = tens.firstIndex(of: x), t >= 2 { current += t * 10 }
            else if x == "hundred" { current = max(current, 1) * 100 }
            else if x == "thousand" { total += max(current, 1) * 1000; current = 0 }
            else if let n = Int(x) { current += n }
            else { return nil }
        }
        return total + current
    }

    /// Lower case, letters, digits and single spaces only.
    public static func plain(_ text: String) -> String {
        let mapped = text.lowercased().map { $0.isLetter || $0.isNumber ? $0 : " " }
        return String(mapped).split(separator: " ").joined(separator: " ")
    }
}

/// The names a program is called by when switching to it or opening it by voice.
public enum ProgramNames {
    private static let dropped: Set<String> = ["microsoft", "google", "mozilla", "adobe", "the"]

    /// A name as it would be said: no brackets, no company words, lower case.
    public static func clean(_ name: String) -> String {
        var n = name
        if let r = try? NSRegularExpression(pattern: "\\([^)]*\\)|\\[[^\\]]*\\]") {
            n = r.stringByReplacingMatches(in: n, range: NSRange(n.startIndex..., in: n), withTemplate: " ")
        }
        n = VoiceCommands.plain(n)
        let words = n.split(separator: " ").map(String.init).filter { !dropped.contains($0) }
        return words.isEmpty ? n : words.joined(separator: " ")
    }

    /// The item whose name fits what was said best (the same name, the same without spaces, a name starting with it,
    /// or one holding all its words); earlier items win a tie. Nil when nothing fits.
    public static func best<T>(_ spoken: String, _ candidates: [(name: String, item: T)]) -> T? {
        let s = clean(spoken)
        guard !s.isEmpty else { return nil }
        let words = s.split(separator: " ").map(String.init)
        let squash = { (x: String) in x.replacingOccurrences(of: " ", with: "") }
        return candidates.first(where: { $0.name == s })?.item
            ?? candidates.first(where: { squash($0.name) == squash(s) })?.item
            ?? candidates.first(where: { $0.name.hasPrefix(s + " ") })?.item
            ?? candidates.first(where: { c in let n = Set(c.name.split(separator: " ").map(String.init)); return words.allSatisfy(n.contains) })?.item
    }
}
