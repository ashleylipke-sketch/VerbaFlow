import Foundation

/// A plain sentence for the person, written by VerbaFlow or by the app. Never technical detail.
struct PlainError: LocalizedError {
    let message: String
    /// The person must sign in (again) before VerbaFlow can be used.
    var signIn = false
    var errorDescription: String? { message }

    static let unreachable = PlainError(message: "VerbaFlow cannot be reached. Check the internet connection, and VerbaFlow's address in the Widget's settings.")
    static let unexpected = PlainError(message: "Something went wrong in VerbaFlow.")
}

struct SignInInfo: Decodable {
    struct Microsoft: Decodable { let tenantId: String; let clientId: String; let scope: String; let authority: String }
    let microsoft: Microsoft?
    let testUsers: Bool
    /// Google and other personal accounts can sign in (through the VerbaFlow website).
    let personalAccounts: Bool?
}

struct WidgetLink: Decodable { let code: String; let secret: String; let expiresInSeconds: Int }

struct WidgetStart: Decodable { let id: String; let liveId: String; let ticket: String; let engine: String }
struct SpokenLanguage: Decodable, Identifiable, Hashable { let code: String; let name: String; let supported: Bool; var id: String { code } }
struct GrammarFix: Decodable { let text: String; let linesChanged: Int; let leftOutAsRewrites: Int }

/// The calls the Widget makes to VerbaFlow: the same as the browser Widget's and the Widget for Windows'.
final class Api {
    let server: URL
    private let signIn: SignIn
    private var info: (SignInInfo, Date)?

    init(server: URL, signIn: SignIn) { self.server = server; self.signIn = signIn }

    /// How this VerbaFlow signs people in (public details), remembered for ten minutes.
    func signInInfo() async throws -> SignInInfo {
        if let (i, at) = info, Date().timeIntervalSince(at) < 600 { return i }
        do {
            let (data, res) = try await URLSession.shared.data(from: server.appendingPathComponent("api/sign-in"))
            guard (res as? HTTPURLResponse)?.statusCode == 200 else { throw PlainError.unreachable }
            let i = try JSONDecoder().decode(SignInInfo.self, from: data)
            info = (i, Date())
            return i
        } catch let e as PlainError { throw e } catch { throw PlainError.unreachable }
    }

    /// Starts linking this app to a personal account: the person agrees to the code on the VerbaFlow website.
    func startLink(device: String) async throws -> WidgetLink {
        let (code, data) = try await post("api/widget-link/start", ["device": device], bearer: nil)
        guard (200..<300).contains(code) else { throw PlainError(message: said(data) ?? PlainError.unexpected.message) }
        return try decode(data)
    }

    /// The sign-in once the code has been agreed on the website, or nil while still waiting.
    func collectLink(secret: String) async throws -> String? {
        let (code, data) = try await post("api/widget-link/poll", ["secret": secret], bearer: nil)
        if code == 202 { return nil }
        guard (200..<300).contains(code) else { throw PlainError(message: said(data) ?? PlainError.unexpected.message) }
        struct T: Decodable { let token: String }
        return (try decode(data) as T).token
    }

    /// The website page where the person agrees to the code.
    func linkPage(code: String) -> URL {
        var allowed = CharacterSet.alphanumerics; allowed.insert(charactersIn: "-")
        let base = server.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return URL(string: "\(base)/#/link-widget?code=\(code.addingPercentEncoding(withAllowedCharacters: allowed) ?? code)")!
    }

    private func post(_ path: String, _ body: [String: Any]?, bearer: String?) async throws -> (Int, Data) {
        var req = URLRequest(url: server.appendingPathComponent(path))
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try JSONSerialization.data(withJSONObject: body ?? [:])
        if let bearer { req.setValue("Bearer \(bearer)", forHTTPHeaderField: "Authorization") }
        do {
            let (data, res) = try await URLSession.shared.data(for: req)
            return ((res as? HTTPURLResponse)?.statusCode ?? 0, data)
        } catch { throw PlainError.unreachable }
    }

    private func said(_ data: Data) -> String? {
        ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["error"] as? String
    }

