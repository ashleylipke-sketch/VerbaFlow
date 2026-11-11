import AppKit
import VerbaFlowShared
import WidgetMacCore

/// One dictation at a time: the shortcut starts listening where the cursor is, the words come back live, the person
/// checks them, and Accept pastes them into the program. Nothing heard is kept: the sound and the words are dropped
/// when the dictation is accepted or cancelled, and VerbaFlow keeps only facts (program, times, counts).
@MainActor
final class Controller {
    private enum Phase { case idle, starting, listening, finishing, check, busy }

    private let makeApi: () throws -> Api
    private let settings: () -> WidgetSettings
    private let openSettings: () -> Void
    private let panel = DictationPanel()

    private var phase = Phase.idle
    private var target: FocusTarget?
    private var widgetId: String?
    private var listener: LiveListener?
    private var mic: Microphone?
    private var prefix = ""
    private var amend = false
    private var beforeFix: String?
    private var failure: String?
    private var spokenMs = 0, reconnects = 0
    private var loudest: Float = 0
    private var lastHeard: LiveListener.Heard?

    init(api: @escaping () throws -> Api, settings: @escaping () -> WidgetSettings, openSettings: @escaping () -> Void) {
        self.makeApi = api; self.settings = settings; self.openSettings = openSettings
        let m = panel.model
        m.stop = { [weak self] in self?.stop() }
        m.cancel = { [weak self] in self?.cancel() }
        m.accept = { [weak self] in Task { await self?.accept() } }
        m.speakMore = { [weak self] in Task { await self?.speakMore() } }
        m.fix = { [weak self] in Task { await self?.fixGrammar() } }
        m.undoFix = { [weak self] in self?.undoFix() }
        m.settings = { [weak self] in self?.openSettings() }
    }

    /// A message near the cursor, for when nothing can start.
    func notice(_ text: String) {
        panel.notice(text, near: FocusTarget.capture()?.anchor ?? NSRect(origin: NSEvent.mouseLocation, size: NSSize(width: 1, height: 1)))
    }

    /// The shortcut: start in the program in front, or stop listening.
    func shortcut(_ target: FocusTarget) async {
        switch phase {
        case .listening: stop(); return
        case .idle: break
        default: panel.takeKeyboard(); return
        }
        if !FocusTarget.trusted {
            FocusTarget.askForTrust()
            panel.notice("Allow the VerbaFlow Widget under System Settings, Privacy & Security, Accessibility, so it can type the words into other programs. Then press ⌥⇧D again.", near: target.anchor)
            return
        }
        if target.isPassword { panel.notice("The Widget does not work in password boxes.", near: target.anchor); return }
        self.target = target
        amend = !target.selectedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        prefix = amend ? target.selectedText : ""
        widgetId = nil; spokenMs = 0; reconnects = 0
        panel.place(near: target.anchor)
        await listen()
    }

    private func listen() async {
        phase = .starting
        lastHeard = nil; beforeFix = nil; failure = nil; loudest = 0
        let m = panel.model
        m.stage = .starting; m.before = prefix; m.heard = ""; m.pending = 0; m.lost = false; m.message = ""; m.bad = false
        panel.appear()
        do {
            let api = try makeApi()
            guard await Microphone.allowed() else {
                throw PlainError(message: "The Widget may not use the microphone. Allow it under System Settings, Privacy & Security, Microphone.")
            }
            let start: WidgetStart
            if let id = widgetId { start = try await api.listenAgain(id) }
            else { start = try await api.start(site: target?.program ?? "unknown.mac", device: "Mac program", languages: settings().languages) }
            widgetId = start.id
            guard phase == .starting else { end(accepted: false, words: nil); return } // cancelled meanwhile
            let l = LiveListener(api: api, start: start)
            l.heard = { [weak self, weak l] h in
                guard let self, self.listener === l else { return }
                self.lastHeard = h
                self.render()
            }
            l.link = { [weak self, weak l] up in
                guard let self, self.listener === l else { return }
                self.panel.model.lost = !up
            }
            l.failed = { [weak self, weak l] message in
                guard let self, self.listener === l else { return }
                self.failure = message
                self.stop()
            }
            let mic = Microphone()
            mic.sound = { [weak l] pcm in l?.write(pcm) }
            mic.level = { [weak self] v in DispatchQueue.main.async { if let self, v > self.loudest { self.loudest = v } } }
            try mic.start()
            listener = l; self.mic = mic
            l.start()
            phase = .listening
            m.stage = .listening
            render()
            quietCheck(l)
        } catch {
            guard phase == .starting else { return }
            let e = error as? PlainError
            if e?.signIn == true { openSettings() }
            showCheck(prefix, e?.message ?? "Something went wrong in the Widget. Try again.", bad: true)
        }
    }

