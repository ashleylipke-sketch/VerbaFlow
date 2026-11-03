import UIKit
import VerbaFlowShared

/// The VerbaFlow keyboard. iPhone does not let a keyboard use the microphone, so the VerbaFlow app listens and this
/// keyboard shows the words and types them in: tap the microphone, speak, Stop, then Accept (or Speak more, Fix grammar,
/// Cancel). The first tap opens the app to start listening; for five minutes after that the app is ready in the
/// background and a tap starts at once.
final class KeyboardViewController: UIInputViewController {
    private let preview = UILabel()
    private let note = UILabel()
    private let row = UIStackView()
    private var status = AppStatus()
    private var lastSent: KeyboardRequest?
    private var waitingForApp: Date?
    private var timer: Timer?
    private var observer: DarwinObserver?
    private var shownPhase: AppStatus.Phase?
    private var shownFixed = false

    override func viewDidLoad() {
        super.viewDidLoad()
        let height = view.heightAnchor.constraint(equalToConstant: 230)
        height.priority = .defaultHigh
        height.isActive = true

        preview.numberOfLines = 0
        preview.font = .preferredFont(forTextStyle: .body)
        preview.lineBreakMode = .byTruncatingHead
        note.numberOfLines = 0
        note.font = .preferredFont(forTextStyle: .footnote)
        note.textColor = .secondaryLabel
        row.axis = .horizontal
        row.spacing = 6
        row.distribution = .fillEqually

        let stack = UIStackView(arrangedSubviews: [preview, note, UIView(), row])
        stack.axis = .vertical
        stack.spacing = 6
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 8),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -8),
            stack.topAnchor.constraint(equalTo: view.topAnchor, constant: 8),
            stack.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -8),
            row.heightAnchor.constraint(equalToConstant: 48),
        ])
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        observer = DarwinObserver(Link.statusChanged) { [weak self] in self?.refresh() }
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
        refresh(force: true)
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        timer?.invalidate()
        timer = nil
        observer = nil
    }

    // MARK: what is shown

    private func refresh(force: Bool = false) {
        let s = Link.read(AppStatus.self, Link.statusKey) ?? AppStatus()
        if !force && s == status { return }
        status = s
        if waitingForApp != nil && (s.phase != .idle || (lastSent.map { s.handled >= $0.no } ?? false)) { waitingForApp = nil }
        show()
    }

    private func show() {
        let s = status
        preview.attributedText = Self.styled(s.text, pending: s.pendingWords)
        if !hasFullAccess {
            note.text = "Turn on Allow Full Access: Settings, General, Keyboard, Keyboards, VerbaFlow."
        } else if waitingForApp != nil {
            note.text = "Opening VerbaFlow to listen. Then come back here with ◀ at the top left."
        } else {
            switch s.phase {
            case .idle: note.text = s.message ?? (s.isAlive() ? "Ready: tap the microphone and speak." : "Tap the microphone and speak.")
            case .starting: note.text = "Getting ready…"
            case .listening: note.text = s.linkLost ? "Connection lost. Still listening; the words will follow." : "Listening. Tap Stop when you have finished."
            case .finishing: note.text = "Finishing the last words…"
            case .checking: note.text = s.message ?? "Check the words, then Accept to type them in."
            case .fixing: note.text = "Fixing grammar and punctuation…"
            case .failed: note.text = s.message
            }
        }
        // Rebuild the buttons only when what they are changes, so a tap is never lost mid-press.
        if shownPhase == s.phase && shownFixed == (s.fixedFrom != nil) && row.arrangedSubviews.count > 0 { return }
        shownPhase = s.phase
        shownFixed = s.fixedFrom != nil
        row.arrangedSubviews.forEach { $0.removeFromSuperview() }
        switch s.phase {
        case .idle, .failed:
            if needsInputModeSwitchKey { row.addArrangedSubview(globe()) }
            row.addArrangedSubview(button("🎤 Speak", .systemRed, bold: true) { [weak self] in self?.speak() })
            row.addArrangedSubview(button("space") { [weak self] in self?.textDocumentProxy.insertText(" ") })
            row.addArrangedSubview(button("⌫") { [weak self] in self?.textDocumentProxy.deleteBackward() })
            row.addArrangedSubview(button("return") { [weak self] in self?.textDocumentProxy.insertText("\n") })
        case .starting, .listening:
            row.addArrangedSubview(button("■ Stop", .systemRed, bold: true) { [weak self] in self?.send(.stop) })
            row.addArrangedSubview(button("Cancel") { [weak self] in self?.send(.cancel) })
        case .finishing, .fixing:
            row.addArrangedSubview(button("Cancel") { [weak self] in self?.send(.cancel) })
        case .checking:
            row.addArrangedSubview(button("Accept", .systemGreen, bold: true) { [weak self] in self?.accept() })
            row.addArrangedSubview(button("Speak more") { [weak self] in self?.send(.more) })
            row.addArrangedSubview(s.fixedFrom != nil
                ? button("Undo fix") { [weak self] in self?.send(.undoFix) }
                : button("Fix grammar") { [weak self] in self?.send(.fixGrammar) })
            row.addArrangedSubview(button("Cancel") { [weak self] in self?.send(.cancel) })
        }
    }

    /// The words heard, the ones still being heard in grey.
    static func styled(_ text: String, pending: Int) -> NSAttributedString {
        let out = NSMutableAttributedString(string: text, attributes: [.foregroundColor: UIColor.label])
        guard pending > 0 else { return out }
        var count = 0
        var start = text.endIndex
        var i = text.endIndex
        // Walk back over the last `pending` words.
        while i > text.startIndex && count < pending {
            let before = text.index(before: i)
            if !text[before].isWhitespace && (before == text.startIndex || text[text.index(before: before)].isWhitespace) { count += 1; start = before }
            i = before
        }
        out.addAttribute(.foregroundColor, value: UIColor.secondaryLabel, range: NSRange(start..<text.endIndex, in: text))
        return out
    }

    // MARK: actions

    private func speak() {
        guard hasFullAccess else { show(); return }
        send(.listen)
        if !status.isAlive() {
            waitingForApp = Date()
            openApp()
            show()
        }
    }

    private func accept() {
        let text = status.text
        if !text.isEmpty { textDocumentProxy.insertText(DictationText.spaced(before: textDocumentProxy.documentContextBeforeInput, text)) }
        send(.accept, words: DictationText.words(text))
    }

    private func send(_ kind: KeyboardRequest.Kind, words: Int? = nil) {
        let r = KeyboardRequest(no: Link.nextNumber(status: status, lastSent: lastSent), kind: kind, words: words)
        lastSent = r
        Link.write(r, Link.requestKey, notify: Link.requestSent)
    }

    /// A keyboard may not open apps itself, so the request goes up to the app the keyboard is shown in, which opens VerbaFlow.
    private func openApp() {
        guard let url = URL(string: "verbaflow://listen"), let appClass = NSClassFromString("UIApplication") else { return }
        var r: UIResponder? = self
        while let x = r {
            if x.isKind(of: appClass) {
                let sel = NSSelectorFromString("openURL:options:completionHandler:")
                typealias Open = @convention(c) (AnyObject, Selector, URL, NSDictionary, ((Bool) -> Void)?) -> Void
                if x.responds(to: sel) {
                    let open = unsafeBitCast(x.method(for: sel), to: Open.self)
                    open(x, sel, url, NSDictionary(), nil)
                }
                return
            }
            r = x.next
        }
    }

    // MARK: buttons

    private func globe() -> UIButton {
        let b = button("🌐") {}
        b.addTarget(self, action: #selector(handleInputModeList(from:with:)), for: .allTouchEvents)
        return b
    }

    private func button(_ title: String, _ tint: UIColor? = nil, bold: Bool = false, _ action: @escaping () -> Void) -> UIButton {
        var config = UIButton.Configuration.filled()
        config.title = title
        config.baseBackgroundColor = tint ?? .secondarySystemBackground
        config.baseForegroundColor = tint == nil ? .label : .white
        config.cornerStyle = .medium
        config.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { a in
            var a = a
            a.font = .systemFont(ofSize: 16, weight: bold ? .semibold : .regular)
            return a
        }
        let b = UIButton(configuration: config, primaryAction: UIAction { _ in action() })
        b.titleLabel?.adjustsFontSizeToFitWidth = true
        return b
    }
}
