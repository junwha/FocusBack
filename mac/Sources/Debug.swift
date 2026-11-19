// Test hooks, only active when the app is started with FOCUSBACK_DEBUG=1.
// Send a command from a shell:  FocusBack.app/Contents/MacOS/FocusBack --debug "<command>"
import AppKit
import PQCore

enum Debug {
    static func run(_ cmd: String, _ w: Widget) {
        let parts = cmd.split(separator: " ", maxSplits: 1).map(String.init)
        let arg = parts.count > 1 ? parts[1] : ""
        let c = core.c
        switch parts.first ?? "" {
        case "focus10": w.startFocus(test: true)                     // same as Option+click on ▶ Start
        case "stop": pq_stop(c, now()); w.awayToast?.hide()
        case "break":                                                // a 10 s test session that has just ended
            w.startFocus(test: true)
            w.handle(pq_tick(c, now() + 11))
        case "extend": pq_extend(c, now()); w.closeBreak(); w.startTick()
        case "bgbreak": w.hideBreak()
        case "showbreak": pq_break_show(c); w.showBreak()
        case "skip": pq_break_skip(c, now()); w.closeBreak()
        case "soon": w.showSoon()
        case "stats": w.openStats()
        case "range": w.stats?.range = arg; w.stats?.reload()
        case "closestats": w.stats?.close()
        case "lang": w.setLang(arg == "ko")
        case "select": w.select(Int32(arg) ?? 0)
        case "hotkey": w.hotkey()
        case "snap": snap(w, URL(fileURLWithPath: arg, isDirectory: true))
        case "quit": NSApp.terminate(nil)
        default: NSLog("FocusBack debug: unknown command %@", cmd)
        }
        w.needsDisplay = true
    }

    /// render every visible window of ours into PNGs (no screen-recording permission needed)
    static func snap(_ w: Widget, _ dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        var shots: [(String, NSView)] = [("widget", w)]
        if let t = w.awayToast, t.visible { shots.append(("away", t.view)) }
        if let t = w.soonToast, t.visible { shots.append(("soon", t.view)) }
        if let b = w.breakWins?.first, b.isVisible, let v = b.contentView { shots.append(("break", v)) }
        if let s = w.stats, s.panel.isVisible { shots.append(("stats", s.view)) }
        var info = ""
        for (name, v) in shots {
            v.displayIfNeeded()
            guard let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) else { continue }
            v.cacheDisplay(in: v.bounds, to: rep)
            try? rep.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent(name + ".png"))
            info += "\(name)\t\(v.window?.windowNumber ?? 0)\t\(NSStringFromRect(v.window?.frame ?? .zero))\n"
        }
        try? info.write(to: dir.appendingPathComponent("windows.txt"), atomically: true, encoding: .utf8)
    }
}
