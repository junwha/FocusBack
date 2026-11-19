// Entry point: single instance, global hotkey (Ctrl+Option+Space), --stats, clean shutdown.
import AppKit
import Carbon

// ---- single instance: a second launch asks the running widget to come back (or to open stats) ----
let args = CommandLine.arguments
try? FileManager.default.createDirectory(at: DataDir.url, withIntermediateDirectories: true)
let lockPath = DataDir.url.appendingPathComponent(".lock").path
let lockFd = open(lockPath, O_CREAT | O_RDWR, 0o644)
let showName = Notification.Name(stableName("show")), statsName = Notification.Name(stableName("stats")), debugName = Notification.Name(stableName("debug"))

/// String.hashValue is randomized per process, so build the notification name from the path itself
func stableName(_ kind: String) -> String {
    var h: UInt64 = 1469598103934665603
    for b in DataDir.url.path.utf8 { h = (h ^ UInt64(b)) &* 1099511628211 }
    return "FocusBack.\(String(h, radix: 36)).\(kind)"
}

if lockFd >= 0 && flock(lockFd, LOCK_EX | LOCK_NB) != 0 {
    let dc = DistributedNotificationCenter.default()
    if let i = args.firstIndex(of: "--debug"), i + 1 < args.count {
        dc.postNotificationName(debugName, object: args[i + 1], userInfo: nil, deliverImmediately: true)
    } else {
        dc.postNotificationName(args.contains("--stats") ? statsName : showName, object: nil, userInfo: nil, deliverImmediately: true)
    }
    exit(0)
}

// ---- global hotkey ----
private var hotkeyHandler: (() -> Void)?
private var hotKeyRef: EventHotKeyRef?

func registerHotKey(_ handler: @escaping () -> Void) {
    hotkeyHandler = handler
    var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
        DispatchQueue.main.async { hotkeyHandler?() }
        return noErr
    }, 1, &spec, nil, nil)
    let id = EventHotKeyID(signature: OSType(0x46515545), id: 1) // 'FQUE'
    RegisterEventHotKey(UInt32(kVK_Space), UInt32(controlKey | optionKey), id, GetApplicationEventTarget(), 0, &hotKeyRef)
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    var widget: Widget!
    var sigterm: DispatchSourceSignal?

    func applicationDidFinishLaunching(_ n: Notification) {
        core = Core() // loads the data folder (backs up items.tsv first)
        widget = Widget()
        registerHotKey { [unowned self] in self.widget.hotkey() }
        let dc = DistributedNotificationCenter.default()
        dc.addObserver(forName: showName, object: nil, queue: .main) { [unowned self] _ in self.widget.showAgain() }
        dc.addObserver(forName: statsName, object: nil, queue: .main) { [unowned self] _ in self.widget.openStats() }
        if ProcessInfo.processInfo.environment["FOCUSBACK_DEBUG"] == "1" {
            dc.addObserver(forName: debugName, object: nil, queue: .main) { [unowned self] n in
                if let c = n.object as? String { Debug.run(c, self.widget) }
            }
        }
        if args.contains("--stats") { widget.openStats() }
        // kill / logout: log a running session and save, like closing the widget
        signal(SIGTERM, SIG_IGN)
        let s = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        s.setEventHandler { NSApp.terminate(nil) }
        s.resume()
        sigterm = s
    }

    // opening the app again from Finder / Launchpad brings the widget back
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        widget.showAgain()
        return false
    }

    func applicationWillTerminate(_ n: Notification) { widget.shutdown() }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory) // no Dock icon, no menu bar
let delegate = AppDelegate()
app.delegate = delegate
app.run()
