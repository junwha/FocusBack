// Stats window: a centered window you can close (✕ / Esc), drawn like the widget.
// The numbers come from the core (pq_stats); this file only draws them.
import AppKit
import PQCore

final class Stats {
    unowned let owner: Widget
    let panel: FloatPanel
    let view = PaintView()
    var range = "7"
    var closeR = NSRect.zero, sheetR = NSRect.zero
    var chips: [(String, NSRect)] = []
    var hover: [(NSRect, String)] = []
    var tipShown: String?
    let tip = Tip()
    static let card = NSColor.white, line = rgb(235, 229, 238), grid = rgb(240, 235, 242)
    static let bar = rgb(90, 168, 116), ink2 = rgb(107, 100, 120)
    var c: OpaquePointer { core.c }

    init(owner: Widget) {
        self.owner = owner
        let size = NSSize(width: 760, height: 640)
        let wa = (NSScreen.screens.first ?? NSScreen.main!).visibleFrame
        panel = makePanel(NSRect(x: wa.minX + (wa.width - size.width) / 2, y: wa.minY + (wa.height - size.height) / 2,
                                 width: size.width, height: size.height), level: .floating)
        panel.becomesKeyOnlyIfNeeded = false
        panel.contentView = view
        view.wantsLayer = true
        view.layer?.cornerRadius = 12
        view.layer?.masksToBounds = true
        view.paint = { [unowned self] _ in self.paint() }
        view.down = { [unowned self] e, p in self.onDown(e, p) }
        view.moved = { [unowned self] p in self.onMove(p) }
        view.exited = { [unowned self] in self.tip.hide(); self.tipShown = nil }
        panel.onKey = { [unowned self] e in
            if e.keyCode == 53 { self.close(); return true } // Esc
            return false
        }
        reload()
    }

    func show() { panel.makeKeyAndOrderFront(nil); panel.orderFrontRegardless() }
    func close() { tip.hide(); panel.orderOut(nil) }

    var days: Int32 { range == "today" ? PQ_RANGE_TODAY : range == "all" ? PQ_RANGE_ALL : Int32(range)! }

    func reload() {
        panel.title = tr("stats.title")
        pq_stats(c, days, now())
        view.needsDisplay = true
    }

    func cardAt(_ r: NSRect) {
        let p = round(r.insetBy(dx: 0.5, dy: 0.5), 14)
        fill(p, Stats.card)
        stroke(p, Stats.line, 1)
    }

    func paint() {
        let W = view.bounds.width, H = view.bounds.height, M: CGFloat = 24
        fill(NSBezierPath(rect: view.bounds), P.bg)
        hover = []; chips = []
        let n = Int(pq_st_sessions(c)), total = pq_st_total(c), done = Int(pq_st_done(c))
        let ext = Int(pq_st_extensions(c)), left = Int(pq_st_left(c)), nd = Int(pq_st_days(c))

        // header: title, range chips, close
        let label = tr(range == "today" ? "stats.range.today" : range == "7" ? "stats.range.7" : range == "30" ? "stats.range.30" : "stats.range.all")
        drawText(tr("stats.title"), F.title, NSRect(x: M, y: 16, width: 300, height: 28), P.ink)
        drawText(core.fmt("stats.subtitle", label, "\(n)"), F.small, NSRect(x: M, y: 42, width: 300, height: 18), P.muted)
        closeR = NSRect(x: W - M - 28, y: 20, width: 28, height: 28)
        fill(round(closeR, 14), P.pillLight)
        drawText("✕", F.main, closeR, P.grayDeep, .center)
        var cx = closeR.minX - 14
        for kv in [("all", tr("stats.chip.all")), ("30", tr("stats.chip.30")), ("7", tr("stats.chip.7")), ("today", tr("stats.chip.today"))] {
            let cw: CGFloat = kv.1.count > 2 ? 48 : 44
            let cr = NSRect(x: cx - cw, y: 22, width: cw, height: 24)
            cx -= cw + 6
            let on = range == kv.0
            fill(round(cr, 12), on ? P.ink : P.pillLight)
            drawText(kv.1, F.small, cr, on ? .white : Stats.ink2, .center)
            chips.append((kv.0, cr))
        }

        // KPI tiles
        let ky: CGFloat = 72, kh: CGFloat = 78, gap: CGFloat = 10, kw = (W - M * 2 - gap * 3) / 4
        let k: [[String]] = [
            [tr("stats.kpi.total"), n > 0 ? core.fmtMin(total) : "–", nd > 1 ? core.fmt("stats.kpi.avg_day", core.fmtMin(total / Double(nd))) : ""],
            [tr("stats.kpi.sessions"), n > 0 ? "\(n)" : "–", ext > 0 ? core.fmt("stats.kpi.ext", "\(ext)") : ""],
            [tr("stats.kpi.completed"), n > 0 ? "\(done * 100 / n)%" : "–", n > 0 ? core.fmt("stats.kpi.done_of", "\(done)", "\(n)") : ""],
            [tr("stats.kpi.avg"), n > 0 ? core.fmtMin(total / Double(n)) : "–", left > 0 ? core.fmt("stats.kpi.left", "\(left)") : ""]]
        for i in 0..<4 {
            let r = NSRect(x: M + CGFloat(i) * (kw + gap), y: ky, width: kw, height: kh)
            cardAt(r)
            drawText(k[i][0], F.small, NSRect(x: r.minX + 14, y: r.minY + 8, width: r.width - 20, height: 18), Stats.ink2)
            drawText(k[i][1], F.kpi, NSRect(x: r.minX + 12, y: r.minY + 26, width: r.width - 20, height: 30), P.ink)
            drawText(k[i][2], F.small, NSRect(x: r.minX + 14, y: r.minY + 54, width: r.width - 20, height: 18), P.muted)
        }

        // chart: one series (single hue, no legend; the title names it)
        let ry = ky + kh + gap, rh: CGFloat = 236
        let chartR = NSRect(x: M, y: ry, width: floor((W - M * 2 - gap) * 0.58), height: rh)
        let taskR = NSRect(x: chartR.maxX + gap, y: ry, width: W - M - chartR.maxX - gap, height: rh)
        cardAt(chartR); cardAt(taskR)
        drawChart(chartR)
        drawTasks(taskR)

        // recent sessions
        let tr_ = NSRect(x: M, y: ry + rh + gap, width: W - M * 2, height: H - (ry + rh + gap) - M)
        cardAt(tr_)
        drawTable(tr_)
    }

