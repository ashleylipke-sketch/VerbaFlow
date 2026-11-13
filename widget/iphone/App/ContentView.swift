import SwiftUI
import VerbaFlowShared

/// The VerbaFlow app: where VerbaFlow is, signing in, the language spoken, and how to switch the keyboard on.
/// While it listens for the keyboard it says so, and how to go back.
struct ContentView: View {
    @EnvironmentObject private var c: Controller
    @Environment(\.openURL) private var openURL
    @State private var who: String?
    @State private var info: SignInInfo?
    @State private var problem: String?
    @State private var busy = false
    @State private var choices: [SpokenLanguage] = []
    @State private var tryIt = ""
    @State private var linkCode: String?

    var body: some View {
        NavigationStack {
            Form {
                if [.starting, .listening, .finishing].contains(c.status.phase) || c.listeningForKeyboard && c.status.phase != .idle {
                    Section {
                        Label(c.status.phase == .listening ? "Listening for the keyboard" : "Getting ready to listen", systemImage: "mic.fill")
                            .foregroundStyle(.red)
                        Text("Go back to the app you were typing in: tap ◀ at the top left of the screen, or swipe along the bottom edge. Your words appear in the VerbaFlow keyboard there.")
                            .font(.callout)
                        if !c.status.text.isEmpty { Text(c.status.text).font(.callout).foregroundStyle(.secondary).lineLimit(4) }
                    }
                }
                if let m = c.status.message, c.status.phase == .failed {
                    Section { Text(m).foregroundStyle(.red) }
                }

                Section {
                    TextField("For example verbaflow.example.com", text: $c.server)
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                        .onSubmit { Task { await refresh() } }
                } header: { Text("VerbaFlow's address") } footer: {
                    Text("The web address you open VerbaFlow at. It must start with https.")
                }

                Section("Sign in") {
                    if let who { Text(c.signIn.isLinked ? "Signed in as \(who) (personal account)" : "Signed in as \(who)") }
                    if c.signIn.isLinked {
                        Button("Sign out") { c.signIn.forget(); who = nil; Task { await refresh() } }
                    } else if let ms = info?.microsoft {
                        if c.signIn.isSignedIn {
                            Button("Sign out") { c.signIn.forget(); who = nil; Task { await refresh() } }
                        } else {
                            Button(busy ? "Signing in…" : "Sign in with Microsoft") { Task { await signIn(ms) } }.disabled(busy)
                        }
                    }
                    if info?.testUsers == true && !c.signIn.isSignedIn {
                        TextField("Test user", text: Binding(get: { c.signIn.testUser }, set: { c.signIn.testUser = $0 }))
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                            .onSubmit { Task { await refresh() } }
                        Text("Test users work only on the VerbaFlow on your own computer.").font(.footnote).foregroundStyle(.secondary)
                    }
                    if info?.personalAccounts == true && !c.signIn.isSignedIn {
                        Button(linkCode == nil ? "Google or another personal account" : "Waiting for you on the website…") { Task { await link() } }
                            .disabled(linkCode != nil)
                        if let linkCode {
                            Text("Your code is \(linkCode). Sign in on the VerbaFlow website that has just opened and agree to this code there, then come back here.")
                                .font(.callout)
                        }
                    }
                    if info == nil { Button("Check the address") { Task { await refresh() } } }
                    if let problem { Text(problem).foregroundStyle(.red).font(.callout) }
                }

                Section("Language you speak") {
                    if choices.isEmpty { Text(c.languages) }
                    else {
                        Picker("Language", selection: $c.languages) {
                            ForEach(choices.filter { $0.supported && $0.live != false }) { Text($0.name).tag($0.code) }
                        }
                    }
                }

                Section {
                    Text("1. Open Settings, General, Keyboard, Keyboards, Add New Keyboard, and choose VerbaFlow.")
                    Text("2. Tap VerbaFlow in that list and turn on Allow Full Access. The keyboard needs it to hear from this app; it sends nothing anywhere itself.")
                    Text("3. In any app, hold the 🌐 key and choose VerbaFlow. Tap the microphone and speak.")
                    Text("The first time, this app opens to start listening: go back with ◀ at the top left. For five minutes after that, the keyboard starts listening straight away (the orange dot shows the microphone is ready).")
                        .font(.footnote).foregroundStyle(.secondary)
                    Button("Open Settings") { if let u = URL(string: UIApplication.openSettingsURLString) { openURL(u) } }
                } header: { Text("Switch the keyboard on") }

                Section("Try it here") {
                    TextField("Tap here, switch to the VerbaFlow keyboard and speak", text: $tryIt, axis: .vertical).lineLimit(3...8)
                }
            }
            .navigationTitle("VerbaFlow")
            .task { await refresh() }
        }
    }

    private func refresh() async {
        problem = nil
        do {
            let api = try c.api()
            let i = try await api.signInInfo()
            info = i
            if c.signIn.isSignedIn || i.testUsers { who = try await api.me() } else { who = nil }
            if choices.isEmpty { choices = (try? await api.languages()) ?? [] }
        } catch {
            if (error as? PlainError)?.signIn != true || info == nil { info = nil }
            problem = (error as? PlainError)?.message ?? PlainError.unexpected.message
            who = nil
        }
    }

    /// Personal accounts (Google and others): the website shows the same code, the person agrees there, and the
    /// app collects VerbaFlow's own sign-in. The code lasts 10 minutes.
    private func link() async {
        problem = nil
        do {
            let api = try c.api()
            let l = try await api.startLink(device: UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone")
            linkCode = l.code
            openURL(api.linkPage(code: l.code))
            let until = Date().addingTimeInterval(TimeInterval(l.expiresInSeconds))
            while Date() < until {
                try await Task.sleep(nanoseconds: 2_000_000_000)
                if let token = try await api.collectLink(secret: l.secret) {
                    c.signIn.keepLinked(api.server, token: token)
                    linkCode = nil
                    await refresh()
                    return
                }
            }
            problem = "The code ran out before it was agreed on the website. Try again."
        } catch {
            problem = (error as? PlainError)?.message ?? PlainError.unexpected.message
        }
        linkCode = nil
    }

    private func signIn(_ ms: SignInInfo.Microsoft) async {
        busy = true
        defer { busy = false }
        do { try await c.signIn.signIn(ms); await refresh() }
        catch { problem = (error as? PlainError)?.message ?? "Sign-in did not finish." }
    }
}