    /// The linked sign-in for this VerbaFlow (it lasts 30 days), renewed once a day; offline, the old one is kept.
    private func linkedHeader() async throws -> (name: String, value: String)? {
        guard let l = signIn.linked(for: server) else { return nil }
        if Date().timeIntervalSince(l.renewed) > 86_400, let r = try? await post("api/sign-in/renew", nil, bearer: l.token) {
            let (code, data) = r
            if code == 401 {
                signIn.forget()
                throw PlainError(message: "Your sign-in has run out. Open the Widget's settings and sign in again.", signIn: true)
            }
            struct T: Decodable { let token: String }
            if (200..<300).contains(code), let t = try? JSONDecoder().decode(T.self, from: data) {
                signIn.keepLinked(server, token: t.token)
                return ("Authorization", "Bearer \(t.token)")
            }
        }
        return ("Authorization", "Bearer \(l.token)")
    }

    func me() async throws -> String {
        let d = try await send("GET", "api/me", nil)
        return ((try? JSONSerialization.jsonObject(with: d)) as? [String: Any])?["name"] as? String ?? ""
    }

    func start(site: String, device: String, languages: String) async throws -> WidgetStart {
        try decode(try await send("POST", "api/widget/start", ["site": site, "device": device, "spokenLanguages": languages]))
    }

    func listenAgain(_ id: String) async throws -> WidgetStart { try decode(try await send("POST", "api/widget/\(id)/listen-again", nil)) }

    func end(_ id: String, accepted: Bool, spokenMs: Int?, words: Int?, reconnects: Int?) async throws {
        var body: [String: Any] = ["outcome": accepted ? "accepted" : "cancelled"]
        if let spokenMs { body["spokenMs"] = spokenMs }
        if let words { body["words"] = words }
        if let reconnects { body["reconnects"] = reconnects }
        _ = try await send("POST", "api/widget/\(id)/end", body)
    }

    func fixGrammar(_ id: String, text: String) async throws -> GrammarFix { try decode(try await send("POST", "api/widget/\(id)/grammar", ["text": text])) }

    func languages() async throws -> [SpokenLanguage] {
        struct List: Decodable { let languages: [SpokenLanguage] }
        return (try decode(try await send("GET", "api/languages", nil)) as List).languages
    }

    /// A new single-use ticket for the live connection (after a lost connection).
    func ticket(_ liveId: String) async throws -> String {
        struct T: Decodable { let ticket: String }
        return (try decode(try await send("POST", "api/live/\(liveId)/ticket", nil)) as T).ticket
    }

    func socketURL(_ liveId: String, ticket: String, fromMs: Int) -> URL {
        var c = URLComponents(url: server.appendingPathComponent("api/live/\(liveId)/socket"), resolvingAgainstBaseURL: false)!
        c.scheme = server.scheme == "https" ? "wss" : "ws"
        c.queryItems = [URLQueryItem(name: "ticket", value: ticket), URLQueryItem(name: "fromMs", value: String(fromMs))]
        return c.url!
    }

    private func decode<T: Decodable>(_ data: Data) throws -> T {
        do { return try JSONDecoder().decode(T.self, from: data) } catch { throw PlainError.unexpected }
    }

    private func send(_ method: String, _ path: String, _ body: [String: Any]?) async throws -> Data {
        let header: (name: String, value: String)
        if let linked = try await linkedHeader() { header = linked }
        else { header = try await signIn.header(try await signInInfo()) }
        var req = URLRequest(url: server.appendingPathComponent(path))
        req.httpMethod = method
        req.setValue(header.value, forHTTPHeaderField: header.name)
        if let body {
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try JSONSerialization.data(withJSONObject: body)
        }
        let data: Data, res: URLResponse
        do { (data, res) = try await URLSession.shared.data(for: req) } catch { throw PlainError.unreachable }
        let code = (res as? HTTPURLResponse)?.statusCode ?? 0
        if code == 401 {
            signIn.forget()
            throw PlainError(message: "Your sign-in has run out. Open the Widget's settings and sign in again.", signIn: true)
        }
        guard (200..<300).contains(code) else {
            let said = ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["error"] as? String
            throw PlainError(message: said ?? PlainError.unexpected.message)
        }
        return data
    }
}
