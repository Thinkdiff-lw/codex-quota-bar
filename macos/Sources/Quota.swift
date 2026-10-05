import Foundation
import CoreFoundation
import CoreGraphics

struct QuotaWindow {
    let remaining: Double?
    let minutes: Double?
    let reset: Date?

    init(_ object: [String: Any]) {
        func number(_ key: String) -> Double? {
            guard let n = object[key] as? NSNumber,
                  CFGetTypeID(n) != CFBooleanGetTypeID(), n.doubleValue.isFinite else { return nil }
            return n.doubleValue
        }
        remaining = number("usedPercent").map { max(0, min(100, 100 - $0)) }
        minutes = number("windowDurationMins")
        reset = number("resetsAt").flatMap { $0 > 0 ? Date(timeIntervalSince1970: $0) : nil }
    }

    var label: String {
        guard let m = minutes, m > 0 else { return "剩余" }
        if m == 10080 { return "周剩余" }
        if m.truncatingRemainder(dividingBy: 1440) == 0 { return "\(Int(m / 1440))天剩余" }
        if m.truncatingRemainder(dividingBy: 60) == 0 { return "\(Int(m / 60))小时剩余" }
        return "\(Int(m))分钟剩余"
    }

    var percentage: String {
        guard let r = remaining else { return "未知" }
        return String(format: r.rounded() == r ? "%.0f%%" : "%.1f%%", r)
    }

    func countdown(now: Date = Date()) -> String? {
        guard let reset = reset else { return nil }
        let seconds = reset.timeIntervalSince(now)
        if seconds <= 0 { return "等待重置" }
        if seconds >= 86400 { return "\(Int(seconds / 86400))天\(Int(seconds / 3600) % 24)小时" }
        if seconds >= 3600 { return "\(Int(seconds / 3600))小时\(Int(seconds / 60) % 60)分钟" }
        return "\(max(1, Int(ceil(seconds / 60))))分钟"
    }

    var resetText: String? {
        guard let reset = reset else { return nil }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "Asia/Shanghai")
        f.dateFormat = "MM/dd HH:mm"
        return f.string(from: reset)
    }
}

struct QuotaSnapshot {
    let buckets: [String: [String: Any]]
    let updated: Date

    init(_ result: [String: Any], now: Date = Date()) {
        var map = result["rateLimitsByLimitId"] as? [String: [String: Any]] ?? [:]
        if map.isEmpty, let legacy = result["rateLimits"] as? [String: Any] {
            map[legacy["limitId"] as? String ?? "codex"] = legacy
        }
        buckets = map
        updated = now
    }

    func selected(_ preferred: String?) -> String? {
        if let preferred = preferred, buckets[preferred] != nil { return preferred }
        return buckets["codex"] != nil ? "codex" : buckets.keys.sorted().first
    }

    func windows(_ id: String?) -> [QuotaWindow] {
        guard let id = id, let bucket = buckets[id] else { return [] }
        return ["primary", "secondary"].compactMap { key in
            (bucket[key] as? [String: Any]).map { QuotaWindow($0) }
        }
    }
}

// Ratios are used only for the user's explicit manual calibration.
// Automatic positioning always uses fresh Accessibility geometry.
struct RelativePlacement {
    static func frame(owner: CGRect, x: Double, y: Double, width: Double, height: Double) -> CGRect {
        let w = min(CGFloat(width), owner.width)
        let h = min(CGFloat(height), owner.height)
        let left = owner.minX + max(0, min(1, x)) * max(0, owner.width - w)
        let top = owner.minY + max(0, min(1, y)) * max(0, owner.height - h)
        return CGRect(x: left, y: top, width: w, height: h)
    }
}
