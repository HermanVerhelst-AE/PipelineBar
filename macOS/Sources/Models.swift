import AppKit
import SwiftUI

enum RunState: String, Codable {
    case succeeded, failed, partiallySucceeded, canceled
    case inProgress, notStarted, postponed, waiting, cancelling, none

    var isRunning: Bool {
        switch self {
        case .inProgress, .notStarted, .postponed, .waiting, .cancelling: return true
        default: return false
        }
    }

    /// Kleurt of telt mee in het menubalkicoon, en kan dus genegeerd worden.
    var needsAttention: Bool { isRunning || self == .failed || self == .partiallySucceeded }

    /// SF Symbol per status.
    var symbol: String {
        switch self {
        case .succeeded: return "checkmark.circle.fill"
        case .failed: return "xmark.circle.fill"
        case .partiallySucceeded: return "exclamationmark.triangle.fill"
        case .canceled: return "stop.circle.fill"
        case .inProgress: return "arrow.triangle.2.circlepath.circle.fill"
        case .notStarted, .postponed: return "clock.fill"
        case .waiting: return "hand.raised.fill"
        case .cancelling: return "stop.circle.fill"
        case .none: return "circle.dashed"
        }
    }

    var nsColor: NSColor {
        switch self {
        case .succeeded: return .systemGreen
        case .failed: return .systemRed
        case .partiallySucceeded, .cancelling: return .systemOrange
        case .inProgress: return .systemBlue
        case .waiting: return .systemPurple
        case .canceled, .notStarted, .postponed, .none: return .secondaryLabelColor
        }
    }

    var color: Color { Color(nsColor: nsColor) }

    var label: String {
        switch self {
        case .succeeded: return "Geslaagd"
        case .failed: return "Mislukt"
        case .partiallySucceeded: return "Deels geslaagd"
        case .canceled: return "Geannuleerd"
        case .inProgress: return "Bezig"
        case .notStarted: return "In wachtrij"
        case .postponed: return "Uitgesteld"
        case .waiting: return "Wacht op goedkeuring"
        case .cancelling: return "Wordt geannuleerd"
        case .none: return "Onbekend"
        }
    }

    /// Lager = hoger in de lijst.
    var priority: Int {
        switch self {
        case .failed: return 0
        case .partiallySucceeded: return 1
        case .waiting: return 2
        case .inProgress, .cancelling: return 3
        case .notStarted, .postponed: return 4
        case .canceled: return 5
        case .succeeded: return 6
        case .none: return 7
        }
    }
}

enum RunKind: String, Codable { case build, release }

struct PipelineRun: Identifiable, Hashable {
    let key: String          // stabiel per pipeline en branch (of release-omgeving)
    let runId: Int
    let project: String
    let name: String
    let kind: RunKind
    let state: RunState
    let number: String
    let branch: String
    let requestedBy: String
    let date: Date?
    let url: URL?

    var id: String { key }

    var detail: String {
        var parts = [number]
        if !branch.isEmpty { parts.append(branch) }
        var text = parts.filter { !$0.isEmpty }.joined(separator: " op ")
        if !requestedBy.isEmpty { text += text.isEmpty ? requestedBy : ", door \(requestedBy)" }
        return text
    }
}

struct ProjectGroup: Identifiable {
    let project: String
    let runs: [PipelineRun]
    var id: String { project }
}

/// Laatst geziene toestand per pipeline, om wijzigingen te detecteren.
struct Snapshot: Codable {
    let runId: Int
    let state: RunState
    let lastResult: RunState?
}

/// Een genegeerde run; vervalt zodra de pipeline een andere run of status heeft.
struct Dismissal: Codable, Hashable {
    let runId: Int
    let state: RunState

    func matches(_ run: PipelineRun) -> Bool { run.runId == runId && run.state == state }
}

enum Prefs {
    static let organization = "organization"
    static let projects = "projects"
    static let pipelineFilter = "pipelineFilter"
    static let includeReleases = "includeReleases"
    static let notifyOnStart = "notifyOnStart"
    static let notifyOnSuccess = "notifyOnSuccess"
    static let refreshMinutes = "refreshMinutes"
    static let maxAgeDays = "maxAgeDays"
    static let onlyMine = "onlyMine"

    static func registerDefaults() {
        UserDefaults.standard.register(defaults: [
            organization: "",
            projects: "",
            pipelineFilter: "",
            includeReleases: true,
            notifyOnStart: true,
            notifyOnSuccess: true,
            refreshMinutes: 1,
            maxAgeDays: 0,
            onlyMine: true,
        ])
    }
}

func shortRelative(_ date: Date, now: Date = Date()) -> String {
    let secs = Int(now.timeIntervalSince(date))
    if secs < 60 { return "nu" }
    if secs < 3600 { return "\(secs / 60) min" }
    if secs < 86_400 { return "\(secs / 3600) u" }
    return "\(secs / 86_400) d"
}
