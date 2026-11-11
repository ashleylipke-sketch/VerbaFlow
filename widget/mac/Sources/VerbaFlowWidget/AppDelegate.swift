import AppKit
import SwiftUI
import WidgetMacCore

/// The VerbaFlow Widget for Mac: an icon in the menu bar and the Option+Shift+D shortcut. It runs until Quit is chosen
/// from the icon's menu, and can start when the person logs in (in the settings).
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private(set) var settings = WidgetSettings.load()
    let signIn = SignIn()
    private var item: NSStatusItem?
    private var hotkey: Hotkey?
    private var controller: Controller?
    private var settingsWindow: NSWindow?
    private var settingsModel: SettingsModel?
    private var apiCache: Api?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let firstRun = !WidgetSettings.exists()
        signIn.testUser = { [weak self] in self?.settings.testUser ?? "alice" }
        controller = Controller(api: { [unowned self] in try self.api() }, settings: { [unowned self] in self.settings },
                                openSettings: { [weak self] in self?.openSettings() })

        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        let image = NSImage(systemSymbolName: "waveform", accessibilityDescription: "VerbaFlow Widget")
        image?.isTemplate = true
        item.button?.image = image
        item.button?.toolTip = "VerbaFlow Widget (⌥⇧D)"
        let menu = NSMenu()
        menu.addItem(withTitle: "Speak here (⌥⇧D)", action: #selector(speakFromMenu), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Settings and sign-in…", action: #selector(openSettingsFromMenu), keyEquivalent: ",").target = self
        menu.addItem(withTitle: "Open VerbaFlow", action: #selector(openVerbaFlow), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit the Widget", action: #selector(quit), keyEquivalent: "q").target = self
        item.menu = menu
        self.item = item

        let h = Hotkey { [weak self] in self?.shortcut() }
        hotkey = h
        let registered = h.register()
        if firstRun || !FocusTarget.trusted { openSettings() }
        if !registered {
            controller?.notice("Option+Shift+D is taken by another program, so the Widget cannot use it. Quit that program, then quit and start the Widget again.")
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        hotkey?.unregister()
        controller?.shutDown()
    }

    func api() throws -> Api {
        if let a = apiCache, a.server == MacText.cleanServer(settings.server) { return a }
        guard let url = MacText.cleanServer(settings.server) else {
            throw PlainError(message: "Open the Widget's settings and type VerbaFlow's address first.", signIn: true)
        }
        let a = Api(server: url, signIn: signIn)
        apiCache = a
        return a
    }

    func save(_ s: WidgetSettings) {
        settings = s
        s.save()
    }

    private func shortcut() {
        guard let target = FocusTarget.capture() else { return }
        if settings.leaveBrowsersToExtension && MacText.browsers.contains(target.program) {
            // Websites are the browser Widget's: it knows the website, and an administrator's blocks apply to websites.
            hotkey?.passOn()
            return
        }
        Task { await controller?.shortcut(target) }
    }

    @objc private func speakFromMenu() {
        // The menu took the keyboard; give it back to the program in front first.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in self?.shortcut() }
    }

    @objc private func openSettingsFromMenu() { openSettings() }

    func openSettings() {
        if let w = settingsWindow {
            NSApp.activate(ignoringOtherApps: true)
            w.makeKeyAndOrderFront(nil)
            return
        }
        let model = SettingsModel(self)
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 640), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = "VerbaFlow Widget settings"
        w.contentView = NSHostingView(rootView: SettingsView(model: model))
        w.isReleasedWhenClosed = false
        w.center()
        settingsModel = model
        settingsWindow = w
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.settingsWindow = nil; self?.settingsModel = nil }
        }
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
        Task { await model.check() }
    }

    @objc private func openVerbaFlow() {
        if let u = MacText.cleanServer(settings.server) { NSWorkspace.shared.open(u) }
    }

    @objc private func quit() { NSApp.terminate(nil) }
}