    func drawChart(_ cr: NSRect) {
        let nb = Int(pq_st_bucket_count(c))
        let vals = (0..<nb).map { pq_st_bucket_min(c, Int32($0)) }
        drawText(tr(pq_st_hourly(c) != 0 ? "stats.chart.hourly" : "stats.chart.daily"), F.main,
                 NSRect(x: cr.minX + 14, y: cr.minY + 8, width: cr.width - 28, height: 24), P.ink)
        let L0 = cr.minX + 44, R0 = cr.maxX - 14, T0 = cr.minY + 42, B0 = cr.maxY - 28
        let mx = max(30, vals.max() ?? 0)
        let step = mx > 240 ? 120 : mx > 120 ? 60 : 30
        let top = ceil(mx / Double(step)) * Double(step)
        var v = 0
        while Double(v) <= top {
            let y = B0 - (B0 - T0) * CGFloat(Double(v) / top)
            Stats.grid.setStroke()
            let ln = NSBezierPath(); ln.lineWidth = 1; ln.move(to: NSPoint(x: L0, y: y + 0.5)); ln.line(to: NSPoint(x: R0, y: y + 0.5)); ln.stroke()
            drawText(v >= 60 && v % 60 == 0 ? "\(v / 60)h" : "\(v)m", F.small, NSRect(x: cr.minX, y: y - 9, width: L0 - cr.minX - 6, height: 18), P.muted, .right)
            v += step
        }
        guard nb > 0 else { return }
        let cw = (R0 - L0) / CGFloat(nb), bw = max(2, min(26, cw - 2))
        for i in 0..<nb {
            let x = L0 + CGFloat(i) * cw + (cw - bw) / 2
            let h = floor((B0 - T0) * CGFloat(vals[i] / top))
            if h > 0 {
                let br = NSRect(x: x, y: B0 - h, width: bw, height: h)
                let rad = min(4, min(bw / 2, h))
                let p = NSBezierPath()
                p.move(to: NSPoint(x: br.minX, y: br.maxY))
                p.line(to: NSPoint(x: br.minX, y: br.minY + rad))
                p.appendArc(withCenter: NSPoint(x: br.minX + rad, y: br.minY + rad), radius: rad, startAngle: 180, endAngle: 270)
                p.line(to: NSPoint(x: br.maxX - rad, y: br.minY))
                p.appendArc(withCenter: NSPoint(x: br.maxX - rad, y: br.minY + rad), radius: rad, startAngle: 270, endAngle: 360)
                p.line(to: NSPoint(x: br.maxX, y: br.maxY))
                p.close()
                fill(p, Stats.bar)
            }
            let tick = str(pq_st_bucket_tick(c, Int32(i)))
            if !tick.isEmpty { drawText(tick, F.small, NSRect(x: x + bw / 2 - 24, y: B0 + 4, width: 48, height: 18), P.muted, .center) }
            hover.append((NSRect(x: L0 + CGFloat(i) * cw, y: T0, width: ceil(cw), height: B0 - T0),
                          str(pq_st_bucket_label(c, Int32(i))) + " · " + (vals[i] > 0 ? core.fmtMin(vals[i]) : tr("stats.chart.none"))))
        }
    }

