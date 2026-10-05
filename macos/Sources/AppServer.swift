import Foundation

enum QuotaError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self { case .message(let s): return s }
    }
}

// One caller on a serial queue; reads stdout independently so large notifications
// cannot block a request. Neither stdout nor stderr is written to disk.
final class AppServer {
    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private let errors = Pipe()
    private let condition = NSCondition()
    private var buffer = Data()
    private var replies: [Int: [String: Any]] = [:]
    private var sequence = 0
    private var closed = false

    init(executable: URL) throws {
        process.executableURL = executable
        process.arguments = ["app-server"] // stdio is the default transport.
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        process.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:" + (environment["PATH"] ?? "")
        process.environment = environment
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard let self = self else { return }
            self.condition.lock()
            defer { self.condition.broadcast(); self.condition.unlock() }
            if data.isEmpty { self.closed = true; handle.readabilityHandler = nil; return }
            self.buffer.append(data)
            while let newline = self.buffer.firstIndex(of: 10) {
                let line = self.buffer.prefix(upTo: newline)
                self.buffer.removeSubrange(...newline)
                if let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                   let id = obj["id"] as? Int {
                    self.replies[id] = obj
                }
            }
            if self.buffer.count > 4 * 1024 * 1024 { self.buffer.removeAll() }
        }
        errors.fileHandleForReading.readabilityHandler = { handle in
            if handle.availableData.isEmpty { handle.readabilityHandler = nil }
        }
        do {
            try process.run()
            _ = try request("initialize", params: ["clientInfo": [
                "name": "codex_quota_bar", "title": "Codex Quota Bar", "version": "1.0.0"
            ]])
            try send(["method": "initialized", "params": [:]])
        } catch {
            close()
            throw error
        }
    }

    private func send(_ obj: [String: Any]) throws {
        var data = try JSONSerialization.data(withJSONObject: obj)
        data.append(10)
        try input.fileHandleForWriting.write(contentsOf: data)
    }

    private func request(_ method: String, params: [String: Any]? = nil) throws -> [String: Any] {
        sequence += 1
        let id = sequence
        var obj: [String: Any] = ["id": id, "method": method]
        if let params = params { obj["params"] = params }
        try send(obj)
        let deadline = Date().addingTimeInterval(20)
        condition.lock()
        defer { condition.unlock() }
        while replies[id] == nil && !closed {
            if !condition.wait(until: deadline) { throw QuotaError.message("额度请求超时，请检查 Codex 登录和网络") }
        }
        guard let reply = replies.removeValue(forKey: id) else {
            throw QuotaError.message("额度连接已断开")
        }
        guard reply["error"] == nil else { throw QuotaError.message("额度接口请求失败，请确认 Codex CLI 已登录 ChatGPT 账户") }
        return reply["result"] as? [String: Any] ?? [:]
    }

    func read() throws -> QuotaSnapshot { QuotaSnapshot(try request("account/rateLimits/read")) }

    func close() {
        condition.lock()
        closed = true
        condition.broadcast()
        condition.unlock()
        output.fileHandleForReading.readabilityHandler = nil
        errors.fileHandleForReading.readabilityHandler = nil
        try? input.fileHandleForWriting.close()
        if process.isRunning { process.terminate() }
    }

    deinit { close() }
}
