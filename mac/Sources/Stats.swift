// Stats window: a centered window you can close (✕ / Esc), drawn like the widget. Port of StatsForm (PQ.cs).
import AppKit

struct Rec {
    var start: Date; var min: Double; var task: String; var group: String; var round: Int; var planned: Int; var kind: String; var outcome: String
}

func parseCsv(_ text: String) -> [[String]] {
    var rows: [[String]] = [], row: [String] = [], f = "", q = false
    var chars = Array(text)
    if chars.first == "\u{feff}" { chars.removeFirst() }
    var i = 0
    func endRow() { row.append(f); f = ""; if row.contains(where: { !$0.isEmpty }) { rows.append(row) }; row = [] }
    while i < chars.count {
        let c = chars[i]
        if q {
            if c == "\"" { if i + 1 < chars.count && chars[i + 1] == "\"" { f.append("\""); i += 1 } else { q = false } }
            else { f.append(c) }
        } else if c == "\"" { q = true }
        else if c == "," { row.append(f); f = "" }
        else if c == "\n" || c == "\r" || c == "\r\n" { endRow() } // Swift treats \r\n as one Character
        else { f.append(c) }
        i += 1
    }
    if !f.isEmpty || !row.isEmpty { endRow() }
    return rows
}

// logs from very old Windows builds used Korean values
private let oldCodes = ["기본": "focus", "연장": "extension", "완료": "done", "중지": "stopped", "이탈": "left", "종료": "quit"]
func logCode(_ v: String) -> String { oldCodes[v] ?? v }

func outcomeLabel(_ o: String) -> String {
    switch o {
    case "done": return T("done", "완료")
    case "stopped": return T("stopped", "중지")
    case "left": return T("left", "이탈")
    case "quit": return T("closed", "종료")
    default: return o
    }
}

final class Stats {
    unowned let owner: Widget
    let panel: FloatPanel
    let view = PaintView()
    var all: [Rec] = []
    var range = "7"
    var closeR = NSRect.zero, sheetR = NSRect.zero
    var chips: [(String, NSRect)] = []
    var hover: [(NSRect, String)] = []
    var tipShown: String?
    let tip = Tip()
    static let card = NSColor.white, line = rgb(235, 229, 238), grid = rgb(240, 235, 242)
    static let bar = rgb(90, 168, 116), ink2 = rgb(107, 100, 120)

