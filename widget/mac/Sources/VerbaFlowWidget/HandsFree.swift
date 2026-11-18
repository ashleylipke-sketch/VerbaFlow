import AppKit
import AVFoundation
import Carbon.HIToolbox
import Speech
import WidgetMacCore

/// Hands-free listening, done by Apple's speech recognition on this Mac itself (on-device: nothing is sent to Apple,
/// VerbaFlow or Azure, and it costs nothing). It is paused while the person dictates.
@MainActor
final class HandsFree {
    enum Mode { case commands, checking, paused }

    private let recognizer: SFSpeechRecognizer
    private var feed: SoundFeed?
    private var request: SFSpeechAudioBufferRecognitionRequest? { didSet { feed?.request = request } }
    private var task: SFSpeechRecognitionTask?
    private var mode = Mode.commands
    private var stable: DispatchWorkItem?
    private var restartTimer: Timer?
    private var acted = ""
    private var generation = 0

    /// A command heard.
    var heard: ((VoiceAction) -> Void)?

    private init(_ recognizer: SFSpeechRecognizer) { self.recognizer = recognizer }

    /// Hands-free listening, or a plain sentence saying why it cannot start.
    static func start(languages: String) async -> Result<HandsFree, PlainError> {
        let allowed = await SoundFeed.speechAllowed()
        guard allowed else {
            return .failure(PlainError(message: "Hands-free needs Speech Recognition. Allow the VerbaFlow Widget under System Settings, Privacy & Security, Speech Recognition, then switch hands-free on again."))
        }
        let wanted = languages.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.first { $0.lowercased().hasPrefix("en") } ?? "en-GB"
        guard let r = SFSpeechRecognizer(locale: Locale(identifier: wanted)) ?? SFSpeechRecognizer(locale: Locale(identifier: "en-US")),
              r.supportsOnDeviceRecognition else {
            return .failure(PlainError(message: "Hands-free needs English speech recognition on this Mac. In System Settings, Keyboard, switch Dictation on with English, wait for it to download, then switch hands-free on again."))
        }
        let h = HandsFree(r)
        do { try h.begin() } catch {
            return .failure(PlainError(message: "Hands-free could not start listening. Check that the Widget may use the microphone (System Settings, Privacy & Security, Microphone)."))
        }
        return .success(h)
    }

    /// What to listen for: commands, the check step's commands, or nothing while dictating.
    func set(_ m: Mode) {
        guard m != mode else { return }
        let wasPaused = mode == .paused
        mode = m
        if m == .paused { end(); return }
        if wasPaused { try? begin() } else { restart() }
    }

    private func begin() throws {
        let f = SoundFeed()
        try f.start()
        feed = f
        newTask()
        // Apple's recogniser is meant for short requests, so a fresh one is started every 50 seconds.
        restartTimer = Timer.scheduledTimer(withTimeInterval: 50, repeats: true) { [weak self] _ in Task { @MainActor in self?.restart() } }
    }

    private func newTask() {
        let r = SFSpeechAudioBufferRecognitionRequest()
        r.requiresOnDeviceRecognition = true
        r.shouldReportPartialResults = true
        r.taskHint = .search
        r.contextualStrings = ["VerbaFlow", "dictate", "switch to", "open", "next cell", "go to cell", "stop dictating", "accept", "speak more", "fix grammar", "cancel"]
            + Programs.running().map(\.name) + Programs.installed().prefix(80).map(\.name)
        request = r
        acted = ""
        generation += 1
        let g = generation
        task = SoundFeed.recognise(recognizer, r) { [weak self] text, final in
            Task { @MainActor in
                // Answers from a request already replaced (including its "cancelled") are ignored.
                guard let self, self.generation == g else { return }
                self.received(text, final: final)
            }
        }
    }

    private func received(_ text: String?, final: Bool) {
        if let text, mode != .paused {
            stable?.cancel()
            // A command is acted on once the words have stayed the same for a moment (a name may still be coming).
            let work = DispatchWorkItem { [weak self] in self?.act(on: text) }
            stable = work
            DispatchQueue.main.asyncAfter(deadline: .now() + (final ? 0 : 0.8), execute: work)
        }
        let g = generation
        if final && mode != .paused { DispatchQueue.main.asyncAfter(deadline: .now() + 0.9) { [weak self] in if self?.generation == g { self?.restart() } } }
    }

    private func act(on text: String) {
        guard text != acted, let command = VoiceCommands.latest(in: text, checking: mode == .checking) else { return }
        acted = text
        restart() // the words are used; the next command starts from nothing
        heard?(command)
    }

    private func restart() {
        guard mode != .paused, feed != nil else { return }
        generation += 1
        stable?.cancel()
        task?.cancel(); request?.endAudio()
        task = nil; request = nil
        newTask()
    }

    private func end() {
        stable?.cancel()
        generation += 1
        restartTimer?.invalidate(); restartTimer = nil
        task?.cancel(); request?.endAudio()
        task = nil; request = nil
        feed?.stop(); feed = nil
    }

    func stop() { mode = .paused; end() }
}

