import AppKit

/// The Widget lives in the menu bar only: no Dock icon and no main window.
@main
enum WidgetApp {
    @MainActor
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        withExtendedLifetime(delegate) { app.run() }
    }
}
