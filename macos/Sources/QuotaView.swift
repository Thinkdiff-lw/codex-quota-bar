import AppKit

final class QuotaPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

final class QuotaView: NSView {
    var windows: [QuotaWindow] = [] { didSet { needsDisplay = true } }
    var status = "读取额度…" { didSet { needsDisplay = true } }
    var stale = false { didSet { needsDisplay = true } }
    var calibrating = false { didSet { needsDisplay = true } }
    var onDrag: ((NSPoint) -> Void)?
    var onDragEnd: (() -> Void)?
    var contextMenu: NSMenu?
    private var dragPoint: NSPoint?
    override var isFlipped: Bool { true }

    private let font = NSFont.systemFont(ofSize: 12, weight: .regular)
    private var secondary: NSColor { NSColor.secondaryLabelColor }

    private func text(_ string: String, x: CGFloat, bright: Bool = false) {
        // Every label and number shares the same font and y coordinate.
        let attributes: [NSAttributedString.Key: Any] = [
            .font: font, .foregroundColor: bright ? NSColor.labelColor : secondary
        ]
        let value = string as NSString
        let h = value.size(withAttributes: attributes).height
        value.draw(at: NSPoint(x: x, y: floor((bounds.height - h) / 2)), withAttributes: attributes)
    }

    private func separator(_ x: CGFloat) {
        NSColor.separatorColor.setStroke()
        let p = NSBezierPath()
        p.move(to: NSPoint(x: x + 0.5, y: 6))
        p.line(to: NSPoint(x: x + 0.5, y: bounds.height - 6))
        p.lineWidth = 1
        p.stroke()
    }

    private func symbol(_ name: String, x: CGFloat) {
        guard let image = NSImage(systemSymbolName: name, accessibilityDescription: nil) else { return }
        let config = NSImage.SymbolConfiguration(pointSize: 13, weight: .regular)
            .applying(NSImage.SymbolConfiguration(paletteColors: [secondary]))
        let icon = image.withSymbolConfiguration(config) ?? image
        icon.draw(in: NSRect(x: x, y: (bounds.height - 16) / 2, width: 16, height: 16),
                  from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
    }

    override func draw(_ dirtyRect: NSRect) {
        let dark = effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        (dark ? NSColor(calibratedWhite: 0.16, alpha: 1) : NSColor(calibratedWhite: 0.95, alpha: 1)).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: 6, yRadius: 6).fill()
        if calibrating { text("拖到输入框空白处 · 右键完成", x: 9); return }
        guard let quota = windows.first else { text(status, x: 9); return }
        let center = NSPoint(x: 13, y: bounds.midY)
        let circle = NSBezierPath(ovalIn: NSRect(x: 6, y: center.y - 7, width: 14, height: 14))
        NSColor.tertiaryLabelColor.setStroke()
        circle.lineWidth = 2
        circle.stroke()
        if let remaining = quota.remaining, remaining > 0 {
            secondary.setStroke()
            let arc = NSBezierPath()
            arc.appendArc(withCenter: center, radius: 7, startAngle: -90,
                          endAngle: -90 + CGFloat(remaining) * 3.6, clockwise: false)
            arc.lineWidth = 2
            arc.lineCapStyle = .round
            arc.stroke()
        }
        var label = quota.label
        let valueWidth = (quota.percentage as NSString).size(withAttributes: [.font: font]).width
        if 26 + (label as NSString).size(withAttributes: [.font: font]).width + 6 + valueWidth > bounds.width - 4 {
            label = "剩余"
        }
        text(label, x: 26)
        let labelWidth = (label as NSString).size(withAttributes: [.font: font]).width
        let percentageX = 26 + ceil(labelWidth) + 6
        text(quota.percentage, x: percentageX, bright: true)
        var x = percentageX + 44
        if let countdown = quota.countdown(), bounds.width >= x + 120 {
            separator(x); x += 10
            symbol("clock", x: x); x += 21
            text(countdown, x: x); x += 88
            if let date = quota.resetText, bounds.width >= x + 120 {
                separator(x); x += 10
                symbol("calendar", x: x); x += 21
                text(date, x: x)
            }
        }
        if stale { text("·", x: bounds.width - 9) }
    }

    override func menu(for event: NSEvent) -> NSMenu? { contextMenu }
    override func mouseDown(with event: NSEvent) { dragPoint = NSEvent.mouseLocation }
    override func mouseDragged(with event: NSEvent) {
        guard let previous = dragPoint, let window = window else { return }
        let current = NSEvent.mouseLocation
        let origin = NSPoint(x: window.frame.minX + current.x - previous.x,
                             y: window.frame.minY + current.y - previous.y)
        window.setFrameOrigin(origin)
        dragPoint = current
        onDrag?(origin)
    }
    override func mouseUp(with event: NSEvent) { dragPoint = nil; onDragEnd?() }
}
