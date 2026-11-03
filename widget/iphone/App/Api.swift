import Foundation

/// A plain sentence for the person, written by VerbaFlow or by the app. Never technical detail.
struct PlainError: LocalizedError {
    let message: String
    /// The person must sign in (again) before VerbaFlow can be used.
    var signIn = false
    var errorDescription: String? { message }

    static let unreachable = PlainError(message: "VerbaFlow cannot be reached. Check the internet connection, and the VerbaFlow address in the VerbaFlow app.")
    static let unexpected = PlainError(message: "Something went wrong in VerbaFlow.")
}

struct SignInInfo: Decodable {
    struct Microsoft: Decodable { let tenantId: String; let clientId: String; let scope: String; let authority: String }
    let microsoft: Microsoft?
    let testUsers: Bool
}

struct WidgetStart: Decodable { let id: String; let liveId: String; let ticket: String; let engine: String }
struct SpokenLanguage: Decodable, Identifiable, Hashable { let code: String; let name: String; let supported: Bool; var id: String { code } }
struct GrammarFix: Decodable { let text: String; let linesChanged: Int; let leftOutAsRewrites: Int }

/// The calls the keyboard's listening makes to VerbaFlow: the same as the browser Widget's and the Widget for Windows'.
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
        let i = try await signInInfo()
        let header = try await signIn.header(i)
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
            throw PlainError(message: "Your sign-in has run out. Open the VerbaFlow app and sign in again.", signIn: true)
        }
        guard (200..<300).contains(code) else {
            let said = ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["error"] as? String
            throw PlainError(message: said ?? PlainError.unexpected.message)
        }
        return data
    }
}
