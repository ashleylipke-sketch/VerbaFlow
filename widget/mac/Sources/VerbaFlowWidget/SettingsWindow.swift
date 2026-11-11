import AppKit
import ServiceManagement
import SwiftUI
import WidgetMacCore

/// What the settings window shows and does: VerbaFlow's address, signing in, the language, paragraphs, the two macOS
/// permissions the Widget needs, and starting when the person logs in.
@MainActor
final class SettingsModel: ObservableObject {
    @Published var server: String
    @Published var languages: String
    @Published var gap: ParagraphGap
    @Published var leaveBrowsers: Bool
    @Published var testUser: String
    @Published var status = ""
    @Published var statusBad = false
    @Published var signedIn = false
    @Published var canMicrosoft = false
    @Published var canLink = false
    @Published var testUsers = false
    @Published var linkCode: String?
    @Published var available: [SpokenLanguage] = []
    @Published var microphone = Microphone.permitted
    @Published var accessibility = FocusTarget.trusted
    @Published var startAtLogin = SMAppService.mainApp.status == .enabled
    @Published var loginNote = ""
    @Published var working = false

    private let app: AppDelegate
    private var linking: Task<Void, Never>?

    init(_ app: AppDelegate) {
        self.app = app
        let s = app.settings
        server = s.server; languages = s.languages; gap = s.gap; leaveBrowsers = s.leaveBrowsersToExtension; testUser = s.testUser
    }

    private var current: WidgetSettings {
        var s = WidgetSettings()
        s.server = MacText.cleanServer(server)?.absoluteString ?? server
        s.languages = languages; s.gap = gap; s.leaveBrowsersToExtension = leaveBrowsers; s.testUser = testUser
        return s
    }

    func save() { app.save(current) }

    /// Saves, then asks VerbaFlow how it signs people in and who is signed in now.
    func check() async {
        guard MacText.cleanServer(server) != nil else {
            status = "Type VerbaFlow's address, for example http://localhost:5044."; statusBad = true; return
        }
        save()
        refreshPermissions()
        working = true
        defer { working = false }
        do {
            let api = try app.api()
            let info = try await api.signInInfo()
            canMicrosoft = info.microsoft != nil
            canLink = info.personalAccounts == true
            testUsers = info.testUsers
            if available.isEmpty, let l = try? await api.languages() { available = l.filter { $0.supported } }
            if !app.signIn.isSignedIn && !info.testUsers {
                signedIn = false
                status = canLink && canMicrosoft ? "Sign in with your Microsoft account, or through the VerbaFlow website for a Google or email sign-in."
                    : canMicrosoft ? "Sign in with your Microsoft account." : "Sign in through the VerbaFlow website."
                statusBad = false
                return
            }
            let name = try await api.me()
            signedIn = app.signIn.isSignedIn
            status = app.signIn.isLinked ? "Signed in through the VerbaFlow website as \(name)."
                : app.signIn.isMicrosoft ? "Signed in with Microsoft as \(name)." : "Using the test user \(name) (this computer only)."
            statusBad = false
        } catch {
            signedIn = app.signIn.isSignedIn
            status = (error as? PlainError)?.message ?? PlainError.unexpected.message
            statusBad = true
        }
    }

    func signInWithMicrosoft() async {
        do {
            let api = try app.api()
            guard let ms = try await api.signInInfo().microsoft else { return }
            try await app.signIn.signIn(ms)
        } catch {
            status = (error as? PlainError)?.message ?? "The Microsoft sign-in did not finish. Try again."
            statusBad = true
            return
        }
        await check()
    }

    /// Google, email and other personal accounts: the person agrees to a code on the VerbaFlow website.
    func linkThroughWebsite() {
        linking?.cancel()
        linking = Task {
            do {
                let api = try app.api()
                let link = try await api.startLink(device: "Mac Widget")
                linkCode = link.code
                NSWorkspace.shared.open(api.linkPage(code: link.code))
                let until = Date().addingTimeInterval(TimeInterval(link.expiresInSeconds))
                while Date() < until {
                    try await Task.sleep(nanoseconds: 2_000_000_000)
                    if let token = try await api.collectLink(secret: link.secret) {
                        app.signIn.keepLinked(api.server, token: token)
                        linkCode = nil
                        await check()
                        return
                    }
                }
                linkCode = nil
                status = "The code ran out before it was agreed on the website. Try again."; statusBad = true
            } catch is CancellationError {
                linkCode = nil
            } catch {
                linkCode = nil
                status = (error as? PlainError)?.message ?? PlainError.unexpected.message; statusBad = true
            }
        }
    }

    func signOut() async {
        linking?.cancel()
        app.signIn.forget()
        signedIn = false
        await check()
    }

    func refreshPermissions() {
        microphone = Microphone.permitted
        accessibility = FocusTarget.trusted
        startAtLogin = SMAppService.mainApp.status == .enabled
    }

    func askMicrophone() async {
        if Microphone.answered { open("Privacy_Microphone") } else { _ = await Microphone.allowed() }
        refreshPermissions()
    }

