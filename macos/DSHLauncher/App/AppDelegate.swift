import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var model: AppModel!
    private var menuBarController: MenuBarController!
    private var configurationWindow: ConfigurationWindowController!
    private let activationNotification = Notification.Name("com.shawnyhu.dshlauncher.activate")

    func applicationDidFinishLaunching(_ notification: Notification) {
        let running = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "com.shawnyhu.dshlauncher")
        if running.count > 1 {
            DistributedNotificationCenter.default().post(name: activationNotification, object: nil)
            NSApp.terminate(nil)
            return
        }
        DistributedNotificationCenter.default().addObserver(
            self,
            selector: #selector(showConfiguration),
            name: activationNotification,
            object: nil
        )

        let layout = AppPaths.standard()
        do { try AppPaths.ensureCreated(layout) }
        catch {
            NSAlert(error: error).runModal()
            NSApp.terminate(nil)
            return
        }
        let logger = AppLogger(fileURL: layout.logFile)
        let store = SettingsStore(layout: layout)
        let notifications = NotificationService()
        model = AppModel(layout: layout, store: store, logger: logger, notifications: notifications)
        configurationWindow = ConfigurationWindowController(model: model)
        menuBarController = MenuBarController(model: model) { [weak self] in self?.configurationWindow.show() }

        Task {
            await logger.info("DSH Launcher \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.2.0") started; PID=\(ProcessInfo.processInfo.processIdentifier)")
            _ = await notifications.requestAuthorization()
            await model.startApplication()
            if model.settings.installations.isEmpty { configurationWindow.show() }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        Task {
            await model?.stopDSH()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    @objc private func showConfiguration() { configurationWindow?.show() }
}
