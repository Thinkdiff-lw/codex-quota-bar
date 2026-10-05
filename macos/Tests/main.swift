import Foundation

var checks = 0
func check(_ condition: @autoclosure () -> Bool, _ name: String) {
    checks += 1
    guard condition() else { fatalError("FAIL: " + name) }
}

let now = Date(timeIntervalSince1970: 1700000000)
let weekly: [String: Any] = ["usedPercent": 31, "windowDurationMins": 10080,
                           "resetsAt": now.addingTimeInterval(6 * 86400 + 15 * 3600).timeIntervalSince1970]
let q = QuotaWindow(weekly)
check(q.remaining == 69, "usage converts to remaining")
check(q.label == "周剩余", "weekly label")
check(q.countdown(now: now) == "6天15小时", "days and hours")
check(QuotaWindow(["usedPercent": 1]).remaining == 99, "numeric one is not a boolean")
check(QuotaWindow(["usedPercent": true]).remaining == nil, "boolean is invalid")
check(QuotaWindow(["usedPercent": 140]).remaining == 0, "upper clamp")
check(QuotaWindow(["usedPercent": -4]).remaining == 100, "lower clamp")
check(QuotaWindow([:]).percentage == "未知", "missing usage never becomes full")
check(QuotaWindow(["resetsAt": 1700000030]).countdown(now: now) == "1分钟", "sub-minute rounds up")
check(QuotaWindow(["resetsAt": 1699999999]).countdown(now: now) == "等待重置", "expired quota does not reset locally")
check(QuotaWindow(["resetsAt": 1700000000]).resetText == "11/15 06:13", "Beijing reset date")
let snapshot = QuotaSnapshot(["rateLimitsByLimitId": ["other": ["primary": weekly],
                                                     "codex": ["secondary": weekly]]])
check(snapshot.selected(nil) == "codex", "prefer Codex bucket")
check(snapshot.selected("other") == "other", "respect user's bucket")
check(snapshot.windows("codex").count == 1, "no fabricated primary window")
check(QuotaSnapshot(["rateLimits": ["primary": weekly]]).windows("codex").count == 1, "legacy API fallback")
check(QuotaSnapshot([:]).windows(nil).isEmpty, "no quotas handled safely")
let owner = CGRect(x: -1000, y: 80, width: 800, height: 600)
let placement = RelativePlacement.frame(owner: owner, x: 0.5, y: 1, width: 360, height: 26)
check(owner.contains(placement), "manual placement stays inside negative-coordinate monitor")
let resized = RelativePlacement.frame(owner: CGRect(x: 100, y: 50, width: 250, height: 200),
                                     x: 2, y: -1, width: 360, height: 26)
check(resized == CGRect(x: 100, y: 50, width: 250, height: 26), "resize clamps width and ratios")

// Real stdio lifecycle against a fake local server; no credentials or network.
// Kept in a fresh temp directory; no batch deletion is performed.
let temp = FileManager.default.temporaryDirectory.appendingPathComponent("quota-test-" + UUID().uuidString)
try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
let fake = temp.appendingPathComponent("codex")
let script = """
#!/usr/bin/env python3
import json, sys
initialized = False
for line in sys.stdin:
    message = json.loads(line)
    method = message['method']
    if method == 'initialized':
        initialized = True
        continue
    if method == 'initialize':
        result = {}
    elif method == 'account/rateLimits/read' and initialized:
        result = {'rateLimits': {'primary': {'usedPercent': 25, 'windowDurationMins': 10080}}}
    else:
        print(json.dumps({'id': message['id'], 'error': {'message': 'unexpected method'}}), flush=True)
        continue
    print(json.dumps({'method': 'ignore/notification'}), flush=True)
    print(json.dumps({'id': message['id'], 'result': result}), flush=True)
"""
try script.write(to: fake, atomically: true, encoding: .utf8)
try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fake.path)
let server = try AppServer(executable: fake)
let read = try server.read()
check(read.windows("codex").first?.remaining == 75, "initialize, initialized, read protocol")
let second = try server.read()
check(second.windows("codex").count == 1, "persistent server supports repeat reads")
server.close()
print("PASS: \(checks) checks")
