import AppKit
import UserNotifications

final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    static let shared = Notifier()
    private let maxPerRefresh = 5

    struct Event {
        let title: String
        let subtitle: String
        let body: String
        let url: URL?
        let thread: String
    }

    func setup() {
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        center.requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    func post(_ events: [Event]) {
        let center = UNUserNotificationCenter.current()
        for event in events.prefix(maxPerRefresh) {
            let content = UNMutableNotificationContent()
            content.title = event.title
            content.subtitle = event.subtitle
            content.body = event.body
            content.sound = .default
            content.threadIdentifier = event.thread
            if let url = event.url { content.userInfo = ["url": url.absoluteString] }
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
        if events.count > maxPerRefresh {
            let content = UNMutableNotificationContent()
            content.title = "Azure DevOps"
            content.body = "En nog \(events.count - maxPerRefresh) andere wijzigingen."
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }

    // Ook tonen als de app zelf actief is.
    func userNotificationCenter(_ center: UNUserNotificationCenter,
                                willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }

    // Klik op een melding opent de run in Azure DevOps.
    func userNotificationCenter(_ center: UNUserNotificationCenter,
                                didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        if let s = response.notification.request.content.userInfo["url"] as? String, let url = URL(string: s) {
            NSWorkspace.shared.open(url)
        }
        completionHandler()
    }
}
