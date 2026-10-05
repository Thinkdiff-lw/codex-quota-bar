import AppKit
import ServiceManagement
import ApplicationServices

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var item: NSStatusItem!
    private var panel: QuotaPanel!
    private let view = QuotaView()
    private let defaults = UserDefaults.standard
    private let anchorQueue = DispatchQueue(label: "quota.anchor", qos: .utility)
    private let serverQueue = DispatchQueue(label: "quota.server", qos: .utility)
    private var server: AppServer? // accessed only on serverQueue
    private var serverPath: URL?
    private var anchorBusy = false
    private var refreshBusy = false
    private var lastRead = Date.distantPast
    private var lastOwner: CGRect?
    private var snapshot: QuotaSnapshot?
    private var selectedBucket: String?
    private var paused = false
    private var manual = false
    private var dragging = false
    private var calibrating = false
    private var targetPID: pid_t?
    private var status = "等待 Codex 打开"
    private var stale = false
    private var timer: Timer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard !NSWorkspace.shared.runningApplications.contains(where: {
            $0.bundleIdentifier == Bundle.main.bundleIdentifier && $0.processIdentifier != ProcessInfo.processInfo.processIdentifier
        }) else { NSApp.terminate(nil); return }
        NSApp.setActivationPolicy(.accessory)
        manual = defaults.bool(forKey: "manualPlacement")
        selectedBucket = defaults.string(forKey: "bucket")
        panel = QuotaPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 26),
                          styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = view
        view.onDrag = { [weak self] _ in self?.dragging = true }
        view.onDragEnd = { [weak self] in self?.savePlacement() }
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = NSImage(systemSymbolName: "chart.pie", accessibilityDescription: "Codex 额度条")
        updateMenu()
        if !CommandLine.arguments.contains("--background") && !AXIsProcessTrusted() { requestAccessibility() }
        timer = Timer.scheduledTimer(withTimeInterval: 0.6, repeats: true) { [weak self] _ in self?.tick() }
        tick()
    }

    private func isCodex(_ application: NSRunningApplication) -> Bool {
        application.bundleURL?.lastPathComponent.lowercased() == "codex.app"
    }

    private func tick() {
        let running = NSWorkspace.shared.runningApplications.filter { isCodex($0) && !$0.isTerminated }
        guard let app = running.first else {
            panel.orderOut(nil)
            if targetPID != nil {
                targetPID = nil; lastRead = .distantPast
                serverQueue.async { [weak self] in self?.server?.close(); self?.server = nil; self?.serverPath = nil }
                status = "等待 Codex 打开"; updateMenu()
            }
            return
        }
        targetPID = app.processIdentifier
        if !refreshBusy && Date().timeIntervalSince(lastRead) >= 60 { refresh(app) }
        view.needsDisplay = true // refresh countdown without extra server calls.
        guard let front = NSWorkspace.shared.frontmostApplication, isCodex(front),
              !paused, AXIsProcessTrusted() else {
            panel.orderOut(nil)
            if !AXIsProcessTrusted() { status = "请开启辅助功能权限"; updateMenu() }
            return
        }
        guard !dragging && !anchorBusy else { return }
        anchorBusy = true
        let pid = front.processIdentifier
        let manualMode = manual || calibrating
        anchorQueue.async { [weak self] in
            let geometry: ComposerAnchor?
            if manualMode, let (_, owner) = AccessibilityAnchor.window(pid) {
                geometry = ComposerAnchor(owner: owner, slot: owner)
            } else { geometry = AccessibilityAnchor.read(pid) }
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.anchorBusy = false
                guard NSWorkspace.shared.frontmostApplication?.processIdentifier == pid,
                      !self.paused && !self.dragging else { self.panel.orderOut(nil); return }
                guard let geometry = geometry else {
                    self.panel.orderOut(nil)
                    self.status = "暂未找到输入框，可从菜单手动校准"; self.updateMenu(); return
                }
                self.lastOwner = geometry.owner
                let frame: CGRect
                if manualMode {
                    let x = self.defaults.object(forKey: "manualX") as? Double ?? 0.3
                    let y = self.defaults.object(forKey: "manualY") as? Double ?? 0.9
                    frame = RelativePlacement.frame(owner: geometry.owner, x: x, y: y,
                                                    width: min(360, geometry.owner.width - 20), height: 26)
                } else {
                    frame = CGRect(x: geometry.slot.minX, y: geometry.slot.minY,
                                   width: min(360, geometry.slot.width), height: 26)
                }
                self.panel.setFrame(AccessibilityAnchor.cocoaRect(frame), display: true)
                self.panel.orderFrontRegardless()
            }
        }
    }

    private func executable(_ app: NSRunningApplication) -> URL? {
        var candidates: [String] = []
        if let configured = defaults.string(forKey: "cliPath") { candidates.append(configured) }
        if let bundle = app.bundleURL?.path {
            candidates += [bundle + "/Contents/Resources/codex", bundle + "/Contents/Resources/bin/codex"]
        }
        candidates += ["/opt/homebrew/bin/codex", "/usr/local/bin/codex"]
        candidates += (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map { String($0) + "/codex" }
        return candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }).map { URL(fileURLWithPath: $0) }
    }

    private func refresh(_ app: NSRunningApplication) {
        guard let path = executable(app) else {
            lastRead = Date(); status = "未找到 Codex CLI：菜单中选择路径"; stale = true; updateContent(); return
        }
        refreshBusy = true
        lastRead = Date()
        serverQueue.async { [weak self] in
            guard let self = self else { return }
            let result: Result<QuotaSnapshot, Error>
            do {
                if self.server == nil || self.serverPath != path {
                    self.server?.close()
                    self.server = try AppServer(executable: path)
                    self.serverPath = path
                }
                result = .success(try self.server!.read())
            } catch {
                self.server?.close(); self.server = nil; self.serverPath = nil
                result = .failure(error)
            }
            DispatchQueue.main.async {
                self.refreshBusy = false
                switch result {
                case .success(let value):
                    self.snapshot = value
                    self.stale = false
                    self.status = value.windows(value.selected(self.selectedBucket)).isEmpty ? "账户暂未返回额度" : "额度已更新"
                case .failure(let error): self.status = error.localizedDescription; self.stale = true
                }
                self.updateContent()
            }
        }
    }

    private func updateContent() {
        view.windows = snapshot?.windows(snapshot?.selected(selectedBucket)) ?? []
        view.status = status
        view.stale = stale
        var lines = view.windows.map {
            "\($0.label) \($0.percentage) · \($0.countdown() ?? "重置未知") · \($0.resetText ?? "日期未知") 重置（北京时间）"
        }
        if let updated = snapshot?.updated { lines.append("读取时间：\(DateFormatter.localizedString(from: updated, dateStyle: .none, timeStyle: .medium))") }
        if stale { lines.append("数据待更新：" + status) }
        view.toolTip = lines.isEmpty ? status : lines.joined(separator: "\n")
        item.button?.toolTip = view.toolTip
        updateMenu()
    }

    private func add(_ menu: NSMenu, _ title: String, _ action: Selector, state: Bool? = nil) -> NSMenuItem {
        let entry = NSMenuItem(title: title, action: action, keyEquivalent: "")
        entry.target = self
        if let state = state { entry.state = state ? .on : .off }
        menu.addItem(entry)
        return entry
    }

    private func updateMenu() {
        let menu = NSMenu()
        let info = NSMenuItem(title: status, action: nil, keyEquivalent: "")
        info.isEnabled = false; menu.addItem(info)
        for quota in view.windows {
            let row = NSMenuItem(title: "\(quota.label) \(quota.percentage) · \(quota.countdown() ?? "重置未知")", action: nil, keyEquivalent: "")
            row.isEnabled = false; menu.addItem(row)
        }
        menu.addItem(.separator())
        _ = add(menu, "立即刷新", #selector(refreshNow))
        if let snapshot = snapshot, snapshot.buckets.count > 1 {
            let parent = NSMenuItem(title: "额度类型", action: nil, keyEquivalent: "")
            let sub = NSMenu()
            for id in snapshot.buckets.keys.sorted() {
                let row = add(sub, id, #selector(selectBucket(_:)), state: snapshot.selected(selectedBucket) == id)
                row.representedObject = id
            }
            parent.submenu = sub; menu.addItem(parent)
        }
        _ = add(menu, "暂时隐藏", #selector(togglePaused), state: paused)
        _ = add(menu, calibrating ? "完成校准" : "手动校准位置", #selector(calibrate))
        _ = add(menu, "恢复自动定位", #selector(restore))
        menu.addItem(.separator())
        _ = add(menu, "开启辅助功能权限…", #selector(requestAccessibility))
        _ = add(menu, "选择 Codex CLI…", #selector(chooseCLI))
        let loginStatus = SMAppService.mainApp.status
        _ = add(menu, "随 macOS 登录启动", #selector(toggleLogin), state: loginStatus == .enabled)
        if loginStatus == .requiresApproval { _ = add(menu, "在系统设置批准登录启动…", #selector(openLoginSettings)) }
        _ = add(menu, "使用说明", #selector(openDocs))
        menu.addItem(.separator())
        _ = add(menu, "退出额度条", #selector(quit))
        item.menu = menu
        view.contextMenu = menu
    }

    @objc private func refreshNow() { lastRead = .distantPast; tick() }
    @objc private func togglePaused() { paused.toggle(); updateMenu(); tick() }
    @objc private func selectBucket(_ sender: NSMenuItem) {
        selectedBucket = sender.representedObject as? String
        defaults.set(selectedBucket, forKey: "bucket"); updateContent()
    }
    @objc private func requestAccessibility() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }
    @objc private func chooseCLI() {
        NSApp.activate(ignoringOtherApps: true)
        let chooser = NSOpenPanel()
        chooser.message = "选择 codex 可执行文件，例如 /opt/homebrew/bin/codex"
        chooser.canChooseDirectories = false; chooser.allowsMultipleSelection = false
        if chooser.runModal() == .OK, let url = chooser.url, FileManager.default.isExecutableFile(atPath: url.path) {
            defaults.set(url.path, forKey: "cliPath"); lastRead = .distantPast
        }
    }
    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
            updateMenu()
        } catch { showError("登录启动设置失败", error.localizedDescription) }
    }
    @objc private func openLoginSettings() { SMAppService.openSystemSettingsLoginItems() }
    @objc private func openDocs() {
        if let url = Bundle.main.url(forResource: "使用说明", withExtension: "html") { NSWorkspace.shared.open(url) }
    }
    @objc private func quit() { NSApp.terminate(nil) }
    @objc private func calibrate() {
        if calibrating { savePlacement(); calibrating = false; view.calibrating = false }
        else {
            calibrating = true; view.calibrating = true; paused = false
            showError("校准位置", "关闭此提示后切回 Codex，将额度条拖到输入框底部的空白处，再右键选择“完成校准”。位置会按窗口比例保存。")
        }
        updateMenu(); tick()
    }
    @objc private func restore() {
        manual = false; calibrating = false; view.calibrating = false
        defaults.set(false, forKey: "manualPlacement"); updateMenu(); tick()
    }

    private func savePlacement() {
        defer { dragging = false }
        guard dragging || calibrating else { return }
        guard let owner = lastOwner else { return }
        let primaryTop = NSScreen.screens.first?.frame.maxY ?? 0
        let frame = panel.frame
        let top = primaryTop - frame.maxY
        let x = max(0, min(1, (frame.minX - owner.minX) / max(1, owner.width - frame.width)))
        let y = max(0, min(1, (top - owner.minY) / max(1, owner.height - frame.height)))
        defaults.set(Double(x), forKey: "manualX"); defaults.set(Double(y), forKey: "manualY")
        defaults.set(true, forKey: "manualPlacement"); manual = true
    }
    private func showError(_ title: String, _ message: String) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert(); alert.messageText = title; alert.informativeText = message; alert.runModal()
    }
    func applicationWillTerminate(_ notification: Notification) { server?.close() }
}

let application = NSApplication.shared
let delegate = AppDelegate()
application.delegate = delegate
application.run()
