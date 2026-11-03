import Foundation

/// What the VerbaFlow app is doing, as the keyboard sees it.
public struct AppStatus: Codable, Equatable, Sendable {
    public enum Phase: String, Codable, Sendable {
        /// Nothing heard yet, or the last text was accepted or cancelled.
        case idle
        /// Starting to listen (connecting, asking for the microphone).
        case starting
        /// Listening: the words come in as they are heard.
        case listening
        /// Stop was pressed: the last words are being finished.
        case finishing
        /// The words are ready to check: Accept, Speak more, Fix grammar or Cancel.
        case checking
        /// Fix grammar is running.
        case fixing
        /// Something went wrong: `message` says what, in a plain sentence.
        case failed
    }
    public var phase: Phase = .idle
    /// All the words so far, paragraphs separated by an empty line.
    public var text = ""
    /// How many words at the end are still being heard (shown in grey).
    public var pendingWords = 0
    /// A plain sentence to show, or nil.
    public var message: String?
    /// The text before Fix grammar, so Undo fix can put it back.
    public var fixedFrom: String?
    /// The number of the last keyboard request the app has dealt with.
    public var handled = 0
    /// When the app last said it is running with the microphone open, so it can listen without being opened.
    public var alive = Date.distantPast
    /// The connection to VerbaFlow was lost and is being made again (listening carries on).
    public var linkLost = false
    public init() {}

    /// Whether the app is running and can start listening straight away.
    public func isAlive(now: Date = Date()) -> Bool { now.timeIntervalSince(alive) < 3 }
}

/// Something the keyboard asks the app to do. Each request has a higher number than the last.
public struct KeyboardRequest: Codable, Equatable, Sendable {
    public enum Kind: String, Codable, Sendable { case listen, stop, accept, cancel, more, fixGrammar, undoFix }
    public var no: Int
    public var kind: Kind
    /// Accept: how many words went into the box (a count only, for the history).
    public var words: Int?
    public init(no: Int, kind: Kind, words: Int? = nil) { self.no = no; self.kind = kind; self.words = words }
}

/// The app and its keyboard share an App Group: the status and the latest request are kept there as JSON, and a
/// Darwin notification says one of them changed. Both sides also look every quarter second, in case one is missed.
public enum Link {
    public static let statusKey = "status"
    public static let requestKey = "request"
    public static let statusChanged = "net.verbaflow.speak.status"
    public static let requestSent = "net.verbaflow.speak.request"

    /// The App Group's shared storage, named by the VFAppGroup entry in Info.plist.
    public static var store: UserDefaults? {
        let group = Bundle.main.object(forInfoDictionaryKey: "VFAppGroup") as? String ?? "group.net.verbaflow.speak"
        return UserDefaults(suiteName: group)
    }

    public static func read<T: Decodable>(_ type: T.Type, _ key: String, in store: UserDefaults? = Link.store) -> T? {
        guard let d = store?.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(T.self, from: d)
    }

    public static func write<T: Encodable>(_ value: T, _ key: String, notify name: String, in store: UserDefaults? = Link.store) {
        guard let d = try? JSONEncoder().encode(value) else { return }
        store?.set(d, forKey: key)
        #if canImport(Darwin)
        CFNotificationCenterPostNotification(CFNotificationCenterGetDarwinNotifyCenter(), CFNotificationName(name as CFString), nil, nil, true)
        #endif
    }

    /// The number for the keyboard's next request: higher than any the app handled or the keyboard sent.
    public static func nextNumber(status: AppStatus?, lastSent: KeyboardRequest?) -> Int {
        max(status?.handled ?? 0, lastSent?.no ?? 0) + 1
    }
}

#if canImport(Darwin)
/// Calls back on the main thread whenever the other side posts the named Darwin notification.
public final class DarwinObserver {
    private let action: @MainActor () -> Void
    private let name: String

    public init(_ name: String, _ action: @escaping @MainActor () -> Void) {
        self.action = action
        self.name = name
        CFNotificationCenterAddObserver(CFNotificationCenterGetDarwinNotifyCenter(), Unmanaged.passUnretained(self).toOpaque(),
            { _, observer, _, _, _ in
                guard let observer else { return }
                let me = Unmanaged<DarwinObserver>.fromOpaque(observer).takeUnretainedValue()
                let action = me.action
                Task { @MainActor in action() }
            }, name as CFString, nil, .deliverImmediately)
    }

    deinit {
        CFNotificationCenterRemoveEveryObserver(CFNotificationCenterGetDarwinNotifyCenter(), Unmanaged.passUnretained(self).toOpaque())
    }
}
#endif
