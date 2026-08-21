import AppKit
import Combine

@MainActor
final class MenuBarController: NSObject, NSMenuDelegate {
    private let model: AppModel
    private let showConfiguration: () -> Void
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private let menu = NSMenu()
    private var cancellables = Set<AnyCancellable>()
    private var flashTimer: Timer?
    private var flashAlternate = false

    init(model: AppModel, showConfiguration: @escaping () -> Void) {
        self.model = model
        self.showConfiguration = showConfiguration
        super.init()
        menu.delegate = self
        statusItem.menu = menu
        statusItem.button?.toolTip = "DSH Launcher"
        statusItem.button?.image = loadWhaleImage()
        bind()
        rebuildMenu()
        applyStatus(model.status)
    }

    func menuWillOpen(_ menu: NSMenu) { rebuildMenu() }

    private func bind() {
        model.$status.sink { [weak self] in self?.applyStatus($0) }.store(in: &cancellables)
        model.$settings.sink { [weak self] _ in self?.rebuildMenu() }.store(in: &cancellables)
        model.$busy.sink { [weak self] _ in self?.rebuildMenu() }.store(in: &cancellables)
    }

    private func rebuildMenu() {
        menu.removeAllItems()
        menu.addItem(disabled("DSH Launcher \(Bundle.main.releaseVersion)"))
        menu.addItem(disabled("当前 DSH：\(model.settings.selectedInstallation?.displayName ?? "未配置")"))
        menu.addItem(disabled("DSH_HOME：\(model.settings.selectedHome?.path ?? "未配置")"))
        menu.addItem(.separator())
        menu.addItem(item("打开 DSH 网页", #selector(openWeb)))
        menu.addItem(item("启动 DSH", #selector(startDSH), enabled: !model.busy && model.settings.selectedInstallation != nil))
        menu.addItem(item("停止 DSH", #selector(stopDSH), enabled: !model.busy && model.ownership != .stopped))
        menu.addItem(item("重启 DSH", #selector(restartDSH), enabled: !model.busy && model.settings.selectedInstallation != nil))
        menu.addItem(.separator())
        menu.addItem(item("检查并更新当前 DSH", #selector(updateDSH), enabled: !model.busy && model.settings.selectedInstallation != nil))
        menu.addItem(item("检查 Launcher 更新", #selector(updateLauncher), enabled: !model.busy))
        menu.addItem(item("配置…", #selector(configure)))
        let login = item("登录时启动", #selector(toggleLogin))
        login.state = model.settings.startAtLogin ? .on : .off
        menu.addItem(login)
        menu.addItem(.separator())
        menu.addItem(item("退出并停止 DSH", #selector(quit)))
    }

    private func item(_ title: String, _ action: Selector, enabled: Bool = true) -> NSMenuItem {
        let value = NSMenuItem(title: title, action: action, keyEquivalent: "")
        value.target = self
        value.isEnabled = enabled
        return value
    }

    private func disabled(_ title: String) -> NSMenuItem {
        let value = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        value.isEnabled = false
        return value
    }

    private func applyStatus(_ snapshot: DSHStatusSnapshot) {
        flashTimer?.invalidate(); flashTimer = nil
        let button = statusItem.button
        button?.image?.isTemplate = snapshot.state == .idle
        button?.contentTintColor = color(snapshot.state)
        button?.toolTip = snapshot.summary
        if snapshot.state == .attention {
            flashTimer = Timer.scheduledTimer(
                timeInterval: 0.5,
                target: self,
                selector: #selector(toggleAttention),
                userInfo: nil,
                repeats: true
            )
        } else { button?.alphaValue = 1 }
    }

    private func color(_ state: DSHActivityState) -> NSColor? {
        switch state {
        case .stopped: return .systemGray
        case .idle: return nil
        case .busy: return .systemGreen
        case .attention: return .systemYellow
        case .incompatible: return .systemOrange
        }
    }

    private func loadWhaleImage() -> NSImage? {
        if let url = Bundle.main.url(forResource: "Whale", withExtension: "svg"), let image = NSImage(contentsOf: url) {
            image.size = NSSize(width: 18, height: 18)
            return image
        }
        return NSImage(systemSymbolName: "fish.fill", accessibilityDescription: "DSH Launcher")
    }

    @objc private func openWeb() { model.openWeb() }
    @objc private func startDSH() { Task { await model.startDSH() } }
    @objc private func stopDSH() { Task { await model.stopDSH() } }
    @objc private func restartDSH() { Task { await model.restartDSH() } }
    @objc private func updateDSH() { Task { await model.updateSelectedInstallation() } }
    @objc private func updateLauncher() { Task { await model.updateLauncher() } }
    @objc private func configure() { showConfiguration() }
    @objc private func toggleLogin() {
        model.settings.startAtLogin.toggle()
        Task { await model.save() }
    }
    @objc private func quit() {
        Task { await model.stopDSH(); NSApp.terminate(nil) }
    }

    @objc private func toggleAttention() {
        flashAlternate.toggle()
        statusItem.button?.alphaValue = flashAlternate ? 0.45 : 1
    }
}

private extension Bundle {
    var releaseVersion: String {
        object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.2.0"
    }
}
