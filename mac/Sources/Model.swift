// Data model + persistence. File formats match the Windows version (PQ.cs):
//   items.tsv     ticks \t wids \t procs \t titles \t text \t sessions \t groupId   (UTF-8 with BOM, \n)
//   groups.tsv    id \t name
//   focus_log.csv start,end,minutes,task,group,round,planned_min,kind,outcome   (UTF-8 BOM, \r\n)
//   opacity.txt   0.95
// On macOS the "window handle" column holds a CGWindowID (0 = whole app) and the "process" column
// holds the app's bundle identifier.
import Foundation

final class Win {
    var wid: UInt32 = 0   // CGWindowID, 0 = app-level link
    var proc = ""         // bundle identifier (or app name)
    var title = ""        // last known window title
    init(wid: UInt32 = 0, proc: String = "", title: String = "") { self.wid = wid; self.proc = proc; self.title = title }
}

/// the window the user is in (or was in)
struct WinRef {
    var wid: UInt32
    var pid: pid_t
    var proc: String
    var title: String
    func same(_ o: WinRef?) -> Bool { guard let o = o else { return false }; return wid == o.wid && proc == o.proc && pid == o.pid }
    /// same window (or same app when either side only knows the app)
    func matches(_ o: WinRef) -> Bool {
        if wid != 0 && o.wid != 0 { return wid == o.wid }
        return !proc.isEmpty && proc == o.proc
    }
}

func winMatch(_ w: Win, _ h: WinRef?) -> Bool {
    guard let h = h else { return false }
    if w.wid != 0 && h.wid != 0 { return w.wid == h.wid }
    return !w.proc.isEmpty && w.proc == h.proc
}

final class Item {
    var text = ""
    var created = Date()
    var wins: [Win] = []      // one task can span several windows
    var lastWid: UInt32 = 0   // which of them you used last
    var sessions = 0          // completed focus sessions -> next session's default length
    var groupId = 0           // 0 = alone; same id = one task group
    func has(_ h: WinRef?) -> Bool { wins.contains { winMatch($0, h) } }
}

// .NET DateTime ticks (local wall clock, 100ns since 0001-01-01) so files stay compatible
enum Ticks {
    static let epoch: Int64 = 621_355_968_000_000_000
    static func from(_ d: Date) -> Int64 {
        let local = d.timeIntervalSince1970 + Double(TimeZone.current.secondsFromGMT(for: d))
        return epoch + Int64((local * 10_000_000).rounded())
    }
    static func date(_ t: Int64) -> Date {
        let local = Double(t - epoch) / 10_000_000
        let guess = Date(timeIntervalSince1970: local)
        return Date(timeIntervalSince1970: local - Double(TimeZone.current.secondsFromGMT(for: guess)))
    }
}

final class Store {
    let dir: URL
    var items: URL { dir.appendingPathComponent("items.tsv") }
    var groups: URL { dir.appendingPathComponent("groups.tsv") }
    var log: URL { dir.appendingPathComponent("focus_log.csv") }
    var opacity: URL { dir.appendingPathComponent("opacity.txt") }
    var backup: URL { dir.appendingPathComponent("items.bak.tsv") }
    /// set when items.tsv exists but could not be read: never overwrite it then
    private(set) var itemsLocked = false

