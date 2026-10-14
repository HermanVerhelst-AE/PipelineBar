import Foundation

@MainActor
final class PipelineStore: ObservableObject {
    @Published private(set) var runs: [PipelineRun] = []
    @Published private(set) var errors: [String] = []
    @Published private(set) var problem: String?
    @Published private(set) var lastUpdated: Date?
    @Published private(set) var isRefreshing = false
    @Published private(set) var dismissed: [String: Dismissal] = [:]

    private var loop: Task<Void, Never>?
    private let defaults = UserDefaults.standard
    private let snapshotKey = "snapshot"
    private let dismissedKey = "dismissed"
    private var me: (organization: String, id: String)?

    init() {
        dismissed = defaults.data(forKey: dismissedKey)
            .flatMap { try? JSONDecoder().decode([String: Dismissal].self, from: $0) } ?? [:]
        restart()
    }

    // MARK: - Afgeleide waarden

    /// Runs die niet genegeerd zijn; enkel die bepalen het icoon en de tellers.
    var activeRuns: [PipelineRun] { runs.filter { !isDismissed($0) } }

    var failedCount: Int { activeRuns.filter { $0.state == .failed }.count }
    var runningCount: Int { activeRuns.filter { $0.state.isRunning }.count }
    var dismissibleCount: Int { activeRuns.filter { $0.state.needsAttention }.count }

    /// Samengevatte status voor het menubalkicoon; nil = niet geconfigureerd of fout.
    var overallState: RunState? {
        if problem != nil { return nil }
        if failedCount > 0 { return .failed }
        if activeRuns.contains(where: { $0.state == .waiting }) { return .waiting }
        if runningCount > 0 { return .inProgress }
        if activeRuns.contains(where: { $0.state == .partiallySucceeded }) { return .partiallySucceeded }
        return runs.isEmpty ? RunState.none : .succeeded
    }

    var badgeCount: Int? {
        if failedCount > 0 { return failedCount }
        if runningCount > 0 { return runningCount }
        return nil
    }

    var groups: [ProjectGroup] {
        Dictionary(grouping: runs, by: \.project)
            .map { ProjectGroup(project: $0.key, runs: $0.value.sorted {
                // Genegeerde runs onderaan.
                let a = (isDismissed($0) ? 1 : 0, $0.state.priority, $0.name.lowercased())
                let b = (isDismissed($1) ? 1 : 0, $1.state.priority, $1.name.lowercased())
                if a != b { return a < b }
                return ($0.date ?? .distantPast) > ($1.date ?? .distantPast)   // nieuwste branch eerst
            }) }
            .sorted { $0.project.localizedStandardCompare($1.project) == .orderedAscending }
    }

    // MARK: - Negeren

    func isDismissed(_ run: PipelineRun) -> Bool { dismissed[run.key]?.matches(run) == true }

    func dismiss(_ run: PipelineRun) {
        dismissed[run.key] = Dismissal(runId: run.runId, state: run.state)
        saveDismissed()
    }

    func undismiss(_ run: PipelineRun) {
        dismissed[run.key] = nil
        saveDismissed()
    }

    func dismissAll() {
        for run in activeRuns where run.state.needsAttention {
            dismissed[run.key] = Dismissal(runId: run.runId, state: run.state)
        }
        saveDismissed()
    }

    private func saveDismissed() {
        if let data = try? JSONEncoder().encode(dismissed) { defaults.set(data, forKey: dismissedKey) }
    }

    // MARK: - Verversen

