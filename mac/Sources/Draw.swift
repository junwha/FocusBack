// Palette, fonts, drawing helpers and the small borderless panels used everywhere.
import AppKit

// UI language: English or Korean. Default follows the system language; switch from the menu.
enum Lang {
    static var ko = false
    static func load(_ url: URL) {
        let v = (try? String(contentsOf: url, encoding: .utf8))?.trimmingCharacters(in: .whitespacesAndNewlines)
        let sys = (Locale.preferredLanguages.first ?? "").hasPrefix("ko")
        ko = v == "ko" || (v != "en" && sys)
    }
}
func T(_ en: String, _ ko: String) -> String { Lang.ko ? ko : en }

func rgb(_ r: Int, _ g: Int, _ b: Int, _ a: Int = 255) -> NSColor {
    NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: CGFloat(a) / 255)
}

func lerp(_ a: NSColor, _ b: NSColor, _ t: CGFloat) -> NSColor {
    let x = a.usingColorSpace(.sRGB)!, y = b.usingColorSpace(.sRGB)!
    return NSColor(srgbRed: x.redComponent + (y.redComponent - x.redComponent) * t,
                   green: x.greenComponent + (y.greenComponent - x.greenComponent) * t,
                   blue: x.blueComponent + (y.blueComponent - x.blueComponent) * t, alpha: 1)
}

// pastel palette (same values as the Windows version)
enum P {
    static let bg = rgb(250, 247, 242)        // cream
    static let ink = rgb(58, 54, 72)          // soft charcoal text
    static let muted = rgb(160, 150, 170)     // header text
    static let inputBg = NSColor.white
    static let greenFill = rgb(198, 238, 208), greenDeep = rgb(70, 155, 95)
    static let redFill = rgb(255, 168, 168), redDeep = rgb(210, 80, 85)
    static let grayDeep = rgb(150, 145, 160)
    static let pill = rgb(235, 232, 240), pillLight = rgb(240, 236, 244), pillOff = rgb(205, 200, 212)
    static let dash = rgb(215, 208, 222)
    static let lav = rgb(232, 226, 246), lavDeep = rgb(125, 105, 170), lavMid = rgb(180, 160, 220)
    static let breakBg = rgb(52, 46, 68)
    static let groupColors = [rgb(170, 150, 215), rgb(240, 160, 120), rgb(110, 170, 220), rgb(120, 190, 160)]
    static func groupColor(_ g: Int) -> NSColor { groupColors[(max(1, g) - 1) % groupColors.count] }
}

// Windows sizes are in points at 96 dpi; AppKit points are 72-based pixels -> scale by 96/72
func pt(_ v: CGFloat) -> CGFloat { v * 96 / 72 }

func pick(_ size: CGFloat, _ names: [String], _ weight: NSFont.Weight) -> NSFont {
    for n in names { if let f = NSFont(name: n, size: pt(size)) { return f } }
    return NSFont.systemFont(ofSize: pt(size), weight: weight)
}

enum F {
    static let main = pick(9.5, ["Pretendard-Medium", "NotoSansKR-Medium"], .medium)
    static let age = pick(8.5, ["Pretendard-SemiBold", "NotoSansKR-SemiBold"], .semibold)
    static let header = pick(8, ["Pretendard-Regular", "NotoSansKR-Regular"], .regular)
    static let menu = pick(9, ["Pretendard-Regular", "NotoSansKR-Regular"], .regular)
    static let big = pick(40, ["Pretendard-SemiBold", "NotoSansKR-SemiBold"], .semibold)
    static let big26 = pick(26, ["Pretendard-SemiBold", "NotoSansKR-SemiBold"], .semibold)
    static let mid = pick(13, ["Pretendard-SemiBold", "NotoSansKR-SemiBold"], .semibold)
    static let title = pick(14, ["Pretendard-SemiBold", "NotoSansKR-SemiBold"], .semibold)
    static let kpi = pick(18, ["Pretendard-SemiBold", "NotoSansKR-SemiBold"], .semibold)
    static let small = pick(8.5, ["Pretendard-Regular", "NotoSansKR-Regular"], .regular)
}

enum HA { case left, center, right }