    init(owner: Widget) {
        self.owner = owner
        let size = NSSize(width: 760, height: 640)
        let wa = (NSScreen.screens.first ?? NSScreen.main!).visibleFrame
        panel = makePanel(NSRect(x: wa.minX + (wa.width - size.width) / 2, y: wa.minY + (wa.height - size.height) / 2,
                                 width: size.width, height: size.height), level: .floating)
        panel.becomesKeyOnlyIfNeeded = false
        panel.title = T("Focus log", "집중 기록")
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

    func reload() {
        all = []
        if let text = try? Store.readText(owner.store.log) {
            for r in parseCsv(text) {
                guard r.count >= 9, let st = Store.stamp.date(from: r[0]) else { continue }
                all.append(Rec(start: st, min: Double(r[2]) ?? 0, task: r[3].isEmpty ? T("(no task)", "(할 일 없음)") : r[3], group: r[4],
                               round: Int(r[5]) ?? 0, planned: Int(r[6]) ?? 0, kind: logCode(r[7]), outcome: logCode(r[8])))
            }
        }
        view.needsDisplay = true
    }

    var cal: Calendar { Calendar.current }

    func inRange() -> [Rec] {
        let today = cal.startOfDay(for: Date())
        return all.filter { r in
            range == "all" || (range == "today" ? r.start >= today : r.start >= cal.date(byAdding: .day, value: 1 - Int(range)!, to: today)!)
        }
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
        let rows = inRange()
        var total = 0.0, done = 0, ext = 0, left = 0
        var days = Set<Date>()
        for r in rows {
            total += r.min; days.insert(cal.startOfDay(for: r.start))
            if r.outcome == "done" { done += 1 }
            if r.kind == "extension" { ext += 1 }
            if r.outcome == "left" { left += 1 }
        }

        // header: title, range chips, close
        let label = range == "today" ? T("Today", "오늘") : range == "7" ? T("Last 7 days", "최근 7일") : range == "30" ? T("Last 30 days", "최근 30일") : T("All time", "전체 기간")
        drawText(T("Focus log", "집중 기록"), F.title, NSRect(x: M, y: 16, width: 300, height: 28), P.ink)
        drawText(label + T(" · \(rows.count) sessions", " · 세션 \(rows.count)개"), F.small, NSRect(x: M, y: 42, width: 300, height: 18), P.muted)
        closeR = NSRect(x: W - M - 28, y: 20, width: 28, height: 28)
        fill(round(closeR, 14), P.pillLight)
        drawText("✕", F.main, closeR, P.grayDeep, .center)
        var cx = closeR.minX - 14
        for kv in [("all", T("All", "전체")), ("30", T("30d", "30일")), ("7", T("7d", "7일")), ("today", T("Today", "오늘"))] {
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
        let n = rows.count
        let k: [[String]] = [
            [T("Total focus", "총 집중"), n > 0 ? fmtMin(total) : "–", days.count > 1 ? T("avg/day ", "하루 평균 ") + fmtMin(total / Double(days.count)) : ""],
            [T("Sessions", "세션"), n > 0 ? "\(n)" : "–", ext > 0 ? T("incl. \(ext) extensions", "연장 \(ext)번 포함") : ""],
            [T("Completed", "끝까지 완료"), n > 0 ? "\(done * 100 / n)%" : "–", n > 0 ? "\(done) / \(n)" + T(" sessions", " 세션") : ""],
            [T("Avg session", "평균 세션"), n > 0 ? fmtMin(total / Double(n)) : "–", left > 0 ? T("left > 1 min: \(left)", "1분 넘게 이탈 \(left)번") : ""]]
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
        drawChart(chartR, rows)
        drawTasks(taskR, rows)

        // recent sessions
        let tr = NSRect(x: M, y: ry + rh + gap, width: W - M * 2, height: H - (ry + rh + gap) - M)
        cardAt(tr)
        drawTable(tr, rows)
    }

    func drawChart(_ c: NSRect, _ rows: [Rec]) {
        var names: [String] = [], ticks: [String] = [], vals: [Double] = []
        let title: String
        let today = cal.startOfDay(for: Date())
        if range == "today" {
            title = T("Focus by hour (today)", "시간대별 집중 (오늘)")
            for h in 0..<24 { names.append("\(h)" + T(":00", "시")); ticks.append(h % 4 == 0 ? "\(h)" + T(":00", "시") : ""); vals.append(0) }
            for r in rows { vals[cal.component(.hour, from: r.start)] += r.min }
        } else {
            title = T("Focus per day", "일별 집중 시간")
            var n = range == "all" ? 14 : Int(range)!
            if range == "all", let first = all.map({ $0.start }).min() {
                let d = cal.dateComponents([.day], from: cal.startOfDay(for: min(first, Date())), to: today).day ?? 0
                n = max(7, min(90, d + 1))
            }
            let every = n > 14 ? Int(ceil(Double(n) / 7)) : 1
            var idx: [Date: Int] = [:]
            let dow = Array(T("SMTWTFS", "일월화수목금토"))
            for i in stride(from: n - 1, through: 0, by: -1) {
                let d = cal.date(byAdding: .day, value: -i, to: today)!
                let j = n - 1 - i
                idx[d] = names.count
                let mo = cal.component(.month, from: d), dy = cal.component(.day, from: d)
                names.append("\(mo)/\(dy) (\(dow[cal.component(.weekday, from: d) - 1]))")
                ticks.append(j % every == 0 || i == 0 ? "\(mo)/\(dy)" : "")
                vals.append(0)
            }
            for r in rows { if let j = idx[cal.startOfDay(for: r.start)] { vals[j] += r.min } }
        }
        drawText(title, F.main, NSRect(x: c.minX + 14, y: c.minY + 8, width: c.width - 28, height: 24), P.ink)
        let L0 = c.minX + 44, R0 = c.maxX - 14, T0 = c.minY + 42, B0 = c.maxY - 28
        let mx = max(30, vals.max() ?? 0)
        let step = mx > 240 ? 120 : mx > 120 ? 60 : 30
        let top = ceil(mx / Double(step)) * Double(step)
        var v = 0
        while Double(v) <= top {
            let y = B0 - (B0 - T0) * CGFloat(Double(v) / top)
            Stats.grid.setStroke()
            let ln = NSBezierPath(); ln.lineWidth = 1; ln.move(to: NSPoint(x: L0, y: y + 0.5)); ln.line(to: NSPoint(x: R0, y: y + 0.5)); ln.stroke()
            drawText(v >= 60 && v % 60 == 0 ? "\(v / 60)h" : "\(v)m", F.small, NSRect(x: c.minX, y: y - 9, width: L0 - c.minX - 6, height: 18), P.muted, .right)
            v += step
        }
        let cw = (R0 - L0) / CGFloat(vals.count), bw = max(2, min(26, cw - 2))
        for i in vals.indices {
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
            if !ticks[i].isEmpty {
                drawText(ticks[i], F.small, NSRect(x: x + bw / 2 - 24, y: B0 + 4, width: 48, height: 18), P.muted, .center)
            }
            hover.append((NSRect(x: L0 + CGFloat(i) * cw, y: T0, width: ceil(cw), height: B0 - T0),
                          names[i] + " · " + (vals[i] > 0 ? fmtMin(vals[i]) : T("no focus", "기록 없음"))))
        }
    }

    func drawTasks(_ c: NSRect, _ rows: [Rec]) {
        drawText(T("Focus by task", "할 일별 집중"), F.main, NSRect(x: c.minX + 14, y: c.minY + 8, width: c.width - 28, height: 24), P.ink)
        if rows.isEmpty {
            drawLines(T("No sessions yet\nPress ▶ Start to focus", "아직 기록이 없어요\n▶ 시작으로 집중해 보세요"), F.small, c, P.muted)
            return
        }
        var by: [String: (Double, Int)] = [:], meta: [String: (String, String)] = [:], order: [String] = []
        for r in rows {
            let key = r.task + "\u{1}" + r.group
            if by[key] == nil { by[key] = (0, 0); meta[key] = (r.task, r.group); order.append(key) }
            by[key]!.0 += r.min; by[key]!.1 += 1
        }
        let keys = order.sorted { by[$0]!.0 > by[$1]!.0 }
        var list: [(String, String)] = [], vals: [Double] = [], cnt: [Int] = []
        for i in keys.indices {
            if i == 5 && keys.count > 6 {
                var rest = 0.0, rn = 0
                for j in 5..<keys.count { rest += by[keys[j]]!.0; rn += by[keys[j]]!.1 }
                list.append((T("\(keys.count - 5) others", "기타 \(keys.count - 5)개"), "")); vals.append(rest); cnt.append(rn)
                break
            }
            list.append(meta[keys[i]]!); vals.append(by[keys[i]]!.0); cnt.append(by[keys[i]]!.1)
        }
        let mx = vals.max() ?? 1
        var y = c.minY + 40
        let rowH: CGFloat = 31, x = c.minX + 14, w = c.width - 28
        for i in list.indices {
            let val = fmtMin(vals[i])
            let vw = measure(val, F.small)
            let name = list[i].0
            let nw = min(measure(name, F.main), w - vw - 8)
            drawText(name, F.main, NSRect(x: x, y: y, width: nw + 1, height: 18), P.ink)
            if !list[i].1.isEmpty {
                drawText(list[i].1, F.small, NSRect(x: x + nw + 6, y: y, width: max(0, w - vw - nw - 12), height: 18), P.muted)
            }
            drawText(val, F.small, NSRect(x: x, y: y, width: w, height: 18), Stats.ink2, .right)
            let track = NSRect(x: x, y: y + 20, width: w, height: 6)
            fill(round(track, 3), Stats.grid)
            fill(round(NSRect(x: x, y: track.minY, width: max(track.height, w * CGFloat(vals[i] / mx)), height: track.height), 3), Stats.bar)
            hover.append((NSRect(x: x, y: y, width: w, height: rowH), name + " · " + fmtMin(vals[i]) + T(" · \(cnt[i]) sessions", " · 세션 \(cnt[i])개")))
            y += rowH
        }
    }

    func drawTable(_ c: NSRect, _ rows: [Rec]) {
        let sorted = rows.sorted { $0.start > $1.start }
        drawText(T("Recent sessions", "최근 세션"), F.main, NSRect(x: c.minX + 14, y: c.minY + 8, width: 200, height: 24), P.ink)
        let link = T("Open log sheet ↗", "기록 시트 열기 ↗")
        let lw = measure(link, F.small) + 8
        sheetR = NSRect(x: c.maxX - 14 - lw, y: c.minY + 10, width: lw, height: 20)
        drawText(link, F.small, sheetR, P.greenDeep, .center)
        let x = c.minX + 14, w = c.width - 28
        let cols: [CGFloat] = [0, 92, 330, 470, 560]
        let heads = [T("Start", "시작"), T("Task", "할 일"), T("Group", "묶음"), T("Focus", "집중"), T("Result", "결과")]
        var y = c.minY + 38
        for i in heads.indices { drawText(heads[i], F.small, NSRect(x: x + cols[i], y: y, width: 120, height: 20), P.muted) }
        Stats.line.setStroke()
        let ln = NSBezierPath(); ln.lineWidth = 1; ln.move(to: NSPoint(x: x, y: y + 22.5)); ln.line(to: NSPoint(x: x + w, y: y + 22.5)); ln.stroke()
        y += 26
        let rowH: CGFloat = 24
        let maxRows = max(0, Int((c.maxY - 8 - y) / rowH))
        if sorted.isEmpty {
            drawText(T("No sessions in this period", "이 기간에는 기록이 없어요"), F.small, NSRect(x: x, y: y, width: w, height: 40), P.muted, .center)
            return
        }
        let hm = DateFormatter(); hm.dateFormat = "HH:mm"
        for r in sorted.prefix(maxRows) {
            let mo = cal.component(.month, from: r.start), dy = cal.component(.day, from: r.start)
            let v = ["\(mo)/\(dy) " + hm.string(from: r.start), r.task, r.group,
                     String(format: "%.1f", r.min) + T("", "분") + (r.planned > 0 ? " / \(r.planned)" : "") + T(" min", ""),
                     outcomeLabel(r.outcome) + (r.kind == "extension" ? T(" · ext.", " · 연장") : "")]
            for j in v.indices {
                let cw2 = (j + 1 < cols.count ? cols[j + 1] : w) - cols[j] - 8
                let col = j == 4 ? (r.outcome == "done" ? P.greenDeep : r.outcome == "left" ? P.redDeep : Stats.ink2) : j == 0 || j == 3 ? Stats.ink2 : P.ink
                drawText(v[j], j == 1 ? F.main : F.small, NSRect(x: x + cols[j], y: y, width: cw2, height: rowH), col)
            }
            y += rowH
        }
    }

    func onDown(_ e: NSEvent, _ p: NSPoint) {
        if closeR.contains(p) { close(); return }
        if sheetR.contains(p) { owner.openLogSheet(); return }
        for (k, r) in chips where r.contains(p) { range = k; view.needsDisplay = true; return }
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
