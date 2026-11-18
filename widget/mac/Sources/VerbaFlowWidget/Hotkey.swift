import Carbon.HIToolbox
import CoreGraphics
import Foundation
import WidgetMacCore

/// The Widget's shortcut (Option+Shift+D unless changed), held for the whole Mac while the Widget runs. A system-wide
/// shortcut needs no permission.
final class Hotkey {
    private static var pressed: (() -> Void)?
    private static var handlerInstalled = false
    private var ref: EventHotKeyRef?
    private(set) var keys = MacShortcut.standard

    init(_ action: @escaping () -> Void) {
        Self.pressed = action
        if !Self.handlerInstalled {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
                DispatchQueue.main.async { Hotkey.pressed?() }
                return noErr
            }, 1, &spec, nil, nil)
            Self.handlerInstalled = true
        }
    }

    /// False when another program already uses the shortcut.
    @discardableResult
    func register() -> Bool {
        guard ref == nil else { return true }
        return take(keys)
    }

    /// Changes to another shortcut. False (and the old one kept) when another program has the new one.
    func change(to new: MacShortcut) -> Bool {
        if new == keys && ref != nil { return true }
        let old = keys
        unregister()
        if take(new) { keys = new; return true }
        _ = take(old)
        return false
    }

    private func take(_ k: MacShortcut) -> Bool {
        guard let code = Self.code(k.key) else { return false }
        var mods = 0
        if k.control { mods |= controlKey }
        if k.option { mods |= optionKey }
        if k.shift { mods |= shiftKey }
        if k.command { mods |= cmdKey }
        let id = EventHotKeyID(signature: OSType(0x5646_5744), id: 1) // "VFWD"
        var r: EventHotKeyRef?
        guard RegisterEventHotKey(UInt32(code), UInt32(mods), id, GetApplicationEventTarget(), 0, &r) == noErr, let r else { return false }
        ref = r
        keys = k
        return true
    }

    /// The key's code on the keyboard (US positions, as macOS names them).
    static func code(_ key: String) -> Int? {
        let letters: [Character: Int] = [
            "A": kVK_ANSI_A, "B": kVK_ANSI_B, "C": kVK_ANSI_C, "D": kVK_ANSI_D, "E": kVK_ANSI_E, "F": kVK_ANSI_F, "G": kVK_ANSI_G,
            "H": kVK_ANSI_H, "I": kVK_ANSI_I, "J": kVK_ANSI_J, "K": kVK_ANSI_K, "L": kVK_ANSI_L, "M": kVK_ANSI_M, "N": kVK_ANSI_N,
            "O": kVK_ANSI_O, "P": kVK_ANSI_P, "Q": kVK_ANSI_Q, "R": kVK_ANSI_R, "S": kVK_ANSI_S, "T": kVK_ANSI_T, "U": kVK_ANSI_U,
            "V": kVK_ANSI_V, "W": kVK_ANSI_W, "X": kVK_ANSI_X, "Y": kVK_ANSI_Y, "Z": kVK_ANSI_Z,
            "0": kVK_ANSI_0, "1": kVK_ANSI_1, "2": kVK_ANSI_2, "3": kVK_ANSI_3, "4": kVK_ANSI_4, "5": kVK_ANSI_5,
            "6": kVK_ANSI_6, "7": kVK_ANSI_7, "8": kVK_ANSI_8, "9": kVK_ANSI_9,
        ]
        let fKeys = [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10, kVK_F11, kVK_F12]
        if key == "Space" { return kVK_Space }
        if key.count == 1, let c = key.first { return letters[c] }
        if key.hasPrefix("F"), let n = Int(key.dropFirst()), (1...12).contains(n) { return fKeys[n - 1] }
        return nil
    }

    func unregister() {
        if let ref { UnregisterEventHotKey(ref) }
        ref = nil
    }

    /// Lets the browser Widget in Chrome and Edge have its own shortcut, Option+Shift+D: this shortcut is let go (when it
    /// is the same), the keys are pressed for that program, and the shortcut is taken back.
    func passOn() {
        let same = keys == .standard
        if same { unregister() }
        Keys.press(CGKeyCode(kVK_ANSI_D), flags: [.maskAlternate, .maskShift])
        if same { DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in self?.register() } }
    }
}

/// Key presses sent to the program in front. macOS lets the Widget do this only once it is allowed under
/// System Settings, Privacy & Security, Accessibility.
enum Keys {
    static func press(_ key: CGKeyCode, flags: CGEventFlags = []) {
        let source = CGEventSource(stateID: .hidSystemState)
        let down = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false)
        down?.flags = flags
        up?.flags = flags
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)
    }

    static func paste() { press(CGKeyCode(kVK_ANSI_V), flags: .maskCommand) }

    /// Types the text as a keyboard would: the characters themselves (any language, whatever the keyboard layout),
    /// Return for a line break, a little at a time so slow programs and remote desktops keep up.
    static func type(_ text: String) async {
        let source = CGEventSource(stateID: .hidSystemState)
        for k in TypedKey.keys(text) {
            switch k {
            case .enter:
                press(CGKeyCode(kVK_Return))
            case .characters(let s):
                let units = Array(s.utf16)
                let down = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: true)
                let up = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: false)
                down?.flags = []; up?.flags = []
                units.withUnsafeBufferPointer { b in
                    down?.keyboardSetUnicodeString(stringLength: b.count, unicodeString: b.baseAddress)
                    up?.keyboardSetUnicodeString(stringLength: b.count, unicodeString: b.baseAddress)
                }
                down?.post(tap: .cghidEventTap)
                up?.post(tap: .cghidEventTap)
            }
            try? await Task.sleep(nanoseconds: 15_000_000)
        }
    }
    static func delete() { press(CGKeyCode(kVK_Delete)) }
}
