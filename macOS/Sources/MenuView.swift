import AppKit
import SwiftUI

@MainActor
struct MenuView: View {
    @EnvironmentObject private var store: PipelineStore
    @Environment(\.openSettings) private var openSettings
    @AppStorage(Prefs.organization) private var organization = ""

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
            Divider()
            footer
        }
        .frame(width: 380)
    }

    // MARK: - Kop

    private var header: some View {
        HStack(spacing: 10) {
            Image(systemName: (store.overallState ?? RunState.none).symbol)
                .font(.system(size: 22))
                .foregroundStyle((store.overallState ?? RunState.none).color)
                .symbolEffect(.pulse, isActive: store.overallState?.isRunning == true)

            VStack(alignment: .leading, spacing: 1) {
                Text(summary).font(.headline)
                TimelineView(.periodic(from: .now, by: 30)) { context in
                    Text(updatedText(now: context.date))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            if store.dismissibleCount > 0 {
                Button { store.dismissAll() } label: { Image(systemName: "eye.slash") }
                    .buttonStyle(.borderless)
                    .help("Alles negeren tot er iets verandert")
            }

            Button {
                Task { await store.refresh() }
            } label: {
                if store.isRefreshing {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: "arrow.clockwise")
                }
            }
            .buttonStyle(.borderless)
            .help("Nu vernieuwen")
            .keyboardShortcut("r")
        }
        .padding(12)
    }

    private var summary: String {
        if store.problem != nil { return "Niet verbonden" }
        if store.failedCount == 1 { return "1 pipeline mislukt" }
        if store.failedCount > 1 { return "\(store.failedCount) pipelines mislukt" }
        if store.runningCount == 1 { return "1 pipeline bezig" }
        if store.runningCount > 1 { return "\(store.runningCount) pipelines bezig" }
        if store.runs.isEmpty { return "Geen recente runs" }
        if store.runs.contains(where: store.isDismissed) { return "Niets nieuws" }
        return "Alles geslaagd"
    }

    private func updatedText(now: Date) -> String {
        guard let date = store.lastUpdated else { return organization.isEmpty ? "Azure DevOps" : organization }
        let ago = shortRelative(date, now: now)
        return ago == "nu" ? "\(organization), net bijgewerkt" : "\(organization), \(ago) geleden bijgewerkt"
    }

    // MARK: - Inhoud

    @ViewBuilder
    private var content: some View {
        if let problem = store.problem {
            VStack(spacing: 10) {
                Image(systemName: "key.horizontal.fill")
                    .font(.system(size: 26))
                    .foregroundStyle(.secondary)
                Text(problem)
                    .multilineTextAlignment(.center)
                    .foregroundStyle(.secondary)
                Button("Instellingen openen") { showSettings() }
                    .controlSize(.large)
                    .keyboardShortcut(.defaultAction)
            }
            .frame(maxWidth: .infinity)
            .padding(24)
        } else if store.runs.isEmpty {
            ContentUnavailableView("Geen recente runs",
                                   systemImage: "tray",
                                   description: Text("Pas de projecten of de periode aan in Instellingen."))
                .padding(.vertical, 12)
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                    ForEach(store.groups) { group in
                        Section {
                            ForEach(group.runs) { RunRow(run: $0) }
                        } header: {
                            ProjectHeader(group: group)
                        }
                    }
                    if !store.errors.isEmpty { errorList }
                }
                .padding(.vertical, 4)
            }
            .frame(height: listHeight)
        }
    }

    private var listHeight: CGFloat {
        let rows = CGFloat(store.runs.count) * 44
        let headers = CGFloat(store.groups.count) * 30
        let errs = CGFloat(store.errors.count) * 20
        return min(rows + headers + errs + 8, 480)
    }

    private var errorList: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(store.errors, id: \.self) { err in
                Label(err, systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .lineLimit(2)
            }
        }
        .padding(.horizontal, 12)
        .padding(.top, 6)
    }

    // MARK: - Voet

    private var footer: some View {
        HStack(spacing: 14) {
            Button {
                if let url = URL(string: "https://dev.azure.com/\(organization)") { NSWorkspace.shared.open(url) }
            } label: {
                Label("Azure DevOps openen", systemImage: "arrow.up.forward.app")
            }
            .disabled(organization.isEmpty)

            Spacer()

            Button { showSettings() } label: { Image(systemName: "gearshape") }
                .help("Instellingen")
                .keyboardShortcut(",")

            Button { NSApp.terminate(nil) } label: { Image(systemName: "power") }
                .help("PipelineBar afsluiten")
                .keyboardShortcut("q")
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
    }

    private func showSettings() {
        NSApp.activate(ignoringOtherApps: true)
        openSettings()
    }
}