    func drawTasks(_ cr: NSRect) {
        drawText(tr("stats.tasks"), F.main, NSRect(x: cr.minX + 14, y: cr.minY + 8, width: cr.width - 28, height: 24), P.ink)
        let nt = Int(pq_st_task_count(c))
        if nt == 0 {
            drawLines(tr("stats.tasks.empty"), F.small, cr, P.muted)
            return
        }
        let vals = (0..<nt).map { pq_st_task_min(c, Int32($0)) }
        let mx = max(vals.max() ?? 1, 0.0001)
        var y = cr.minY + 40
        let rowH: CGFloat = 31, x = cr.minX + 14, w = cr.width - 28
        for i in 0..<nt {
            let val = core.fmtMin(vals[i])
            let vw = measure(val, F.small)
            let name = str(pq_st_task_name(c, Int32(i))), grp = str(pq_st_task_group(c, Int32(i)))
            let nw = min(measure(name, F.main), w - vw - 8)
            drawText(name, F.main, NSRect(x: x, y: y, width: nw + 1, height: 18), P.ink)
            if !grp.isEmpty { drawText(grp, F.small, NSRect(x: x + nw + 6, y: y, width: max(0, w - vw - nw - 12), height: 18), P.muted) }
            drawText(val, F.small, NSRect(x: x, y: y, width: w, height: 18), Stats.ink2, .right)
            let track = NSRect(x: x, y: y + 20, width: w, height: 6)
            fill(round(track, 3), Stats.grid)
            fill(round(NSRect(x: x, y: track.minY, width: max(track.height, w * CGFloat(vals[i] / mx)), height: track.height), 3), Stats.bar)
            hover.append((NSRect(x: x, y: y, width: w, height: rowH),
                          core.fmt("stats.tasks.tip", name, val, "\(pq_st_task_sessions(c, Int32(i)))")))
            y += rowH
        }
    }

    func drawTable(_ cr: NSRect) {
        drawText(tr("stats.recent"), F.main, NSRect(x: cr.minX + 14, y: cr.minY + 8, width: 200, height: 24), P.ink)
        let link = tr("stats.open_log")
        let lw = measure(link, F.small) + 8
        sheetR = NSRect(x: cr.maxX - 14 - lw, y: cr.minY + 10, width: lw, height: 20)
        drawText(link, F.small, sheetR, P.greenDeep, .center)
        let x = cr.minX + 14, w = cr.width - 28
        let cols: [CGFloat] = [0, 92, 330, 470, 560]
        let heads = ["stats.col.start", "stats.col.task", "stats.col.group", "stats.col.focus", "stats.col.result"].map(tr)
        var y = cr.minY + 38
        for i in heads.indices { drawText(heads[i], F.small, NSRect(x: x + cols[i], y: y, width: 120, height: 20), P.muted) }
        Stats.line.setStroke()
        let ln = NSBezierPath(); ln.lineWidth = 1; ln.move(to: NSPoint(x: x, y: y + 22.5)); ln.line(to: NSPoint(x: x + w, y: y + 22.5)); ln.stroke()
        y += 26
        let rowH: CGFloat = 24
        let maxRows = max(0, Int((cr.maxY - 8 - y) / rowH))
        let nr = Int(pq_st_row_count(c))
        if nr == 0 {
            drawText(tr("stats.empty"), F.small, NSRect(x: x, y: y, width: w, height: 40), P.muted, .center)
            return
        }
        for i in 0..<min(maxRows, nr) {
            let k = Int32(i)
            let outcome = str(pq_st_row_outcome(c, k)), kind = str(pq_st_row_kind(c, k))
            let planned = Int(pq_st_row_planned(c, k))
            let mins = String(format: "%.1f", pq_st_row_min(c, k)).replacingOccurrences(of: ",", with: ".")
            let v = [str(pq_fmt_stamp(pq_st_row_start(c, k))), str(pq_st_row_task(c, k)), str(pq_st_row_group(c, k)),
                     planned > 0 ? core.fmt("stats.row.min", mins, "\(planned)") : mins,
                     str(pq_outcome_label(c, outcome)) + (kind == "extension" ? tr("stats.row.ext") : "")]
            for j in v.indices {
                let cw2 = (j + 1 < cols.count ? cols[j + 1] : w) - cols[j] - 8
                let col = j == 4 ? (outcome == "done" ? P.greenDeep : outcome == "left" ? P.redDeep : Stats.ink2) : j == 0 || j == 3 ? Stats.ink2 : P.ink
                drawText(v[j], j == 1 ? F.main : F.small, NSRect(x: x + cols[j], y: y, width: cw2, height: rowH), col)
            }
            y += rowH
        }
    }

    func onDown(_ e: NSEvent, _ p: NSPoint) {
        if closeR.contains(p) { close(); return }
        if sheetR.contains(p) { owner.openLogSheet(); return }
        for (k, r) in chips where r.contains(p) { range = k; reload(); return }
        if p.y < 64 { panel.performDrag(with: e) } // drag by the header
    }

    func onMove(_ p: NSPoint) {
        var hand = closeR.contains(p) || sheetR.contains(p)
        for (_, r) in chips where r.contains(p) { hand = true }
        (hand ? NSCursor.pointingHand : NSCursor.arrow).set()
        let t = hover.first { $0.0.contains(p) }?.1
        if t == tipShown { return }
        tipShown = t
        if let t = t {
            tip.show(t, at: panel.convertPoint(toScreen: view.convert(NSPoint(x: p.x + 12, y: p.y - 6), to: nil)), seconds: 4)
        } else { tip.hide() }
    }
}
