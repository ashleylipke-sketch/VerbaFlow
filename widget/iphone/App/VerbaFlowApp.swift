import SwiftUI

@main
struct VerbaFlowApp: App {
    @StateObject private var controller = Controller.shared

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(controller)
                // The keyboard opens verbaflow://listen when the app is not already listening for it.
                .onOpenURL { url in
                    if url.scheme == "verbaflow" { controller.openedFromKeyboard() }
                }
        }
    }
}
