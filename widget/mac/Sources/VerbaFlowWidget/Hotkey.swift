import Carbon.HIToolbox
import CoreGraphics
import Foundation

/// The Option+Shift+D shortcut, held for the whole Mac while the Widget runs. A system-wide shortcut needs no permission.
final class Hotkey {
    private static var pressed: (() -> Void)?
    private static var handlerInstalled = false
    private var ref: EventHotKeyRef?

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

    /// False when another program already uses Option+Shift+D.
    @discardableResult
    func register() -> Bool {
        guard ref == nil else { return true }
        let id = EventHotKeyID(signature: OSType(0x5646_5744), id: 1) // "VFWD"
        let status = RegisterEventHotKey(UInt32(kVK_ANSI_D), UInt32(optionKey | shiftKey), id, GetApplicationEventTarget(), 0, &ref)
        if status != noErr { ref = nil }
        return ref != nil
    }

    func unregister() {
        if let ref { UnregisterEventHotKey(ref) }
        ref = nil
    }

    /// Lets the program in front have Option+Shift+D (the browser Widget in Chrome and Edge): the shortcut is let go,
    /// the keys are pressed for that program, and the shortcut is taken back.
    func passOn() {
        unregister()
        Keys.press(CGKeyCode(kVK_ANSI_D), flags: [.maskAlternate, .maskShift])
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in self?.register() }
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
    static func delete() { press(CGKeyCode(kVK_Delete)) }
}
