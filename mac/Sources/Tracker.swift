// Which window is the user in? NSWorkspace app activation + Accessibility (focused window changes).
// Without the Accessibility permission everything works per app instead of per window.
import AppKit
import ApplicationServices

@_silgen_name("_AXUIElementGetWindow")
func _AXUIElementGetWindow(_ element: AXUIElement, _ wid: UnsafeMutablePointer<CGWindowID>) -> AXError

final class Tracker {
    var onChange: (() -> Void)?
    private var observer: AXObserver?
    private var observedPid: pid_t = 0
    private(set) var trusted = AXIsProcessTrusted()
    static let ignored: Set<String> = ["com.apple.dock", "com.apple.loginwindow", "com.apple.Spotlight", "com.apple.controlcenter",
                                       "com.apple.notificationcenterui", "com.apple.systemuiserver", "com.apple.screencaptureui",
                                       "com.apple.WindowManager"]
    static let myPid = ProcessInfo.processInfo.processIdentifier

    init() {
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] _ in
            self?.appChanged()
        }
    }

    /// re-check the permission (cheap); true if it changed
    @discardableResult func refreshTrust() -> Bool {
        let t = AXIsProcessTrusted()
        if t == trusted { return false }
        trusted = t
        if !t { removeObserver() }
        return true
    }

    func promptForTrust() {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(opts)
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    private func appChanged() {
        refreshTrust()
        if trusted, let app = NSWorkspace.shared.frontmostApplication { observe(app.processIdentifier) }
        onChange?()
    }

    private func removeObserver() {
        if let o = observer { CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(o), .defaultMode) }
        observer = nil; observedPid = 0
    }

    private func observe(_ pid: pid_t) {
        if pid == observedPid || pid == Tracker.myPid { return }
        removeObserver()
        var obs: AXObserver?
        let cb: AXObserverCallback = { _, _, _, refcon in
            guard let refcon = refcon else { return }
            Unmanaged<Tracker>.fromOpaque(refcon).takeUnretainedValue().onChange?()
        }
        guard AXObserverCreate(pid, cb, &obs) == .success, let o = obs else { return }
        let app = AXUIElementCreateApplication(pid)
        let me = Unmanaged.passUnretained(self).toOpaque()
        AXObserverAddNotification(o, app, kAXFocusedWindowChangedNotification as CFString, me)
        AXObserverAddNotification(o, app, kAXMainWindowChangedNotification as CFString, me)
        CFRunLoopAddSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(o), .defaultMode)
        observer = o; observedPid = pid
    }

    func start() {
        if trusted, let app = NSWorkspace.shared.frontmostApplication { observe(app.processIdentifier) }
    }

    // ---------- current window ----------

    static func procOf(_ app: NSRunningApplication) -> String { app.bundleIdentifier ?? app.localizedName ?? "" }

    /// the window the user is in now; nil for things that aren't app windows (Dock, desktop, this widget)
    func current() -> WinRef? {
        guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
        let pid = app.processIdentifier
        let proc = Tracker.procOf(app)
        if pid == Tracker.myPid || Tracker.ignored.contains(proc) { return nil }
        let name = app.localizedName ?? proc
        if !trusted { return WinRef(wid: 0, pid: pid, proc: proc, title: name) }
        let axApp = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(axApp, 0.5)
        guard let w: AXUIElement = Tracker.attr(axApp, kAXFocusedWindowAttribute) ?? Tracker.attr(axApp, kAXMainWindowAttribute) else {
            // no window (e.g. Finder showing only the desktop): Finder = desktop -> ignore; other apps: app-level
            return proc == "com.apple.finder" ? nil : WinRef(wid: 0, pid: pid, proc: proc, title: name)
        }
        var wid: CGWindowID = 0
        _ = _AXUIElementGetWindow(w, &wid)
        let title: String = Tracker.attr(w, kAXTitleAttribute) ?? ""
        return WinRef(wid: wid, pid: pid, proc: proc, title: title.isEmpty ? name : title)
    }

    static func attr<T>(_ e: AXUIElement, _ name: String) -> T? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(e, name as CFString, &v) == .success, let v = v else { return nil }
        return v as? T
    }

    // ---------- CG window list ----------

    struct CGWin { let wid: UInt32; let pid: pid_t; let frame: NSRect /* Cocoa coords */ }

    /// app windows front-to-back (layer 0, visible)
    static func cgWindows(onScreen: Bool = true) -> [CGWin] {
        let opts: CGWindowListOption = onScreen ? [.optionOnScreenOnly, .excludeDesktopElements] : [.optionAll, .excludeDesktopElements]
        guard let info = CGWindowListCopyWindowInfo(opts, kCGNullWindowID) as? [[String: Any]] else { return [] }
        let ph = NSScreen.screens.first?.frame.height ?? 0
        var out: [CGWin] = []
        for d in info {
            guard (d[kCGWindowLayer as String] as? Int) == 0,
                  let wid = d[kCGWindowNumber as String] as? UInt32,
                  let pid = d[kCGWindowOwnerPID as String] as? pid_t,
                  let bd = d[kCGWindowBounds as String] as? NSDictionary,
                  let r = CGRect(dictionaryRepresentation: bd) else { continue }
            if (d[kCGWindowAlpha as String] as? Double ?? 1) <= 0 || pid == myPid { continue }
            out.append(CGWin(wid: wid, pid: pid, frame: NSRect(x: r.minX, y: ph - r.maxY, width: r.width, height: r.height)))
        }
        return out
    }

    static func area(_ r: NSRect) -> CGFloat { r.width <= 0 || r.height <= 0 ? 0 : r.width * r.height }

    /// h plus the windows tiled next to it: walk front-to-back below h and take every big window
    /// on the same screen that is (almost) fully visible, i.e. not covered by anything above it.
    func snapGroup(_ h: WinRef) -> [WinRef] {
        var group = [h]
        guard trusted, h.wid != 0 else { return group }
        let wins = Tracker.cgWindows()
        guard let me = wins.first(where: { $0.wid == h.wid }) else { return group }
        let center = NSPoint(x: me.frame.midX, y: me.frame.midY)
        let screen = (NSScreen.screens.first { $0.frame.contains(center) } ?? NSScreen.screens.first!).visibleFrame
        let rh = me.frame.intersection(screen)
        if Tracker.area(rh) >= Tracker.area(screen) * 0.85 { return group } // maximized: nothing beside it
        var above = [rh]
        var covered = Tracker.area(rh)
        var past = false
        for w in wins {
            if w.wid == h.wid { past = true; continue }
            let r = w.frame.intersection(screen)
            if Tracker.area(r) < Tracker.area(screen) * 0.15 { if past { above.append(r) }; continue } // small: still occludes
            if !past { above.append(r); continue }
            var hidden: CGFloat = 0
            for a in above { hidden += Tracker.area(a.intersection(r)) }
            if hidden <= Tracker.area(r) / 10, let app = NSRunningApplication(processIdentifier: w.pid) {
                let proc = Tracker.procOf(app)
                if !Tracker.ignored.contains(proc) {
                    group.append(WinRef(wid: w.wid, pid: w.pid, proc: proc, title: Tracker.title(of: w.wid, pid: w.pid) ?? app.localizedName ?? proc))
                    covered += Tracker.area(r)
                }
            }
            above.append(r)
            if covered >= Tracker.area(screen) * 0.95 || group.count >= 4 { break } // screen filled
        }
        return group
    }

    // ---------- AX window lookup ----------

    static func axWindows(_ pid: pid_t) -> [(AXUIElement, UInt32)] {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.5)
        guard let ws: [AXUIElement] = attr(app, kAXWindowsAttribute) else { return [] }
        return ws.map { w in var id: CGWindowID = 0; _ = _AXUIElementGetWindow(w, &id); return (w, id) }
    }

    static func title(of wid: UInt32, pid: pid_t) -> String? {
        guard let w = axWindows(pid).first(where: { $0.1 == wid })?.0 else { return nil }
        return attr(w, kAXTitleAttribute)
    }

    static func apps(_ proc: String) -> [NSRunningApplication] {
        NSWorkspace.shared.runningApplications.filter { procOf($0) == proc && !$0.isTerminated }
    }

    /// re-find windows after a restart: same id if still alive, else exact title, else any window of the app.
    /// never map two links onto the same window. Without the permission links stay app-level.
    func resolve(_ it: Item) {
        guard trusted else { return }
        let alive = Dictionary(Tracker.cgWindows(onScreen: false).map { ($0.wid, $0.pid) }, uniquingKeysWith: { a, _ in a })
        var used = Set<UInt32>()
        var pending: [Win] = []
        for w in it.wins {
            if w.wid != 0, !used.contains(w.wid), let pid = alive[w.wid],
               let app = NSRunningApplication(processIdentifier: pid), w.proc.isEmpty || Tracker.procOf(app) == w.proc {
                used.insert(w.wid)
            } else if w.wid != 0 { pending.append(w) }
        }
        for w in pending {
            w.wid = 0
            if w.proc.isEmpty { continue }
            var byTitle: UInt32 = 0, byProc: UInt32 = 0
            outer: for app in Tracker.apps(w.proc) {
                for (ax, id) in Tracker.axWindows(app.processIdentifier) where id != 0 && !used.contains(id) {
                    let t: String = Tracker.attr(ax, kAXTitleAttribute) ?? ""
                    if !t.isEmpty && t == w.title { byTitle = id; break outer }
                    if byProc == 0 { byProc = id }
                }
            }
            w.wid = byTitle != 0 ? byTitle : byProc // 0 = app-level until it's linked again
            if w.wid != 0 { used.insert(w.wid) }
        }
    }

    func alive(_ w: Win) -> Bool {
        if Tracker.apps(w.proc).isEmpty { return false }
        if w.wid == 0 || !trusted { return true }
        return Tracker.cgWindows(onScreen: false).contains { $0.wid == w.wid }
    }

    /// bring the window forward (and un-minimize it); activate = also give it focus
    func raise(_ w: Win, activate: Bool) {
        guard let app = Tracker.apps(w.proc).first else { return }
        if trusted && w.wid != 0, let ax = Tracker.axWindows(app.processIdentifier).first(where: { $0.1 == w.wid })?.0 {
            AXUIElementSetAttributeValue(ax, kAXMinimizedAttribute as CFString, kCFBooleanFalse)
            if activate { AXUIElementSetAttributeValue(ax, kAXMainAttribute as CFString, kCFBooleanTrue) }
            AXUIElementPerformAction(ax, kAXRaiseAction as CFString)
        }
        if activate {
            if app.isHidden { app.unhide() }
            app.activate(options: [])
        }
    }
}