    func restart() {
        loop?.cancel()
        me = nil   // token kan gewijzigd zijn
        loop = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh()
                let minutes = max(1, self?.defaults.integer(forKey: Prefs.refreshMinutes) ?? 1)
                try? await Task.sleep(nanoseconds: UInt64(minutes) * 60_000_000_000)
            }
        }
    }

    func refresh() async {
        guard !isRefreshing else { return }
        let org = (defaults.string(forKey: Prefs.organization) ?? "").trimmingCharacters(in: .whitespaces)
        guard !org.isEmpty else {
            problem = "Stel je Azure DevOps-organisatie in."
            runs = []
            return
        }
        guard let token = Keychain.read(account: org), !token.isEmpty else {
            problem = "Voeg een Personal Access Token toe voor \(org)."
            runs = []
            return
        }

        isRefreshing = true
        defer { isRefreshing = false }

        var client = AzureDevOpsClient(organization: org, token: token,
                                       maxAgeDays: max(0, defaults.integer(forKey: Prefs.maxAgeDays)))
        let includeReleases = defaults.bool(forKey: Prefs.includeReleases)

        do {
            if defaults.bool(forKey: Prefs.onlyMine) {
                if me?.organization != org { me = (org, try await client.currentUserId()) }
                client.requestedFor = me?.id
            }
            let client = client
            var projects = (defaults.string(forKey: Prefs.projects) ?? "")
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
            if projects.isEmpty { projects = try await client.projects() }

            var collected: [PipelineRun] = []
            var errs: [String] = []
            try await withThrowingTaskGroup(of: (String, Result<[PipelineRun], Error>).self) { group in
                for project in projects {
                    group.addTask {
                        do {
                            var result = try await client.builds(project: project)
                            if includeReleases {
                                // Geen klassieke releases of geen Release-rechten: builds tonen we toch.
                                result += (try? await client.deployments(project: project)) ?? []
                            }
                            return (project, .success(result))
                        } catch ADOError.unauthorized {
                            throw ADOError.unauthorized
                        } catch {
                            return (project, .failure(error))
                        }
                    }
                }
                for try await (project, result) in group {
                    switch result {
                    case .success(let r): collected += r
                    case .failure(let e): errs.append("\(project): \(e.localizedDescription)")
                    }
                }
            }

            collected = applyFilter(collected)
            detectChanges(collected)
            pruneDismissed(collected)
            runs = collected
            errors = errs.sorted()
            problem = nil
            lastUpdated = Date()
        } catch ADOError.unauthorized {
            problem = ADOError.unauthorized.errorDescription
        } catch {
            problem = "Azure DevOps is niet bereikbaar: \(error.localizedDescription)"
        }
    }

    /// Vergeet negeringen van pipelines die intussen een andere run of status hebben.
    private func pruneDismissed(_ current: [PipelineRun]) {
        let byKey = Dictionary(current.map { ($0.key, $0) }, uniquingKeysWith: { a, _ in a })
        let kept = dismissed.filter { key, d in byKey[key].map(d.matches) ?? true }
        if kept != dismissed {
            dismissed = kept
            saveDismissed()
        }
    }

    private func applyFilter(_ runs: [PipelineRun]) -> [PipelineRun] {
        let pattern = defaults.string(forKey: Prefs.pipelineFilter) ?? ""
        guard !pattern.isEmpty,
              let rx = try? NSRegularExpression(pattern: pattern, options: .caseInsensitive) else { return runs }
        return runs.filter {
            rx.firstMatch(in: $0.name, range: NSRange($0.name.startIndex..., in: $0.name)) != nil
        }
    }

    // MARK: - Wijzigingen en meldingen

    private func detectChanges(_ current: [PipelineRun]) {
        let stored = defaults.data(forKey: snapshotKey)
            .flatMap { try? JSONDecoder().decode([String: Snapshot].self, from: $0) }
        let seeded = stored != nil          // eerste run: enkel toestand vastleggen
        var snapshot = stored ?? [:]
        var events: [Notifier.Event] = []

        for run in current {
            let prev = snapshot[run.key]
            if seeded, let event = event(for: run, previous: prev) { events.append(event) }
            let last = run.state.isRunning ? prev?.lastResult : run.state
            snapshot[run.key] = Snapshot(runId: run.runId, state: run.state, lastResult: last)
        }

        if let data = try? JSONEncoder().encode(snapshot) { defaults.set(data, forKey: snapshotKey) }
        Notifier.shared.post(events)
    }

    private func event(for run: PipelineRun, previous prev: Snapshot?) -> Notifier.Event? {
        guard let prev else { return nil }   // nieuwe pipeline: geen melding
        let sameRun = prev.runId == run.runId
        if sameRun && prev.state == run.state { return nil }

        let title: String
        if run.state.isRunning {
            if run.state == .waiting {
                title = "Wacht op goedkeuring"
            } else if !sameRun && defaults.bool(forKey: Prefs.notifyOnStart) {
                title = "Gestart"
            } else {
                return nil
            }
        } else {
            switch run.state {
            case .succeeded:
                if prev.lastResult == .failed || prev.lastResult == .partiallySucceeded {
                    title = "Hersteld"
                } else if defaults.bool(forKey: Prefs.notifyOnSuccess) {
                    title = "Geslaagd"
                } else {
                    return nil
                }
            case .failed: title = "Mislukt"
            case .partiallySucceeded: title = "Deels geslaagd"
            case .canceled: title = "Geannuleerd"
            default: return nil
            }
        }

        return Notifier.Event(
            title: "\(title): \(run.name)",
            subtitle: run.project,
            body: run.detail.isEmpty ? run.state.label : run.detail,
            url: run.url,
            thread: run.key
        )
    }
}