// MARK: - Rijen

@MainActor
private struct ProjectHeader: View {
    @EnvironmentObject private var store: PipelineStore
    let group: ProjectGroup

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "folder.fill")
                .foregroundStyle(.secondary)
                .imageScale(.small)
            Text(group.project)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.secondary)
            Spacer()
            let failed = group.runs.filter { $0.state == .failed && !store.isDismissed($0) }.count
            if failed > 0 {
                Text("\(failed) mislukt")
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.red)
            }
        }
        .padding(.horizontal, 12)
        .padding(.top, 8)
        .padding(.bottom, 4)
        .background(.regularMaterial)
    }
}

@MainActor
private struct RunRow: View {
    @EnvironmentObject private var store: PipelineStore
    let run: PipelineRun
    @State private var hovering = false

    private var dismissed: Bool { store.isDismissed(run) }

    var body: some View {
        HStack(spacing: 4) {
            Button {
                if let url = run.url { NSWorkspace.shared.open(url) }
            } label: {
                HStack(spacing: 10) {
                    Image(systemName: run.state.symbol)
                        .font(.system(size: 17))
                        .foregroundStyle(dismissed ? Color.secondary : run.state.color)
                        .symbolEffect(.pulse, isActive: run.state == .inProgress && !dismissed)
                        .frame(width: 22)

                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 4) {
                            if run.kind == .release {
                                Image(systemName: "shippingbox")
                                    .imageScale(.small)
                                    .foregroundStyle(.secondary)
                                    .help("Release-pipeline")
                            }
                            Text(run.name)
                                .font(.system(size: 13, weight: .medium))
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                        Text(run.detail.isEmpty ? run.state.label : run.detail)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }

                    Spacer(minLength: 8)

                    if let date = run.date {
                        TimelineView(.periodic(from: .now, by: 30)) { context in
                            Text(shortRelative(date, now: context.date))
                                .font(.caption)
                                .foregroundStyle(.tertiary)
                                .monospacedDigit()
                        }
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(dismissed
                  ? "\(run.state.label), genegeerd. Klik om te openen in Azure DevOps."
                  : "\(run.state.label). Klik om te openen in Azure DevOps.")

            // Vaste breedte, zodat de tijd niet verspringt bij het aanwijzen.
            Group {
                if hovering && dismissed {
                    Button { store.undismiss(run) } label: { Image(systemName: "eye") }
                        .help("Opnieuw tonen")
                } else if hovering && run.state.needsAttention {
                    Button { store.dismiss(run) } label: { Image(systemName: "eye.slash") }
                        .help("Negeren tot er iets verandert")
                }
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.secondary)
            .frame(width: 18)
        }
        .opacity(dismissed ? 0.55 : 1)
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(
            RoundedRectangle(cornerRadius: 6, style: .continuous)
                .fill(hovering ? Color.primary.opacity(0.08) : .clear)
        )
        .padding(.horizontal, 4)
        .onHover { hovering = $0 }
        .contextMenu {
            if dismissed {
                Button("Opnieuw tonen") { store.undismiss(run) }
            } else if run.state.needsAttention {
                Button("Negeren") { store.dismiss(run) }
            }
        }
    }
}
