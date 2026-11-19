// The widget: task queue, input card, focus bar, grace card, break screen. Port of PQForm (PQ.cs).
import AppKit

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

    let store = Store()
    let tracker = Tracker()
    var items: [Item] = []
    var groupNames: [Int: String] = [:]
    var panel: FloatPanel!
    let input = Field()
    let editBox = Field()
    let tip = Tip()
    var editCommit: ((String) -> Void)?
    var heights: [CGFloat] = []
    var scrollY: CGFloat = 0
    var sel = -1
    var dragIndex = -1, tipIndex = -1
    var dragStart = NSPoint.zero
    var dragItem: Item?      // card currently lifted
    var mergeTarget: Item?   // card it would be grouped with if dropped now
    var dropTime: TimeInterval = 0
    var lastExternal: WinRef? // last window the user was in that is not this widget
    var forceGhost = false, ghostShown = true
    var ghostTextX: CGFloat = 0
    var repaint: Timer?

    // ---- focus timer state ----
    static let tierFocus = [15, 30, 60], tierBreak = [3, 5, 15]
    var selTier = 0, focusTier = 0
    var barItem: Item?
    var focusing = false
    var focusStart = Date(), focusEnd = Date()
    var freezeFrom = Date()   // start of the current segment (others' clocks frozen from here; logged from here)
    var pausedSec: Double = 0 // paused (away) seconds inside the current segment
    var plannedMin = 0
    var extension_ = false    // current segment is a "+5 min" extension
    var doneStart = Date(), doneEnd = Date()
    var focusWins: [WinRef] = []
    var focusItem: Item?
    var warnedItem: Item?; var warnTime: TimeInterval = 0
    var barNote = ""; var barWarn = false; var barNoteUntil = Date.distantPast
    var minusR = NSRect.zero, plusR = NSRect.zero, goR = NSRect.zero, stopR = NSRect.zero
    var tick: Timer?
    var lastFocusItem: Item?

    // ---- away (left the task while focusing) ----
    var away = false
    var awayStart = Date()
    let awaySec: Double = 60
    var awayToast: Toast?
    var awayHome: WinRef?
    var backR = NSRect.zero, quitR = NSRect.zero

    // ---- break ----
    var breakWins: [FloatPanel]?
    var breakStart = Date(), breakEnd = Date()
    var breakMin = 5
    var skipR = NSRect.zero, extR = NSRect.zero, bgR = NSRect.zero
    var doneWins: [WinRef]?; var doneItem: Item?; var doneTier = 0
    let extendMin = 5
    var breakHidden = false, soonShown = false
    var soonToast: Toast?
    let soonSec: Double = 10

    var stats: Stats?

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    init() {
        super.init(frame: NSRect(x: 0, y: 0, width: 280, height: 200))
        store.backupItems() // keep a copy before anything is written
        Lang.load(store.dir.appendingPathComponent("lang.txt"))

        panel = makePanel(NSRect(x: 0, y: 0, width: W, height: 200), level: .floating)
        panel.contentView = self
        panel.alphaValue = store.loadOpacity()
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

        groupNames = store.loadGroups()
        items = store.loadItems()
        for it in items { tracker.resolve(it) } // re-link to the live windows after a restart
        normalizeGroups()
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
        if tracker.refreshTrust() { relayout(); fit(); needsDisplay = true }
        guard let h = h else { return }
        if h.same(lastExternal) { return }
        let prev = lastExternal
        lastExternal = h
        if focusing {
            if !focusOwns(h) { if !away { startAway(prev) } } // left the task: 1-minute grace
            else if away { endAway(true) }                   // came back in time: resume
        }
        var changed = false
        for it in items {
            let inPrev = it.has(prev), inNow = it.has(h)
            if inNow {
                it.lastWid = h.wid
                for w in it.wins {
                    if winMatch(w, h) { if w.wid == h.wid { w.title = h.title }; continue } // keep title fresh for restarts
                    // came back to one window of the task -> bring its partners up too (no focus change)
                    if !inPrev && w.wid != 0 && tracker.trusted { tracker.raise(w, activate: false) }
                }
            }
            // entering or leaving the task (or its group) restarts its clock; moving inside the group doesn't
            if gHas(it, prev) != gHas(it, h) { it.created = Date(); changed = true }
        }
        if changed { save() }
        updateGhost()     // entry appears only in windows without an item
        updateBarItem()   // bar shows this task's next session length
        needsDisplay = true
    }

    func makeWin(_ h: WinRef) -> Win { Win(wid: h.wid, proc: h.proc, title: h.title) }

    /// link the item to the window you were in + whatever is tiled beside it
    func attach(_ it: Item, _ h: WinRef?) {
        guard let h = h else { return }
        it.wins = tracker.snapGroup(h).map(makeWin)
        it.lastWid = h.wid
    }

    func addWindow(_ it: Item?, _ h: WinRef?) {
        guard let it = it, let h = h, !it.has(h) else { return }
        it.wins.append(makeWin(h))
        changed()
    }

    /// bring all of the task's windows forward; focus the one you used last
    func switchTo(_ it: Item?) {
        guard let it = it else { return }
        tracker.resolve(it)
        let live = it.wins.filter { tracker.alive($0) }
        guard let focus = live.first(where: { $0.wid != 0 && $0.wid == it.lastWid }) ?? live.first else { Sound.beep(); return }
        for w in live where w !== focus { tracker.raise(w, activate: false) }
        tracker.raise(focus, activate: true)
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
        let h = panel.frame.height
        panel.setFrameOrigin(NSPoint(x: wa.maxX - W - 10, y: wa.maxY - 44 - h))
    }

    func ensureOnScreen() {
        let f = panel.frame
        let p = NSPoint(x: f.midX, y: f.maxY - 10)
        if NSScreen.screens.contains(where: { $0.visibleFrame.contains(p) }) { return } // still visible somewhere
        placeDefault()
    }

    func showAgain() {
        ensureOnScreen()
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
        for i in items.indices { total += rowHeight(i) }
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

    func changed() { save(); relayout(); updateGhost(); updateBarItem(); fit(); needsDisplay = true }

    func save() { store.save(items, groupNames: groupNames) }

    // ---------- language ----------

    func setLang(_ ko: Bool) {
        Lang.ko = ko
        try? (ko ? "ko" : "en").write(to: store.dir.appendingPathComponent("lang.txt"), atomically: true, encoding: .utf8)
        setCue()
        needsDisplay = true
        awayToast?.view.needsDisplay = true
        soonToast?.view.needsDisplay = true
        breakWins?.forEach { $0.contentView?.needsDisplay = true }
        stats?.view.needsDisplay = true
    }

    static func headerText() -> String { "✿  " + T("To-do", "할 일") + "    ⌃⌥Space" }

    func setCue() {
        input.placeholderAttributedString = NSAttributedString(string: T("+ task for this window", "+ 이 창의 할 일"),
                                                               attributes: [.font: F.main, .foregroundColor: P.muted])
    }

    // ---------- menus ----------

    func opacityMenu() -> NSMenuItem {
        let m = NSMenuItem(title: T("Opacity", "투명도"), action: nil, keyEquivalent: "")
        let sub = NSMenu()
        for p in [100, 90, 80, 70, 60, 50, 40] {
            sub.addItem(menuItem("\(p)%", checked: Int((panel.alphaValue * 100).rounded()) == p) { [unowned self] in self.setOpacity(CGFloat(p) / 100) })
        }
        m.submenu = sub
        return m
    }

    func commonItems(_ m: NSMenu) {
        m.addItem(menuItem(T("Stats", "통계 보기")) { [unowned self] in self.openStats() })
        m.addItem(menuItem(T("Open log sheet (CSV)", "기록 시트 열기 (CSV)")) { [unowned self] in self.openLogSheet() })
        m.addItem(opacityMenu())
        let lm = NSMenuItem(title: T("Language", "언어"), action: nil, keyEquivalent: "")
        let sub = NSMenu()
        sub.addItem(menuItem("English", checked: !Lang.ko) { [unowned self] in self.setLang(false) })
        sub.addItem(menuItem("한국어", checked: Lang.ko) { [unowned self] in self.setLang(true) })
        lm.submenu = sub
        m.addItem(lm)
        m.addItem(menuItem(T("Quit", "종료")) { NSApp.terminate(nil) })
    }

    func itemMenu() -> NSMenu {
        let m = NSMenu(); m.font = F.menu; m.autoenablesItems = false
        let it = sel >= 0 && sel < items.count ? items[sel] : nil
        m.addItem(menuItem(T("Go to this window (click / Enter)", "이 창으로 이동 (클릭 / Enter)")) { [unowned self] in self.trySwitch(it) })
        m.addItem(menuItem(T("Done (Shift+click / Del)", "완료 (Shift+클릭 / Del)")) { [unowned self] in self.removeSelected() })
        m.addItem(menuItem(T("+ Also link the previous window", "+ 직전 창도 이 할 일에 연결")) { [unowned self] in self.addWindow(it, self.lastExternal) })
        m.addItem(menuItem(T("Re-link windows (previous + neighbor)", "창 연결 다시 하기 (직전 창 + 옆 창)")) { [unowned self] in
            if let it = it { self.attach(it, self.lastExternal); self.changed() }
        })
        if let it = it, it.groupId != 0 {
            m.addItem(menuItem(T("Remove from group", "묶음에서 빼기")) { [unowned self] in self.leaveGroup(it) })
        }
        m.addItem(menuItem(T("Move to top", "맨 위로")) { [unowned self] in self.moveSelected(-self.sel) })
        m.addItem(menuItem(T("Reset timer", "타이머 리셋")) { [unowned self] in self.resetSelected() })
        m.addItem(menuItem(T("Restart focus rounds (from 15 min)", "집중 회차 처음으로 (15분부터)")) { [unowned self] in
            if let it = it {
                self.setTaskSessions(it, 0)
                if it === self.barItem || Widget.sameGroup(it, self.barItem) { self.selTier = 0 }
                self.changed()
            }
        })
        m.addItem(.separator())
        commonItems(m)
        return m
    }

    func headerMenu() -> NSMenu {
        let m = NSMenu(); m.font = F.menu; m.autoenablesItems = false
        commonItems(m)
        return m
    }

    func setOpacity(_ v: CGFloat) {
        let x = max(0.3, min(1.0, (v * 100).rounded() / 100))
        panel.alphaValue = x
        store.saveOpacity(x)
    }

    // ---------- input ----------

    func isFocused(_ f: NSTextField) -> Bool {
        guard let ed = f.currentEditor() else { return false }
        return panel.firstResponder === ed
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy sel: Selector) -> Bool {
        if control === input {
            if sel == #selector(NSResponder.insertNewline(_:)) || sel == #selector(NSResponder.insertNewlineIgnoringFieldEditor(_:)) {
                let t = input.stringValue.trimmingCharacters(in: .whitespaces)
                if t.isEmpty { return true }
                let it = Item(); it.text = t; it.created = Date()
                attach(it, lastExternal)
                let shift = NSApp.currentEvent?.modifierFlags.contains(.shift) ?? false
                if shift { items.insert(it, at: 0) } else { items.append(it) } // Shift+Enter = top
                input.stringValue = ""
                forceGhost = false
                backToWork() // the entry turns into the item in place and you're back in your window
                changed()
                return true
            }
            if sel == #selector(NSResponder.cancelOperation(_:)) {
                input.stringValue = ""
                forceGhost = false
                backToWork()
                updateGhost()
                return true
            }
            if sel == #selector(NSResponder.moveUp(_:)) && !items.isEmpty {
                panel.makeFirstResponder(self); sel_(items.count - 1); return true
            }
            return false
        }
        if control === editBox {
            if sel == #selector(NSResponder.insertNewline(_:)) { endEdit(true); return true }
            if sel == #selector(NSResponder.cancelOperation(_:)) { endEdit(false); return true }
        }
        return false
    }

    func controlTextDidChange(_ obj: Notification) { if (obj.object as? NSTextField) === input { updateGhost() } }

    func controlTextDidEndEditing(_ obj: Notification) {
        let f = obj.object as? NSTextField
        if f === input { forceGhost = false; DispatchQueue.main.async { self.updateGhost(); self.needsDisplay = true } }
        else if f === editBox { endEdit(true) }
    }

    private func sel_(_ i: Int) { sel = i; needsDisplay = true }

    /// list keys (after Up from the entry): Del = done, Enter = go, Opt+Up/Down = move, Esc = back
    func listKey(_ e: NSEvent) -> Bool {
        guard panel.firstResponder === self else { return false }
        switch Int(e.keyCode) {
        case 51, 117: removeSelected(); return true
        case 36, 76: trySwitch(sel >= 0 && sel < items.count ? items[sel] : nil); return true
        case 126: if e.modifierFlags.contains(.option) { moveSelected(-1) } else if sel > 0 { sel_(sel - 1) }; return true
        case 125: if e.modifierFlags.contains(.option) { moveSelected(1) } else if sel + 1 < items.count { sel_(sel + 1) }; return true
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
        for i in items.indices {
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
        let it = items[i]
        if it.groupId == 0 { return b }
        let top = firstOfGroup(i) ? hdrH : 0, bot = lastOfGroup(i) ? secPad : 0
        return NSRect(x: b.minX + secPad, y: b.minY + top, width: b.width - secPad * 2, height: b.height - top - bot)
    }

    /// section rectangle (list coordinates) of the group containing row i
    func sectionRect(_ i: Int) -> NSRect {
        var a = i, b = i
        while a > 0 && Widget.sameGroup(items[a - 1], items[i]) { a -= 1 }
        while b + 1 < items.count && Widget.sameGroup(items[b + 1], items[i]) { b += 1 }
        let ra = itemRect(a), rb = itemRect(b)
        let top = ra.minY + (firstOfGroup(a) ? 3 : 0)
        return NSRect(x: 0, y: top, width: listW - 1, height: rb.maxY - (lastOfGroup(b) ? 1 : 0) - top)
    }

    /// recompute row heights (section header / bottom padding)
    func relayout() {
        heights = items.indices.map { i in row + (firstOfGroup(i) ? hdrH : 0) + (lastOfGroup(i) ? secPad : 0) }
    }

    // ---------- drawing ----------

    override func draw(_ dirtyRect: NSRect) {
        NSColor.clear.set(); bounds.fill()
        fill(NSBezierPath(rect: bounds), P.bg)
        drawText(Widget.headerText(), F.header, NSRect(x: padL, y: padT, width: listW, height: headerH), P.muted)
        if !tracker.trusted {
            drawText(T("⚠ Per-app tracking · click to allow Accessibility", "⚠ 앱 단위로 추적 중 · 클릭해서 손쉬운 사용 허용"),
                     F.small, NSRect(x: padL, y: padT + headerH, width: listW, height: noticeH - 2), P.redDeep)
        }
        NSGraphicsContext.saveGraphicsState()
        NSRect(x: padL, y: listY, width: listW, height: listH).clip()
        let t = NSAffineTransform(); t.translateX(by: padL, yBy: listY); t.concat()
        for i in items.indices {
            let b = itemRect(i)
            if b.maxY < 0 || b.minY > listH { continue }
            drawItem(i, b)
        }
        NSGraphicsContext.restoreGraphicsState()
        if ghostShown { drawGhost() }
        drawBar()
    }

    func drawItem(_ i: Int, _ b: NSRect) {
        let it = items[i]
        let active = gHas(it, lastExternal) // whole group shows "working"
        // while focusing, the other items' clocks are frozen and they stay white
        let frozen = focusing && !active && !inFocus(it)
        let now = frozen ? (it.created < freezeFrom ? freezeFrom : it.created) : Date()
        let m = now.timeIntervalSince(it.created) / 60
        // active window's item = green; others white, one step redder per 10 min (max 6 steps)
        let t: CGFloat = frozen ? 0 : CGFloat(min(6, max(0, Int(m / 10)))) / 6
        var fillC = active ? P.greenFill : lerp(.white, P.redFill, t)
        let deep = active ? P.greenDeep : lerp(P.grayDeep, P.redDeep, t)
        let isSel = i == sel
        let lifted = it === dragItem

        // section: one rounded tinted box around the whole group; each row paints its slice of it
        if it.groupId != 0 {
            let gc = P.groupColor(it.groupId)
            let sr = sectionRect(i)
            NSGraphicsContext.saveGraphicsState()
            b.clip()
            let p = round(sr, 13)
            fill(p, lerp(P.bg, gc, 0.16))
            stroke(p, lerp(P.bg, gc, 0.45), 1.2)
            if firstOfGroup(i) {
                let cnt = items.filter { Widget.sameGroup($0, it) }.count
                let hr = NSRect(x: sr.minX + 12, y: sr.minY + 1, width: sr.width - 24, height: hdrH - 4)
                let name = groupName(it.groupId)
                let editingHere = editCommit != nil && !editBox.isHidden && abs(editBox.frame.minY - (listY + hr.minY)) < hdrH
                if !editingHere {
                    drawText(name, F.age, hr, lerp(gc, P.ink, 0.45))
                    let nw = min(measure(name, F.age), hr.width)
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
        if isSel || lifted { stroke(path, deep, lifted ? 2 : 1.6) } else { stroke(path, lerp(fillC, deep, 0.25), 1) }
        if it === mergeTarget { // "drop here to group"
            stroke(path, it.groupId != 0 ? P.groupColor(it.groupId) : P.groupColor(1), 2.4, dash: true)
        }

        // age badge on the right
        let mins = Int(max(0, m))
        let age = active ? (focusing ? T("● focusing", "● 집중 중") : T("● working", "● 작업 중"))
            : mins < 60 ? "\(mins)m" : "\(mins / 60)h" + String(format: "%02d", mins % 60)
        let bh: CGFloat = 18, bw = measure(age, F.age) + 12
        let badge = NSRect(x: r.maxX - bw - 6, y: r.minY + (r.height - bh) / 2, width: bw, height: bh)
        fill(round(badge, bh / 2), lerp(fillC, deep, 0.12))
        drawText(age, F.age, badge, deep, .center)

        // index + text
        let txtR = NSRect(x: r.minX + doneW, y: r.minY, width: badge.minX - r.minX - doneW - 6, height: r.height)
        drawText("\(i + 1)   " + it.text, F.main, txtR, P.ink)
    }

    func drawGhost() {
        let r = cardRect(NSRect(x: padL, y: ghostY, width: listW, height: row))
        let p = round(r.insetBy(dx: 0.5, dy: 0.5), 9)
        fill(p, P.inputBg)
        stroke(p, isFocused(input) ? P.grayDeep : P.dash, 1.2, dash: true)
        drawText("\(items.count + 1)", F.main, NSRect(x: r.minX + doneW, y: r.minY, width: ghostTextX - doneW, height: r.height), P.muted)
    }

    func layoutGhost() {
        input.isHidden = !ghostShown
        let r = cardRect(NSRect(x: padL, y: ghostY, width: listW, height: row))
        ghostTextX = doneW + measure("\(items.count + 1)   ", F.main) - 3
        let h = ceil(F.main.ascender - F.main.descender + F.main.leading) + 2
        input.frame = NSRect(x: r.minX + ghostTextX, y: r.minY + (r.height - h) / 2, width: r.width - ghostTextX - 10, height: h)
    }

    /// ghost entry only when the window you're in isn't linked to any item (or nothing is queued yet)
    func updateGhost() {
        let linked = items.contains { $0.has(lastExternal) }
        let show = forceGhost || isFocused(input) || !input.stringValue.isEmpty || !linked || items.isEmpty
        if ghostShown != show { ghostShown = show; fit() }
        layoutGhost()
        needsDisplay = true
    }

    func drawBar() {
        let r = cardRect(NSRect(x: padL, y: barY + 4, width: listW, height: barH - 4))
        minusR = .zero; plusR = .zero; goR = .zero; stopR = .zero
        let note = Date() < barNoteUntil
        let ph: CGFloat = 20, py = r.minY + (r.height - ph) / 2
        let card = round(r.insetBy(dx: 0.5, dy: 0.5), 9)

        if breakWins != nil {
            fill(card, P.lav)
            drawText(T("Break  ", "휴식 중  ") + clockText(breakEnd.timeIntervalSinceNow) + (breakHidden ? T("  · click: break screen", "  · 클릭: 휴식 화면") : ""),
                     F.age, NSRect(x: r.minX + doneW, y: r.minY, width: r.width - doneW * 2, height: r.height), P.lavDeep)
            return
        }
        if focusing {
            let total = focusEnd.timeIntervalSince(focusStart), left = focusEnd.timeIntervalSince(away ? awayStart : Date())
            let done = CGFloat(max(0, min(1, 1 - left / total)))
            fill(card, .white)
            NSGraphicsContext.saveGraphicsState()
            card.addClip()
            P.greenFill.setFill()
            NSRect(x: r.minX, y: r.minY, width: r.width * done, height: r.height + 1).fill()
            NSGraphicsContext.restoreGraphicsState()
            stroke(card, (barWarn && note) || away ? P.redDeep : P.greenDeep, 1.4)
            let sw: CGFloat = 54
            stopR = NSRect(x: r.maxX - sw - 6, y: py, width: sw, height: ph)
            pill(stopR, P.pill, P.grayDeep, T("■ Stop", "■ 그만"), F.age)
            let txt = note ? barNote
                : away ? T("⏸ Paused · back within ", "⏸ 일시정지 · ") + clockText(awaySec - Date().timeIntervalSince(awayStart)) + T("", " 안에 복귀")
                : T("Focus  ", "집중  ") + clockText(left) + T("  left", "  남음")
            drawText(txt, F.age, NSRect(x: r.minX + doneW, y: r.minY, width: stopR.minX - r.minX - doneW - 4, height: r.height),
                     (note && barWarn) || away ? P.redDeep : P.greenDeep)
            return
        }
        // idle: [<] 30 min [>]      [▶ Start]
        fill(card, .white)
        stroke(card, P.dash, 1)
        let gw: CGFloat = 58
        goR = NSRect(x: r.maxX - gw - 6, y: py, width: gw, height: ph)
        pill(goR, P.greenFill, P.greenDeep, T("▶ Start", "▶ 시작"), F.age)
        if note {
            drawText(barNote, F.age, NSRect(x: r.minX + doneW, y: r.minY, width: goR.minX - r.minX - doneW - 4, height: r.height),
                     barWarn ? P.redDeep : P.greenDeep)
            return
        }
        let bs = ph, x = r.minX + 8
        minusR = NSRect(x: x, y: py, width: bs, height: bs)
        pill(minusR, P.pillLight, selTier > 0 ? P.grayDeep : P.pillOff, "<", F.age)
        let lw: CGFloat = 44
        drawText("\(Widget.tierFocus[selTier])" + T(" min", "분"), F.main, NSRect(x: minusR.maxX, y: r.minY, width: lw, height: r.height), P.ink, .center)
        plusR = NSRect(x: minusR.maxX + lw, y: py, width: bs, height: bs)
        // which round of this task + its break length
        drawText(T("R\(selTier + 1) · \(Widget.tierBreak[selTier])m rest", "\(selTier + 1)회차 · 휴식 \(Widget.tierBreak[selTier])분"),
                 F.age, NSRect(x: plusR.maxX + 6, y: r.minY, width: goR.minX - plusR.maxX - 8, height: r.height), P.muted)
        pill(plusR, P.pillLight, selTier < Widget.tierFocus.count - 1 ? P.grayDeep : P.pillOff, ">", F.age)
    }

    // ---------- mouse ----------

    func inList(_ p: NSPoint) -> Bool { p.y >= listY && p.y < listY + listH && p.x >= padL && p.x < padL + listW }
    func listPoint(_ p: NSPoint) -> NSPoint { NSPoint(x: p.x - padL, y: p.y - listY) }

    override func mouseDown(with e: NSEvent) {
        let p = convert(e.locationInWindow, from: nil)
        tip.hide()
        if e.modifierFlags.contains(.control) { rightMouseDown(with: e); return } // Ctrl+click = right-click on macOS
        if p.y < padT + headerH { panel.performDrag(with: e); return }              // drag the widget by its header
        if p.y < listY { tracker.promptForTrust(); return }                          // permission notice
        if inList(p) {
            if dragItem != nil || Date().timeIntervalSince1970 - dropTime < 0.3 { dragIndex = -1; return }
            let lp = listPoint(p)
            dragIndex = index(atListY: lp.y)
            dragStart = lp
            if dragIndex >= 0 { sel_(dragIndex) }
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
            sel_(i)
            NSMenu.popUpContextMenu(itemMenu(), with: e, for: self)
        }
    }

    override func mouseDragged(with e: NSEvent) {
        guard dragItem != nil || dragIndex >= 0 else { return }
        let lp = listPoint(convert(e.locationInWindow, from: nil))
        if dragItem == nil {
            if abs(lp.y - dragStart.y) < 4 { return }
            dragItem = items[dragIndex] // lift
            dragIndex = -1              // a drag is not a click
            NSCursor.closedHand.set()
            tip.hide(); tipIndex = -1
            needsDisplay = true
        }
        // live reorder: the lifted card jumps to the row under the cursor.
        // the part of a card you reach first (its near side ~60%) = "drop onto it" -> makes a group.
        let n = items.count
        var to = lp.y < 0 ? 0 : index(atListY: lp.y)
        let outside = to < 0 || lp.y < 0
        if to < 0 { to = n - 1 }
        guard let d = dragItem, let cur = items.firstIndex(where: { $0 === d }) else { return }
        var merge: Item?
        if !outside && to != cur {
            let rr = itemRect(to)
            let frac = (lp.y - rr.minY) / rr.height
            if to > cur ? frac < 0.6 : frac > 0.4 { merge = items[to] }
        }
        if merge !== mergeTarget { mergeTarget = merge; needsDisplay = true }
        if merge == nil && to != cur {
            items.remove(at: cur)
            items.insert(d, at: to)
            sel = to
            relayout() // sections follow the new order
            fit()
        }
    }

    override func mouseUp(with e: NSEvent) {
        let lp = listPoint(convert(e.locationInWindow, from: nil))
        if let d = dragItem { // drop
            dragItem = nil
            let newGroup = mergeTarget != nil && mergeTarget!.groupId == 0
            if let t = mergeTarget { joinGroup(d, t) } // dropped onto a card -> group
            mergeTarget = nil
            normalizeGroups(d)
            dragIndex = -1
            dropTime = Date().timeIntervalSince1970
            NSCursor.arrow.set()
            changed()
            if newGroup && d.groupId != 0 { let gid = d.groupId; DispatchQueue.main.async { self.renameGroup(gid) } } // name the new section
            return
        }
        let i = index(atListY: lp.y)
        let click = dragIndex >= 0 && i == dragIndex
        dragIndex = -1
        if !click { return }
        let it = items[i]
        let mods = e.modifierFlags
        if mods.contains(.shift) { sel = i; removeSelected() }                       // Shift+click = done
        else if mods.contains(.command) { }                                          // Cmd+click = just select
        else if firstOfGroup(i) && lp.y < itemRect(i).minY + hdrH { renameGroup(it.groupId) } // section header
        else if it.has(lastExternal) { renameItem(i) }                                // the task you're already in -> rename it
        else { trySwitch(it) }
    }

    override func mouseMoved(with e: NSEvent) {
        let p = convert(e.locationInWindow, from: nil)
        let hot = [minusR, plusR, goR, stopR].contains { $0.contains(p) } || (!tracker.trusted && p.y >= padT + headerH && p.y < listY)
        (hot ? NSCursor.pointingHand : NSCursor.arrow).set()
        let i = inList(p) && dragItem == nil ? index(atListY: listPoint(p).y) : -1
        if i == tipIndex { return }
        tipIndex = i
        if i < 0 { tip.hide(); return }
        let it = items[i]
        var whereText = ""
        for w in it.wins { whereText += "\n→ " + appName(w.proc) + " — " + w.title }
        if it.wins.isEmpty { whereText += "\n→ " + T("(no linked window)", "(연결된 창 없음)") }
        let sp = panel.convertPoint(toScreen: convert(NSPoint(x: p.x + 12, y: p.y + 16), to: nil))
        tip.show(it.text + whereText, at: sp, seconds: 5)
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
        if p.y < listY { if e.phase == [] || e.phase == .changed { setOpacity(panel.alphaValue + (dy > 0 ? 0.05 : -0.05)) }; return }
        if p.y >= barY { if !focusing && breakWins == nil && abs(dy) >= 1 && (e.phase == [] || e.phase == .began) { stepTier(dy > 0 ? 1 : -1) }; return }
        if inList(p) { scrollY -= e.hasPreciseScrollingDeltas ? dy : dy * row; fit() }
    }

    func appName(_ proc: String) -> String {
        if let a = Tracker.apps(proc).first, let n = a.localizedName { return n }
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: proc) { return url.deletingPathExtension().lastPathComponent }
        return proc
    }

    // ---------- list operations ----------

    func moveSelected(_ delta: Int) {
        let i = sel
        guard i >= 0 && i < items.count else { return }
        let to = max(0, min(items.count - 1, i + delta))
        if to == i { return }
        let it = items.remove(at: i)
        items.insert(it, at: to)
        sel = to
        normalizeGroups(it)
        changed()
    }

    func removeSelected() {
        let i = sel
        guard i >= 0 && i < items.count else { return }
        items.remove(at: i)
        sel = items.isEmpty ? -1 : min(i, items.count - 1)
        tipIndex = -1; tip.hide()
        changed()
    }

    func resetSelected() {
        guard sel >= 0 && sel < items.count else { return }
        items[sel].created = Date()
        changed()
    }

    // ---------- task groups ----------

    static func sameGroup(_ a: Item?, _ b: Item?) -> Bool {
        guard let a = a, let b = b else { return false }
        return a.groupId != 0 && a.groupId == b.groupId
    }

    /// is h one of this item's windows, or of any item in its group?
    func gHas(_ it: Item, _ h: WinRef?) -> Bool {
        if it.has(h) { return true }
        if it.groupId == 0 || h == nil { return false }
        return items.contains { $0 !== it && $0.groupId == it.groupId && $0.has(h) }
    }

    /// drop `it` onto `target`: same group, placed right after the target's group
    func joinGroup(_ it: Item, _ target: Item) {
        if target.groupId == 0 { target.groupId = (items.map { $0.groupId }.max() ?? 0) + 1 }
        it.groupId = target.groupId
        items.removeAll { $0 === it }
        var at = items.firstIndex { $0 === target }!
        while at + 1 < items.count && Widget.sameGroup(items[at + 1], target) { at += 1 }
        items.insert(it, at: at + 1)
        sel = at + 1
    }

    func leaveGroup(_ it: Item?) {
        guard let it = it, it.groupId != 0 else { return }
        let g = it.groupId
        it.groupId = 0
        // move it just below its old group so the group stays in one piece
        items.removeAll { $0 === it }
        let last = items.lastIndex { $0.groupId == g } ?? -1
        items.insert(it, at: last < 0 ? 0 : last + 1)
        sel = last < 0 ? 0 : last + 1
        normalizeGroups()
        changed()
    }

    /// after a reorder: dropped between two members -> joins them; a member with no neighbour in its group -> leaves
    func normalizeGroups(_ dropped: Item? = nil) {
        let n = items.count
        if let d = dropped, let i = items.firstIndex(where: { $0 === d }) {
            let a = i > 0 ? items[i - 1] : nil, b = i + 1 < n ? items[i + 1] : nil
            if Widget.sameGroup(a, b) { d.groupId = a!.groupId }
        }
        var lone: [Item] = []
        for i in 0..<n where items[i].groupId != 0 {
            let up = i > 0 && Widget.sameGroup(items[i - 1], items[i]), down = i + 1 < n && Widget.sameGroup(items[i + 1], items[i])
            if !up && !down { lone.append(items[i]) }
        }
        for it in lone { it.groupId = 0 }
    }

    func firstOfGroup(_ i: Int) -> Bool { let it = items[i]; return it.groupId != 0 && (i == 0 || !Widget.sameGroup(items[i - 1], it)) }
    func lastOfGroup(_ i: Int) -> Bool { let it = items[i]; return it.groupId != 0 && (i + 1 >= items.count || !Widget.sameGroup(items[i + 1], it)) }

    func groupName(_ g: Int) -> String {
        if let n = groupNames[g], !n.isEmpty { return n }
        return T("Group", "묶음")
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
        guard let c = editCommit else { return }
        editCommit = nil
        let t = editBox.stringValue.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "\t", with: " ")
        editBox.isHidden = true
        if commit && !t.isEmpty { c(t) }
        needsDisplay = true
        backToWork()
    }

    func renameItem(_ i: Int) {
        let it = items[i]
        let r = cardRect(cardBounds(i, itemRect(i)))
        let x = r.minX + doneW + measure("\(i + 1)   ", F.main) - 3
        beginEdit(NSRect(x: x, y: r.minY, width: r.maxX - x - 70, height: r.height), it.text, P.greenFill) { [unowned self] t in
            it.text = t; self.changed()
        }
    }

    func renameGroup(_ g: Int) {
        guard let first = items.firstIndex(where: { $0.groupId == g }) else { return }
        let sr = sectionRect(first)
        let rc = NSRect(x: sr.minX + 12, y: sr.minY + 2, width: sr.width - 60, height: hdrH - 5)
        beginEdit(rc, groupName(g), lerp(P.bg, P.groupColor(g), 0.16)) { [unowned self] t in
            self.groupNames[g] = t; self.save(); self.needsDisplay = true
        }
    }

    // ---------- focus timer ----------

    /// items that make up the task being focused on, computed live so items/groups added after Start count too:
    /// the focus item, any item linked to a window you started in, and every member of their groups
    func focusTask() -> [Item] {
        guard focusing else { return [] }
        var set: [Item] = []
        var groups = Set<Int>()
        for it in items {
            let root = it === focusItem || it.wins.contains { w in focusWins.contains { winMatch(w, $0) } }
            if root { set.append(it); if it.groupId != 0 { groups.insert(it.groupId) } }
        }
        for it in items where it.groupId != 0 && groups.contains(it.groupId) && !set.contains(where: { $0 === it }) { set.append(it) }
        return set
    }

    func inFocus(_ it: Item?) -> Bool { guard let it = it else { return false }; return focusTask().contains { $0 === it } }

    /// is window h part of the task being focused on?
    func focusOwns(_ h: WinRef?) -> Bool {
        guard let h = h else { return false }
        if focusWins.contains(where: { $0.matches(h) }) { return true }
        return focusTask().contains { $0.has(h) }
    }

    /// clicking another item while focusing: warn once, second click (within 6s) goes
    func trySwitch(_ it: Item?) {
        guard let it = it else { return }
        if focusing && !inFocus(it) {
            let now = Date().timeIntervalSince1970
            if warnedItem !== it || now - warnTime > 6 {
                warnedItem = it; warnTime = now
                note(T("⚠ Focusing! Click again to switch (1 min grace)", "⚠ 집중 중! 한 번 더 누르면 이동 (1분 유예)"), true)
                Sound.exclamation()
                return
            }
        }
        switchTo(it) // leaving starts the 1-minute grace
    }

    /// < > : move between rounds (15/3, 30/5, 60/15); remembered for the task
    func stepTier(_ d: Int) {
        selTier = max(0, min(Widget.tierFocus.count - 1, selTier + d))
        if let b = barItem { setTaskSessions(b, selTier); save() }
        needsDisplay = true
    }

    /// a group is one task: its round is the highest of its members
    func tier(_ it: Item?) -> Int {
        guard let it = it else { return 0 }
        var n = it.sessions
        for o in items where Widget.sameGroup(o, it) { n = max(n, o.sessions) }
        return min(Widget.tierFocus.count - 1, n)
    }

    func setTaskSessions(_ it: Item?, _ v: Int) {
        guard let it = it else { return }
        for o in items where o === it || Widget.sameGroup(o, it) { o.sessions = v }
    }

    func itemOf(_ h: WinRef?) -> Item? { items.first { $0.has(h) } }

    /// the window you're in changed: the bar shows that task's next default again
    func updateBarItem() {
        if focusing { return }
        let it: Item?
        if let l = lastFocusItem, items.contains(where: { $0 === l }), gHas(l, lastExternal) { it = l } else { it = itemOf(lastExternal) }
        if it !== barItem { barItem = it; selTier = tier(it) }
        needsDisplay = true
    }

    func startFocus() {
        guard let h = lastExternal else { Sound.beep(); return }
        focusWins = tracker.snapGroup(h) // the window you were in + windows tiled beside it
        focusItem = items.first { $0.has(h) }
        if let f = focusItem { // the whole group counts as "the task": moving between its windows keeps the timer
            for it in items where it === f || Widget.sameGroup(it, f) {
                for w in it.wins where w.wid != 0 || !w.proc.isEmpty { focusWins.append(WinRef(wid: w.wid, pid: 0, proc: w.proc, title: w.title)) }
            }
            lastFocusItem = f
        }
        focusing = true
        focusStart = Date()
        focusTier = selTier
        focusEnd = focusStart.addingTimeInterval(Double(Widget.tierFocus[focusTier]) * 60)
        freezeFrom = focusStart; pausedSec = 0; plannedMin = Widget.tierFocus[focusTier]; extension_ = false
        breakMin = Widget.tierBreak[focusTier]
        warnedItem = nil
        barNoteUntil = .distantPast
        startTick()
        needsDisplay = true
        backToWork()
    }

    func barMouseDown(_ e: NSEvent, _ p: NSPoint) {
        if breakWins != nil && breakHidden { showBreak(); return } // bring the break screen back
        if breakWins != nil { return }
        if focusing { if stopR.contains(p) { stopFocus(T("Focus stopped", "집중 중지"), false) }; return }
        if minusR.contains(p) { stepTier(-1) }
        else if plusR.contains(p) { stepTier(1) }
        else if goR.contains(p) {
            startFocus()
            // Option+click (or Ctrl+click on Windows) = 10-second test run
            if focusing && e.modifierFlags.contains(.option) { startTestRun() }
        }
    }

    func startTestRun() {
        focusEnd = focusStart.addingTimeInterval(10); plannedMin = 0
        needsDisplay = true
    }

    // ---------- focus log (CSV sheet) + stats ----------

    /// one row per focus segment: when, how long (paused time excluded), which task/section, how it ended
    func logSession(_ outcome: String) {
        let end = away ? awayStart : Date()
        let min = (end.timeIntervalSince(freezeFrom) - pausedSec) / 60
        if min < 0.5 { return } // ignore accidental starts
        var it = focusItem
        if it == nil { it = focusTask().first }
        let task = it?.text ?? T("(no task)", "(할 일 없음)")
        let grp = it != nil && it!.groupId != 0 ? groupName(it!.groupId) : ""
        let line = [Store.stamp.string(from: freezeFrom), Store.stamp.string(from: end),
                    String(format: "%.1f", min).replacingOccurrences(of: ",", with: "."), Store.csv(task), Store.csv(grp),
                    "\(focusTier + 1)", "\(plannedMin)", extension_ ? "extension" : "focus", outcome].joined(separator: ",")
        store.appendLog(line)
    }

    func openStats() {
        if let s = stats, s.panel.isVisible { s.reload(); s.panel.makeKeyAndOrderFront(nil); return }
        stats = Stats(owner: self)
        stats?.show()
    }

    func openLogSheet() {
        store.ensureLog()
        NSWorkspace.shared.open(store.log)
    }

    // ---- left the task while focusing: pause, show a centered 1-minute warning ----

    func startAway(_ from: WinRef?) {
        away = true
        awayStart = Date()
        awayHome = from != nil && focusOwns(from) ? from : nil
        if awayHome == nil { awayHome = focusWins.first }
        if awayToast == nil {
            let t = Toast(size: NSSize(width: 340, height: 168))
            t.view.paint = { [unowned self] v in self.paintAway(v) }
            t.view.down = { [unowned self] _, p in
                if self.backR.contains(p) { self.goBack() }
                else if self.quitR.contains(p) { self.stopFocus(T("Focus stopped", "집중 중지"), false) }
            }
            t.view.moved = { [unowned self] p in (self.backR.contains(p) || self.quitR.contains(p) ? NSCursor.pointingHand : NSCursor.arrow).set() }
            awayToast = t
        }
        awayToast!.showCentered()
        Sound.exclamation()
        startTick()
        needsDisplay = true
    }

    func goBack() {
        guard let h = awayHome else { return }
        tracker.raise(Win(wid: h.wid, proc: h.proc, title: h.title), activate: true)
    }

    func endAway(_ resume: Bool) {
        if !away { return }
        away = false
        if resume {
            let d = Date().timeIntervalSince(awayStart)
            focusEnd = focusEnd.addingTimeInterval(d); pausedSec += d // paused time doesn't count
        }
        awayToast?.hide()
        if resume { note(T("Welcome back · focus continues", "돌아왔어요 · 집중 이어서"), false) }
        needsDisplay = true
    }

    func paintAway(_ v: PaintView) {
        let w = v.bounds.width, h = v.bounds.height
        fill(NSBezierPath(rect: v.bounds), P.bg)
        stroke(round(v.bounds.insetBy(dx: 1.2, dy: 1.2), 11), P.redFill, 2.4)
        drawText(T("⏸  You're focusing", "⏸  집중 중이에요"), F.mid, NSRect(x: 0, y: 14, width: w, height: 28), P.redDeep, .center)
        drawText(clockText(awaySec - Date().timeIntervalSince(awayStart)), F.big26, NSRect(x: 0, y: 42, width: w, height: 50), P.ink, .center)
        drawText(T("Come back within 1 min to keep the timer", "1분 안에 돌아가면 타이머가 이어져요"), F.main, NSRect(x: 0, y: 92, width: w, height: 22), P.muted, .center)
        let bw: CGFloat = 120, bh: CGFloat = 26, y = h - bh - 14, gap: CGFloat = 10
        backR = NSRect(x: w / 2 - bw - gap / 2, y: y, width: bw, height: bh)
        quitR = NSRect(x: w / 2 + gap / 2, y: y, width: bw, height: bh)
        pill(backR, P.greenFill, P.greenDeep, T("↩ Go back", "↩ 돌아가기"), F.age)
        pill(quitR, P.pill, P.grayDeep, T("■ Stop", "■ 그만하기"), F.age)
    }

    /// stop (reset) the focus timer; the time spent focusing doesn't count toward the other items' age
    func stopFocus(_ noteText: String?, _ warn: Bool, _ outcome: String = "stopped") {
        if !focusing { return }
        logSession(outcome) // before clearing away state: time away isn't focus time
        if away { endAway(false) }
        let el = Date().timeIntervalSince(freezeFrom)
        let task = focusTask()
        for it in items where !task.contains(where: { $0 === it }) {
            it.created = it.created < freezeFrom ? it.created.addingTimeInterval(el) : Date()
        }
        focusing = false
        focusItem = nil; warnedItem = nil
        focusWins = []
        if let n = noteText { note(n, warn) }
        save()
        needsDisplay = true
    }

    func note(_ text: String, _ warn: Bool) {
        barNote = text; barWarn = warn
        barNoteUntil = Date().addingTimeInterval(4)
        startTick()
        needsDisplay = true
    }

    // 1 s timer: runs only while focusing / on break / showing a note
    func startTick() {
        if tick != nil { return }
        let t = Timer(timeInterval: 1, repeats: true) { [weak self] _ in self?.onTick() }
        t.tolerance = 0.1
        RunLoop.main.add(t, forMode: .common)
        tick = t
    }

    func stopTick() { tick?.invalidate(); tick = nil }

    func onTick() {
        let now = Date()
        if away {
            if now.timeIntervalSince(awayStart) >= awaySec {
                stopFocus(T("Didn't come back within 1 min · focus reset", "1분 안에 안 돌아와서 집중 초기화"), true, "left")
            } else { awayToast?.view.needsDisplay = true }
        }
        if focusing && !away && now >= focusEnd {
            for ft in focusTask() { ft.sessions = max(ft.sessions, focusTier + 1) } // next round for the whole task
            lastFocusItem = focusItem
            doneWins = focusWins; doneItem = focusItem; doneTier = focusTier
            doneStart = focusStart; doneEnd = now
            stopFocus(nil, false, "done")
            barItem = nil; updateBarItem()
            startBreak()
        }
        if breakWins != nil && breakHidden {
            if now >= breakEnd { endBreak() }
            else if breakEnd.timeIntervalSince(now) <= soonSec && !soonShown { showSoon() }
            else if let s = soonToast, s.visible { s.view.needsDisplay = true }
        } else if let bw = breakWins {
            if now >= breakEnd { endBreak() }
            else { for w in bw { w.orderFrontRegardless(); w.contentView?.needsDisplay = true } } // stay on top
        }
        needsDisplay = true
        if !focusing && breakWins == nil && now >= barNoteUntil { stopTick() }
    }

    // ---------- break overlay ----------

    func startBreak() {
        breakStart = Date()
        breakEnd = breakStart.addingTimeInterval(Double(breakMin) * 60)
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
                    if e.clickCount >= 2 && self.skipR.contains(p) { self.endBreak(); return } // emergency exit
                    if self.extR.contains(p) { self.extendFocus() } else if self.bgR.contains(p) { self.hideBreak() }
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

    func endBreak(quiet: Bool = false) {
        guard let ws = breakWins else { return }
        breakWins = nil
        breakHidden = false; soonShown = false
        soonToast?.hide()
        for w in ws { w.orderOut(nil); w.close() }
        if !quiet {
            Sound.asterisk()
            note(T("Break over! Ready to start again?", "휴식 끝! 다시 시작해볼까요"), false)
        }
        needsDisplay = true
    }

    /// from the break screen: same task, same round, 5 more minutes; then the break comes back
    func extendFocus() {
        guard breakWins != nil, let dw = doneWins else { return }
        endBreak(quiet: true)
        focusWins = dw
        focusItem = doneItem != nil && items.contains(where: { $0 === doneItem }) ? doneItem : nil
        focusTier = doneTier
        breakMin = Widget.tierBreak[focusTier]
        focusing = true
        let now = Date()
        focusStart = now.addingTimeInterval(-doneEnd.timeIntervalSince(doneStart)) // keep the time already focused on the bar
        focusEnd = now.addingTimeInterval(Double(extendMin) * 60)
        freezeFrom = now; pausedSec = 0; plannedMin = extendMin; extension_ = true
        warnedItem = nil
        startTick()
        note(T("+\(extendMin) min · break comes back after", "+\(extendMin)분 연장 · 끝나면 다시 휴식"), false)
    }

    /// break keeps counting with the cover hidden; a small card pops up 10 s before it ends
    func hideBreak() {
        guard let ws = breakWins else { return }
        breakHidden = true
        for w in ws { w.orderOut(nil) }
        if breakEnd.timeIntervalSinceNow <= soonSec { showSoon() }
        note(T("Resting in the background · alert 10 s before the end", "백그라운드로 휴식 중 · 끝나기 10초 전에 알려줄게요"), false)
        startTick()
    }

    func showBreak() {
        guard let ws = breakWins, breakHidden else { return }
        breakHidden = false
        soonToast?.hide()
        for w in ws { w.orderFrontRegardless() }
        needsDisplay = true
    }

    func showSoon() {
        soonShown = true
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
        drawText(T("Break is almost over", "휴식이 곧 끝나요"), F.mid, NSRect(x: 0, y: 12, width: w, height: 28), P.lavDeep, .center)
        drawText(clockText(breakEnd.timeIntervalSinceNow), F.big26, NSRect(x: 0, y: 40, width: w, height: 50), P.ink, .center)
        drawText(T("Get ready to come back · click to close", "자리로 돌아올 준비 · 클릭하면 닫혀요"), F.age, NSRect(x: 0, y: 94, width: w, height: 22), P.muted, .center)
    }

    func paintBreak(_ v: PaintView) {
        fill(NSBezierPath(rect: v.bounds), P.breakBg)
        let cw: CGFloat = 420, ch: CGFloat = 292
        let c = NSRect(x: (v.bounds.width - cw) / 2, y: (v.bounds.height - ch) / 2, width: cw, height: ch)
        fill(round(c, 24), P.bg)
        drawText(T("Break time", "휴식 시간"), F.mid, NSRect(x: c.minX, y: c.minY + 26, width: c.width, height: 30), P.lavDeep, .center)
        let left = breakEnd.timeIntervalSinceNow
        drawText(clockText(left), F.big, NSRect(x: c.minX, y: c.minY + 62, width: c.width, height: 80), P.ink, .center)
        drawText(T("Look away from the screen, get up, drink water, stretch", "화면에서 눈을 떼고, 일어나서 물 한 잔 + 스트레칭"), F.main,
                 NSRect(x: c.minX, y: c.minY + 150, width: c.width, height: 24), P.muted, .center)
        // "+5 min focus" + "rest in the background" buttons
        let ebw: CGFloat = 150, ebh: CGFloat = 30, egap: CGFloat = 12, ey = c.minY + 188
        if doneWins == nil {
            extR = .zero
            bgR = NSRect(x: c.minX + (c.width - ebw) / 2, y: ey, width: ebw, height: ebh)
        } else {
            extR = NSRect(x: c.midX - ebw - egap / 2, y: ey, width: ebw, height: ebh)
            bgR = NSRect(x: c.midX + egap / 2, y: ey, width: ebw, height: ebh)
            pill(extR, P.greenFill, P.greenDeep, T("+\(extendMin) min focus", "+\(extendMin)분 더 집중"), F.main)
        }
        pill(bgR, P.pill, P.grayDeep, T("Rest in background", "백그라운드로 쉬기"), F.main)
        // progress
        let pr = NSRect(x: c.minX + 50, y: c.maxY - 40, width: c.width - 100, height: 8)
        let done = CGFloat(max(0, min(1, 1 - left / (Double(breakMin) * 60))))
        fill(round(pr, pr.height / 2), P.lav)
        if done > 0.01 { fill(round(NSRect(x: pr.minX, y: pr.minY, width: max(pr.height, pr.width * done), height: pr.height), pr.height / 2), P.lavMid) }
        // small emergency exit at the bottom of the screen
        let skip = T("Urgent? Double-click here to skip the break", "급한 일이면 여기를 더블클릭해서 건너뛰기")
        let sw = measure(skip, F.age)
        skipR = NSRect(x: (v.bounds.width - sw) / 2 - 10, y: v.bounds.height - 70, width: sw + 20, height: 28)
        drawText(skip, F.age, skipR, rgb(120, 112, 140), .center)
    }

    // ---------- shutdown ----------

    func shutdown() {
        stopFocus(nil, false, "quit") // log a running session when the widget closes
        endEdit(true)
        save()
    }
}
