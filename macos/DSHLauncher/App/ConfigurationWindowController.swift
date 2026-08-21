import AppKit
import SwiftUI

@MainActor
final class ConfigurationWindowController: NSWindowController {
    init(model: AppModel) {
        let host = NSHostingController(rootView: ConfigurationView(model: model))
        let window = NSWindow(contentViewController: host)
        window.title = "DSH Launcher 配置"
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.setContentSize(NSSize(width: 780, height: 620))
        window.minSize = NSSize(width: 680, height: 520)
        window.isReleasedWhenClosed = false
        window.center()
        super.init(window: window)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show() {
        showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
