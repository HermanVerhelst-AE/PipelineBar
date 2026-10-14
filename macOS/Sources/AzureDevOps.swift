import Foundation

enum ADOError: LocalizedError {
    case unauthorized, notFound, http(Int)

    var errorDescription: String? {
        switch self {
        case .unauthorized: return "Token ongeldig, verlopen of zonder leesrechten."
        case .notFound: return "Niet gevonden. Controleer de naam van organisatie of project."
        case .http(let code): return "Azure DevOps gaf foutcode \(code)."
        }
    }
}

// MARK: - API-antwoorden

private struct ADOList<T: Decodable>: Decodable { let value: [T] }
private struct ADOProject: Decodable { let name: String }
private struct ADOLink: Decodable { let href: String }
private struct ADOLinks: Decodable { let web: ADOLink? }
private struct ADOIdentity: Decodable { let id: String?; let displayName: String? }
private struct ADOConnectionData: Decodable { let authenticatedUser: ADOIdentity }

private struct ADOBuild: Decodable {
    struct Definition: Decodable { let id: Int; let name: String }
    let id: Int
    let status: String?
    let result: String?
    let definition: Definition
    let buildNumber: String?
    let sourceBranch: String?
    /// Bij een trigger vanuit een andere repo staat hier de branch die de run startte.
    let triggerInfo: [String: String]?
    let requestedFor: ADOIdentity?
    let queueTime: Date?
    let startTime: Date?
    let finishTime: Date?
    let _links: ADOLinks?
}

private struct ADODeployment: Decodable {
    struct Ref: Decodable { let id: Int?; let name: String? }
    struct Release: Decodable { let name: String?; let _links: ADOLinks? }
    let id: Int
    let deploymentStatus: String?
    let operationStatus: String?
    let definitionEnvironmentId: Int?
    let releaseDefinition: Ref?
    let releaseEnvironment: Ref?
    let release: Release?
    let requestedFor: ADOIdentity?
    let queuedOn: Date?
    let startedOn: Date?
    let completedOn: Date?
}

// MARK: - Client

struct AzureDevOpsClient {
    let organization: String
    let token: String
    let maxAgeDays: Int      // 0 = enkel vandaag
    /// Identiteits-id; indien gezet enkel runs die deze gebruiker startte.
    var requestedFor: String? = nil

