import AppKit
import SwiftUI
import VerbaFlowShared

/// What the panel shows. Red: listening, the words appear as they are heard (grey while still being heard). Amber:
/// check and change the words, then Accept.
@MainActor
final class PanelModel: ObservableObject {
    enum Stage { case starting, listening, finishing, check, busy, notice }

    @Published var stage: Stage = .starting
    @Published var before = ""
    @Published var heard = ""
    @Published var pending = 0
    @Published var lost = false
    @Published var message = ""
    @Published var bad = false
    @Published var text = ""
    @Published var canFix = false
    @Published var canUndoFix = false
    @Published var amend = false

    var stop: () -> Void = {}
    var cancel: () -> Void = {}
    var accept: () -> Void = {}
    var speakMore: () -> Void = {}
    var fix: () -> Void = {}
    var undoFix: () -> Void = {}
    var settings: () -> Void = {}
}

private let red = Color(red: 176 / 255, green: 42 / 255, blue: 55 / 255)
private let amber = Color(red: 166 / 255, green: 106 / 255, blue: 0)
private let green = Color(red: 21 / 255, green: 115 / 255, blue: 71 / 255)

struct PanelView: View {
    @ObservedObject var model: PanelModel

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(title).font(.system(size: 13, weight: .bold))
                Spacer()
                Text(note).font(.system(size: 11))
            }
            .foregroundColor(.white)
            .padding(.horizontal, 12).padding(.vertical, 7)
            .background(model.stage == .check || model.stage == .busy ? amber : red)

            Group {
                switch model.stage {
                case .check, .busy:
                    TextEditor(text: $model.text)
                        .font(.system(size: 14))
                        .disabled(model.stage == .busy)
                        .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.gray.opacity(0.35)))
                case .notice:
                    EmptyView()
                default:
                    ScrollViewReader { scroll in
                        ScrollView {
                            live.font(.system(size: 14)).frame(maxWidth: .infinity, alignment: .leading).textSelection(.disabled)
                            Color.clear.frame(height: 1).id("end")
                        }
                        .onChange(of: model.heard) { _ in scroll.scrollTo("end") }
                    }
                }
            }
            .padding(.horizontal, 12).padding(.top, 8)

            if !model.message.isEmpty {
                Text(model.message)
                    .font(.system(size: 12))
                    .foregroundColor(model.bad ? red : Color(white: 0.3))
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 12).padding(.top, 6)
            }

            HStack(spacing: 8) { buttons }
                .padding(.horizontal, 12).padding(.vertical, 10)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(width: 460, height: model.stage == .notice ? nil : 240)
        .background(Color(nsColor: .windowBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    private var title: String {
        switch model.stage {
        case .starting: return "Starting…"
        case .listening: return "Listening"
        case .finishing: return "Finishing…"
        case .check, .busy: return "Check the text, then Accept"
        case .notice: return "VerbaFlow Widget"
        }
    }

    private var note: String {
        switch model.stage {
        case .listening: return model.lost ? "Connection lost: still recording" : "⌥⇧D to stop"
        case .check: return "⌘↩ to accept"
        default: return ""
        }
    }

    /// The words so far: those already in the panel, then the heard ones, the last few grey while still being heard.
    private var live: Text {
        let before = model.before.trimmingCharacters(in: .whitespacesAndNewlines)
        if before.isEmpty && model.heard.isEmpty { return Text("Start speaking…").foregroundColor(.gray) }
        var firm = "", grey = ""
        var wordsLeft = DictationText.words(model.heard) - model.pending
        var piece = ""
        func flush() {
            guard !piece.isEmpty else { return }
            let isSpace = piece.allSatisfy { $0.isWhitespace }
            if isSpace { if wordsLeft > 0 { firm += piece } else { grey += piece } }
            else { if wordsLeft > 0 { firm += piece } else { grey += piece }; wordsLeft -= 1 }
            piece = ""
        }
        var lastSpace: Bool? = nil
        for c in model.heard {
            let s = c.isWhitespace
            if lastSpace != nil && s != lastSpace { flush() }
            piece.append(c); lastSpace = s
        }
        flush()
        let gap = before.isEmpty || DictationText.joinsOn(model.heard) ? "" : " "
        return Text(before + gap).foregroundColor(.primary) + Text(firm).foregroundColor(.primary) + Text(grey).foregroundColor(.gray)
    }

    @ViewBuilder private var buttons: some View {
        switch model.stage {
        case .starting, .finishing:
            Button("Cancel", action: model.cancel).keyboardShortcut(.cancelAction)
        case .listening:
            Button("Stop", action: model.stop)
            Button("Cancel", action: model.cancel).keyboardShortcut(.cancelAction)
        case .check, .busy:
            let any = !model.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            let busy = model.stage == .busy
            Button(action: model.accept) { Text("Accept").bold() }
                .keyboardShortcut(.return, modifiers: .command)
                .tint(green).buttonStyle(.borderedProminent)
                .disabled(busy || !(any || model.amend))
            Button("Speak more", action: model.speakMore).disabled(busy)
            if model.canUndoFix { Button("Undo fix", action: model.undoFix).disabled(busy) }
            else if model.canFix { Button("Fix grammar", action: model.fix).disabled(busy || !any) }
            Button("Cancel", action: model.cancel).keyboardShortcut(.cancelAction).disabled(busy)
            Spacer()
            Button("Settings", action: model.settings).buttonStyle(.link)
        case .notice:
            Button("OK", action: model.cancel).keyboardShortcut(.cancelAction)
            Button("Settings", action: model.settings).buttonStyle(.link)
        }
    }
}

/// The small window beside the cursor. It floats over every program and is shown without taking the keyboard from
/// the program while listening; for checking it takes the keyboard without bringing the Widget to the front.
final class PanelWindow: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class DictationPanel {
    let model = PanelModel()
    private let window: PanelWindow
    private var hideNotice: DispatchWorkItem?

    init() {
        window = PanelWindow(contentRect: NSRect(x: 0, y: 0, width: 460, height: 240),
                             styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        window.level = .floating
        window.isFloatingPanel = true
        window.hidesOnDeactivate = false
        window.isMovableByWindowBackground = true
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        window.backgroundColor = .clear
        window.isOpaque = false
        window.hasShadow = true
        window.isReleasedWhenClosed = false
        let host = NSHostingView(rootView: PanelView(model: model))
        window.contentView = host
    }

    var isVisible: Bool { window.isVisible }

    /// Below the cursor, or above it when there is no room below.
    func place(near anchor: NSRect) {
        let size = window.contentView?.fittingSize ?? NSSize(width: 460, height: 240)
        let screen = NSScreen.screens.first(where: { $0.frame.intersects(anchor.insetBy(dx: -1, dy: -1)) }) ?? NSScreen.main
        let area = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let height = max(size.height, 240)
        var y = anchor.minY - 8 - height
        if y < area.minY + 8 { y = min(area.maxY - height - 8, anchor.maxY + 8) }
        let x = max(area.minX + 8, min(area.maxX - 460 - 8, anchor.minX))
        window.setFrameOrigin(NSPoint(x: x, y: y))
    }

    /// Shown in front without taking the keyboard.
    func appear() {
        hideNotice?.cancel()
        window.setContentSize(window.contentView?.fittingSize ?? NSSize(width: 460, height: 240))
        window.orderFrontRegardless()
    }

    /// Takes the keyboard, for checking the text.
    func takeKeyboard() {
        appear()
        window.makeKey()
    }

    func hide() {
        hideNotice?.cancel()
        window.orderOut(nil)
    }

    /// A short message near the cursor, gone after a few seconds.
    func notice(_ text: String, near anchor: NSRect) {
        model.stage = .notice
        model.message = text
        model.bad = false
        place(near: anchor)
        appear()
        let w = DispatchWorkItem { [weak self] in
            if self?.model.stage == .notice { self?.window.orderOut(nil) }
        }
        hideNotice = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 8, execute: w)
    }
}