func measure(_ s: String, _ font: NSFont) -> CGFloat {
    ceil((s as NSString).size(withAttributes: [.font: font]).width)
}

/// single line, vertically centered, ellipsis at the end
func drawText(_ s: String, _ font: NSFont, _ r: NSRect, _ color: NSColor, _ align: HA = .left) {
    guard r.width > 1, !s.isEmpty else { return }
    let ps = NSMutableParagraphStyle()
    ps.lineBreakMode = .byTruncatingTail
    ps.alignment = align == .left ? .left : align == .center ? .center : .right
    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: ps]
    let h = ceil(font.ascender - font.descender + font.leading)
    let rr = NSRect(x: r.minX, y: r.minY + (r.height - h) / 2, width: r.width, height: h)
    (s as NSString).draw(with: rr, options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], attributes: attrs)
}

/// several lines, centered both ways
func drawLines(_ s: String, _ font: NSFont, _ r: NSRect, _ color: NSColor) {
    let ps = NSMutableParagraphStyle(); ps.alignment = .center
    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: ps]
    let b = (s as NSString).boundingRect(with: NSSize(width: r.width, height: r.height), options: [.usesLineFragmentOrigin], attributes: attrs)
    (s as NSString).draw(with: NSRect(x: r.minX, y: r.minY + (r.height - b.height) / 2, width: r.width, height: b.height),
                         options: [.usesLineFragmentOrigin], attributes: attrs)
}

/// rounded rectangle; rad like the Windows Round(): corner radius, capped by the rect size
func round(_ r: NSRect, _ rad: CGFloat) -> NSBezierPath {
    let d = min(rad * 2, min(r.width, r.height))
    return NSBezierPath(roundedRect: r, xRadius: d / 2, yRadius: d / 2)
}

func fill(_ p: NSBezierPath, _ c: NSColor) { c.setFill(); p.fill() }
func stroke(_ p: NSBezierPath, _ c: NSColor, _ w: CGFloat, dash: Bool = false) {
    c.setStroke(); p.lineWidth = w
    if dash { p.setLineDash([w * 3, w], count: 2, phase: 0) } else { p.setLineDash(nil, count: 0, phase: 0) }
    p.stroke()
}

func pill(_ r: NSRect, _ fillC: NSColor, _ fg: NSColor, _ text: String, _ font: NSFont) {
    fill(round(r, r.height / 2), fillC)
    drawText(text, font, r, fg, .center)
}

func clockText(_ secs: Double) -> String {
    let t = Int(ceil(max(0, secs)))
    return "\(t / 60):" + String(format: "%02d", t % 60)
}

func fmtMin(_ m: Double) -> String {
    let v = Int(m.rounded())
    return v < 60 ? "\(v)" + T(" min", "분") : "\(v / 60)" + T("h", "시간") + (v % 60 > 0 ? " \(v % 60)" + T("m", "분") : "")
}

enum Sound {
    static func beep() { NSSound.beep() }
    static func exclamation() { NSSound(named: "Funk")?.play() }
    static func asterisk() { NSSound(named: "Glass")?.play() }
}

// ---------- panels ----------

/// borderless panel that never activates the app; becomes key only when a text field needs it
final class FloatPanel: NSPanel {
    var keyable = true
    override var canBecomeKey: Bool { keyable }
    override var canBecomeMain: Bool { false }
    var onKey: ((NSEvent) -> Bool)?
    override func keyDown(with e: NSEvent) { if onKey?(e) != true { super.keyDown(with: e) } }
    override func cancelOperation(_ sender: Any?) { if let k = onKey, let e = NSApp.currentEvent, k(e) { return } }
}

