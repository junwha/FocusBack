// Swift side of the shared C core (core/pq_core.h): the data folder, a thin wrapper, and the
// window keys the core sees. All rules and state live in the core.
import AppKit
import PQCore

func now() -> Double { Date().timeIntervalSince1970 }

/// the data folder: ~/Library/Application Support/FocusBack, or FOCUSBACK_DATA_DIR for tests.
/// The folder of versions from before the rename is moved over once; if that fails it is used as is.
enum DataDir {
    static let legacyName = "ADHD-PQ" // data folder name before the app was called FocusBack
    static let url: URL = {
        if let custom = ProcessInfo.processInfo.environment["FOCUSBACK_DATA_DIR"], !custom.isEmpty {
            return URL(fileURLWithPath: (custom as NSString).expandingTildeInPath, isDirectory: true)
        }
        let fm = FileManager.default
        let base = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("FocusBack", isDirectory: true)
        let old = base.appendingPathComponent(legacyName, isDirectory: true)
        var isDir: ObjCBool = false
        if !fm.fileExists(atPath: dir.path) && fm.fileExists(atPath: old.path, isDirectory: &isDir) && isDir.boolValue {
            do { try fm.moveItem(at: old, to: dir) } catch { return old } // keep using the old folder this run
        }
        return dir
    }()
}

/// the window the user is in (or was in)
struct WinRef {
    var wid: UInt32      // CGWindowID, 0 = only the app is known
    var pid: pid_t
    var proc: String     // bundle identifier
    var title: String
    /// what the core stores: the CGWindowID, or a negative per-app id when tracking per app
    var key: pq_win { wid != 0 ? pq_win(wid) : Core.appKey(proc) }
}

func str(_ p: UnsafePointer<CChar>?) -> String { p.map { String(cString: $0) } ?? "" }

final class Core {
    let c: OpaquePointer

    init() {
        try? FileManager.default.createDirectory(at: DataDir.url, withIntermediateDirectories: true)
        let ko = (Locale.preferredLanguages.first ?? "").hasPrefix("ko")
        c = pq_open(DataDir.url.path, ko ? 1 : 0, now())
    }

    func close() { pq_close(c, now()) }

    /// a stable negative id per app (FNV-1a of the bundle id), so per-app links survive restarts
    static func appKey(_ proc: String) -> pq_win {
        var h: UInt64 = 1469598103934665603
        for b in proc.utf8 { h = (h ^ UInt64(b)) &* 1099511628211 }
        return -pq_win(h & 0x3fff_ffff_ffff_ffff) - 1
    }

    // ---- strings ----
    func tr(_ key: String) -> String { str(pq_tr(c, key)) }
    func fmt(_ key: String, _ args: String...) -> String {
        let cs = args.map { strdup($0) }
        defer { cs.forEach { free($0) } }
        func a(_ i: Int) -> UnsafePointer<CChar>? { i < cs.count ? UnsafePointer(cs[i]) : nil }
        return str(pq_fmt(c, key, a(0), a(1), a(2)))
    }
    func fmtMin(_ m: Double) -> String { str(pq_fmt_min(c, m)) }
    var ko: Bool { pq_lang_ko(c) != 0 }

    // ---- queue ----
    var count: Int { Int(pq_count(c)) }
    func id(_ index: Int) -> Int32 { pq_id_at(c, Int32(index)) }
    func index(_ id: Int32) -> Int { Int(pq_index_of(c, id)) }
    var ids: [Int32] { (0..<count).map { id($0) } }
    func text(_ id: Int32) -> String { str(pq_text(c, id)) }
    func group(_ id: Int32) -> Int32 { pq_group(c, id) }
    func groupName(_ g: Int32) -> String { str(pq_group_name(c, g)) }
    func wins(_ id: Int32) -> [(key: pq_win, proc: String, title: String)] {
        (0..<pq_win_count(c, id)).map { k in (pq_win_id(c, id, k), str(pq_win_proc(c, id, k)), str(pq_win_title(c, id, k))) }
    }
    func has(_ id: Int32, _ h: WinRef?) -> Bool { h.map { pq_has(c, id, $0.key) != 0 } ?? false }
    func firstOfGroup(_ i: Int) -> Bool { pq_first_of_group(c, Int32(i)) != 0 }
    func lastOfGroup(_ i: Int) -> Bool { pq_last_of_group(c, Int32(i)) != 0 }
    var state: Int32 { pq_state(c) }
    var focusing: Bool { state == PQ_FOCUS || state == PQ_AWAY }
    func note() -> String? { pq_note(c, now()).map { String(cString: $0) } }
}

/// set in main.swift before anything else runs
var core: Core!
func tr(_ key: String) -> String { core.tr(key) }
func clockText(_ secs: Double) -> String { str(pq_fmt_clock(secs)) }
