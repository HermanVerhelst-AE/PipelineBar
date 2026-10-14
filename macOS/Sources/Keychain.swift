import Foundation
import Security

/// Bewaart het Personal Access Token in de macOS-sleutelhanger, per organisatie.
/// Elke leesactie kan een toegangsvraag van macOS geven, dus we lezen één keer per start.
@MainActor
enum Keychain {
    static let service = "be.ae.pipelinebar"
    private static var cache: [String: String] = [:]

    private static func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    static func read(account: String) -> String? {
        if let token = cache[account] { return token }
        var q = query(account)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess,
              let data = out as? Data,
              let token = String(data: data, encoding: .utf8) else { return nil }
        cache[account] = token
        return token
    }

    @discardableResult
    static func save(_ token: String, account: String) -> Bool {
        delete(account: account)
        var q = query(account)
        q[kSecValueData as String] = Data(token.utf8)
        q[kSecAttrLabel as String] = "PipelineBar (Azure DevOps)"
        guard SecItemAdd(q as CFDictionary, nil) == errSecSuccess else { return false }
        cache[account] = token
        return true
    }

    static func delete(account: String) {
        cache[account] = nil
        SecItemDelete(query(account) as CFDictionary)
    }
}
