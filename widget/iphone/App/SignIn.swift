import AuthenticationServices
import CryptoKit
import Foundation
import Security
import UIKit

/// Signs in with a Microsoft work account (authorisation code with PKCE, in Apple's secure sign-in sheet), or uses a
/// test user when this VerbaFlow allows them (the owner's own computer only). Tokens are kept in this iPhone's
/// keychain, readable only by this app, and never leave it except to Microsoft and VerbaFlow.
final class SignIn: NSObject, ASWebAuthenticationPresentationContextProviding {
    struct Tokens: Codable { var access: String; var refresh: String?; var expires: Date }

    /// The redirect registered in Microsoft Entra under "iOS / macOS" for this app's bundle ID.
    static var redirect: String { "msauth.\(Bundle.main.bundleIdentifier ?? "net.verbaflow.speak")://auth" }
    private static let keychainItem = "microsoft-sign-in"
    /// Kept while the sign-in sheet is open; Apple closes the sheet if nothing holds on to it.
    private var session: ASWebAuthenticationSession?

    var testUser: String {
        get { UserDefaults.standard.string(forKey: "testUser") ?? "alice" }
        set { UserDefaults.standard.set(newValue.trimmingCharacters(in: .whitespaces).lowercased(), forKey: "testUser") }
    }
    var isSignedIn: Bool { Self.load() != nil }

    /// The header to send: a Microsoft token, or X-Dev-User for a test user.
    func header(_ info: SignInInfo) async throws -> (name: String, value: String) {
        if let ms = info.microsoft, var t = Self.load() {
            if t.expires.timeIntervalSinceNow < 120 {
                guard let refresh = t.refresh else { forget(); throw needed }
                t = try await token(ms, ["grant_type": "refresh_token", "refresh_token": refresh])
            }
            return ("Authorization", "Bearer \(t.access)")
        }
        if info.testUsers { return ("X-Dev-User", testUser) }
        throw needed
    }

    private var needed: PlainError { PlainError(message: "Open the VerbaFlow app and sign in first.", signIn: true) }

    func forget() { Self.save(nil) }

    /// Opens Microsoft's sign-in page in Apple's secure sheet and keeps the tokens that come back.
    @MainActor func signIn(_ ms: SignInInfo.Microsoft) async throws {
        let verifier = Self.random()
        let challenge = Data(SHA256.hash(data: Data(verifier.utf8))).base64URL
        let state = Self.random()
        var c = URLComponents(string: "\(ms.authority)/oauth2/v2.0/authorize")!
        c.queryItems = [
            .init(name: "client_id", value: ms.clientId), .init(name: "response_type", value: "code"),
            .init(name: "redirect_uri", value: Self.redirect), .init(name: "response_mode", value: "query"),
            .init(name: "scope", value: "\(ms.scope) offline_access openid profile"),
            .init(name: "code_challenge", value: challenge), .init(name: "code_challenge_method", value: "S256"),
            .init(name: "state", value: state), .init(name: "prompt", value: "select_account"),
        ]
        let scheme = String(Self.redirect.split(separator: ":")[0])
        let back: URL = try await withCheckedThrowingContinuation { done in
            let s = ASWebAuthenticationSession(url: c.url!, callbackURLScheme: scheme) { url, error in
                if let url { done.resume(returning: url) } else { done.resume(throwing: error ?? PlainError(message: "Sign-in did not finish.")) }
            }
            s.presentationContextProvider = self
            s.prefersEphemeralWebBrowserSession = false
            session = s
            if !s.start() { done.resume(throwing: PlainError(message: "Sign-in could not be opened. Try again.")) }
        }
        session = nil
        let items = URLComponents(url: back, resolvingAgainstBaseURL: false)?.queryItems ?? []
        guard items.first(where: { $0.name == "state" })?.value == state,
              let code = items.first(where: { $0.name == "code" })?.value else {
            throw PlainError(message: "Microsoft did not sign you in. Try again, or ask your administrator.")
        }
        _ = try await token(ms, ["grant_type": "authorization_code", "code": code, "code_verifier": verifier])
    }

    private func token(_ ms: SignInInfo.Microsoft, _ fields: [String: String]) async throws -> Tokens {
        var req = URLRequest(url: URL(string: "\(ms.authority)/oauth2/v2.0/token")!)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        var all = fields
        all["client_id"] = ms.clientId
        all["redirect_uri"] = Self.redirect
        all["scope"] = "\(ms.scope) offline_access openid profile"
        var allowed = CharacterSet.alphanumerics; allowed.insert(charactersIn: "-._~")
        req.httpBody = all.map { "\($0.key)=\($0.value.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")" }.joined(separator: "&").data(using: .utf8)
        struct Reply: Decodable { let access_token: String?; let refresh_token: String?; let expires_in: Int? }
        let data: Data
        do { (data, _) = try await URLSession.shared.data(for: req) } catch { throw PlainError.unreachable }
        guard let r = try? JSONDecoder().decode(Reply.self, from: data), let access = r.access_token else {
            forget()
            throw PlainError(message: "Microsoft did not sign you in. Open the VerbaFlow app and sign in again.", signIn: true)
        }
        let t = Tokens(access: access, refresh: r.refresh_token ?? Self.load()?.refresh, expires: Date().addingTimeInterval(TimeInterval(r.expires_in ?? 3600)))
        Self.save(t)
        return t
    }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        UIApplication.shared.connectedScenes.compactMap { ($0 as? UIWindowScene)?.keyWindow }.first ?? ASPresentationAnchor()
    }

    private static func random() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64URL
    }

    private static func query() -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "VerbaFlow", kSecAttrAccount as String: keychainItem]
    }

    private static func load() -> Tokens? {
        var q = query()
        q[kSecReturnData as String] = true
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let d = out as? Data else { return nil }
        return try? JSONDecoder().decode(Tokens.self, from: d)
    }

    private static func save(_ t: Tokens?) {
        SecItemDelete(query() as CFDictionary)
        guard let t, let d = try? JSONEncoder().encode(t) else { return }
        var q = query()
        q[kSecValueData as String] = d
        q[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(q as CFDictionary, nil)
    }
}

extension Data {
    var base64URL: String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
}
