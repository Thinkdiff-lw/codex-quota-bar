import AppKit
import ApplicationServices

struct ComposerAnchor {
    let owner: CGRect // Quartz / AX coordinates: y increases downwards.
    let slot: CGRect
}

enum AccessibilityAnchor {
    static func attribute(_ element: AXUIElement, _ key: String) -> CFTypeRef? {
        var value: CFTypeRef?
        return AXUIElementCopyAttributeValue(element, key as CFString, &value) == .success ? value : nil
    }

    static func rect(_ element: AXUIElement) -> CGRect? {
        guard let p = attribute(element, kAXPositionAttribute),
              let s = attribute(element, kAXSizeAttribute),
              CFGetTypeID(p) == AXValueGetTypeID(), CFGetTypeID(s) == AXValueGetTypeID() else { return nil }
        var position = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(p as! AXValue, .cgPoint, &position),
              AXValueGetValue(s as! AXValue, .cgSize, &size), size.width > 0, size.height > 0 else { return nil }
        return CGRect(origin: position, size: size)
    }

    static func window(_ pid: pid_t) -> (AXUIElement, CGRect)? {
        let application = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(application, 0.2)
        var candidate = attribute(application, kAXFocusedWindowAttribute)
        if candidate == nil { candidate = (attribute(application, kAXWindowsAttribute) as? [AXUIElement])?.first }
        guard let candidate = candidate, CFGetTypeID(candidate) == AXUIElementGetTypeID() else { return nil }
        let win = candidate as! AXUIElement
        guard let r = rect(win) else { return nil }
        return (win, r)
    }

    static func read(_ pid: pid_t) -> ComposerAnchor? {
        guard let (win, owner) = window(pid) else { return nil }
        let deadline = Date().addingTimeInterval(1.5)
        var stack: [(AXUIElement, Int)] = [(win, 0)]
        var editors: [CGRect] = []
        var buttons: [(CGRect, String)] = []
        var visited = 0
        while let (element, depth) = stack.popLast(), visited < 5000, Date() < deadline {
            visited += 1
            AXUIElementSetMessagingTimeout(element, 0.15)
            let role = attribute(element, kAXRoleAttribute) as? String ?? ""
            if role == kAXTextAreaRole || role == kAXTextFieldRole {
                if let r = rect(element), r.width > 200, r.midY > owner.midY,
                   r.height >= 18, r.maxY < owner.maxY + 4 { editors.append(r) }
            }
            if role == kAXButtonRole, let r = rect(element), r.midY > owner.midY {
                // Only button metadata; never request AXValue (editor/chat content).
                let title = attribute(element, kAXTitleAttribute) as? String ?? ""
                let description = attribute(element, kAXDescriptionAttribute) as? String ?? ""
                buttons.append((r, title + " " + description))
            }
            if depth < 24, let children = attribute(element, kAXChildrenAttribute) as? [AXUIElement] {
                stack.append(contentsOf: children.reversed().map { ($0, depth + 1) })
            }
        }
        guard let editor = editors.sorted(by: { $0.maxY > $1.maxY }).first else { return nil }
        let row = buttons.filter { b in
            b.0.midY >= editor.maxY - 8 && b.0.midY < editor.maxY + 100 &&
            b.0.minX >= editor.minX - 8 && b.0.maxX <= editor.maxX + 8
        }
        let permissionWords = ["权限", "批准", "permission", "approval", "approve", "access"]
        guard let left = row.filter({ b in permissionWords.contains { b.1.lowercased().contains($0) } })
            .sorted(by: { $0.0.minX < $1.0.minX }).first else { return nil }
        let right = row.filter { $0.0.minX > left.0.maxX + 20 && abs($0.0.midY - left.0.midY) < 14 }
            .sorted(by: { $0.0.minX < $1.0.minX }).first
        guard let right = right else { return nil }
        let slot = CGRect(x: left.0.maxX + 8, y: left.0.midY - 13,
                          width: right.0.minX - left.0.maxX - 16, height: 26)
        guard slot.width >= 100, owner.contains(slot) else { return nil }
        return ComposerAnchor(owner: owner, slot: slot)
    }

    static func cocoaRect(_ ax: CGRect) -> CGRect {
        // The first display is the Quartz primary display, even when the Dock
        // and the user's current window are on a different monitor.
        let primaryTop = NSScreen.screens.first?.frame.maxY ?? 0
        return CGRect(x: ax.minX, y: primaryTop - ax.maxY, width: ax.width, height: ax.height)
    }
}
