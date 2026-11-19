// The widget: task queue, input card, focus bar, grace card, break screen.
// All rules and state live in the shared C core (core/pq_core.h); this file tracks windows,
// draws what the core says and reports what the user does.
import AppKit
import PQCore

/// text field that reports when it gets focus
final class Field: NSTextField {
    var onFocus: (() -> Void)?
    override func becomeFirstResponder() -> Bool {
        let ok = super.becomeFirstResponder()
        if ok { onFocus?() }
        return ok
    }
}

final class Widget: NSView, NSTextFieldDelegate {
    // ---- geometry (points; same numbers as the Windows version at 100%) ----
    let W: CGFloat = 280, row: CGFloat = 34, doneW: CGFloat = 12, hdrH: CGFloat = 26, secPad: CGFloat = 7
    let padL: CGFloat = 10, padT: CGFloat = 6, padR: CGFloat = 10, padB: CGFloat = 10, headerH: CGFloat = 22
    var barH: CGFloat { row + 4 }
    var listW: CGFloat { W - padL - padR }
    var noticeH: CGFloat { tracker.trusted ? 0 : 20 }
    var listY: CGFloat { padT + headerH + noticeH }
    var listH: CGFloat = 0
    var ghostY: CGFloat { listY + listH }
    var barY: CGFloat { ghostY + (ghostShown ? row : 0) }

    let tracker = Tracker()
    var panel: FloatPanel!
    let input = Field()
    let editBox = Field()
    let tip = Tip()
    var editCommit: ((String) -> Void)?
    var heights: [CGFloat] = []
    var scrollY: CGFloat = 0
    var sel: Int32 = 0                  // selected item id (0 = none)
    var dragIndex = -1, tipIndex = -1
    var dragStart = NSPoint.zero
    var dragItem: Int32 = 0             // card currently lifted
    var mergeTarget: Int32 = 0          // card it would be grouped with if dropped now
    var dropTime: TimeInterval = 0
    var lastExternal: WinRef?           // last window the user was in that is not this widget
    var forceGhost = false, ghostShown = true
    var ghostTextX: CGFloat = 0
    var repaint: Timer?
    var tick: Timer?
    var minusR = NSRect.zero, plusR = NSRect.zero, goR = NSRect.zero, stopR = NSRect.zero

    var awayToast: Toast?
    var backR = NSRect.zero, quitR = NSRect.zero
    var breakWins: [FloatPanel]?
    var skipR = NSRect.zero, extR = NSRect.zero, bgR = NSRect.zero
    var soonToast: Toast?
    var stats: Stats?

