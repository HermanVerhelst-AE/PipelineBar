import AppKit
import ServiceManagement
import SwiftUI

@MainActor
struct SettingsView: View {
    @EnvironmentObject private var store: PipelineStore

    @AppStorage(Prefs.organization) private var organization = ""
    @AppStorage(Prefs.projects) private var projects = ""
    @AppStorage(Prefs.pipelineFilter) private var pipelineFilter = ""
    @AppStorage(Prefs.includeReleases) private var includeReleases = true
    @AppStorage(Prefs.notifyOnStart) private var notifyOnStart = true
    @AppStorage(Prefs.notifyOnSuccess) private var notifyOnSuccess = true
    @AppStorage(Prefs.refreshMinutes) private var refreshMinutes = 1
    @AppStorage(Prefs.maxAgeDays) private var maxAgeDays = 0
    @AppStorage(Prefs.onlyMine) private var onlyMine = true

    @State private var token = ""
    @State private var hasToken = false
    @State private var testing = false
    @State private var testResult: (ok: Bool, text: String)?
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled

    var body: some View {
        Form {
            Section {
                TextField("Organisatie", text: $organization, prompt: Text("naam uit dev.azure.com/…"))
                    .onSubmit(applyOrganization)

                SecureField("Personal Access Token", text: $token,
                            prompt: Text(hasToken ? "Opgeslagen in de sleutelhanger" : "Plak je token"))
                    .onSubmit(saveToken)

                HStack {
                    Button("Token opslaan", action: saveToken)
                        .disabled(token.isEmpty || organization.isEmpty)
                    Button("Verbinding testen", action: testConnection)
                        .disabled(organization.isEmpty || (!hasToken && token.isEmpty) || testing)
                    if testing { ProgressView().controlSize(.small) }
                    Spacer()
                    if let r = testResult {
                        Label(r.text, systemImage: r.ok ? "checkmark.circle.fill" : "xmark.circle.fill")
                            .foregroundStyle(r.ok ? Color.green : Color.red)
                            .font(.callout)
                            .lineLimit(2)
                    }
                }
            } header: {
                Text("Verbinding")
            } footer: {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Het token heeft enkel Build (Read) en Release (Read) nodig en blijft in je sleutelhanger.")
                    if let url = URL(string: "https://dev.azure.com/\(organization)/_usersSettings/tokens"),
                       !organization.isEmpty {
                        Link("Token aanmaken in Azure DevOps", destination: url)
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }

            Section("Pipelines") {
                Toggle("Enkel runs die ik startte", isOn: $onlyMine)
                TextField("Projecten", text: $projects, prompt: Text("Alle projecten"))
                    .onSubmit { store.restart() }
                TextField("Filter op naam", text: $pipelineFilter, prompt: Text("bv. ^(api|web)"))
                    .onSubmit { store.restart() }
                Toggle("Klassieke release-pipelines tonen", isOn: $includeReleases)
                Picker("Runs tonen van", selection: $maxAgeDays) {
                    Text("Vandaag").tag(0)
                    Text("De laatste 7 dagen").tag(7)
                    Text("De laatste 30 dagen").tag(30)
                    Text("De laatste 90 dagen").tag(90)
                    Text("De laatste 365 dagen").tag(365)
                }
                Picker("Vernieuwen", selection: $refreshMinutes) {
                    Text("Elke minuut").tag(1)
                    Text("Elke 2 minuten").tag(2)
                    Text("Elke 5 minuten").tag(5)
                    Text("Elke 15 minuten").tag(15)
                }
            }

            Section {
                Toggle("Wanneer een run start", isOn: $notifyOnStart)
                Toggle("Wanneer een run slaagt", isOn: $notifyOnSuccess)
                Button("Meldingen in Systeeminstellingen") {
                    if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension") {
                        NSWorkspace.shared.open(url)
                    }
                }
            } header: {
                Text("Meldingen")
            } footer: {
                Text("Mislukt, hersteld en wacht op goedkeuring meld je altijd.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Algemeen") {
                Toggle("Openen bij inloggen", isOn: $launchAtLogin)
            }
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .fixedSize(horizontal: false, vertical: true)
        .onAppear { hasToken = Keychain.read(account: organization) != nil }
        .onDisappear { store.restart() }
        .onChange(of: includeReleases) { store.restart() }
        .onChange(of: onlyMine) { store.restart() }
        .onChange(of: maxAgeDays) { store.restart() }
        .onChange(of: refreshMinutes) { store.restart() }
        .onChange(of: launchAtLogin) { setLaunchAtLogin(launchAtLogin) }
    }

    private func applyOrganization() {
        hasToken = Keychain.read(account: organization) != nil
        testResult = nil
        store.restart()
    }

    private func saveToken() {
        let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !organization.isEmpty else { return }
        if Keychain.save(trimmed, account: organization) {
            token = ""
            hasToken = true
            testResult = (true, "Token opgeslagen")
            store.restart()
        } else {
            testResult = (false, "Opslaan in de sleutelhanger mislukt")
        }
    }

    private func testConnection() {
        let candidate = token.isEmpty ? (Keychain.read(account: organization) ?? "") : token
        testing = true
        testResult = nil
        Task {
            defer { testing = false }
            do {
                let client = AzureDevOpsClient(organization: organization, token: candidate, maxAgeDays: maxAgeDays)
                let count = try await client.projects().count
                testResult = (true, "Verbonden met \(count) project\(count == 1 ? "" : "en")")
            } catch {
                testResult = (false, error.localizedDescription)
            }
        }
    }

    private func setLaunchAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            launchAtLogin = SMAppService.mainApp.status == .enabled
        }
    }
}