    func askAccessibility() {
        FocusTarget.askForTrust()
        open("Privacy_Accessibility")
    }

    private func open(_ pane: String) {
        if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") { NSWorkspace.shared.open(u) }
    }

    func setStartAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            loginNote = ""
        } catch {
            loginNote = "macOS did not allow it. Move the Widget into the Applications folder first, or add it under System Settings, General, Login Items."
        }
        startAtLogin = SMAppService.mainApp.status == .enabled
    }
}

struct SettingsView: View {
    @ObservedObject var model: SettingsModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 8) {
                Text("Click where you want the words in any program (Mail, Outlook, Word, WhatsApp…), press ⌥⇧D (Option, Shift and D) and speak. Press ⌥⇧D again (or Stop) when you are done, check the words, then Accept.")
                    .foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)

                heading("1. VerbaFlow")
                Text("VerbaFlow's address. On the computer where VerbaFlow runs: http://localhost:5044.").foregroundColor(.secondary)
                TextField("http://localhost:5044", text: $model.server).textFieldStyle(.roundedBorder).frame(width: 340)
                HStack {
                    Button("Save and check") { Task { await model.check() } }.disabled(model.working)
                    if model.canMicrosoft && !model.signedIn { Button("Sign in with Microsoft") { Task { await model.signInWithMicrosoft() } } }
                    if model.canLink && !model.signedIn { Button("Sign in through the VerbaFlow website") { model.linkThroughWebsite() } }
                    if model.signedIn { Button("Sign out") { Task { await model.signOut() } } }
                }
                if let code = model.linkCode {
                    Text("Your browser opened VerbaFlow. Sign in there and agree to the code \(code). This window carries on by itself.")
                        .fixedSize(horizontal: false, vertical: true)
                }
                if !model.status.isEmpty {
                    Text(model.status).foregroundColor(model.statusBad ? .red : .primary).fixedSize(horizontal: false, vertical: true)
                }
                if model.testUsers && !model.signedIn {
                    Picker("Test user (this computer only)", selection: $model.testUser) {
                        ForEach(["alice", "bob", "carol", "dave"], id: \.self) { Text($0) }
                    }.frame(width: 340)
                }

                heading("2. The language you speak")
                if model.available.isEmpty {
                    TextField("en-GB", text: $model.languages).textFieldStyle(.roundedBorder).frame(width: 340)
                } else {
                    Picker("", selection: $model.languages) {
                        if !model.available.contains(where: { $0.code == model.languages }) { Text(model.languages).tag(model.languages) }
                        ForEach(model.available) { Text($0.name).tag($0.code) }
                    }.labelsHidden().frame(width: 340)
                }

                heading("3. New paragraphs")
                Text("Word and Pages put space after each paragraph themselves; most email and chat boxes do not, so an empty line is left there.")
                    .foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)
                Picker("", selection: $model.gap) {
                    Text("Automatic (an empty line, except in Word and Pages)").tag(ParagraphGap.automatic)
                    Text("Always an empty line between paragraphs").tag(ParagraphGap.emptyLine)
                    Text("Never an empty line, just a new line").tag(ParagraphGap.newLineOnly)
                }.labelsHidden().frame(width: 340)

                heading("4. What macOS must allow")
                permission("Microphone", ok: model.microphone, button: "Allow the microphone") { Task { await model.askMicrophone() } }
                permission("Accessibility (to type the words into other programs)", ok: model.accessibility, button: "Open Accessibility settings") { model.askAccessibility() }
                Text("In Accessibility, switch on VerbaFlow Widget. After an update of the Widget, macOS may ask again.")
                    .foregroundColor(.secondary).font(.caption).fixedSize(horizontal: false, vertical: true)

                heading("5. Other")
                Toggle("In Chrome and Edge, leave ⌥⇧D to the browser Widget", isOn: $model.leaveBrowsers)
                Toggle("Start the Widget when I log in to this Mac", isOn: Binding(get: { model.startAtLogin }, set: { model.setStartAtLogin($0) }))
                if !model.loginNote.isEmpty { Text(model.loginNote).foregroundColor(.red).fixedSize(horizontal: false, vertical: true) }

                HStack {
                    Button("Save") { model.save(); NSApp.keyWindow?.close() }.keyboardShortcut(.defaultAction)
                    Button("Close") { NSApp.keyWindow?.close() }
                }.padding(.top, 12)
            }
            .padding(20)
            .frame(width: 520, alignment: .leading)
        }
        .frame(width: 520, height: 640)
        .onAppear { model.refreshPermissions() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in model.refreshPermissions() }
    }

    private func heading(_ t: String) -> some View { Text(t).font(.headline).padding(.top, 10) }

    private func permission(_ name: String, ok: Bool, button: String, action: @escaping () -> Void) -> some View {
        HStack {
            Text(ok ? "✓" : "✗").foregroundColor(ok ? .green : .red).bold()
            Text(name)
            Spacer()
            if !ok { Button(button, action: action) }
        }.frame(width: 470)
    }
}
