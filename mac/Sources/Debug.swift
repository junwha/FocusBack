// Test hooks, only active when the app is started with FOCUSQUEUE_DEBUG=1.
// Send a command from a shell:  FocusQueue.app/Contents/MacOS/FocusQueue --debug "<command>"
import AppKit

enum Debug {
    static func run(_ cmd: String, _ w: Widget) {
        let parts = cmd.split(separator: " ", maxSplits: 1).map(String.init)
        let arg = parts.count > 1 ? parts[1] : ""
        switch parts.first ?? "" {
        case "focus10": w.startFocus(); if w.focusing { w.startTestRun() }   // same as Option+click on ▶ Start
        case "stop": w.stopFocus(T("Focus stopped", "집중 중지"), false)
        case "break": // what happens when a session ends, without waiting for one
            if let h = w.lastExternal { w.doneWins = [h]; w.doneItem = w.items.first { $0.has(h) } }
            w.doneStart = Date().addingTimeInterval(-600); w.doneEnd = Date(); w.breakMin = Int(arg) ?? 3
            w.startBreak()
        case "away": w.startAway(w.lastExternal)
        case "back": w.endAway(true)
        case "extend": w.extendFocus()
        case "bgbreak": w.hideBreak()
        case "showbreak": w.showBreak()
        case "skip": w.endBreak()
        case "soon": w.showSoon()
        case "stats": w.openStats()
        case "range": w.stats?.range = arg; w.stats?.view.needsDisplay = true
        case "closestats": w.stats?.close()
        case "lang": w.setLang(arg == "ko")
        case "select": w.sel = Int(arg) ?? -1; w.needsDisplay = true
        case "rename": if let i = Int(arg), i < w.items.count { w.renameItem(i) }
        case "endedit": w.endEdit(false)
        case "hotkey": w.hotkey()
        case "snap": snap(w, URL(fileURLWithPath: arg, isDirectory: true))
        case "quit": NSApp.terminate(nil)
        default: NSLog("FocusQueue debug: unknown command %@", cmd)
        }
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
