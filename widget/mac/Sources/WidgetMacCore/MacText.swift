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

/// How the accepted words are put into a program.
public enum InsertMethod: String, CaseIterable, Codable, Sendable {
    /// Paste with ⌘V: the usual way, understood by nearly every program.
    case paste
    /// Type the letters one by one, as a keyboard would: works where pasting does not (remote desktops, virtual machines).
    case type
    /// Only copy the words; the person pastes them where they want.
    case copy

    public var describe: String {
        switch self {
        case .paste: return "Paste (⌘V)"
        case .type: return "Type the letters"
        case .copy: return "Only copy (I paste myself)"
        }
    }

    /// Remote desktops and virtual machines: a paste would use this Mac's clipboard, which the other computer may not
    /// see, so the letters are typed instead.
    public static let typePrograms: Set<String> = [
        "com.microsoft.rdc.macos", "com.microsoft.rdc.mac", "com.citrix.receiver.icaviewer.mac", "com.vmware.fusion",
        "com.parallels.desktop.console", "com.utmapp.utm", "com.realvnc.vncviewer", "com.teamviewer.teamviewer",
        "com.philandro.anydesk", "com.apple.screensharing",
    ]

    /// The way used when the person has not chosen one for this program.
    public static func usual(for program: String) -> InsertMethod { typePrograms.contains(program.lowercased()) ? .type : .paste }
}

/// One key press when typing: some characters, or Return.
public enum TypedKey: Equatable, Sendable {
    case characters(String)
    case enter

    /// The text as key presses: characters in pieces of at most 16 (macOS takes no more in one key press), Return for a line break.
    public static func keys(_ text: String) -> [TypedKey] {
        var out: [TypedKey] = []
        var piece = ""
        for c in text.replacingOccurrences(of: "\r\n", with: "\n") {
            if c == "\n" || c == "\r" {
                if !piece.isEmpty { out.append(.characters(piece)); piece = "" }
                out.append(.enter)
                continue
            }
            if piece.utf16.count + String(c).utf16.count > 16 { out.append(.characters(piece)); piece = "" }
            piece.append(c)
        }
        if !piece.isEmpty { out.append(.characters(piece)) }
        return out
    }
}

/// The keyboard shortcut that starts and stops the Widget, stored as "Option+Shift+D". It needs Control or Option, or
/// Command with another key held, so it never types a letter or takes a program's own ⌘ shortcut.
public struct MacShortcut: Equatable, Sendable {
    public var control = false, option = false, shift = false, command = false
    public var key: String

    public static let standard = MacShortcut(option: true, shift: true, key: "D")

    public init(control: Bool = false, option: Bool = false, shift: Bool = false, command: Bool = false, key: String) {
        self.control = control; self.option = option; self.shift = shift; self.command = command; self.key = key
    }

    /// The choices of keys to hold down offered in the settings, as stored.
    public static let holds = ["Option+Shift", "Control+Shift", "Control+Option", "Control+Option+Shift", "Option+Shift+Command", "Control+Command"]
    /// The keys offered: letters, digits, F1 to F12 and Space.
    public static let keyChoices: [String] = (65...90).map { String(UnicodeScalar(UInt8($0))) } + (0...9).map(String.init) + (1...12).map { "F\($0)" } + ["Space"]

    public var hold: String {
        [(control, "Control"), (option, "Option"), (shift, "Shift"), (command, "Command")].filter { $0.0 }.map { $0.1 }.joined(separator: "+")
    }

    public var text: String { hold + "+" + key }

    /// As a Mac shows it: ⌃⌥⇧⌘ then the key.
    public var symbols: String {
        (control ? "⌃" : "") + (option ? "⌥" : "") + (shift ? "⇧" : "") + (command ? "⌘" : "") + (key == "Space" ? "Space" : key)
    }

    public static func parse(_ text: String) -> MacShortcut? {
        var s = MacShortcut(key: "")
        for raw in text.split(separator: "+") {
            let p = raw.trimmingCharacters(in: .whitespaces).lowercased()
            switch p {
            case "control", "ctrl", "⌃": s.control = true
            case "option", "alt", "⌥": s.option = true
            case "shift", "⇧": s.shift = true
            case "command", "cmd", "⌘": s.command = true
            default:
                guard s.key.isEmpty, let k = keyChoices.first(where: { $0.lowercased() == p }) else { return nil }
                s.key = k
            }
        }
        guard !s.key.isEmpty, s.control || s.option || s.command else { return nil }
        if s.command && !s.control && !s.option && !s.shift { return nil } // ⌘ and a key is a program's own shortcut
        return s
    }

    public static func from(hold: String, key: String) -> MacShortcut? { parse(hold + "+" + key) }
}

/// The Widget's settings, kept in this Mac user's preferences.
public struct WidgetSettings: Codable, Equatable, Sendable {
    public static let defaultServer = "http://localhost:5044"
    public var server: String = defaultServer
    public var languages: String = "en-GB"
    public var gap: ParagraphGap = .automatic
    /// In Chrome and Edge, the shortcut is left to the browser Widget (its shortcut is Option+Shift+D).
    public var leaveBrowsersToExtension = true
    public var testUser = "alice"
    /// The keyboard shortcut, as "Option+Shift+D".
    public var shortcut = MacShortcut.standard.text
    /// The way chosen for a program (by its bundle ID, lower case), where it differs from the usual one.
    public var insertMethods: [String: InsertMethod] = [:]

    public init() {}

    private enum CodingKeys: String, CodingKey { case server, languages, gap, leaveBrowsersToExtension, testUser, shortcut, insertMethods }

    /// Settings saved by an older Widget lack the newer keys; those take their defaults.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        server = try c.decodeIfPresent(String.self, forKey: .server) ?? Self.defaultServer
        languages = try c.decodeIfPresent(String.self, forKey: .languages) ?? "en-GB"
        gap = (try? c.decodeIfPresent(ParagraphGap.self, forKey: .gap)) ?? .automatic
        leaveBrowsersToExtension = try c.decodeIfPresent(Bool.self, forKey: .leaveBrowsersToExtension) ?? true
        testUser = try c.decodeIfPresent(String.self, forKey: .testUser) ?? "alice"
        shortcut = try c.decodeIfPresent(String.self, forKey: .shortcut) ?? MacShortcut.standard.text
        let raw = (try? c.decodeIfPresent([String: String].self, forKey: .insertMethods)) ?? [:]
        insertMethods = raw.reduce(into: [:]) { m, x in if let v = InsertMethod(rawValue: x.value) { m[x.key.lowercased()] = v } }
    }

    /// The shortcut to use (the standard one when the stored one is not allowed).
    public var keys: MacShortcut { MacShortcut.parse(shortcut) ?? .standard }

    /// How the words go into this program: the person's choice, else the usual way for it.
    public func method(for program: String) -> InsertMethod { insertMethods[program.lowercased()] ?? InsertMethod.usual(for: program) }

    /// These settings with this program's way chosen; choosing the usual way forgets the choice.
    public func with(_ method: InsertMethod, for program: String) -> WidgetSettings {
        var s = self
        let key = program.lowercased()
        if method == InsertMethod.usual(for: key) { s.insertMethods.removeValue(forKey: key) } else { s.insertMethods[key] = method }
        return s
    }

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