    /// After a few seconds of nothing but silence, a note: the microphone may be muted or the wrong one.
    private func quietCheck(_ l: LiveListener) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 4) { [weak self] in
            guard let self, self.listener === l, self.phase == .listening, self.loudest < 0.01 else { return }
            self.panel.model.message = "Nothing heard yet. Check that the microphone is on and is the one chosen in System Settings, Sound, Input."
            self.panel.model.bad = true
        }
    }

    private func render() {
        guard phase == .listening || phase == .finishing else { return }
        let m = panel.model
        let h = lastHeard
        m.heard = h.map { DictationText.heard($0.segments, startsWithBreak: $0.startsWithBreak) } ?? ""
        m.pending = h?.pendingWords ?? 0
        if loudest >= 0.01, m.bad, failure == nil { m.message = ""; m.bad = false }
    }

    private func stop() {
        guard phase == .listening, let l = listener else { return }
        phase = .finishing
        panel.model.stage = .finishing
        mic?.stop(); mic = nil
        l.ended = { [weak self, weak l] complete, last in
            guard let self, let l, self.listener === l, self.phase == .finishing else { return }
            if let last { self.lastHeard = last }
            self.spokenMs += l.spokenMs
            self.reconnects += l.reconnects
            self.listener = nil
            let heard = self.lastHeard.map { DictationText.heard($0.segments, startsWithBreak: $0.startsWithBreak) } ?? ""
            let text = DictationText.join(self.prefix, heard)
            if let f = self.failure { self.showCheck(text, f, bad: true) }
            else if !complete { self.showCheck(text, "The last words may be missing: VerbaFlow could not be reached to finish them.", bad: true) }
            else { self.showCheck(text, "", bad: false) }
        }
        l.stop()
    }

    private func showCheck(_ text: String, _ message: String, bad: Bool) {
        phase = .check
        let m = panel.model
        m.stage = .check
        m.text = text
        m.message = message
        m.bad = bad
        m.canFix = widgetId != nil
        m.canUndoFix = beforeFix != nil
        m.amend = amend
        panel.takeKeyboard()
    }

    private func speakMore() async {
        guard phase == .check else { return }
        prefix = panel.model.text
        await listen()
    }

    /// AI correction of grammar and punctuation, shown to check before Accept; Undo fix puts the text back.
    private func fixGrammar() async {
        guard phase == .check, let id = widgetId else { return }
        let text = panel.model.text
        phase = .busy
        panel.model.stage = .busy
        panel.model.message = "Checking grammar and punctuation…"
        panel.model.bad = false
        do {
            let r = try await makeApi().fixGrammar(id, text: text)
            guard phase == .busy else { return }
            if r.linesChanged == 0 { showCheck(text, "No corrections needed.", bad: false); return }
            beforeFix = text
            showCheck(r.text, "Corrected \(r.linesChanged) line\(r.linesChanged == 1 ? "" : "s"). Check the text, then Accept.", bad: false)
        } catch {
            if phase == .busy { showCheck(text, (error as? PlainError)?.message ?? PlainError.unexpected.message, bad: true) }
        }
    }

    private func undoFix() {
        guard phase == .check, let before = beforeFix else { return }
        beforeFix = nil
        showCheck(before, "The text is back as it was.", bad: false)
    }

    private func accept() async {
        guard phase == .check, let target else { return }
        var text = panel.model.text
        while let c = text.last, c.isWhitespace { text.removeLast() }
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !amend { return }
        phase = .busy
        var pasted = MacText.forPasting(text, emptyLineBetweenParagraphs: MacText.emptyLine(settings().gap, program: target.program))
        if !amend { pasted = DictationText.spaced(before: target.textBefore, pasted) }
        end(accepted: true, words: DictationText.words(text))
        panel.hide()
        let ok = pasted.isEmpty ? await Inserter.deleteSelection(in: target) : await Inserter.paste(pasted, into: target)
        if !ok {
            Inserter.copy(pasted)
            panel.notice("The program was closed, so the words were copied instead. Paste them where you want them with ⌘V.", near: target.anchor)
        }
        reset()
    }

    private func cancel() {
        if phase == .idle { panel.hide(); return }
        let l = listener
        mic?.stop(); mic = nil
        end(accepted: false, words: nil)
        listener = nil
        l?.close()
        panel.hide()
        if let t = target, !t.app.isTerminated { t.app.activate(options: []) }
        reset()
    }

    /// Tells VerbaFlow the dictation ended (counts only); it then throws away the live session and its words.
    private func end(accepted: Bool, words: Int?) {
        guard let id = widgetId else { return }
        widgetId = nil
        let spoken = spokenMs + (listener?.spokenMs ?? 0)
        let rec = reconnects + (listener?.reconnects ?? 0)
        guard let api = try? makeApi() else { return }
        Task { try? await api.end(id, accepted: accepted, spokenMs: spoken, words: words, reconnects: rec) }
    }

    private func reset() {
        phase = .idle
        target = nil; prefix = ""; lastHeard = nil; beforeFix = nil; failure = nil; amend = false
        panel.model.text = ""
    }

    /// The Widget is quitting.
    func shutDown() {
        mic?.stop()
        end(accepted: false, words: nil)
        listener?.close()
    }
}
