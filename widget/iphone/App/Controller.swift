import Foundation
import UIKit
import VerbaFlowShared

/// The app's side of the keyboard: it listens when the keyboard asks, and keeps the keyboard told what it heard.
/// After a listen the microphone stays open for five minutes, so the next press of the keyboard's microphone starts
/// at once without opening this app; then it is switched off.
@MainActor
final class Controller: ObservableObject {
    static let shared = Controller()
    /// How long the microphone stays ready after the last use.
    static let readyFor: TimeInterval = 5 * 60

    @Published private(set) var status = AppStatus()
    @Published var server: String {
        didSet { UserDefaults.standard.set(server, forKey: "server"); apiCache = nil }
    }
    @Published var languages: String {
        didSet { UserDefaults.standard.set(languages, forKey: "languages") }
    }
    /// Shown in the app while it listens for the keyboard, to say how to get back.
    @Published private(set) var listeningForKeyboard = false

    let signIn = SignIn()
    private let mic = Microphone()
    private var listener: LiveListener?
    private var widgetId: String?
    /// The words heard before "Speak more".
    private var before = ""
    private var lastUse = Date()
    private var timer: Timer?
    private var observer: DarwinObserver?
    private var apiCache: Api?

    private init() {
        server = UserDefaults.standard.string(forKey: "server") ?? ""
        languages = UserDefaults.standard.string(forKey: "languages") ?? "en-GB"
        if let s = Link.read(AppStatus.self, Link.statusKey) { status = s }
        // Whatever was going on before this app was closed has ended.
        status.phase = status.phase == .checking ? .checking : .idle
        status.pendingWords = 0
        publish()
        observer = DarwinObserver(Link.requestSent) { [weak self] in self?.checkRequests() }
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }

    /// VerbaFlow's address as typed, or nil when it is not one (http or https only).
    static func cleanServer(_ typed: String) -> URL? {
        var t = typed.trimmingCharacters(in: .whitespaces)
        while t.hasSuffix("/") { t.removeLast() }
        if !t.isEmpty && !t.contains("://") { t = "https://" + t }
        guard let u = URL(string: t), let scheme = u.scheme, ["http", "https"].contains(scheme), u.host != nil else { return nil }
        return u
    }

    func api() throws -> Api {
        if let a = apiCache { return a }
        guard let url = Self.cleanServer(server) else {
            throw PlainError(message: "Open the VerbaFlow app and type VerbaFlow's address first.", signIn: true)
        }
        let a = Api(server: url, signIn: signIn)
        apiCache = a
        return a
    }

    /// Opened from the keyboard (verbaflow://listen): deal with what it asked.
    func openedFromKeyboard() {
        listeningForKeyboard = true
        checkRequests()
    }

    private func tick() {
        checkRequests()
        guard mic.isOn else { return }
        let busy = [.starting, .listening, .finishing, .fixing].contains(status.phase)
        if busy { lastUse = Date() }
        if !busy && Date().timeIntervalSince(lastUse) > Self.readyFor {
            mic.stop()
            status.alive = .distantPast
            listeningForKeyboard = false
            publish()
            return
        }
        // Once a second is enough to say "still here".
        if Date().timeIntervalSince(status.alive) > 1 { status.alive = Date(); publish() }
    }

    private func checkRequests() {
        guard let r = Link.read(KeyboardRequest.self, Link.requestKey), r.no > status.handled else { return }
        status.handled = r.no
        publish()
        Task { await handle(r) }
    }

    private func handle(_ r: KeyboardRequest) async {
        switch r.kind {
        case .listen: await listen(again: false)
        case .more: await listen(again: true)
        case .stop:
            guard let listener, status.phase == .listening || status.phase == .starting else { return }
            status.phase = .finishing
            publish()
            listener.stop()
        case .accept: await end(accepted: true, words: r.words)
        case .cancel: await end(accepted: false, words: nil)
        case .fixGrammar: await fixGrammar()
        case .undoFix:
            if let from = status.fixedFrom { status.text = from; status.fixedFrom = nil; status.message = nil; publish() }
        }
    }