    private static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        // Azure geeft tot 7 decimalen; enkel de seconden zijn nodig.
        d.dateDecodingStrategy = .custom { dec in
            let c = try dec.singleValueContainer()
            let s = try c.decode(String.self)
            if let date = ISO8601DateFormatter().date(from: String(s.prefix(19)) + "Z") { return date }
            throw DecodingError.dataCorruptedError(in: c, debugDescription: "Ongeldige datum: \(s)")
        }
        return d
    }()

    private var since: String {
        let start = maxAgeDays == 0
            ? Calendar.current.startOfDay(for: Date())
            : Date().addingTimeInterval(-Double(maxAgeDays) * 86_400)
        return ISO8601DateFormatter().string(from: start)
    }

    private func esc(_ s: String) -> String {
        s.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(CharacterSet(charactersIn: "/"))) ?? s
    }

    private func get<T: Decodable>(_ base: String, _ path: String, _ query: [String: String],
                                   apiVersion: String = "7.1") async throws -> T {
        var comps = URLComponents(string: base + path)!
        comps.queryItems = query.map { URLQueryItem(name: $0.key, value: $0.value) }
            + [URLQueryItem(name: "api-version", value: apiVersion)]
        var req = URLRequest(url: comps.url!, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        req.setValue("Basic " + Data(":\(token)".utf8).base64EncodedString(), forHTTPHeaderField: "Authorization")
        req.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await URLSession.shared.data(for: req)
        guard let http = response as? HTTPURLResponse else { throw ADOError.http(0) }
        switch http.statusCode {
        case 200..<300: break
        case 401, 403: throw ADOError.unauthorized
        case 404: throw ADOError.notFound
        default: throw ADOError.http(http.statusCode)
        }
        // Een ongeldig token geeft soms 203 met een HTML-aanmeldpagina.
        guard (http.value(forHTTPHeaderField: "Content-Type") ?? "").contains("json") else {
            throw ADOError.unauthorized
        }
        return try Self.decoder.decode(T.self, from: data)
    }

    private var devBase: String { "https://dev.azure.com/\(esc(organization))" }
    private var vsrmBase: String { "https://vsrm.dev.azure.com/\(esc(organization))" }

    func projects() async throws -> [String] {
        let list: ADOList<ADOProject> = try await get(devBase, "/_apis/projects", ["$top": "500"])
        return list.value.map(\.name).sorted()
    }

    /// Id van de gebruiker achter het token.
    func currentUserId() async throws -> String {
        let data: ADOConnectionData = try await get(devBase, "/_apis/connectionData", [:], apiVersion: "7.1-preview")
        guard let id = data.authenticatedUser.id else { throw ADOError.unauthorized }
        return id
    }

    private func isMine(_ who: ADOIdentity?) -> Bool {
        guard let requestedFor else { return true }
        return who?.id?.caseInsensitiveCompare(requestedFor) == .orderedSame
    }

    func builds(project: String) async throws -> [PipelineRun] {
        var query = [
            "queryOrder": "queueTimeDescending",
            "$top": "1000",
            "minTime": since,
        ]
        if let requestedFor { query["requestedFor"] = requestedFor }
        let list: ADOList<ADOBuild> = try await get(devBase, "/\(esc(project))/_apis/build/builds", query)
        var seen = Set<String>()
        var runs: [PipelineRun] = []
        for b in list.value where isMine(b.requestedFor) {
            let branchRef = b.triggerInfo?["ci.sourceBranch"] ?? b.sourceBranch ?? ""
            let key = "build:\(project):\(b.definition.id):\(branchRef)"
            guard seen.insert(key).inserted else { continue }   // nieuwste eerst

            let completed = b.status == "completed"
            let state: RunState = completed
                ? RunState(rawValue: b.result ?? "") ?? .none
                : RunState(rawValue: b.status ?? "") ?? .none
            runs.append(PipelineRun(
                key: key,
                runId: b.id,
                project: project,
                name: b.definition.name,
                kind: .build,
                state: state,
                number: b.buildNumber ?? "",
                branch: Self.shortBranch(branchRef),
                requestedBy: b.requestedFor?.displayName ?? "",
                date: completed ? b.finishTime : (b.startTime ?? b.queueTime),
                url: b._links?.web.flatMap { URL(string: $0.href) }
            ))
        }
        return runs
    }

    func deployments(project: String) async throws -> [PipelineRun] {
        let list: ADOList<ADODeployment> = try await get(vsrmBase, "/\(esc(project))/_apis/release/deployments", [
            "queryOrder": "descending",
            "$top": "200",
            "minStartedTime": since,
        ])
        var seen = Set<String>()
        var runs: [PipelineRun] = []
        for d in list.value where isMine(d.requestedFor) {
            let envKey = d.definitionEnvironmentId.map(String.init) ?? d.releaseEnvironment?.name ?? "?"
            let key = "release:\(project):\(d.releaseDefinition?.id ?? 0):\(envKey)"
            guard seen.insert(key).inserted else { continue }   // nieuwste eerst

            let state: RunState
            switch (d.deploymentStatus ?? "", d.operationStatus ?? "") {
            case ("inProgress", "Pending"): state = .waiting
            case ("inProgress", "Queued"), ("inProgress", "QueuedForAgent"),
                 ("inProgress", "Scheduled"), ("inProgress", "Deferred"): state = .notStarted
            case ("inProgress", _): state = .inProgress
            case ("succeeded", _): state = .succeeded
            case ("failed", _): state = .failed
            case ("partiallySucceeded", _): state = .partiallySucceeded
            case ("notDeployed", _): state = .canceled
            default: state = .none
            }

            runs.append(PipelineRun(
                key: key,
                runId: d.id,
                project: project,
                name: "\(d.releaseDefinition?.name ?? "?") → \(d.releaseEnvironment?.name ?? "?")",
                kind: .release,
                state: state,
                number: d.release?.name ?? "",
                branch: "",
                requestedBy: d.requestedFor?.displayName ?? "",
                date: d.completedOn ?? d.startedOn ?? d.queuedOn,
                url: d.release?._links?.web.flatMap { URL(string: $0.href) }
            ))
        }
        return runs
    }

    static func shortBranch(_ ref: String?) -> String {
        guard let ref else { return "" }
        if ref.hasPrefix("refs/pull/") {
            let num = ref.dropFirst("refs/pull/".count).split(separator: "/").first ?? ""
            return "PR \(num)"
        }
        for prefix in ["refs/heads/", "refs/tags/"] where ref.hasPrefix(prefix) {
            return String(ref.dropFirst(prefix.count))
        }
        return ref
    }
}
