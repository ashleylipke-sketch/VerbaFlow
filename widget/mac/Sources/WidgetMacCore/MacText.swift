import Foundation

/// How paragraphs are separated when the words are put into a program.
public enum ParagraphGap: String, CaseIterable, Codable, Sendable {
    /// An empty line between paragraphs, except in programs that space paragraphs themselves (Word, Pages).
    case automatic
    case emptyLine
    case newLineOnly
}

/// The Mac's rules for the Widget's text, the same as the Widget for Windows' (DictationText.cs).
public enum MacText {
    /// Programs that put space after each paragraph themselves, so an empty line would make the gap twice as big.
    public static let spacedParagraphs: Set<String> = ["com.microsoft.word", "com.apple.iwork.pages"]

    /// Browsers where the browser Widget (the extension) has the same shortcut and knows the website.
    public static let browsers: Set<String> = ["com.google.chrome", "com.microsoft.edgemac"]

    /// The text to paste: paragraphs (separated by an empty line in the heard text) joined as the program wants them.
    public static func forPasting(_ text: String, emptyLineBetweenParagraphs: Bool) -> String {
        var t = text.replacingOccurrences(of: "\r\n", with: "\n")
        while let c = t.last, c.isWhitespace { t.removeLast() }
        let gap = emptyLineBetweenParagraphs ? "\n\n" : "\n"
        var out = ""
        // "new paragraph" said first: the words start a new paragraph after what is already in the box.
        if t.hasPrefix("\n") { out += gap }
        let inner = t.trimmingCharacters(in: CharacterSet(charactersIn: "\n"))
        let paras = inner.components(separatedBy: "\n\n").map { $0.trimmingCharacters(in: CharacterSet(charactersIn: "\n")) }.filter { !$0.isEmpty }
        out += paras.joined(separator: gap)
        return out
    }

    public static func emptyLine(_ gap: ParagraphGap, program: String) -> Bool {
        switch gap {
        case .emptyLine: return true
        case .newLineOnly: return false
        case .automatic: return !spacedParagraphs.contains(program.lowercased())
        }
    }

    /// What VerbaFlow's history and an administrator's blocks see for a Mac program: its bundle ID in lower case
    /// (com.microsoft.outlook), which has the same form as a website address.
    public static func site(bundleId: String?) -> String {
        let s = (bundleId ?? "").lowercased().filter { $0.isLetter || $0.isNumber || $0 == "." || $0 == "-" }
        return s.isEmpty ? "unknown.mac" : s
    }

    /// VerbaFlow's address as typed, or nil when it is not one (http or https only).
    public static func cleanServer(_ typed: String) -> URL? {
        var t = typed.trimmingCharacters(in: .whitespaces)
        while t.hasSuffix("/") { t.removeLast() }
        if !t.isEmpty && !t.contains("://") { t = "https://" + t }
        guard let u = URL(string: t), let scheme = u.scheme?.lowercased(), ["http", "https"].contains(scheme),
              let host = u.host, !host.isEmpty else { return nil }
        return u
    }
}

/// The Widget's settings, kept in this Mac user's preferences.
public struct WidgetSettings: Codable, Equatable, Sendable {
    public static let defaultServer = "http://localhost:5044"
    public var server: String = defaultServer
    public var languages: String = "en-GB"
    public var gap: ParagraphGap = .automatic
    /// In Chrome and Edge, Option+Shift+D is left to the browser Widget.
    public var leaveBrowsersToExtension = true
    public var testUser = "alice"

    public init() {}

    private static let key = "settings"

    public static func load(_ defaults: UserDefaults = .standard) -> WidgetSettings {
        guard let d = defaults.data(forKey: key), let s = try? JSONDecoder().decode(WidgetSettings.self, from: d) else { return WidgetSettings() }
        return s
    }

    public func save(_ defaults: UserDefaults = .standard) {
        if let d = try? JSONEncoder().encode(self) { defaults.set(d, forKey: Self.key) }
    }

    /// Whether this Mac has a saved settings file yet (the first start opens the settings).
    public static func exists(_ defaults: UserDefaults = .standard) -> Bool { defaults.data(forKey: key) != nil }
}