    private func listen(again: Bool) async {
        if [.starting, .listening, .finishing].contains(status.phase) { return }
        before = again ? status.text : ""
        if !again { status.text = ""; status.fixedFrom = nil }
        status.phase = .starting
        status.message = nil
        status.pendingWords = 0
        status.linkLost = false
        publish()
        do {
            let api = try api()
            guard await Microphone.allowed() else {
                throw PlainError(message: "VerbaFlow may not use the microphone. Turn it on in Settings, VerbaFlow, Microphone.")
            }
            do { try mic.start() } catch {
                throw PlainError(message: "The microphone could not be started. Open the VerbaFlow app, then try again.")
            }
            let start: WidgetStart
            if again, let id = widgetId { start = try await api.listenAgain(id) }
            else {
                if let id = widgetId { try? await api.end(id, accepted: false, spokenMs: nil, words: nil, reconnects: nil) }
                start = try await api.start(site: Self.site, device: Self.device, languages: languages)
            }
            widgetId = start.id
            let l = LiveListener(api: api, start: start)
            l.heard = { [weak self] h in self?.heard(h) }
            l.link = { [weak self] up in self?.status.linkLost = !up; self?.publish() }
            l.failed = { [weak self] message in self?.fail(message) }
            l.ended = { [weak self] _, last in self?.finished(last) }
            listener = l
            mic.sound = { [weak l] pcm in l?.write(pcm) }
            l.start()
            status.phase = .listening
            status.alive = Date()
            lastUse = Date()
            publish()
        } catch {
            fail((error as? PlainError)?.message ?? PlainError.unexpected.message)
        }
    }

    private func heard(_ h: LiveListener.Heard) {
        guard status.phase == .listening || status.phase == .finishing else { return }
        status.text = DictationText.join(before, DictationText.heard(h.segments, startsWithBreak: h.startsWithBreak))
        status.pendingWords = h.pendingWords
        publish()
    }

    private func finished(_ last: LiveListener.Heard?) {
        mic.sound = nil
        guard status.phase == .listening || status.phase == .finishing else { return }
        if let last { heard(last) }
        status.phase = status.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && before.isEmpty ? .idle : .checking
        status.pendingWords = 0
        if status.phase == .idle { status.message = "Nothing was heard. Try again a little closer to the microphone." }
        publish()
    }

    private func fail(_ message: String) {
        mic.sound = nil
        listener?.close()
        status.phase = status.text.isEmpty ? .failed : .checking
        status.pendingWords = 0
        status.message = message
        publish()
    }

    private func end(accepted: Bool, words: Int?) async {
        mic.sound = nil
        let l = listener
        listener = nil
        l?.close()
        let id = widgetId
        widgetId = nil
        status.phase = .idle
        status.text = ""
        status.fixedFrom = nil
        status.pendingWords = 0
        status.message = nil
        listeningForKeyboard = false
        publish()
        // Only counts are kept in VerbaFlow's history; what was said is thrown away there.
        if let id, let api = try? api() {
            try? await api.end(id, accepted: accepted, spokenMs: l?.spokenMs, words: words, reconnects: l?.reconnects)
        }
    }

    private func fixGrammar() async {
        guard let id = widgetId, status.phase == .checking, !status.text.isEmpty else { return }
        let original = status.text
        status.phase = .fixing
        status.message = nil
        publish()
        do {
            let fix = try await api().fixGrammar(id, text: original)
            status.phase = .checking
            if fix.text == original { status.message = "No changes were needed." }
            else { status.fixedFrom = original; status.text = fix.text }
        } catch {
            status.phase = .checking
            status.message = (error as? PlainError)?.message ?? PlainError.unexpected.message
        }
        publish()
    }

    private func publish() { Link.write(status, Link.statusKey, notify: Link.statusChanged) }

    /// What VerbaFlow's history and an administrator's blocks see. iPhone does not tell a keyboard which app it is in.
    static var site: String { UIDevice.current.userInterfaceIdiom == .pad ? "ipad" : "iphone" }
    static var device: String { UIDevice.current.userInterfaceIdiom == .pad ? "iPad keyboard" : "iPhone keyboard" }
}
