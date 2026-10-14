import AppKit
import SwiftUI

@main
@MainActor
struct PipelineBarApp: App {
    @StateObject private var store: PipelineStore

    init() {
        Prefs.registerDefaults()
        Notifier.shared.setup()
        _store = StateObject(wrappedValue: PipelineStore())
    }

    var body: some Scene {
        MenuBarExtra {
            MenuView()
                .environmentObject(store)
        } label: {
            MenuBarLabel(store: store)
        }
        .menuBarExtraStyle(.window)

        SwiftUI.Settings {
            SettingsView()
                .environmentObject(store)
        }
    }
}

/// Het icoon in de menubalk: het app-symbool als er niets te melden is, in kleur zodra iets aandacht vraagt.
@MainActor
struct MenuBarLabel: View {
    @ObservedObject var store: PipelineStore

    // Eén afbeelding: een HStack van icoon en tekst verspringt bij het aanklikken.
    var body: some View {
        Image(nsImage: MenuBarIcon.image(for: store.overallState, count: store.badgeCount))
    }
}

enum MenuBarIcon {
    static func image(for state: RunState?, count: Int?) -> NSImage {
        let name: String
        var tint: NSColor?

        switch state {
        case .some(.failed): name = "xmark.circle.fill"; tint = .systemRed
        case .some(.partiallySucceeded): name = "exclamationmark.triangle.fill"; tint = .systemOrange
        case .some(.waiting): name = "hand.raised.fill"; tint = .systemPurple
        case .some(let s) where s.isRunning: name = "arrow.triangle.2.circlepath"
        case .some: name = "arrow.triangle.branch"   // niets te melden: het symbool van het app-icoon
        case nil: name = "exclamationmark.circle"
        }

        let base = NSImage.SymbolConfiguration(pointSize: 14, weight: .medium)
        let config = tint.map { base.applying(NSImage.SymbolConfiguration(paletteColors: [$0])) } ?? base
        guard let symbol = NSImage(systemSymbolName: name, accessibilityDescription: state?.label ?? "Azure DevOps")?
            .withSymbolConfiguration(config) else { return NSImage() }
        guard let count else {
            // Template = volgt licht/donker van de menubalk; met kleur moet dat uit.
            symbol.isTemplate = (tint == nil)
            return symbol
        }

        let font = NSFont.monospacedDigitSystemFont(ofSize: NSFont.menuBarFont(ofSize: 0).pointSize, weight: .regular)
        let text = NSAttributedString(string: "\(count)", attributes: [.font: font])
        let textSize = text.size()
        let gap: CGFloat = 3
        let size = NSSize(width: ceil(symbol.size.width + gap + textSize.width),
                          height: ceil(max(symbol.size.height, textSize.height)))

        let image = NSImage(size: size, flipped: false) { _ in
            symbol.draw(in: NSRect(x: 0, y: (size.height - symbol.size.height) / 2,
                                   width: symbol.size.width, height: symbol.size.height))
            // Wordt getekend met het uiterlijk van de menubalk, dus labelColor klopt in licht en donker.
            let colored = NSAttributedString(string: "\(count)", attributes: [
                .font: font, .foregroundColor: tint == nil ? NSColor.black : NSColor.labelColor,
            ])
            colored.draw(at: NSPoint(x: symbol.size.width + gap, y: (size.height - textSize.height) / 2))
            return true
        }
        image.isTemplate = (tint == nil)
        image.accessibilityDescription = "\(state?.label ?? "Azure DevOps"): \(count)"
        return image
    }
}
