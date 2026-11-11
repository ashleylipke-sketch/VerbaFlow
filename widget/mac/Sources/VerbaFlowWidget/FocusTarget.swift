import AppKit
import ApplicationServices
import Carbon.HIToolbox
import WidgetMacCore

/// Where the words go: the program in front when the shortcut was pressed, where its cursor is, and (where the program
/// tells macOS) the selected text, the character before the cursor and whether it is a password box. Reading this
/// needs the Accessibility permission; without it only the program is known.
struct FocusTarget {
    let app: NSRunningApplication
    /// The program's bundle ID as VerbaFlow records it (com.microsoft.outlook).
    let program: String
    /// Where to put the panel, in screen coordinates (bottom-left origin, as AppKit uses).
    let anchor: NSRect
    let isPassword: Bool
    let selectedText: String
    let textBefore: String?

    /// Whether the Widget may read the program in front and press keys for it.
    static var trusted: Bool { AXIsProcessTrusted() }

    /// Asks macOS to show its Accessibility question (once; after that the person goes to System Settings).
    static func askForTrust() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    /// The program in front now, or nil when it is the Widget itself or none.
    static func capture() -> FocusTarget? {
        guard let app = NSWorkspace.shared.frontmostApplication, app.processIdentifier != getpid() else { return nil }
        let program = MacText.site(bundleId: app.bundleIdentifier)
        // macOS's own sign that a password is being typed, whatever the program.
        var password = IsSecureEventInputEnabled()
        var selected = "", before: String? = nil
        var anchor = NSRect.zero
        if trusted {
            let system = AXUIElementCreateSystemWide()
            AXUIElementSetMessagingTimeout(system, 1.0)
            if let el: AXUIElement = attribute(system, kAXFocusedUIElementAttribute) {
                AXUIElementSetMessagingTimeout(el, 1.0)
                let role: String? = attribute(el, kAXRoleAttribute)
                let subrole: String? = attribute(el, kAXSubroleAttribute)
                if subrole == (kAXSecureTextFieldSubrole as String) || role == "AXSecureTextField" { password = true }
                if !password {
                    selected = attribute(el, kAXSelectedTextAttribute) ?? ""
                    if let range: CFRange = rangeAttribute(el, kAXSelectedTextRangeAttribute) {
                        if range.location > 0 { before = string(el, CFRange(location: range.location - 1, length: 1)) }
                        anchor = bounds(el, CFRange(location: range.location, length: max(range.length, 0))) ?? .zero
                        if anchor.isEmpty, range.location > 0 { anchor = bounds(el, CFRange(location: range.location - 1, length: 1)) ?? .zero }
                    }
                }
                if anchor.isEmpty, let frame = frame(el) { anchor = NSRect(x: frame.minX, y: frame.minY, width: min(frame.width, 400), height: min(frame.height, 40)) }
            }
        }
        if anchor.isEmpty {
            let m = NSEvent.mouseLocation
            return FocusTarget(app: app, program: program, anchor: NSRect(x: m.x, y: m.y, width: 1, height: 1),
                               isPassword: password, selectedText: selected, textBefore: before)
        }
        return FocusTarget(app: app, program: program, anchor: flip(anchor), isPassword: password, selectedText: selected, textBefore: before)
    }

    // MARK: Accessibility

    private static func attribute<T>(_ el: AXUIElement, _ name: String) -> T? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, name as CFString, &value) == .success, let value else { return nil }
        return value as? T
    }

    private static func rangeAttribute(_ el: AXUIElement, _ name: String) -> CFRange? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, name as CFString, &value) == .success, let value,
              CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var range = CFRange()
        return AXValueGetValue(value as! AXValue, .cfRange, &range) ? range : nil
    }

    private static func parameterised(_ el: AXUIElement, _ name: String, _ range: CFRange) -> CFTypeRef? {
        var r = range
        guard let param = AXValueCreate(.cfRange, &r) else { return nil }
        var value: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(el, name as CFString, param, &value) == .success else { return nil }
        return value
    }

    private static func string(_ el: AXUIElement, _ range: CFRange) -> String? {
        parameterised(el, kAXStringForRangeParameterizedAttribute, range) as? String
    }

    /// Where some text is on the screen, in Accessibility coordinates (top-left origin).
    private static func bounds(_ el: AXUIElement, _ range: CFRange) -> NSRect? {
        guard let v = parameterised(el, kAXBoundsForRangeParameterizedAttribute, range), CFGetTypeID(v) == AXValueGetTypeID() else { return nil }
        var rect = CGRect.zero
        guard AXValueGetValue(v as! AXValue, .cgRect, &rect), rect.height > 0 else { return nil }
        return NSRect(x: rect.minX, y: rect.minY, width: max(rect.width, 2), height: rect.height)
    }

    private static func frame(_ el: AXUIElement) -> NSRect? {
        var p: CFTypeRef?, s: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, kAXPositionAttribute as CFString, &p) == .success,
              AXUIElementCopyAttributeValue(el, kAXSizeAttribute as CFString, &s) == .success, let p, let s else { return nil }
        var point = CGPoint.zero, size = CGSize.zero
        guard AXValueGetValue(p as! AXValue, .cgPoint, &point), AXValueGetValue(s as! AXValue, .cgSize, &size), size.height > 0 else { return nil }
        return NSRect(origin: point, size: size)
    }

    /// Accessibility measures from the top of the main screen; AppKit from its bottom.
    private static func flip(_ r: NSRect) -> NSRect {
        let height = NSScreen.screens.first?.frame.height ?? 0
        return NSRect(x: r.minX, y: height - r.maxY, width: r.width, height: r.height)
    }
}