    init() {
        if let custom = ProcessInfo.processInfo.environment["FOCUSQUEUE_DATA_DIR"], !custom.isEmpty {
            dir = URL(fileURLWithPath: (custom as NSString).expandingTildeInPath, isDirectory: true)
        } else {
            let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            dir = base.appendingPathComponent("ADHD-PQ", isDirectory: true)
        }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    /// copy items.tsv -> items.bak.tsv before anything touches it (every launch)
    func backupItems() {
        let fm = FileManager.default
        guard let a = try? fm.attributesOfItem(atPath: items.path), let size = a[.size] as? Int, size > 0 else { return }
        try? fm.removeItem(at: backup)
        try? fm.copyItem(at: items, to: backup)
    }

    static let US: Character = "\u{1f}" // separates multiple linked windows inside a column
    static func clean(_ x: String) -> String {
        String(x.map { $0 == "\t" || $0 == "\r" || $0 == "\n" || $0 == US ? " " : $0 })
    }

    static func readText(_ url: URL) throws -> String {
        let data = try Data(contentsOf: url)
        guard var s = String(data: data, encoding: .utf8) else { throw CocoaError(.fileReadInapplicableStringEncoding) }
        if s.hasPrefix("\u{feff}") { s.removeFirst() }
        return s
    }

    static func writeAtomic(_ text: String, to url: URL, bom: Bool) throws {
        var data = Data()
        if bom { data.append(contentsOf: [0xEF, 0xBB, 0xBF]) }
        data.append(text.data(using: .utf8)!)
        let tmp = url.appendingPathExtension("tmp")
        try data.write(to: tmp)
        if rename(tmp.path, url.path) != 0 { throw CocoaError(.fileWriteUnknown) }
    }

    func loadGroups() -> [Int: String] {
        var names: [Int: String] = [:]
        guard let text = try? Store.readText(groups) else { return names }
        for line in text.split(whereSeparator: { $0 == "\n" || $0 == "\r\n" }) {
            let f = line.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
            if f.count >= 2, let g = Int(f[0]) { names[g] = f[1] }
        }
        return names
    }

    func loadItems() -> [Item] {
        var out: [Item] = []
        guard FileManager.default.fileExists(atPath: items.path) else { return out }
        guard let text = try? Store.readText(items) else { itemsLocked = true; return out }
        for raw in text.split(whereSeparator: { $0 == "\n" || $0 == "\r\n" }) {
            let line = raw.hasSuffix("\r") ? String(raw.dropLast()) : String(raw)
            let f = line.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
            guard f.count >= 2, let ticks = Int64(f[0]) else { continue }
            let it = Item()
            it.created = Ticks.date(ticks)
            if f.count >= 5 {
                let hs = f[1].split(separator: Store.US, omittingEmptySubsequences: false).map(String.init)
                let ps = f[2].split(separator: Store.US, omittingEmptySubsequences: false).map(String.init)
                let ts = f[3].split(separator: Store.US, omittingEmptySubsequences: false).map(String.init)
                for k in 0..<hs.count {
                    // Windows handles are bigger than a CGWindowID: treat those as unknown
                    let hw = UInt32(exactly: Int64(hs[k]) ?? 0) ?? 0
                    let p = k < ps.count ? ps[k] : ""
                    if hw == 0 && p.isEmpty { continue }
                    it.wins.append(Win(wid: hw, proc: p, title: k < ts.count ? ts[k] : ""))
                }
                if f.count >= 6, let sess = Int(f[5]) {
                    it.sessions = sess; it.text = f[4]
                    if f.count >= 7, let g = Int(f[6]) { it.groupId = g }
                } else {
                    it.text = f[4...].joined(separator: " ")
                }
            } else {
                it.text = f[1...].joined(separator: " ") // oldest 2-column format
            }
            out.append(it)
        }
        return out
    }

    func save(_ list: [Item], groupNames: [Int: String]) {
        if !itemsLocked {
            var sb = ""
            let sep = String(Store.US)
            for it in list {
                let hs = it.wins.map { String($0.wid) }, ps = it.wins.map { Store.clean($0.proc) }, ts = it.wins.map { Store.clean($0.title) }
                sb += "\(Ticks.from(it.created))\t\(hs.joined(separator: sep))\t\(ps.joined(separator: sep))\t\(ts.joined(separator: sep))\t"
                sb += "\(Store.clean(it.text))\t\(it.sessions)\t\(it.groupId)\n"
            }
            try? Store.writeAtomic(sb, to: items, bom: true)
        }
        // names of sections still in use
        let used = Set(list.filter { $0.groupId != 0 }.map { $0.groupId })
        var gs = ""
        for k in groupNames.keys.sorted() where used.contains(k) { gs += "\(k)\t\(Store.clean(groupNames[k]!))\n" }
        try? Store.writeAtomic(gs, to: groups, bom: true)
    }

    func loadOpacity() -> CGFloat {
        if let s = try? String(contentsOf: opacity, encoding: .utf8),
           let v = Double(s.trimmingCharacters(in: .whitespacesAndNewlines)) { return CGFloat(max(0.3, min(1.0, v))) }
        return 0.95
    }

    func saveOpacity(_ v: CGFloat) {
        try? String(format: "%.2f", Double(v)).replacingOccurrences(of: ",", with: ".")
            .write(to: opacity, atomically: true, encoding: .utf8)
    }

    // ---- focus log ----
    static let logHeader = "start,end,minutes,task,group,round,planned_min,kind,outcome"
    // kind: focus | extension    outcome: done | stopped | left | quit

    static func csv(_ v: String) -> String {
        v.contains(where: { $0 == "," || $0 == "\"" || $0 == "\n" || $0 == "\r" })
            ? "\"" + v.replacingOccurrences(of: "\"", with: "\"\"") + "\"" : v
    }

    func ensureLog() {
        if !FileManager.default.fileExists(atPath: log.path) {
            var d = Data([0xEF, 0xBB, 0xBF]) // BOM: spreadsheet apps read non-ASCII task names
            d.append((Store.logHeader + "\r\n").data(using: .utf8)!)
            try? d.write(to: log)
        }
    }

    func appendLog(_ line: String) {
        ensureLog()
        guard let h = try? FileHandle(forWritingTo: log) else { return }
        h.seekToEndOfFile()
        h.write((line + "\r\n").data(using: .utf8)!)
        try? h.close()
    }

    static let stamp: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return f
    }()
}