    var c: OpaquePointer { core.c }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    init() {
        super.init(frame: NSRect(x: 0, y: 0, width: 280, height: 200))
        panel = makePanel(NSRect(x: 0, y: 0, width: W, height: 200), level: .floating)
        panel.contentView = self
        panel.alphaValue = CGFloat(pq_opacity(c))
        wantsLayer = true
        layer?.cornerRadius = 8
        layer?.masksToBounds = true
        panel.onKey = { [unowned self] e in self.listKey(e) }

        for f in [input, editBox] {
            f.isBordered = false
            f.focusRingType = .none
            f.drawsBackground = true
            f.font = F.main
            f.textColor = P.ink
            f.delegate = self
            f.cell?.isScrollable = true
            f.cell?.wraps = false
            f.cell?.usesSingleLineMode = true
            addSubview(f)
        }
        input.backgroundColor = P.inputBg
        input.onFocus = { [unowned self] in self.needsDisplay = true; self.updateGhost() }
        editBox.isHidden = true
        setCue()

        tracker.onChange = { [unowned self] in self.track(self.tracker.current()) }
        tracker.start()
        for id in core.ids { tracker.resolve(id) } // re-link to the live windows after a restart
        relayout()
        track(tracker.current())
        fit()
        placeDefault()
        panel.orderFrontRegardless()

        // display unplugged / resolution changed: pull the widget back if it ended up off-screen
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            self?.ensureOnScreen()
        }
        NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification, object: panel, queue: .main) { [weak self] _ in
            guard let self = self else { return }
            if self.editCommit != nil { self.endEdit(true) }
            if self.isFocused(self.input) { self.panel.makeFirstResponder(nil) }
            self.forceGhost = false
            self.updateGhost()
        }
        // ages only change every few minutes -> repaint every 20 s (near-zero CPU)
        repaint = Timer.scheduledTimer(withTimeInterval: 20, repeats: true) { [weak self] _ in self?.needsDisplay = true }
        repaint?.tolerance = 5
    }

    required init?(coder: NSCoder) { fatalError() }

    // ---------- window tracking ----------

    func track(_ h: WinRef?) {
        if tracker.refreshTrust() {
            for id in core.ids { tracker.resolve(id) } // per-window <-> per-app
            relayout(); fit(); needsDisplay = true
        }
        guard let h = h else { return }
        let prev = lastExternal
        // came back to one window of a task -> bring its partner windows up too (no focus change)
        var partners: [(pq_win, String)] = []
        if tracker.trusted {
            for id in core.ids where core.has(id, h) && !core.has(id, prev) {
                for w in core.wins(id) where w.key > 0 && w.key != h.key { partners.append((w.key, w.proc)) }
            }
        }
        lastExternal = h
        let ev = pq_activate(c, h.key, h.title, now())
        if ev == 0 { return }
        handle(ev)
        for (k, p) in partners { tracker.raise(k, p, activate: false) }
        updateGhost()
        needsDisplay = true
    }

    /// link the item to the window you were in + whatever is tiled beside it
    func attach(_ id: Int32, _ h: WinRef?) {
        guard let h = h else { return }
        for (i, w) in tracker.snapGroup(h).enumerated() { pq_link(c, id, w.key, w.proc, w.title, i == 0 ? 1 : 0) }
    }

    func addWindow(_ id: Int32, _ h: WinRef?) {
        guard id != 0, let h = h else { return }
        pq_link(c, id, h.key, h.proc, h.title, 0)
        changed()
    }

    /// bring all of the task's windows forward; focus the one you used last
    func switchTo(_ id: Int32) {
        guard id != 0 else { return }
        tracker.resolve(id)
        let live = core.wins(id).filter { tracker.alive($0.key, $0.proc) }
        let last = pq_last_win(c, id)
        guard let focus = live.first(where: { $0.key == last }) ?? live.first else { Sound.beep(); return }
        for w in live where w.key != focus.key { tracker.raise(w.key, w.proc, activate: false) }
        tracker.raise(focus.key, focus.proc, activate: true)
    }

    /// give the keyboard back to the window you were working in
    func backToWork() {
        if panel.isKeyWindow {
            panel.makeFirstResponder(nil)
            if let h = lastExternal, let app = NSRunningApplication(processIdentifier: h.pid) { app.activate(options: []) }
            if panel.isKeyWindow { panel.orderOut(nil); panel.orderFrontRegardless() } // drop key status without hiding
        }
    }

    // ---------- placement ----------

    var primary: NSScreen { NSScreen.screens.first ?? NSScreen.main! }

    /// top-right of the main screen
    func placeDefault() {
        let wa = primary.visibleFrame
        panel.setFrameOrigin(NSPoint(x: wa.maxX - W - 10, y: wa.maxY - 44 - panel.frame.height))
    }

    func ensureOnScreen() {
        let f = panel.frame
        let p = NSPoint(x: f.midX, y: f.maxY - 10)
        if NSScreen.screens.contains(where: { $0.visibleFrame.contains(p) }) { return } // still visible somewhere
        placeDefault()
    }

    func showAgain() {
        placeDefault()
        panel.orderFrontRegardless()
    }

    /// global hotkey: show the entry even for an already-linked window and type into it
    func hotkey() {
        track(tracker.current())
        forceGhost = true
        updateGhost()
        ensureOnScreen()
        panel.orderFrontRegardless()
        panel.makeKey()
        panel.makeFirstResponder(input)
    }

    func fit() {
        var total: CGFloat = 0
        for i in 0..<core.count { total += rowHeight(i) }
        let maxH = primary.visibleFrame.height * 2 / 3
        let ghost = ghostShown ? row : 0
        listH = max(0, min(total, maxH - padT - headerH - noticeH - ghost - barH - padB)) // scrolls if very long
        scrollY = max(0, min(scrollY, total - listH))
        let h = barY + barH + padB
        let f = panel.frame
        if abs(f.height - h) > 0.5 || f.width != W {
            panel.setFrame(NSRect(x: f.minX, y: f.maxY - h, width: W, height: h), display: true) // keep the top edge
            panel.invalidateShadow()
        }
        layoutGhost()
        if editCommit != nil { editBox.isHidden = true; editCommit = nil }
        needsDisplay = true
    }

    /// the queue changed (the core already saved it)
    func changed() { relayout(); updateGhost(); fit(); needsDisplay = true }

    // ---------- language ----------

    func setLang(_ ko: Bool) {
        pq_set_lang_ko(c, ko ? 1 : 0)
        setCue()
        needsDisplay = true
        awayToast?.view.needsDisplay = true
        soonToast?.view.needsDisplay = true
        breakWins?.forEach { $0.contentView?.needsDisplay = true }
        if let s = stats { s.reload() }
    }

    static func headerText() -> String { "✿  " + tr("header.title") + "    ⌃⌥Space" }

    func setCue() {
        input.placeholderAttributedString = NSAttributedString(string: tr("entry.placeholder"),
                                                               attributes: [.font: F.main, .foregroundColor: P.muted])
    }

    // ---------- menus ----------

    func opacityMenu() -> NSMenuItem {
        let m = NSMenuItem(title: tr("menu.opacity"), action: nil, keyEquivalent: "")
        let sub = NSMenu()
        let cur = Int((pq_opacity(c) * 100).rounded())
        for p in [100, 90, 80, 70, 60, 50, 40] {
            sub.addItem(menuItem("\(p)%", checked: cur == p) { [unowned self] in self.setOpacity(Double(p) / 100) })
        }
        m.submenu = sub
        return m
    }

    func commonItems(_ m: NSMenu) {
        m.addItem(menuItem(tr("menu.stats")) { [unowned self] in self.openStats() })
        m.addItem(menuItem(tr("menu.open_log")) { [unowned self] in self.openLogSheet() })
        m.addItem(opacityMenu())
        let lm = NSMenuItem(title: tr("menu.language"), action: nil, keyEquivalent: "")
        let sub = NSMenu()
        sub.addItem(menuItem("English", checked: !core.ko) { [unowned self] in self.setLang(false) })
        sub.addItem(menuItem("한국어", checked: core.ko) { [unowned self] in self.setLang(true) })
        lm.submenu = sub
        m.addItem(lm)
        m.addItem(menuItem(tr("menu.quit")) { NSApp.terminate(nil) })
    }

    func itemMenu() -> NSMenu {
        let m = NSMenu(); m.font = F.menu; m.autoenablesItems = false
        let id = sel
        m.addItem(menuItem(tr("menu.go")) { [unowned self] in self.trySwitch(id) })
        m.addItem(menuItem(tr("menu.done_mac")) { [unowned self] in self.removeSelected() })
        m.addItem(menuItem(tr("menu.link_prev")) { [unowned self] in self.addWindow(id, self.lastExternal) })
        m.addItem(menuItem(tr("menu.relink")) { [unowned self] in
            if id != 0 { self.attach(id, self.lastExternal); self.changed() }
        })
        if id != 0 && core.group(id) != 0 {
            m.addItem(menuItem(tr("menu.leave_group")) { [unowned self] in pq_leave(self.c, id); self.sel = id; self.changed() })
        }
        m.addItem(menuItem(tr("menu.to_top")) { [unowned self] in if id != 0 { pq_move(self.c, id, 0, 0); self.changed() } })
        m.addItem(menuItem(tr("menu.reset_timer")) { [unowned self] in if id != 0 { pq_reset_age(self.c, id, now()); self.changed() } })
        m.addItem(menuItem(tr("menu.reset_rounds")) { [unowned self] in if id != 0 { pq_reset_rounds(self.c, id); self.changed() } })
        m.addItem(.separator())
        commonItems(m)
        return m
    }

    func headerMenu() -> NSMenu {
        let m = NSMenu(); m.font = F.menu; m.autoenablesItems = false
        commonItems(m)
        return m
    }

    func setOpacity(_ v: Double) {
        pq_set_opacity(c, v)
        panel.alphaValue = CGFloat(pq_opacity(c))
    }

    // ---------- input ----------

    func isFocused(_ f: NSTextField) -> Bool {
        guard let ed = f.currentEditor() else { return false }
        return panel.firstResponder === ed
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy cmd: Selector) -> Bool {
        if control === input {
            if cmd == #selector(NSResponder.insertNewline(_:)) || cmd == #selector(NSResponder.insertNewlineIgnoringFieldEditor(_:)) {
                let t = input.stringValue.trimmingCharacters(in: .whitespaces)
                if t.isEmpty { return true }
                let shift = NSApp.currentEvent?.modifierFlags.contains(.shift) ?? false // Shift+Enter = top
                let id = pq_add(c, t, shift ? 1 : 0, now())
                attach(id, lastExternal)
                input.stringValue = ""
                forceGhost = false
                backToWork() // the entry turns into the item in place and you're back in your window
                changed()
                return true
            }
            if cmd == #selector(NSResponder.cancelOperation(_:)) {
                input.stringValue = ""
                forceGhost = false
                backToWork()
                updateGhost()
                return true
            }
            if cmd == #selector(NSResponder.moveUp(_:)) && core.count > 0 {
                panel.makeFirstResponder(self); select(core.id(core.count - 1)); return true
            }
            return false
        }
        if control === editBox {
            if cmd == #selector(NSResponder.insertNewline(_:)) { endEdit(true); return true }
            if cmd == #selector(NSResponder.cancelOperation(_:)) { endEdit(false); return true }
        }
        return false
    }

    func controlTextDidChange(_ obj: Notification) { if (obj.object as? NSTextField) === input { updateGhost() } }

    func controlTextDidEndEditing(_ obj: Notification) {
        let f = obj.object as? NSTextField
        if f === input { forceGhost = false; DispatchQueue.main.async { self.updateGhost(); self.needsDisplay = true } }
        else if f === editBox { endEdit(true) }
    }

    func select(_ id: Int32) { sel = id; needsDisplay = true }

    /// list keys (after Up from the entry): Delete = done, Enter = go, Opt+Up/Down = move, Esc = back
    func listKey(_ e: NSEvent) -> Bool {
        guard panel.firstResponder === self else { return false }
        let i = core.index(sel)
        switch Int(e.keyCode) {
        case 51, 117: removeSelected(); return true
        case 36, 76: trySwitch(sel); return true
        case 126:
            if e.modifierFlags.contains(.option) { moveSelected(-1) } else if i > 0 { select(core.id(i - 1)) }
            return true
        case 125:
            if e.modifierFlags.contains(.option) { moveSelected(1) } else if i >= 0 && i + 1 < core.count { select(core.id(i + 1)) }
            return true
        case 53: backToWork(); return true
        default: return false
        }
    }
    override func keyDown(with e: NSEvent) { if !listKey(e) { super.keyDown(with: e) } }

    // ---------- list geometry ----------

    func rowHeight(_ i: Int) -> CGFloat { i >= 0 && i < heights.count ? heights[i] : row }

    /// row rectangle in list coordinates (scroll applied)
    func itemRect(_ i: Int) -> NSRect {
        var y = -scrollY
        for k in 0..<i { y += rowHeight(k) }
        return NSRect(x: 0, y: y, width: listW, height: rowHeight(i))
    }

    func index(atListY y: CGFloat) -> Int {
        var top = -scrollY
        for i in 0..<core.count {
            let h = rowHeight(i)
            if y >= top && y < top + h { return i }
            top += h
        }
        return -1
    }

    /// same card shape for items and the ghost entry
    func cardRect(_ b: NSRect) -> NSRect {
        let gap: CGFloat = 3
        return NSRect(x: b.minX, y: b.minY + gap, width: b.width - 1, height: b.height - gap * 2 + gap / 2)
    }

    /// where the card sits inside its row (rows of a group leave room for the section frame)
    func cardBounds(_ i: Int, _ b: NSRect) -> NSRect {
        if core.group(core.id(i)) == 0 { return b }
        let top = core.firstOfGroup(i) ? hdrH : 0, bot = core.lastOfGroup(i) ? secPad : 0
        return NSRect(x: b.minX + secPad, y: b.minY + top, width: b.width - secPad * 2, height: b.height - top - bot)
    }

    /// section rectangle (list coordinates) of the group containing row i
    func sectionRect(_ i: Int) -> NSRect {
        let g = core.group(core.id(i))
        var a = i, b = i
        while a > 0 && core.group(core.id(a - 1)) == g { a -= 1 }
        while b + 1 < core.count && core.group(core.id(b + 1)) == g { b += 1 }
        let ra = itemRect(a), rb = itemRect(b)
        let top = ra.minY + (core.firstOfGroup(a) ? 3 : 0)
        return NSRect(x: 0, y: top, width: listW - 1, height: rb.maxY - (core.lastOfGroup(b) ? 1 : 0) - top)
    }

    /// recompute row heights (section header / bottom padding)
    func relayout() {
        heights = (0..<core.count).map { i in row + (core.firstOfGroup(i) ? hdrH : 0) + (core.lastOfGroup(i) ? secPad : 0) }
    }

    // ---------- drawing ----------

    override func draw(_ dirtyRect: NSRect) {
        NSColor.clear.set(); bounds.fill()
        fill(NSBezierPath(rect: bounds), P.bg)
        drawText(Widget.headerText(), F.header, NSRect(x: padL, y: padT, width: listW, height: headerH), P.muted)
        if !tracker.trusted {
            drawText("⚠ " + tr("mac.ax_notice"), F.small, NSRect(x: padL, y: padT + headerH, width: listW, height: noticeH - 2), P.redDeep)
        }
        NSGraphicsContext.saveGraphicsState()
        NSRect(x: padL, y: listY, width: listW, height: listH).clip()
        let t = NSAffineTransform(); t.translateX(by: padL, yBy: listY); t.concat()
        let tnow = now()
        for i in 0..<core.count {
            let b = itemRect(i)
            if b.maxY < 0 || b.minY > listH { continue }
            drawItem(i, b, tnow)
        }
        NSGraphicsContext.restoreGraphicsState()
        if ghostShown { drawGhost() }
        drawBar()
    }

    func drawItem(_ i: Int, _ b: NSRect, _ tnow: Double) {
        let id = core.id(i)
        let active = pq_active(c, id) != 0 // whole group shows "working"
        // active window's item = green; others white, one step redder per 10 min (max 6 steps); frozen while focusing
        let t = CGFloat(pq_heat(c, id, tnow)) / 6
        var fillC = active ? P.greenFill : lerp(.white, P.redFill, t)
        let deep = active ? P.greenDeep : lerp(P.grayDeep, P.redDeep, t)
        let lifted = id == dragItem
        let g = core.group(id)

        // section: one rounded tinted box around the whole group; each row paints its slice of it
        if g != 0 {
            let gc = P.groupColor(Int(g))
            let sr = sectionRect(i)
            NSGraphicsContext.saveGraphicsState()
            b.clip()
            let p = round(sr, 13)
            fill(p, lerp(P.bg, gc, 0.16))
            stroke(p, lerp(P.bg, gc, 0.45), 1.2)
            if core.firstOfGroup(i) {
                let cnt = pq_group_size(c, g)
                let hr = NSRect(x: sr.minX + 12, y: sr.minY + 1, width: sr.width - 24, height: hdrH - 4)
                let name = core.groupName(g)
                let editingHere = editCommit != nil && !editBox.isHidden && abs(editBox.frame.minY - (listY + hr.minY)) < hdrH
                if !editingHere {
                    drawText(name, F.age, NSRect(x: hr.minX, y: hr.minY, width: hr.width - 40, height: hr.height), lerp(gc, P.ink, 0.45))
                    let nw = min(measure(name, F.age), hr.width - 40)
                    drawText("· \(cnt)", F.age, NSRect(x: hr.minX + nw + 6, y: hr.minY, width: 40, height: hr.height), lerp(gc, P.bg, 0.25))
                }
            }
            NSGraphicsContext.restoreGraphicsState()
        }
        var r = cardRect(cardBounds(i, b))
        if lifted {
            // soft drop shadow + slightly inset card = "picked up"
            let sh = NSRect(x: r.minX + 2, y: r.minY + 2, width: r.width - 2, height: r.height - 1)
            fill(round(sh, 9), rgb(60, 40, 80, 40))
            r = NSRect(x: r.minX, y: r.minY - 1, width: r.width - 2, height: r.height)
            fillC = lerp(fillC, deep, 0.08)
        }
        let path = round(r.insetBy(dx: 0.5, dy: 0.5), 9)
        fill(path, fillC)
        if id == sel || lifted { stroke(path, deep, lifted ? 2 : 1.6) } else { stroke(path, lerp(fillC, deep, 0.25), 1) }
        if id == mergeTarget { // "drop here to group"
            stroke(path, P.groupColor(g != 0 ? Int(g) : 1), 2.4, dash: true)
        }

        // age badge on the right
        let age = active ? tr(core.focusing ? "item.focusing" : "item.working") : str(pq_fmt_age(c, pq_age_min(c, id, tnow)))
        let bh: CGFloat = 18, bw = measure(age, F.age) + 12
        let badge = NSRect(x: r.maxX - bw - 6, y: r.minY + (r.height - bh) / 2, width: bw, height: bh)
        fill(round(badge, bh / 2), lerp(fillC, deep, 0.12))
        drawText(age, F.age, badge, deep, .center)

        // index + text
        let txtR = NSRect(x: r.minX + doneW, y: r.minY, width: badge.minX - r.minX - doneW - 6, height: r.height)
        drawText("\(i + 1)   " + core.text(id), F.main, txtR, P.ink)
    }

    func drawGhost() {
        let r = cardRect(NSRect(x: padL, y: ghostY, width: listW, height: row))
        let p = round(r.insetBy(dx: 0.5, dy: 0.5), 9)
        fill(p, P.inputBg)
        stroke(p, isFocused(input) ? P.grayDeep : P.dash, 1.2, dash: true)
        drawText("\(core.count + 1)", F.main, NSRect(x: r.minX + doneW, y: r.minY, width: ghostTextX - doneW, height: r.height), P.muted)
    }

    func layoutGhost() {
        input.isHidden = !ghostShown
        let r = cardRect(NSRect(x: padL, y: ghostY, width: listW, height: row))
        ghostTextX = doneW + measure("\(core.count + 1)   ", F.main) - 3
        let h = ceil(F.main.ascender - F.main.descender + F.main.leading) + 2
        input.frame = NSRect(x: r.minX + ghostTextX, y: r.minY + (r.height - h) / 2, width: r.width - ghostTextX - 10, height: h)
    }

    /// ghost entry only when the window you're in isn't linked to any item (or nothing is queued yet)
    func updateGhost() {
        let show = forceGhost || isFocused(input) || !input.stringValue.isEmpty || pq_needs_entry(c) != 0
        if ghostShown != show { ghostShown = show; fit() }
        layoutGhost()
        needsDisplay = true
    }

    func drawBar() {
        let tnow = now()
        let r = cardRect(NSRect(x: padL, y: barY + 4, width: listW, height: barH - 4))
        minusR = .zero; plusR = .zero; goR = .zero; stopR = .zero
        let note = core.note()
        let warn = note != nil && pq_note_warn(c) != 0
        let ph: CGFloat = 20, py = r.minY + (r.height - ph) / 2
        let card = round(r.insetBy(dx: 0.5, dy: 0.5), 9)
        let state = core.state

        if state == PQ_BREAK {
            fill(card, P.lav)
            let left = clockText(pq_break_left(c, tnow))
            drawText(core.fmt(pq_break_hidden(c) != 0 ? "bar.break_hidden" : "bar.break", left), F.age,
                     NSRect(x: r.minX + doneW, y: r.minY, width: r.width - doneW * 2, height: r.height), P.lavDeep)
            return
        }
        if state == PQ_FOCUS || state == PQ_AWAY {
            let away = state == PQ_AWAY
            let done = CGFloat(pq_focus_progress(c, tnow))
            fill(card, .white)
            NSGraphicsContext.saveGraphicsState()
            card.addClip()
            P.greenFill.setFill()
            NSRect(x: r.minX, y: r.minY, width: r.width * done, height: r.height + 1).fill()
            NSGraphicsContext.restoreGraphicsState()
            stroke(card, warn || away ? P.redDeep : P.greenDeep, 1.4)
            let sw: CGFloat = 54
            stopR = NSRect(x: r.maxX - sw - 6, y: py, width: sw, height: ph)
            pill(stopR, P.pill, P.grayDeep, tr("bar.stop"), F.age)
            let txt = note ?? (away ? core.fmt("bar.paused", clockText(pq_away_left(c, tnow)))
                                    : core.fmt("bar.focus_left", clockText(pq_focus_left(c, tnow))))
            drawText(txt, F.age, NSRect(x: r.minX + doneW, y: r.minY, width: stopR.minX - r.minX - doneW - 4, height: r.height),
                     warn || away ? P.redDeep : P.greenDeep)
            return
        }
        // idle: [<] 30 min [>]      [▶ Start]
        fill(card, .white)
        stroke(card, P.dash, 1)
        let gw: CGFloat = 58
        goR = NSRect(x: r.maxX - gw - 6, y: py, width: gw, height: ph)
        pill(goR, P.greenFill, P.greenDeep, tr("bar.start"), F.age)
        if let n = note {
            drawText(n, F.age, NSRect(x: r.minX + doneW, y: r.minY, width: goR.minX - r.minX - doneW - 4, height: r.height),
                     warn ? P.redDeep : P.greenDeep)
            return
        }
        let st = Int(pq_sel_tier(c)), last = Int(pq_tier_count()) - 1
        let bs = ph, x = r.minX + 8
        minusR = NSRect(x: x, y: py, width: bs, height: bs)
        pill(minusR, P.pillLight, st > 0 ? P.grayDeep : P.pillOff, "<", F.age)
        let lw: CGFloat = 44
        drawText(core.fmt("bar.minutes", "\(pq_tier_focus(Int32(st)))"), F.main, NSRect(x: minusR.maxX, y: r.minY, width: lw, height: r.height), P.ink, .center)
        plusR = NSRect(x: minusR.maxX + lw, y: py, width: bs, height: bs)
        // which round of this task + its break length
        drawText(core.fmt("bar.round", "\(st + 1)", "\(pq_tier_break(Int32(st)))"),
                 F.age, NSRect(x: plusR.maxX + 6, y: r.minY, width: goR.minX - plusR.maxX - 8, height: r.height), P.muted)
        pill(plusR, P.pillLight, st < last ? P.grayDeep : P.pillOff, ">", F.age)
    }

    // ---------- mouse ----------

    func inList(_ p: NSPoint) -> Bool { p.y >= listY && p.y < listY + listH && p.x >= padL && p.x < padL + listW }
    func listPoint(_ p: NSPoint) -> NSPoint { NSPoint(x: p.x - padL, y: p.y - listY) }

    override func mouseDown(with e: NSEvent) {
        let p = convert(e.locationInWindow, from: nil)
        tip.hide()
        if e.modifierFlags.contains(.control) && !goR.contains(p) { rightMouseDown(with: e); return } // Ctrl+click = right-click
        if p.y < padT + headerH { panel.performDrag(with: e); return }              // drag the widget by its header
        if p.y < listY { tracker.promptForTrust(); return }                          // permission notice
        if inList(p) {
            if dragItem != 0 || Date().timeIntervalSince1970 - dropTime < 0.3 { dragIndex = -1; return }
            let lp = listPoint(p)
            dragIndex = index(atListY: lp.y)
            dragStart = lp
            if dragIndex >= 0 { select(core.id(dragIndex)) }
            return
        }
        if ghostShown && p.y >= ghostY && p.y < ghostY + row { panel.makeKey(); panel.makeFirstResponder(input); return }
        if p.y >= barY { barMouseDown(e, p) }
    }

    override func rightMouseDown(with e: NSEvent) {
        let p = convert(e.locationInWindow, from: nil)
        tip.hide()
        if p.y < listY { NSMenu.popUpContextMenu(headerMenu(), with: e, for: self); return }
        if inList(p) {
            let i = index(atListY: listPoint(p).y)
            if i < 0 { return }
            select(core.id(i))
            NSMenu.popUpContextMenu(itemMenu(), with: e, for: self)
        }
    }

    override func mouseDragged(with e: NSEvent) {
        guard dragItem != 0 || dragIndex >= 0 else { return }
        let lp = listPoint(convert(e.locationInWindow, from: nil))
        if dragItem == 0 {
            if abs(lp.y - dragStart.y) < 4 { return }
            dragItem = core.id(dragIndex) // lift
            dragIndex = -1                // a drag is not a click
            NSCursor.closedHand.set()
            tip.hide(); tipIndex = -1
            needsDisplay = true
        }
        // live reorder: the lifted card jumps to the row under the cursor.
        // the part of a card you reach first (its near side ~60%) = "drop onto it" -> makes a group.
        let n = core.count
        var to = lp.y < 0 ? 0 : index(atListY: lp.y)
        let outside = to < 0 || lp.y < 0
        if to < 0 { to = n - 1 }
        let cur = core.index(dragItem)
        guard cur >= 0 else { return }
        var merge: Int32 = 0
        if !outside && to != cur {
            let rr = itemRect(to)
            let frac = (lp.y - rr.minY) / rr.height
            if to > cur ? frac < 0.6 : frac > 0.4 { merge = core.id(to) }
        }
        if merge != mergeTarget { mergeTarget = merge; needsDisplay = true }
        if merge == 0 && to != cur {
            pq_move(c, dragItem, Int32(to), 1)
            relayout() // sections follow the new order
            fit()
        }
    }

    override func mouseUp(with e: NSEvent) {
        let lp = listPoint(convert(e.locationInWindow, from: nil))
        if dragItem != 0 { // drop
            let d = dragItem
            dragItem = 0
            var created: Int32 = 0
            if mergeTarget != 0 { pq_join(c, d, mergeTarget, &created) } // dropped onto a card -> group
            mergeTarget = 0
            pq_move(c, d, Int32(core.index(d)), 0) // group fix-up + save
            dragIndex = -1
            dropTime = Date().timeIntervalSince1970
            NSCursor.arrow.set()
            changed()
            let g = core.group(d)
            if created != 0 && g != 0 { DispatchQueue.main.async { self.renameGroup(g) } } // name the new section
            return
        }
        let i = index(atListY: lp.y)
        let click = dragIndex >= 0 && i == dragIndex
        dragIndex = -1
        if !click { return }
        let id = core.id(i)
        let mods = e.modifierFlags
        if mods.contains(.shift) { sel = id; removeSelected() }                                      // Shift+click = done
        else if mods.contains(.command) { }                                                         // Cmd+click = just select
        else if core.firstOfGroup(i) && lp.y < itemRect(i).minY + hdrH { renameGroup(core.group(id)) } // section header
        else if core.has(id, lastExternal) { renameItem(i) }                                       // the task you're in -> rename
        else { trySwitch(id) }
    }

    override func mouseMoved(with e: NSEvent) {
        let p = convert(e.locationInWindow, from: nil)
        let hot = [minusR, plusR, goR, stopR].contains { $0.contains(p) } || (!tracker.trusted && p.y >= padT + headerH && p.y < listY)
        (hot ? NSCursor.pointingHand : NSCursor.arrow).set()
        let i = inList(p) && dragItem == 0 ? index(atListY: listPoint(p).y) : -1
        if i == tipIndex { return }
        tipIndex = i
        if i < 0 { tip.hide(); return }
        let id = core.id(i)
        var whereText = ""
        let wins = core.wins(id)
        for w in wins { whereText += "\n→ " + appName(w.proc) + " — " + w.title }
        if wins.isEmpty { whereText += "\n→ " + tr("item.no_window") }
        let sp = panel.convertPoint(toScreen: convert(NSPoint(x: p.x + 12, y: p.y + 16), to: nil))
        tip.show(core.text(id) + whereText, at: sp, seconds: 5)
    }

    override func mouseExited(with e: NSEvent) { tip.hide(); tipIndex = -1 }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self, userInfo: nil))
    }

    override func scrollWheel(with e: NSEvent) {
        let p = convert(e.locationInWindow, from: nil)
        let dy = e.scrollingDeltaY
        if dy == 0 { return }
        if p.y < listY { if e.phase == [] || e.phase == .changed { setOpacity(pq_opacity(c) + (dy > 0 ? 0.05 : -0.05)) }; return }
        if p.y >= barY {
            if core.state == PQ_IDLE && abs(dy) >= 1 && (e.phase == [] || e.phase == .began) { pq_step_tier(c, dy > 0 ? 1 : -1); needsDisplay = true }
            return
        }
        if inList(p) { scrollY -= e.hasPreciseScrollingDeltas ? dy : dy * row; fit() }
    }

    func appName(_ proc: String) -> String {
        if let a = Tracker.apps(proc).first, let n = a.localizedName { return n }
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: proc) { return url.deletingPathExtension().lastPathComponent }
        return proc
    }

    // ---------- list operations ----------

    func moveSelected(_ delta: Int) {
        let i = core.index(sel)
        guard i >= 0 else { return }
        let to = max(0, min(core.count - 1, i + delta))
        if to == i { return }
        pq_move(c, sel, Int32(to), 0)
        changed()
    }

    func removeSelected() {
        let i = core.index(sel)
        guard i >= 0 else { return }
        pq_remove(c, sel)
        sel = core.count == 0 ? 0 : core.id(min(i, core.count - 1))
        tipIndex = -1; tip.hide()
        changed()
    }

    // ---- inline edit (item text / section name) ----

    func beginEdit(_ rc: NSRect, _ text: String, _ back: NSColor, _ commit: @escaping (String) -> Void) {
        endEdit(false)
        editCommit = commit
        editBox.backgroundColor = back
        editBox.stringValue = text
        let h = ceil(F.main.ascender - F.main.descender + F.main.leading) + 2
        editBox.frame = NSRect(x: padL + rc.minX, y: listY + rc.minY + (rc.height - h) / 2, width: rc.width, height: h)
        editBox.isHidden = false
        panel.makeKey()
        panel.makeFirstResponder(editBox)
        editBox.currentEditor()?.selectAll(nil)
        needsDisplay = true
    }

    func endEdit(_ commit: Bool) {
        guard let cm = editCommit else { return }
        editCommit = nil
        let t = editBox.stringValue.trimmingCharacters(in: .whitespaces)
        editBox.isHidden = true
        if commit && !t.isEmpty { cm(t) }
        needsDisplay = true
        backToWork()
    }

    func renameItem(_ i: Int) {
        let id = core.id(i)
        let r = cardRect(cardBounds(i, itemRect(i)))
        let x = r.minX + doneW + measure("\(i + 1)   ", F.main) - 3
        beginEdit(NSRect(x: x, y: r.minY, width: r.maxX - x - 70, height: r.height), core.text(id), P.greenFill) { [unowned self] t in
            pq_rename(self.c, id, t); self.changed()
        }
    }

    func renameGroup(_ g: Int32) {
        guard let first = (0..<core.count).first(where: { core.group(core.id($0)) == g }) else { return }
        let sr = sectionRect(first)
        let rc = NSRect(x: sr.minX + 12, y: sr.minY + 2, width: sr.width - 60, height: hdrH - 5)
        beginEdit(rc, core.groupName(g), lerp(P.bg, P.groupColor(Int(g)), 0.16)) { [unowned self] t in
            pq_set_group_name(self.c, g, t); self.needsDisplay = true
        }
    }

    // ---------- focus timer ----------

    /// clicking another item while focusing: warn once (the core sets the note), second click goes
    func trySwitch(_ id: Int32) {
        guard id != 0 else { return }
        if pq_try_switch(c, id, now()) == 0 {
            Sound.exclamation()
            startTick()
            needsDisplay = true
            return
        }
        switchTo(id) // leaving starts the 1-minute grace
    }

    func startFocus(test: Bool) {
        guard let h = lastExternal else { Sound.beep(); return }
        let wins = tracker.snapGroup(h).map { $0.key } // the window you were in + windows tiled beside it
        if pq_start(c, wins, Int32(wins.count), test ? 1 : 0, now()) == 0 { Sound.beep(); return }
        startTick()
        needsDisplay = true
        backToWork()
    }

    func barMouseDown(_ e: NSEvent, _ p: NSPoint) {
        let state = core.state
        if state == PQ_BREAK {
            if pq_break_hidden(c) != 0 { pq_break_show(c); showBreak() } // bring the break screen back
            return
        }
        if state == PQ_FOCUS || state == PQ_AWAY {
            if stopR.contains(p) { pq_stop(c, now()); awayToast?.hide(); needsDisplay = true; startTick() }
            return
        }
        if minusR.contains(p) { pq_step_tier(c, -1) }
        else if plusR.contains(p) { pq_step_tier(c, 1) }
        // Option+click (Ctrl+click on Windows) = 10-second test run
        else if goR.contains(p) { startFocus(test: e.modifierFlags.contains(.option) || e.modifierFlags.contains(.control)) }
        needsDisplay = true
    }

    func openStats() {
        if let s = stats, s.panel.isVisible { s.reload(); s.panel.makeKeyAndOrderFront(nil); return }
        stats = Stats(owner: self)
        stats?.show()
    }

    func openLogSheet() { NSWorkspace.shared.open(URL(fileURLWithPath: str(pq_log_path(c)))) }

    // ---------- clock: 250 ms timer, only while the core needs it ----------

    func startTick() {
        if tick != nil { return }
        let t = Timer(timeInterval: 0.25, repeats: true) { [weak self] _ in self?.onTick() }
        t.tolerance = 0.05
        RunLoop.main.add(t, forMode: .common)
        tick = t
    }

    func stopTick() { tick?.invalidate(); tick = nil }

    func onTick() {
        handle(pq_tick(c, now()))
        if let a = awayToast, a.visible { a.view.needsDisplay = true }
        if let s = soonToast, s.visible { s.view.needsDisplay = true }
        if let bw = breakWins, pq_break_hidden(c) == 0 { for w in bw { w.orderFrontRegardless(); w.contentView?.needsDisplay = true } } // stay on top
        needsDisplay = true
        if pq_needs_tick(c, now()) == 0 { stopTick() }
    }

    /// react to PQ_EV_* flags from the core
    func handle(_ ev: Int32) {
        if ev & PQ_EV_AWAY_START != 0 { showAway() }
        if ev & (PQ_EV_AWAY_END | PQ_EV_AWAY_TIMEOUT) != 0 { awayToast?.hide() }
        if ev & PQ_EV_FOCUS_DONE != 0 { awayToast?.hide(); startBreak() }
        if ev & PQ_EV_BREAK_SOON != 0 { showSoon() }
        if ev & PQ_EV_BREAK_END != 0 { closeBreak() }
        if pq_needs_tick(c, now()) != 0 { startTick() }
    }

    // ---- left the task while focusing: the centered 1-minute card ----

    func showAway() {
        if awayToast == nil {
            let t = Toast(size: NSSize(width: 340, height: 168))
            t.view.paint = { [unowned self] v in self.paintAway(v) }
            t.view.down = { [unowned self] _, p in
                if self.backR.contains(p) { self.goBack() }
                else if self.quitR.contains(p) { pq_stop(self.c, now()); self.awayToast?.hide(); self.needsDisplay = true; self.startTick() }
            }
            t.view.moved = { [unowned self] p in (self.backR.contains(p) || self.quitR.contains(p) ? NSCursor.pointingHand : NSCursor.arrow).set() }
            awayToast = t
        }
        awayToast!.showCentered()
        Sound.exclamation()
    }

    func goBack() {
        let home = pq_away_home(c)
        if home == 0 { return }
        let proc = core.ids.lazy.flatMap { core.wins($0) }.first { $0.key == home }?.proc ?? procOf(home)
        tracker.raise(home, proc, activate: true)
    }

    /// the app of a window id the queue doesn't know (e.g. a window you started focusing in)
    func procOf(_ key: pq_win) -> String {
        if key > 0, let pid = Tracker.cgWindows(onScreen: false).first(where: { pq_win($0.wid) == key })?.pid,
           let app = NSRunningApplication(processIdentifier: pid) { return Tracker.procOf(app) }
        return NSWorkspace.shared.runningApplications.map(Tracker.procOf).first { Core.appKey($0) == key } ?? ""
    }

    func paintAway(_ v: PaintView) {
        let w = v.bounds.width, h = v.bounds.height
        fill(NSBezierPath(rect: v.bounds), P.bg)
        stroke(round(v.bounds.insetBy(dx: 1.2, dy: 1.2), 11), P.redFill, 2.4)
        drawText(tr("away.title"), F.mid, NSRect(x: 0, y: 14, width: w, height: 28), P.redDeep, .center)
        drawText(clockText(pq_away_left(c, now())), F.big26, NSRect(x: 0, y: 42, width: w, height: 50), P.ink, .center)
        drawText(tr("away.body"), F.main, NSRect(x: 0, y: 92, width: w, height: 22), P.muted, .center)
        let bw: CGFloat = 120, bh: CGFloat = 26, y = h - bh - 14, gap: CGFloat = 10
        backR = NSRect(x: w / 2 - bw - gap / 2, y: y, width: bw, height: bh)
        quitR = NSRect(x: w / 2 + gap / 2, y: y, width: bw, height: bh)
        pill(backR, P.greenFill, P.greenDeep, tr("away.back"), F.age)
        pill(quitR, P.pill, P.grayDeep, tr("away.stop"), F.age)
    }

    // ---------- break overlay ----------

    func startBreak() {
        closeBreak()
        var wins: [FloatPanel] = []
        for (k, sc) in NSScreen.screens.enumerated() {
            let w = makePanel(sc.frame, level: .screenSaver, shadow: false)
            w.keyable = false
            w.isOpaque = true
            w.backgroundColor = P.breakBg // opaque: actually rest
            let v = PaintView(frame: NSRect(origin: .zero, size: sc.frame.size))
            if k == 0 {
                v.paint = { [unowned self] v in self.paintBreak(v) }
                v.down = { [unowned self] e, p in
                    if e.clickCount >= 2 && self.skipR.contains(p) { pq_break_skip(self.c, now()); self.closeBreak(); return } // emergency exit
                    if self.extR.contains(p) { pq_extend(self.c, now()); self.closeBreak(); self.startTick() }
                    else if self.bgR.contains(p) { self.hideBreak() }
                    self.needsDisplay = true
                }
                v.moved = { [unowned self] p in
                    (self.skipR.contains(p) || self.extR.contains(p) || self.bgR.contains(p) ? NSCursor.pointingHand : NSCursor.arrow).set()
                }
            } else {
                v.paint = { v in fill(NSBezierPath(rect: v.bounds), P.breakBg) }
            }
            w.contentView = v
            w.setFrame(sc.frame, display: true)
            w.orderFrontRegardless()
            wins.append(w)
        }
        breakWins = wins
        Sound.asterisk()
        startTick()
        needsDisplay = true
    }

    func closeBreak() {
        soonToast?.hide()
        guard let ws = breakWins else { return }
        breakWins = nil
        for w in ws { w.orderOut(nil); w.close() }
        if core.state != PQ_BREAK && pq_note(c, now()) != nil && !core.focusing { Sound.asterisk() } // natural end: "break over"
        needsDisplay = true
    }

    /// "Rest in background": the break keeps counting with the cover hidden
    func hideBreak() {
        pq_break_hide(c, now())
        breakWins?.forEach { $0.orderOut(nil) }
        handle(pq_tick(c, now())) // the "almost over" card may be due right away
        startTick()
    }

    func showBreak() {
        soonToast?.hide()
        breakWins?.forEach { $0.orderFrontRegardless() }
        needsDisplay = true
    }

    func showSoon() {
        if soonToast == nil {
            let t = Toast(size: NSSize(width: 300, height: 128))
            t.view.paint = { [unowned self] v in self.paintSoon(v) }
            t.view.down = { [unowned self] _, _ in self.soonToast?.hide() }
            soonToast = t
        }
        soonToast!.showCentered()
        Sound.exclamation()
    }

    func paintSoon(_ v: PaintView) {
        let w = v.bounds.width
        fill(NSBezierPath(rect: v.bounds), P.bg)
        stroke(round(v.bounds.insetBy(dx: 1.2, dy: 1.2), 11), P.lavMid, 2.4)
        drawText(tr("soon.title"), F.mid, NSRect(x: 0, y: 12, width: w, height: 28), P.lavDeep, .center)
        drawText(clockText(pq_break_left(c, now())), F.big26, NSRect(x: 0, y: 40, width: w, height: 50), P.ink, .center)
        drawText(tr("soon.body"), F.age, NSRect(x: 0, y: 94, width: w, height: 22), P.muted, .center)
    }

    func paintBreak(_ v: PaintView) {
        let tnow = now()
        fill(NSBezierPath(rect: v.bounds), P.breakBg)
        let cw: CGFloat = 420, ch: CGFloat = 292
        let cr = NSRect(x: (v.bounds.width - cw) / 2, y: (v.bounds.height - ch) / 2, width: cw, height: ch)
        fill(round(cr, 24), P.bg)
        drawText(tr("break.title"), F.mid, NSRect(x: cr.minX, y: cr.minY + 26, width: cr.width, height: 30), P.lavDeep, .center)
        drawText(clockText(pq_break_left(c, tnow)), F.big, NSRect(x: cr.minX, y: cr.minY + 62, width: cr.width, height: 80), P.ink, .center)
        drawText(tr("break.body"), F.main, NSRect(x: cr.minX, y: cr.minY + 150, width: cr.width, height: 24), P.muted, .center)
        // "+5 min focus" + "rest in the background" buttons
        let ebw: CGFloat = 150, ebh: CGFloat = 30, egap: CGFloat = 12, ey = cr.minY + 188
        if pq_can_extend(c) == 0 {
            extR = .zero
            bgR = NSRect(x: cr.minX + (cr.width - ebw) / 2, y: ey, width: ebw, height: ebh)
        } else {
            extR = NSRect(x: cr.midX - ebw - egap / 2, y: ey, width: ebw, height: ebh)
            bgR = NSRect(x: cr.midX + egap / 2, y: ey, width: ebw, height: ebh)
            pill(extR, P.greenFill, P.greenDeep, core.fmt("break.extend", "5"), F.main)
        }
        pill(bgR, P.pill, P.grayDeep, tr("break.background"), F.main)
        // progress
        let pr = NSRect(x: cr.minX + 50, y: cr.maxY - 40, width: cr.width - 100, height: 8)
        let done = CGFloat(pq_break_progress(c, tnow))
        fill(round(pr, pr.height / 2), P.lav)
        if done > 0.01 { fill(round(NSRect(x: pr.minX, y: pr.minY, width: max(pr.height, pr.width * done), height: pr.height), pr.height / 2), P.lavMid) }
        // small emergency exit at the bottom of the screen
        let skip = tr("break.skip")
        let sw = measure(skip, F.age)
        skipR = NSRect(x: (v.bounds.width - sw) / 2 - 10, y: v.bounds.height - 70, width: sw + 20, height: 28)
        drawText(skip, F.age, skipR, rgb(120, 112, 140), .center)
    }

    // ---------- shutdown ----------

    func shutdown() {
        endEdit(true)
        core.close() // logs a running session ("quit") and saves
    }
}