/// The microphone's sound handed to the recogniser's current request, on the microphone's own thread.
final class SoundFeed: @unchecked Sendable {
    private let engine = AVAudioEngine()
    private let lock = NSLock()
    private var current: SFSpeechAudioBufferRecognitionRequest?

    var request: SFSpeechAudioBufferRecognitionRequest? {
        get { lock.lock(); defer { lock.unlock() }; return current }
        set { lock.lock(); current = newValue; lock.unlock() }
    }

    func start() throws {
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else { throw PlainError(message: "No microphone was found.") }
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { [weak self] buffer, _ in self?.request?.append(buffer) }
        engine.prepare()
        do { try engine.start() } catch { input.removeTap(onBus: 0); throw error }
    }

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
    }

    // Apple calls these back on its own threads, so they are made outside the main-thread classes.
    static func speechAllowed() async -> Bool {
        await withCheckedContinuation { c in SFSpeechRecognizer.requestAuthorization { c.resume(returning: $0 == .authorized) } }
    }

    static func recognise(_ recognizer: SFSpeechRecognizer, _ request: SFSpeechAudioBufferRecognitionRequest,
                          _ handler: @escaping @Sendable (String?, Bool) -> Void) -> SFSpeechRecognitionTask {
        recognizer.recognitionTask(with: request) { result, error in
            handler(result?.bestTranscription.formattedString, result?.isFinal == true || error != nil)
        }
    }
}

/// The programs a person can name by voice: the ones running (for "switch to …") and the installed ones (for "open …").
@MainActor
enum Programs {
    struct Running { let name: String; let app: NSRunningApplication }
    struct Installed { let name: String; let url: URL }

    static func running() -> [Running] {
        NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular && $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
            .compactMap { a in a.localizedName.map { Running(name: ProgramNames.clean($0), app: a) } }
            .filter { $0.name.count >= 2 }
    }

    private static var cache: [Installed]?
    private static var cacheRead = Date.distantPast

    /// The programs in Applications (and its Utilities), the system's and the person's own, read again every 10 minutes.
    static func installed() -> [Installed] {
        if let c = cache, Date().timeIntervalSince(cacheRead) < 600 { return c }
        let fm = FileManager.default
        let folders = ["/Applications", "/Applications/Utilities", "/System/Applications", "/System/Applications/Utilities",
                       (NSHomeDirectory() as NSString).appendingPathComponent("Applications")]
        var list: [Installed] = []
        var seen = Set<String>()
        for f in folders {
            for item in (try? fm.contentsOfDirectory(atPath: f)) ?? [] where item.hasSuffix(".app") {
                let name = ProgramNames.clean(String(item.dropLast(4)))
                if name.count >= 2, seen.insert(name).inserted { list.append(Installed(name: name, url: URL(fileURLWithPath: f).appendingPathComponent(item))) }
            }
        }
        cache = list; cacheRead = Date()
        return list
    }

    /// "switch to Mail": brings it to the front. False when it is not running.
    static func switchTo(_ spoken: String) -> Bool {
        let front = NSWorkspace.shared.frontmostApplication
        let all = running()
        let ordered = all.filter { $0.app != front } + all.filter { $0.app == front }
        guard let match = ProgramNames.best(spoken, ordered.map { (name: $0.name, item: $0.app) }) else { return false }
        if match.isHidden { match.unhide() }
        return match.activate(options: [.activateAllWindows])
    }

    /// "open Excel": starts it. False when no program of that name is installed.
    static func open(_ spoken: String) -> Bool {
        guard let match = ProgramNames.best(spoken, installed().map { (name: $0.name, item: $0.url) }) else { return false }
        NSWorkspace.shared.openApplication(at: match, configuration: NSWorkspace.OpenConfiguration())
        return true
    }

    static func press(_ key: VoiceKey) {
        switch key {
        case .nextCell, .tab: Keys.press(CGKeyCode(kVK_Tab))
        case .previousCell: Keys.press(CGKeyCode(kVK_Tab), flags: .maskShift)
        case .down: Keys.press(CGKeyCode(kVK_DownArrow))
        case .up: Keys.press(CGKeyCode(kVK_UpArrow))
        case .right: Keys.press(CGKeyCode(kVK_RightArrow))
        case .left: Keys.press(CGKeyCode(kVK_LeftArrow))
        case .enter: Keys.press(CGKeyCode(kVK_Return))
        case .escape: Keys.press(CGKeyCode(kVK_Escape))
        case .backspace: Keys.press(CGKeyCode(kVK_Delete))
        case .delete: Keys.press(CGKeyCode(kVK_ForwardDelete))
        }
    }

    /// "go to cell B5": Excel's Go To box (Control+G), the address, Return.
    static func goToCell(_ cell: String) async {
        Keys.press(CGKeyCode(kVK_ANSI_G), flags: .maskControl)
        try? await Task.sleep(nanoseconds: 400_000_000)
        await Keys.type(cell)
        try? await Task.sleep(nanoseconds: 100_000_000)
        Keys.press(CGKeyCode(kVK_Return))
    }
}