func makePanel(_ rect: NSRect, level: NSWindow.Level, shadow: Bool = true) -> FloatPanel {
    let p = FloatPanel(contentRect: rect, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
    p.isFloatingPanel = true
    p.level = level
    p.backgroundColor = .clear
    p.isOpaque = false
    p.hasShadow = shadow
    p.hidesOnDeactivate = false
    p.isReleasedWhenClosed = false
    p.becomesKeyOnlyIfNeeded = true
    p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
    return p
}

/// flipped view that paints and reports mouse events through closures
class PaintView: NSView {
    var paint: ((PaintView) -> Void)?
    var down: ((NSEvent, NSPoint) -> Void)?
    var moved: ((NSPoint) -> Void)?
    var exited: (() -> Void)?
    var wheel: ((NSEvent, NSPoint) -> Void)?
    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func draw(_ dirtyRect: NSRect) { paint?(self) }
    override func mouseDown(with e: NSEvent) { down?(e, convert(e.locationInWindow, from: nil)) }
    override func mouseMoved(with e: NSEvent) { moved?(convert(e.locationInWindow, from: nil)) }
    override func mouseExited(with e: NSEvent) { exited?() }
    override func scrollWheel(with e: NSEvent) { wheel?(e, convert(e.locationInWindow, from: nil)) }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                       owner: self, userInfo: nil))
    }
}

/// small always-on-top card with rounded corners (Windows: Toast)
final class Toast {
    let panel: FloatPanel
    let view = PaintView()
    init(size: NSSize, level: NSWindow.Level = .statusBar, radius: CGFloat = 11) {
        panel = makePanel(NSRect(origin: .zero, size: size), level: level)
        panel.keyable = false
        panel.contentView = view
        view.wantsLayer = true
        view.layer?.cornerRadius = radius
        view.layer?.masksToBounds = true
    }
    func showCentered() {
        let wa = (NSScreen.screens.first ?? NSScreen.main!).visibleFrame
        let s = panel.frame.size
        panel.setFrameOrigin(NSPoint(x: wa.minX + (wa.width - s.width) / 2, y: wa.minY + (wa.height - s.height) / 2))
        panel.orderFrontRegardless()
        view.needsDisplay = true
    }
    func hide() { panel.orderOut(nil) }
    var visible: Bool { panel.isVisible }
}

/// hover hint (Windows: ToolTip)
final class Tip {
    private let panel: FloatPanel
    private let view = PaintView()
    private var text = ""
    private var timer: Timer?
    private let font = F.menu
    init() {
        panel = makePanel(NSRect(x: 0, y: 0, width: 10, height: 10), level: .popUpMenu, shadow: true)
        panel.keyable = false
        panel.ignoresMouseEvents = true
        panel.contentView = view
        view.paint = { [unowned self] v in
            let b = v.bounds.insetBy(dx: 0.5, dy: 0.5)
            fill(round(b, 5), .white)
            stroke(round(b, 5), P.dash, 1)
            var y: CGFloat = 5
            let lh = ceil(self.font.ascender - self.font.descender + self.font.leading) + 1
            for line in self.text.components(separatedBy: "\n") {
                drawText(line, self.font, NSRect(x: 8, y: y, width: v.bounds.width - 14, height: lh), P.ink)
                y += lh
            }
        }
    }
    /// p = top-left in screen coordinates
    func show(_ t: String, at p: NSPoint, seconds: TimeInterval) {
        text = t
        let lines = t.components(separatedBy: "\n")
        let lh = ceil(font.ascender - font.descender + font.leading) + 1
        let w = min(520, (lines.map { measure($0, font) }.max() ?? 0) + 18)
        let h = CGFloat(lines.count) * lh + 10
        panel.setFrame(NSRect(x: p.x, y: p.y - h, width: w, height: h), display: true)
        view.needsDisplay = true
        panel.orderFrontRegardless()
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: seconds, repeats: false) { [weak self] _ in self?.hide() }
    }
    func hide() { timer?.invalidate(); timer = nil; panel.orderOut(nil) }
}

/// menu item that runs a closure
final class MenuAction: NSObject {
    let fn: () -> Void
    init(_ fn: @escaping () -> Void) { self.fn = fn }
    @objc func run(_ sender: Any?) { fn() }
}

func menuItem(_ title: String, checked: Bool = false, _ fn: @escaping () -> Void) -> NSMenuItem {
    let a = MenuAction(fn)
    let m = NSMenuItem(title: title, action: #selector(MenuAction.run(_:)), keyEquivalent: "")
    m.target = a
    m.representedObject = a // keep the action alive
    m.state = checked ? .on : .off
    return m
}
