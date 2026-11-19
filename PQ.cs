// ADHD Priority Queue - tiny always-on-top widget (top-right).
// Build: build.bat  (uses the C# compiler built into Windows, no installs)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

class Win
{
    public IntPtr H;          // window handle
    public string Proc = "";  // process name (for re-finding after restart)
    public string Title = ""; // last known title
}

class Item
{
    public string Text;
    public DateTime Created;
    public List<Win> Wins = new List<Win>(); // one task can span several snapped windows
    public IntPtr LastH;                      // which of them you used last (gets focus on click)
    public int Sessions;                      // completed focus sessions -> next session's default length
    public int GroupId;                       // 0 = alone; same id = one task group (windows share state/timers)
    public bool Has(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        foreach (var w in Wins) if (w.H == h) return true;
        return false;
    }
    public override string ToString() { return Text; }
}

// flicker-free owner-drawn panel
class DBPanel : Panel
{
    public DBPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}

// full-screen break cover: no taskbar/Alt+Tab entry, can't be closed by Alt+F4
// small always-on-top card that never takes focus (you keep typing where you are)
class Toast : Form
{
    public Toast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8 | 0x08000000; return cp; } // TOOLWINDOW|TOPMOST|NOACTIVATE
    }
}

class Overlay : Form
{
    public bool AllowClose;
    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
    }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose) e.Cancel = true;
        base.OnFormClosing(e);
    }
}

class PQForm : Form
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

    const int WM_HOTKEY = 0x0312;

    readonly string path;
    readonly Label header;
    readonly TextBox input;
    readonly Panel inputBox;
    readonly Font ageFont;
    readonly ContextMenuStrip menu;

    // first installed font wins (Korean-friendly first)
    static Font Pick(float size, params string[] names)
    {
        foreach (var n in names)
        {
            try { var ff = new FontFamily(n); return new Font(ff, size); } catch { }
        }
        return new Font("Malgun Gothic", size);
    }
    readonly ListBox list;
    readonly Timer timer;
    readonly ToolTip tip;
    readonly float s; // DPI scale
    readonly int row, doneW;
    int dragIndex = -1, tipIndex = -1;
    Point dragStart;
    Item dragItem; // card currently lifted
    Item mergeTarget; // card it would be grouped with if dropped now

    // ---- sections (groups) + inline rename ----
    TextBox editBox;
    Action<string> editCommit;
    Dictionary<int, string> groupNames = new Dictionary<int, string>();
    string groupsPath;
    int[] heights = new int[0];          // row heights computed by Relayout()
    int HdrH { get { return (int)(26 * s); } }  // section header above the first card
    int SecPad { get { return (int)(7 * s); } } // section padding below the last card / at the sides
    int dropTick;

    // last window the user was in that is not this widget (event-driven, no polling)
    static readonly uint MyPid = (uint)Process.GetCurrentProcess().Id;
    IntPtr lastExternal;
    IntPtr hook;
    readonly WinEventProc hookProc; // keep delegate alive

    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    // pastel palette
    static readonly Color Bg = Color.FromArgb(250, 247, 242);       // cream
    static readonly Color Ink = Color.FromArgb(58, 54, 72);         // soft charcoal text
    static readonly Color Muted = Color.FromArgb(160, 150, 170);    // header text
    static readonly Color InputBg = Color.White;
    static readonly Color GreenFill = Color.FromArgb(198, 238, 208), GreenDeep = Color.FromArgb(70, 155, 95);
    static readonly Color RedFill = Color.FromArgb(255, 168, 168), RedDeep = Color.FromArgb(210, 80, 85);
    static readonly Color GrayDeep = Color.FromArgb(150, 145, 160);

    static Color Lerp(Color a, Color b, float t)
    {
        return Color.FromArgb(a.R + (int)((b.R - a.R) * t), a.G + (int)((b.G - a.G) * t), a.B + (int)((b.B - a.B) * t));
    }

    string settingsPath;

    // ---- focus timer state ----
    readonly DBPanel bar;
    readonly Timer tick;            // 1s, runs only while focusing / on break / showing a note
    // per task: 1st session 15/3, 2nd 30/5, then 60/15. -/+ overrides the focus length for now.
    static readonly int[] TierFocus = { 15, 30, 60 }, TierBreak = { 3, 5, 15 };
    int selTier;     // round picked with < > (0-based); follows the task's progress
    int focusTier;   // round of the running session
    Item barItem;    // task of the window you're in (what the bar shows)
    bool focusing;
    DateTime focusStart, focusEnd;
    DateTime freezeFrom;   // start of the current segment (others' clocks frozen from here; logged from here)
    double pausedSec;      // paused (away) seconds inside the current segment
    int plannedMin;        // planned length of the current segment
    bool extension;        // current segment is a "+5분" extension
    DateTime doneStart, doneEnd; // finished session, so an extension continues its bar
    HashSet<IntPtr> focusWins = new HashSet<IntPtr>();
    Item focusItem;
    Item warnedItem; int warnTick;
    string barNote; bool barWarn; DateTime barNoteUntil;
    Rectangle minusR, plusR, goR, stopR;
    List<Overlay> breakForms; DateTime breakStart, breakEnd;
    int breakMin = 5;
    Rectangle skipR;
    Font bigFont, midFont;

    PQForm()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ADHD-PQ");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "items.tsv");
        settingsPath = Path.Combine(dir, "opacity.txt");
        groupsPath = Path.Combine(dir, "groups.tsv");
        logPath = Path.Combine(dir, "focus_log.csv");

        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        row = (int)(34 * s);
        doneW = (int)(12 * s); // left inner padding of a row
        ageFont = Pick(8.5f, "Pretendard SemiBold", "Noto Sans KR Medium", "Segoe UI Semibold"); // needs Hangul for "작업 중"

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        Opacity = LoadOpacity();
        Padding = new Padding((int)(10 * s), (int)(6 * s), (int)(10 * s), (int)(10 * s));
        Font = Pick(9.5f, "Pretendard Medium", "Pretendard", "Noto Sans KR Medium", "Malgun Gothic");
        Width = (int)(280 * s);

        header = new Label
        {
            Dock = DockStyle.Top,
            Height = (int)(22 * s),
            Text = "✿  할 일    Ctrl+Alt+Space",
            ForeColor = Muted,
            Font = Pick(8f, "Pretendard", "Noto Sans KR", "Malgun Gothic"),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.SizeAll,
        };
        header.MouseDown += (o, e) =>
        {
            if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
        };
        header.MouseWheel += (o, e) => SetOpacity(Opacity + (e.Delta > 0 ? 0.05 : -0.05));

        // "ghost" entry: an empty dashed card at the end of the queue, shown only while
        // you're in a window that no item is linked to. Type + Enter and it becomes an item in place.
        input = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = InputBg,
            ForeColor = Ink,
            Font = Font,
        };
        inputBox = new Panel { Dock = DockStyle.Top, Height = row, BackColor = Bg, Cursor = Cursors.IBeam };
        inputBox.Controls.Add(input);
        inputBox.Resize += (o, e) => LayoutGhost();
        inputBox.Click += (o, e) => { Activate(); input.Focus(); };
        inputBox.Paint += (o, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = CardRect(new Rectangle(0, 0, inputBox.Width, inputBox.Height));
            using (var card = Round(r, (int)(9 * s)))
            {
                using (var b = new SolidBrush(InputBg)) g.FillPath(b, card);
                using (var pen = new Pen(input.Focused ? GrayDeep : Color.FromArgb(215, 208, 222), 1.2f * s)
                       { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                    g.DrawPath(pen, card);
            }
            TextRenderer.DrawText(g, (list.Items.Count + 1).ToString(), Font,
                new Rectangle(r.X + doneW, r.Y, ghostTextX - doneW, r.Height), Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
        input.GotFocus += (o, e) => inputBox.Invalidate();
        input.LostFocus += (o, e) => { forceGhost = false; BeginInvoke((Action)UpdateGhost); }; // after focus settles
        input.KeyDown += InputKeyDown;
        input.HandleCreated += (o, e) => SendMessage(input.Handle, 0x1501, (IntPtr)1, "+ 이 창의 할 일");

        list = new ListBox
        {
            Dock = DockStyle.Top,
            DrawMode = DrawMode.OwnerDrawVariable, // first/last card of a group gets room for the section frame
            BorderStyle = BorderStyle.None,
            BackColor = Bg,
            IntegralHeight = false,
            AllowDrop = true,
        };
        list.DrawItem += DrawItem;
        list.MeasureItem += (o, e) => e.ItemHeight = RowHeight(e.Index);
        editBox = new TextBox { BorderStyle = BorderStyle.None, Visible = false, ForeColor = Ink, Font = Font };
        editBox.KeyDown += (o, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndEdit(true); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(false); }
        };
        editBox.LostFocus += (o, e) => EndEdit(true);
        list.Controls.Add(editBox);
        list.MouseDown += (o, e) =>
        {
            // ListBox re-raises MouseDown/Up when items move under the cursor; ignore those while/just after dragging
            if (dragItem != null || Environment.TickCount - dropTick < 300) { dragIndex = -1; return; }
            dragIndex = e.Button == MouseButtons.Left ? list.IndexFromPoint(e.Location) : -1;
            dragStart = e.Location;
        };
        list.MouseMove += ListMouseMove;
        list.MouseUp += ListMouseUp;
        list.MouseLeave += (o, e) => { tip.Hide(list); tipIndex = -1; };
        list.KeyDown += ListKeyDown;

        tip = new ToolTip { InitialDelay = 400, ShowAlways = true };

        // right-click = this menu; Shift+click = done
        menu = new ContextMenuStrip { Font = Pick(9f, "Pretendard", "Noto Sans KR", "Malgun Gothic") };
        menu.Items.Add("이 창으로 이동 (클릭 / Enter)", null, (o, e) => TrySwitch(list.SelectedItem as Item));
        menu.Items.Add("완료 (Shift+클릭 / Del)", null, (o, e) => RemoveSelected());
        menu.Items.Add("+ 직전 창도 이 할 일에 연결", null, (o, e) => AddWindow(list.SelectedItem as Item, lastExternal));
        menu.Items.Add("창 연결 다시 하기 (직전 창 + 옆 창)", null, (o, e) =>
        {
            var it = list.SelectedItem as Item;
            if (it != null) { Attach(it, lastExternal); Changed(); }
        });
        var leave = new ToolStripMenuItem("묶음에서 빼기", null, (o, e) => LeaveGroup(list.SelectedItem as Item));
        menu.Items.Add(leave);
        menu.Opening += (o, e) => { var it = list.SelectedItem as Item; leave.Visible = it != null && it.GroupId != 0; };
        menu.Items.Add("맨 위로", null, (o, e) => MoveSelected(-list.SelectedIndex));
        menu.Items.Add("타이머 리셋", null, (o, e) => ResetSelected());
        menu.Items.Add("집중 회차 처음으로 (15분부터)", null, (o, e) =>
        {
            var it = list.SelectedItem as Item;
            if (it != null) { SetTaskSessions(it, 0); if (it == barItem || SameGroup(it, barItem)) selTier = 0; Changed(); bar.Invalidate(); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("통계 보기", null, (o, e) => OpenStats());
        menu.Items.Add("기록 시트 열기 (CSV)", null, (o, e) => OpenLogSheet());
        menu.Items.Add(OpacityMenu());
        menu.Items.Add("종료", null, (o, e) => Close());
        var hmenu = new ContextMenuStrip { Font = menu.Font };
        hmenu.Items.Add("통계 보기", null, (o, e) => OpenStats());
        hmenu.Items.Add("기록 시트 열기 (CSV)", null, (o, e) => OpenLogSheet());
        hmenu.Items.Add(OpacityMenu());
        hmenu.Items.Add("종료", null, (o, e) => Close());
        header.ContextMenuStrip = hmenu;

        // focus timer bar at the bottom
        bar = new DBPanel { Dock = DockStyle.Top, Height = row + (int)(4 * s), BackColor = Bg };
        bar.Paint += PaintBar;
        bar.MouseDown += BarMouseDown;
        bar.MouseMove += (o, e) =>
        {
            bool hot = minusR.Contains(e.Location) || plusR.Contains(e.Location) || goR.Contains(e.Location) || stopR.Contains(e.Location);
            bar.Cursor = hot ? Cursors.Hand : Cursors.Default;
        };
        bar.MouseWheel += (o, e) => { if (!focusing && breakForms == null) StepTier(e.Delta > 0 ? 1 : -1); };
        tick = new Timer { Interval = 1000 };
        tick.Tick += OnTick;
        bigFont = Pick(40f, "Pretendard SemiBold", "Noto Sans KR Medium", "Segoe UI Semibold");
        midFont = Pick(13f, "Pretendard SemiBold", "Noto Sans KR Medium", "Malgun Gothic");

        // all Dock=Top: last added ends up topmost -> header, list, ghost entry, focus bar
        Controls.Add(bar);
        Controls.Add(inputBox);
        Controls.Add(list);
        Controls.Add(header);

        LoadItems();
        NormalizeGroups();
        Relayout();
        Fit();
        PlaceDefault();
        // monitor unplugged / resolution changed: pull the widget back if it ended up off-screen
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (o, e) => BeginInvoke((Action)EnsureOnScreen);
        // running PQ.exe again (e.g. from the Start menu) brings this one back instead of doing nothing
        var showEvt = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "ADHD-PQ-show");
        var waiter = new System.Threading.Thread(() =>
        {
            while (true) { showEvt.WaitOne(); try { BeginInvoke((Action)(() => { PlaceDefault(); Show(); Activate(); })); } catch { return; } }
        }) { IsBackground = true };
        waiter.Start();
        // "PQ.exe --stats" opens the stats window of the running widget
        var statsEvt = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "ADHD-PQ-stats");
        new System.Threading.Thread(() =>
        {
            while (true) { statsEvt.WaitOne(); try { BeginInvoke((Action)OpenStats); } catch { return; } }
        }) { IsBackground = true }.Start();

        // repaint only every 20s -> near-zero CPU
        timer = new Timer { Interval = 20000 };
        timer.Tick += (o, e) => list.Invalidate();
        timer.Start();

        hookProc = OnForegroundChanged;
        hook = SetWinEventHook(3 /*EVENT_SYSTEM_FOREGROUND*/, 3, IntPtr.Zero, hookProc, 0, 0, 0 /*OUTOFCONTEXT*/);
        Track(GetForegroundWindow());
    }

    // ---------- window tracking ----------

    void OnForegroundChanged(IntPtr hk, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time) { Track(hwnd); }

    void Track(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        h = GetAncestor(h, 3 /*GA_ROOTOWNER: dialogs/popups count as their main window*/);
        if (h == IntPtr.Zero || h == Handle) return;
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid == MyPid) return; // our own menus/tooltips
        string cls = ClassOf(h);
        if (cls == "Shell_TrayWnd" || cls == "Progman" || cls == "WorkerW" || cls == "Shell_SecondaryTrayWnd"
            || cls == "Windows.UI.Core.CoreWindow" || cls == "XamlExplorerHostIslandWindow") return; // taskbar/desktop/start/alt-tab
        if (h == lastExternal) return;
        IntPtr prev = lastExternal;
        lastExternal = h;
        if (list == null) return;
        if (focusing)
        {
            if (!FocusOwns(h)) { if (!away) StartAway(prev); } // left the task: 1-minute grace
            else if (away) EndAway(true);                                // came back in time: resume
        }
        bool changed = false;
        foreach (Item it in list.Items)
        {
            bool inPrev = it.Has(prev), inNow = it.Has(h);
            if (inNow)

            {
                it.LastH = h;
                foreach (var w in it.Wins)
                {
                    if (w.H == h) { w.Title = TitleOf(h); continue; } // keep title fresh for restarts
                    // came back to one window of the task -> bring its partners up too (just below it, no focus change)
                    if (!inPrev && w.H != IntPtr.Zero && IsWindow(w.H))
                    {
                        if (IsIconic(w.H)) ShowWindow(w.H, 4 /*SW_SHOWNOACTIVATE*/);
                        SetWindowPos(w.H, h, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x4000 /*NOSIZE|NOMOVE|NOACTIVATE|ASYNC*/);
                    }
                }
            }
            // entering or leaving the task (or its group) restarts its clock; moving inside the group doesn't
            if (GHas(it, prev) != GHas(it, h)) { it.Created = DateTime.Now; changed = true; }
        }
        if (changed) Save();
        UpdateGhost();     // entry appears only in windows without an item
        UpdateBarItem();   // bar shows this task's next session length
        list.Invalidate(); // recolor the active item
    }

    // usable app window? (visible, titled, not minimized/cloaked/tool/topmost overlay)
    static bool IsAppWindow(IntPtr h, IntPtr self)
    {
        if (!IsWindowVisible(h) || IsIconic(h) || h == self) return false;
        int ex = GetWindowLong(h, -20 /*GWL_EXSTYLE*/);
        if ((ex & (0x80 /*TOOLWINDOW*/ | 0x8 /*TOPMOST*/ | 0x08000000 /*NOACTIVATE*/)) != 0) return false;
        int cloaked;
        if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out cloaked, 4) == 0 && cloaked != 0) return false;
        if (TitleOf(h).Length == 0) return false;
        uint pid; GetWindowThreadProcessId(h, out pid);
        return pid != MyPid;
    }

    static Rectangle RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return Rectangle.FromLTRB(r.L, r.T, r.R, r.B); }
    static long Area(Rectangle r) { return r.Width <= 0 || r.Height <= 0 ? 0 : (long)r.Width * r.Height; }

    // h plus the windows snapped next to it: walk z-order below h and take every big window
    // on the same monitor that is (almost) fully visible, i.e. not covered by anything above it.
    List<IntPtr> SnapGroup(IntPtr h) { return SnapGroup(h, Handle); }
    static List<IntPtr> SnapGroup(IntPtr h, IntPtr self)
    {
        var group = new List<IntPtr> { h };
        var screen = Screen.FromHandle(h).WorkingArea;
        Rectangle rh = Rectangle.Intersect(RectOf(h), screen);
        if (Area(rh) >= Area(screen) * 85 / 100) return group; // maximized: nothing beside it

        var above = new List<Rectangle> { rh }; // everything seen so far occludes what's below
        long covered = Area(rh);
        bool past = false;
        EnumWindows((w, l) => // top -> bottom
        {
            if (w == h) { past = true; return true; }
            if (!IsAppWindow(w, self)) return true;
            Rectangle r = Rectangle.Intersect(RectOf(w), screen);
            if (Area(r) < Area(screen) * 15 / 100) { if (past) above.Add(r); return true; } // small: ignore but it still occludes
            if (!past) { above.Add(r); return true; }
            long hidden = 0;
            foreach (var a in above) hidden += Area(Rectangle.Intersect(a, r));
            if (hidden <= Area(r) / 10) { group.Add(w); covered += Area(r); }
            above.Add(r);
            return covered < Area(screen) * 95 / 100 && group.Count < 4; // screen filled -> stop
        }, IntPtr.Zero);
        return group;
    }

    static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    static string TitleOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
    static string ProcOf(IntPtr h)
    {
        try { uint pid; GetWindowThreadProcessId(h, out pid); return Process.GetProcessById((int)pid).ProcessName; }
        catch { return ""; }
    }

    Win MakeWin(IntPtr h) { return new Win { H = h, Title = TitleOf(h), Proc = ProcOf(h) }; }

    // link the item to the window you were in + whatever is snapped beside it
    void Attach(Item it, IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return;
        it.Wins.Clear();
        foreach (var w in SnapGroup(h)) it.Wins.Add(MakeWin(w));
        it.LastH = h;
    }

    void AddWindow(Item it, IntPtr h)
    {
        if (it == null || h == IntPtr.Zero || !IsWindow(h) || it.Has(h)) return;
        it.Wins.Add(MakeWin(h));
        Changed();
    }

    // re-find windows: same handle if still alive, else (after restart) exact title, else same app.
    // never map two links onto the same window.
    void ResolveAll(Item it)
    {
        var used = new HashSet<IntPtr>();
        var pending = new List<Win>();
        foreach (var w in it.Wins)
        {
            if (w.H != IntPtr.Zero && IsWindow(w.H) && !used.Contains(w.H) && (w.Proc == "" || ProcOf(w.H) == w.Proc)) used.Add(w.H);
            else pending.Add(w);
        }
        foreach (var w in pending)
        {
            w.H = IntPtr.Zero;
            if (w.Proc == "") continue;
            IntPtr byTitle = IntPtr.Zero, byProc = IntPtr.Zero;
            var wl = w;
            EnumWindows((h, l) =>
            {
                if (used.Contains(h) || !IsWindowVisible(h)) return true;
                string t = TitleOf(h);
                if (t.Length == 0 || ProcOf(h) != wl.Proc) return true;
                if (t == wl.Title) { byTitle = h; return false; }
                if (byProc == IntPtr.Zero) byProc = h;
                return true;
            }, IntPtr.Zero);
            w.H = byTitle != IntPtr.Zero ? byTitle : byProc;
            if (w.H != IntPtr.Zero) used.Add(w.H);
        }
    }

    // bring all of the task's windows forward; focus the one you used last
    void SwitchTo(Item it)
    {
        if (it == null) return;
        ResolveAll(it);
        IntPtr focus = IntPtr.Zero;
        foreach (var w in it.Wins)
        {
            if (w.H == IntPtr.Zero) continue;
            if (IsIconic(w.H)) ShowWindow(w.H, 9 /*SW_RESTORE*/);
            if (focus == IntPtr.Zero || w.H == it.LastH) focus = w.H;
        }
        if (focus == IntPtr.Zero) { System.Media.SystemSounds.Beep.Play(); return; }
        foreach (var w in it.Wins)
            if (w.H != IntPtr.Zero && w.H != focus)
                SetWindowPos(w.H, IntPtr.Zero /*HWND_TOP*/, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 /*NOSIZE|NOMOVE|NOACTIVATE*/);
        SetForegroundWindow(focus);
    }

    // ---------- form plumbing ----------

    // WS_EX_TOOLWINDOW: not shown in Alt+Tab or the taskbar
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotKey(Handle, 1, 0x0001 | 0x0002 | 0x4000 /*ALT|CTRL|NOREPEAT*/, 0x20 /*Space*/);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopFocus(null, false, "종료"); // log a running session when the widget closes
        UnregisterHotKey(Handle, 1);
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            Track(GetForegroundWindow());
            forceGhost = true; // hotkey can add another item even for an already-linked window
            UpdateGhost();
            EnsureOnScreen();
            Show();
            Activate();
            input.Focus();
        }
        base.WndProc(ref m);
    }

    // ---------- input ----------

    void InputKeyDown(object o, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            string t = input.Text.Trim();
            if (t.Length == 0) return;
            var it = new Item { Text = t, Created = DateTime.Now };
            Attach(it, lastExternal);
            if (e.Shift) list.Items.Insert(0, it); else list.Items.Add(it); // Shift+Enter = top
            input.Clear();
            forceGhost = false;
            BackToWork(); // the entry turns into the item in place and you're back in your window
            Changed();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            input.Clear();
            forceGhost = false;
            BackToWork();
        }
        else if (e.KeyCode == Keys.Up && list.Items.Count > 0) { list.Focus(); list.SelectedIndex = list.Items.Count - 1; e.Handled = true; }
    }

    void BackToWork()
    {
        if (lastExternal != IntPtr.Zero && IsWindow(lastExternal)) SetForegroundWindow(lastExternal);
    }

    void ListKeyDown(object o, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter) { TrySwitch(list.SelectedItem as Item); e.Handled = true; }
        else if (e.Alt && e.KeyCode == Keys.Up) { MoveSelected(-1); e.Handled = true; }
        else if (e.Alt && e.KeyCode == Keys.Down) { MoveSelected(1); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { BackToWork(); e.Handled = true; }
    }

    void ListMouseMove(object o, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && (dragItem != null || dragIndex >= 0))
        {
            if (dragItem == null)
            {
                if (Math.Abs(e.Y - dragStart.Y) < SystemInformation.DragSize.Height) return;
                dragItem = (Item)list.Items[dragIndex]; // lift
                dragIndex = -1;                          // a drag is not a click
                list.Cursor = Cursors.SizeNS;
                tip.Hide(list); tipIndex = -1;
                list.Invalidate();
            }
            // live reorder: the lifted card jumps to the row under the cursor.
            // the part of a card you reach first (its near side ~60%) = "drop onto it" -> makes a group.
            int n = list.Items.Count;
            int to = e.Y < 0 ? 0 : list.IndexFromPoint(new Point(list.Width / 2, e.Y));
            bool outside = to < 0 || e.Y < 0;
            if (to < 0) to = n - 1;
            int cur = list.Items.IndexOf(dragItem);
            Item merge = null;
            if (!outside && to != cur)
            {
                var rr = list.GetItemRectangle(to);
                float frac = (e.Y - rr.Y) / (float)rr.Height;
                if (to > cur ? frac < 0.6f : frac > 0.4f) merge = (Item)list.Items[to];
            }
            if (merge != mergeTarget) { mergeTarget = merge; list.Invalidate(); }
            if (merge == null && to != cur)
            {
                list.BeginUpdate();
                list.Items.RemoveAt(cur);
                list.Items.Insert(to, dragItem);
                list.SelectedIndex = to;
                list.EndUpdate();
                Relayout(); // sections follow the new order
                Fit();
            }
            return;
        }
        if (dragItem != null) { var d = dragItem; dragItem = null; mergeTarget = null; list.Cursor = Cursors.Default; NormalizeGroups(d); Changed(); } // button released elsewhere
        int i = list.IndexFromPoint(e.Location);
        if (i == tipIndex) return;
        tipIndex = i;
        if (i < 0) { tip.Hide(list); return; }
        var it = (Item)list.Items[i];
        var where = new StringBuilder();
        foreach (var w in it.Wins) where.Append("\n→ ").Append(w.Proc).Append(" — ").Append(w.Title);
        if (it.Wins.Count == 0) where.Append("\n→ (연결된 창 없음)");
        tip.Show(it.Text + where, list, e.X + (int)(12 * s), e.Y + (int)(16 * s), 5000);
    }

    void ListMouseUp(object o, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            int ri = list.IndexFromPoint(e.Location);
            if (ri < 0) return;
            list.SelectedIndex = ri;
            menu.Show(list, e.Location); // right-click = menu
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (dragItem != null) // drop
        {
            var d = dragItem;
            dragItem = null;
            bool newGroup = mergeTarget != null && mergeTarget.GroupId == 0;
            if (mergeTarget != null) JoinGroup(d, mergeTarget); // dropped onto a card -> group
            mergeTarget = null;
            NormalizeGroups(d);
            dragIndex = -1;
            dropTick = Environment.TickCount;
            list.Cursor = Cursors.Default;
            Changed();
            if (newGroup && d.GroupId != 0) { int gid = d.GroupId; BeginInvoke((Action)(() => RenameGroup(gid))); } // name the new section
            return;
        }
        int i = list.IndexFromPoint(e.Location);
        bool click = dragIndex >= 0 && i == dragIndex;
        dragIndex = -1;
        if (!click) return;
        var it = (Item)list.Items[i];
        if ((ModifierKeys & Keys.Shift) != 0) { list.SelectedIndex = i; RemoveSelected(); } // Shift+click = done
        else if ((ModifierKeys & Keys.Control) != 0) { }                                   // Ctrl+click = just select
        else if (FirstOfGroup(i) && e.Y < list.GetItemRectangle(i).Top + HdrH) RenameGroup(it.GroupId); // section header
        else if (it.Has(lastExternal)) RenameItem(i);  // the task you're already in -> rename it
        else TrySwitch(it);
    }

    void MoveSelected(int delta)
    {
        int i = list.SelectedIndex;
        if (i < 0) return;
        int to = Math.Max(0, Math.Min(list.Items.Count - 1, i + delta));
        if (to == i) return;
        object it = list.Items[i];
        list.Items.RemoveAt(i);
        list.Items.Insert(to, it);
        list.SelectedIndex = to;
        NormalizeGroups((Item)it);
        Changed();
    }

    void RemoveSelected()
    {
        int i = list.SelectedIndex;
        if (i < 0) return;
        list.Items.RemoveAt(i);
        if (list.Items.Count > 0) list.SelectedIndex = Math.Min(i, list.Items.Count - 1);
        tipIndex = -1; tip.Hide(list);
        Changed();
    }

    void ResetSelected()
    {
        var it = list.SelectedItem as Item;
        if (it == null) return;
        it.Created = DateTime.Now;
        Changed();
    }

    // ---------- drawing ----------

    void DrawItem(object o, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Bg)) g.FillRectangle(bg, e.Bounds);
        if (e.Index < 0 || e.Index >= list.Items.Count) return;

        var it = (Item)list.Items[e.Index];
        bool active = GHas(it, lastExternal); // whole group shows 작업 중
        // while focusing, the other items' clocks are frozen and they stay white
        bool frozen = focusing && !active && !InFocus(it);
        DateTime now = frozen ? (it.Created < freezeFrom ? freezeFrom : it.Created) : DateTime.Now;
        double m = (now - it.Created).TotalMinutes;

        // active window's item = green; others white, one step redder per 10 min (max 6 steps)
        float t = frozen ? 0 : Math.Min(6, (int)(m / 10)) / 6f;
        Color fill = active ? GreenFill : Lerp(Color.White, RedFill, t);
        Color deep = active ? GreenDeep : Lerp(GrayDeep, RedDeep, t);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        bool lifted = it == dragItem;

        // section: one rounded tinted box around the whole group; each row paints its slice of it
        if (it.GroupId != 0)
        {
            Color gc = GroupColor(it.GroupId);
            var sr = SectionRect(e.Index);
            var clip = g.Clip;
            g.SetClip(e.Bounds);
            using (var p = Round(sr, (int)(13 * s)))
            {
                using (var b = new SolidBrush(Lerp(Bg, gc, 0.16f))) g.FillPath(b, p);
                using (var pen = new Pen(Lerp(Bg, gc, 0.45f), 1.2f * s)) g.DrawPath(pen, p);
            }
            if (FirstOfGroup(e.Index))
            {
                int cnt = 0;
                foreach (Item o2 in list.Items) if (SameGroup(o2, it)) cnt++;
                var hr = new Rectangle(sr.X + (int)(12 * s), sr.Y + (int)(1 * s), sr.Width - (int)(24 * s), HdrH - (int)(4 * s));
                string name = GroupName(it.GroupId);
                var hf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                if (editCommit == null || !editBox.Visible || editBox.Top > hr.Bottom || editBox.Bottom < hr.Top)
                {
                    TextRenderer.DrawText(g, name, ageFont, hr, Lerp(gc, Ink, 0.45f), hf);
                    int nw = TextRenderer.MeasureText(g, name, ageFont, Size.Empty, hf | TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, "· " + cnt, ageFont, new Rectangle(hr.X + nw + (int)(9 * s), hr.Y, (int)(40 * s), hr.Height), Lerp(gc, Bg, 0.25f), hf);
                }
            }
            g.Clip = clip;
        }
        var r = CardRect(CardBounds(e.Index, e.Bounds));
        if (lifted)
        {
            // soft drop shadow + slightly inset card = "picked up"
            var sh = new Rectangle(r.X + (int)(2 * s), r.Y + (int)(2 * s), r.Width - (int)(2 * s), r.Height - (int)(1 * s));
            using (var p2 = Round(sh, (int)(9 * s)))
            using (var b = new SolidBrush(Color.FromArgb(40, 60, 40, 80))) g.FillPath(b, p2);
            r = new Rectangle(r.X, r.Y - (int)(1 * s), r.Width - (int)(2 * s), r.Height);
            fill = Lerp(fill, deep, 0.08f);
        }
        using (var path = Round(r, (int)(9 * s)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var pen = (sel || lifted) ? new Pen(deep, (lifted ? 2f : 1.6f) * s) : new Pen(Lerp(fill, deep, 0.25f), 1f * s)) g.DrawPath(pen, path);
            if (it == mergeTarget) // "drop here to group"
            {
                Color mc = it.GroupId != 0 ? GroupColor(it.GroupId) : GroupColor(1);
                using (var pen = new Pen(mc, 2.4f * s) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) g.DrawPath(pen, path);
            }
        }


        // age badge on the right
        int mins = (int)m;
        string age = active ? (focusing ? "● 집중 중" : "● 작업 중") : mins < 60 ? mins + "m" : (mins / 60) + "h" + (mins % 60).ToString("00");
        var tf = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        Size asz = TextRenderer.MeasureText(g, age, ageFont, Size.Empty, tf);
        int bh = (int)(18 * s), bw = asz.Width + (int)(12 * s);
        var badge = new Rectangle(r.Right - bw - (int)(6 * s), r.Y + (r.Height - bh) / 2, bw, bh);
        using (var path = Round(badge, bh / 2))
        using (var b = new SolidBrush(Lerp(fill, deep, 0.12f))) g.FillPath(b, path);
        TextRenderer.DrawText(g, age, ageFont, badge, deep,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | tf);

        // index + text
        var txtR = new Rectangle(r.X + doneW, r.Y, badge.X - r.X - doneW - (int)(6 * s), r.Height);
        TextRenderer.DrawText(g, (e.Index + 1) + "   " + it.Text, list.Font, txtR, Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // top-right of the primary screen, below the title-bar buttons of maximized windows
    void PlaceDefault()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - (int)(10 * s), wa.Top + (int)(44 * s));
    }

    void EnsureOnScreen()
    {
        var me = new Rectangle(Location, Size);
        foreach (var sc in Screen.AllScreens)
            if (sc.WorkingArea.Contains(new Point(me.X + me.Width / 2, me.Y + 10))) return; // still visible somewhere
        PlaceDefault();
    }

    void Changed() { Save(); Relayout(); UpdateGhost(); UpdateBarItem(); Fit(); list.Invalidate(); }

    // same card shape for items and the ghost entry
    Rectangle CardRect(Rectangle b)
    {
        int gap = (int)(3 * s);
        return new Rectangle(b.X, b.Y + gap, b.Width - 1, b.Height - gap * 2 + gap / 2);
    }

    int ghostTextX; // where typed text starts, aligned with item text

    void LayoutGhost()
    {
        Rectangle r = CardRect(new Rectangle(0, 0, inputBox.Width, inputBox.Height));
        string num = (list.Items.Count + 1) + "   ";
        ghostTextX = doneW + TextRenderer.MeasureText(num, Font).Width - (int)(3 * s);
        int h = input.PreferredHeight;
        input.SetBounds(r.X + ghostTextX, r.Y + (r.Height - h) / 2 + (int)(1 * s), r.Width - ghostTextX - (int)(10 * s), h);
        inputBox.Invalidate();
    }

    bool forceGhost; // hotkey: show the entry even in an already-linked window

    // ghost entry only when the window you're in isn't linked to any item (or nothing is queued yet)
    void UpdateGhost()
    {
        if (list == null || inputBox == null) return;
        bool linked = false;
        foreach (Item it in list.Items) if (it.Has(lastExternal)) { linked = true; break; }
        bool show = forceGhost || input.Focused || input.TextLength > 0 || !linked || list.Items.Count == 0;
        if (ghostShown != show) { ghostShown = show; inputBox.Visible = show; Fit(); }
        LayoutGhost();
    }

    bool ghostShown = true;

    static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int rad)
    {
        int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // rounded window corners
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        int rad = (int)(16 * s);
        IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, rad, rad);
        Region = Region.FromHrgn(rgn);
        DeleteObject(rgn);
    }

    // ---------- focus timer ----------

    // items that make up the task being focused on, computed live so items/groups added after Start count too:
    // the focus item, any item linked to a window you started in, and every member of their groups
    HashSet<Item> FocusTask()
    {
        var set = new HashSet<Item>();
        if (!focusing) return set;
        var groups = new HashSet<int>();
        foreach (Item it in list.Items)
        {
            bool root = it == focusItem;
            foreach (var w in it.Wins) if (focusWins.Contains(w.H)) { root = true; break; }
            if (root) { set.Add(it); if (it.GroupId != 0) groups.Add(it.GroupId); }
        }
        foreach (Item it in list.Items) if (it.GroupId != 0 && groups.Contains(it.GroupId)) set.Add(it);
        return set;
    }

    bool InFocus(Item it) { return it != null && FocusTask().Contains(it); }

    // is window h part of the task being focused on?
    bool FocusOwns(IntPtr h)
    {
        if (focusWins.Contains(h)) return true;
        foreach (var it in FocusTask()) if (it.Has(h)) return true;
        return false;
    }

    // clicking another item while focusing: warn once, second click (within 6s) goes and resets the timer
    void TrySwitch(Item it)
    {
        if (it == null) return;
        if (focusing && !InFocus(it))
        {
            if (warnedItem != it || Environment.TickCount - warnTick > 6000)
            {
                warnedItem = it; warnTick = Environment.TickCount;
                Note("⚠ 집중 중! 한 번 더 누르면 이동 (1분 유예)", true);
                System.Media.SystemSounds.Exclamation.Play();
                return;
            }
        }
        SwitchTo(it); // leaving starts the 1-minute grace
    }

    // < > : move between rounds (15/3, 30/5, 60/15); remembered for the task
    void StepTier(int d)
    {
        selTier = Math.Max(0, Math.Min(TierFocus.Length - 1, selTier + d));
        if (barItem != null) { SetTaskSessions(barItem, selTier); Save(); }
        bar.Invalidate();
    }

    // a group is one task: its round is the highest of its members
    int Tier(Item it)
    {
        if (it == null) return 0;
        int n = it.Sessions;
        foreach (Item o in list.Items) if (SameGroup(o, it)) n = Math.Max(n, o.Sessions);
        return Math.Min(TierFocus.Length - 1, n);
    }

    void SetTaskSessions(Item it, int v)
    {
        if (it == null) return;
        foreach (Item o in list.Items) if (o == it || SameGroup(o, it)) o.Sessions = v;
    }

    Item lastFocusItem; // when a window belongs to several items, the bar follows the one you focused on last

    // ---------- task groups ----------

    static bool SameGroup(Item a, Item b) { return a != null && b != null && a.GroupId != 0 && a.GroupId == b.GroupId; }

    // is h one of this item's windows, or of any item in its group?
    bool GHas(Item it, IntPtr h)
    {
        if (it.Has(h)) return true;
        if (it.GroupId == 0 || h == IntPtr.Zero) return false;
        foreach (Item o in list.Items) if (o != it && o.GroupId == it.GroupId && o.Has(h)) return true;
        return false;
    }

    // drop `it` onto `target`: same group, placed right after the target's group
    void JoinGroup(Item it, Item target)
    {
        if (target.GroupId == 0)
        {
            int max = 0;
            foreach (Item o in list.Items) max = Math.Max(max, o.GroupId);
            target.GroupId = max + 1;
        }
        it.GroupId = target.GroupId;
        list.Items.Remove(it);
        int at = list.Items.IndexOf(target);
        while (at + 1 < list.Items.Count && SameGroup((Item)list.Items[at + 1], target)) at++;
        list.Items.Insert(at + 1, it);
        list.SelectedItem = it;
    }

    void LeaveGroup(Item it)
    {
        if (it == null || it.GroupId == 0) return;
        int g = it.GroupId;
        it.GroupId = 0;
        // move it just below its old group so the group stays in one piece
        list.Items.Remove(it);
        int last = -1;
        for (int k = 0; k < list.Items.Count; k++) if (((Item)list.Items[k]).GroupId == g) last = k;
        list.Items.Insert(last < 0 ? 0 : last + 1, it);
        list.SelectedItem = it;
        NormalizeGroups();
        Changed();
    }

    // after a reorder: dropped between two members -> joins them; a member with no neighbour in its group -> leaves
    void NormalizeGroups(Item dropped = null)
    {
        int n = list.Items.Count;
        if (dropped != null)
        {
            int i = list.Items.IndexOf(dropped);
            Item a = i > 0 ? (Item)list.Items[i - 1] : null, b = i + 1 < n ? (Item)list.Items[i + 1] : null;
            if (SameGroup(a, b)) dropped.GroupId = a.GroupId;
        }
        var lone = new List<Item>();
        for (int i = 0; i < n; i++)
        {
            var it = (Item)list.Items[i];
            if (it.GroupId == 0) continue;
            bool up = i > 0 && SameGroup((Item)list.Items[i - 1], it), down = i + 1 < n && SameGroup((Item)list.Items[i + 1], it);
            if (!up && !down) lone.Add(it);
        }
        foreach (var it in lone) it.GroupId = 0;
    }

    bool FirstOfGroup(int i) { var it = (Item)list.Items[i]; return it.GroupId != 0 && (i == 0 || !SameGroup((Item)list.Items[i - 1], it)); }
    bool LastOfGroup(int i) { var it = (Item)list.Items[i]; return it.GroupId != 0 && (i + 1 >= list.Items.Count || !SameGroup((Item)list.Items[i + 1], it)); }

    int RowHeight(int i) { return i >= 0 && i < heights.Length ? heights[i] : row; }

    // recompute row heights (section header / bottom padding) and re-add the items so the ListBox re-measures
    void Relayout()
    {
        int n = list.Items.Count;
        var items = new object[n];
        list.Items.CopyTo(items, 0);
        var h = new int[n];
        for (int i = 0; i < n; i++) h[i] = row + (FirstOfGroup(i) ? HdrH : 0) + (LastOfGroup(i) ? SecPad : 0);
        bool same = h.Length == heights.Length;
        for (int i = 0; same && i < n; i++) same = h[i] == heights[i];
        if (same) return;
        heights = h;
        object sel = list.SelectedItem;
        int top = list.TopIndex;
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(items);
        if (sel != null) list.SelectedItem = sel;
        list.TopIndex = Math.Min(top, Math.Max(0, n - 1));
        list.EndUpdate();
    }

    string GroupName(int g) { string n; return groupNames.TryGetValue(g, out n) && n.Length > 0 ? n : "묶음"; }

    // section rectangle (list coordinates) of the group containing row i
    Rectangle SectionRect(int i)
    {
        int a = i, b = i;
        while (a > 0 && SameGroup((Item)list.Items[a - 1], (Item)list.Items[i])) a--;
        while (b + 1 < list.Items.Count && SameGroup((Item)list.Items[b + 1], (Item)list.Items[i])) b++;
        Rectangle ra = list.GetItemRectangle(a), rb = list.GetItemRectangle(b);
        int top = ra.Top + (FirstOfGroup(a) ? (int)(3 * s) : 0);
        return Rectangle.FromLTRB(0, top, list.Width - 1, rb.Bottom - (LastOfGroup(b) ? (int)(1 * s) : 0));
    }

    // ---- inline edit (item text / section name) ----
    void BeginEdit(Rectangle rc, string text, Color back, Action<string> commit)
    {
        EndEdit(false);
        editCommit = commit;
        editBox.BackColor = back;
        editBox.Text = text;
        int h = editBox.PreferredHeight;
        editBox.SetBounds(rc.X, rc.Y + (rc.Height - h) / 2 + (int)(1 * s), rc.Width, h);
        editBox.Visible = true;
        Activate();
        editBox.Focus();
        editBox.SelectAll();
    }

    void EndEdit(bool commit)
    {
        if (editCommit == null) return;
        var c = editCommit; editCommit = null;
        string t = editBox.Text.Trim().Replace('\t', ' ');
        editBox.Visible = false;
        if (commit && t.Length > 0) c(t);
        list.Invalidate();
        BackToWork();
    }

    void RenameItem(int i)
    {
        var it = (Item)list.Items[i];
        var r = CardRect(CardBounds(i, list.GetItemRectangle(i)));
        int x = r.X + doneW + TextRenderer.MeasureText((i + 1) + "   ", list.Font).Width - (int)(3 * s);
        BeginEdit(new Rectangle(x, r.Y, r.Right - x - (int)(70 * s), r.Height), it.Text, GreenFill,
            t => { it.Text = t; Changed(); });
    }

    void RenameGroup(int g)
    {
        int first = -1;
        for (int i = 0; i < list.Items.Count; i++) if (((Item)list.Items[i]).GroupId == g) { first = i; break; }
        if (first < 0) return;
        var sr = SectionRect(first);
        var rc = new Rectangle(sr.X + (int)(12 * s), sr.Y + (int)(2 * s), sr.Width - (int)(60 * s), HdrH - (int)(5 * s));
        BeginEdit(rc, GroupName(g), Lerp(Bg, GroupColor(g), 0.16f), t => { groupNames[g] = t; Save(); list.Invalidate(); });
    }

    // where the card sits inside its row (rows of a group leave room for the section frame)
    Rectangle CardBounds(int i, Rectangle b)
    {
        var it = (Item)list.Items[i];
        if (it.GroupId == 0) return b;
        int top = FirstOfGroup(i) ? HdrH : 0, bot = LastOfGroup(i) ? SecPad : 0;
        int pad = SecPad;
        return new Rectangle(b.X + pad, b.Y + top, b.Width - pad * 2, b.Height - top - bot);
    }

    static readonly Color[] GroupColors = {
        Color.FromArgb(170, 150, 215), Color.FromArgb(240, 160, 120), Color.FromArgb(110, 170, 220), Color.FromArgb(120, 190, 160) };
    static Color GroupColor(int g) { return GroupColors[(g - 1) % GroupColors.Length]; }

    Item ItemOf(IntPtr h)
    {
        foreach (Item it in list.Items) if (it.Has(h)) return it;
        return null;
    }

    // the window you're in changed: the bar shows that task's next default again
    void UpdateBarItem()
    {
        if (focusing) return;
        var it = lastFocusItem != null && list.Items.Contains(lastFocusItem) && GHas(lastFocusItem, lastExternal) ? lastFocusItem : ItemOf(lastExternal);
        if (it != barItem) { barItem = it; selTier = Tier(it); }
        bar.Invalidate();
    }

    void StartFocus()
    {
        IntPtr h = lastExternal;
        if (h == IntPtr.Zero || !IsWindow(h)) { System.Media.SystemSounds.Beep.Play(); return; }
        focusWins = new HashSet<IntPtr>(SnapGroup(h)); // the window you were in + windows snapped beside it
        focusItem = null;
        foreach (Item it in list.Items)
            if (it.Has(h)) { focusItem = it; break; }
        if (focusItem != null) // the whole group counts as "the task": moving between its windows keeps the timer
            foreach (Item it in list.Items)
                if (it == focusItem || SameGroup(it, focusItem))
                    foreach (var w in it.Wins) if (w.H != IntPtr.Zero) focusWins.Add(w.H);
        if (focusItem != null) lastFocusItem = focusItem;
        focusing = true;
        focusStart = DateTime.Now;
        focusTier = selTier;
        focusEnd = focusStart.AddMinutes(TierFocus[focusTier]);
        freezeFrom = focusStart; pausedSec = 0; plannedMin = TierFocus[focusTier]; extension = false;
        breakMin = TierBreak[focusTier];
        warnedItem = null;
        barNoteUntil = DateTime.MinValue;
        tick.Start();
        bar.Invalidate(); list.Invalidate();
        BackToWork();
    }

    // ---------- focus log (CSV sheet) + stats dashboard ----------

    string logPath;
    const string LogHeader = "시작,끝,집중(분),할 일,묶음,회차,계획(분),종류,결과";

    static string Csv(string v)
    {
        v = v ?? "";
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // one row per focus segment: when, how long (paused time excluded), which task/section, how it ended
    void LogSession(string outcome)
    {
        try
        {
            var end = away ? awayStart : DateTime.Now;
            double min = ((end - freezeFrom).TotalSeconds - pausedSec) / 60.0;
            if (min < 0.5) return; // ignore accidental starts
            Item it = focusItem;
            if (it == null) foreach (var ft in FocusTask()) { it = ft; break; }
            string task = it != null ? it.Text : "(할 일 없음)";
            string grp = it != null && it.GroupId != 0 ? GroupName(it.GroupId) : "";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string line = string.Join(",", new[] {
                freezeFrom.ToString("yyyy-MM-dd HH:mm:ss"), end.ToString("yyyy-MM-dd HH:mm:ss"),
                min.ToString("0.0", inv), Csv(task), Csv(grp), (focusTier + 1).ToString(),
                plannedMin.ToString(), extension ? "연장" : "기본", outcome });
            if (!File.Exists(logPath)) File.WriteAllText(logPath, LogHeader + "\r\n", new UTF8Encoding(true)); // BOM: Excel reads Korean
            File.AppendAllText(logPath, line + "\r\n", new UTF8Encoding(false));
        }
        catch { }
    }

    StatsForm stats;

    // stats: a centered window you can close (✕ / Esc), drawn like the widget
    void OpenStats()
    {
        if (stats != null && !stats.IsDisposed) { stats.Reload(); stats.Activate(); return; }
        stats = new StatsForm(this);
        stats.Show();
    }

    void OpenLogSheet()
    {
        try
        {
            if (!File.Exists(logPath)) File.WriteAllText(logPath, LogHeader + "\r\n", new UTF8Encoding(true));
            Process.Start(logPath);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "기록 시트"); }
    }

    // ---- left the task while focusing: pause, show a centered 1-minute warning ----
    bool away;
    DateTime awayStart;
    const int AwaySec = 60;
    Toast awayForm;
    IntPtr awayHome; // focus window to go back to
    Rectangle backR, quitR;

    void StartAway(IntPtr from)
    {
        away = true;
        awayStart = DateTime.Now;
        awayHome = from != IntPtr.Zero && FocusOwns(from) ? from : IntPtr.Zero;
        if (awayHome == IntPtr.Zero) foreach (var w in focusWins) if (IsWindow(w)) { awayHome = w; break; }
        if (awayForm == null)
        {
            awayForm = new Toast { BackColor = Bg, Size = new Size((int)(340 * s), (int)(168 * s)) };
            IntPtr rgn = CreateRoundRectRgn(0, 0, awayForm.Width + 1, awayForm.Height + 1, (int)(22 * s), (int)(22 * s));
            awayForm.Region = Region.FromHrgn(rgn); DeleteObject(rgn);
            awayForm.Paint += PaintAway;
            awayForm.MouseDown += (o, e) =>
            {
                if (backR.Contains(e.Location)) { if (awayHome != IntPtr.Zero && IsWindow(awayHome)) SetForegroundWindow(awayHome); }
                else if (quitR.Contains(e.Location)) StopFocus("집중 중지", false);
            };
            awayForm.MouseMove += (o, e) => awayForm.Cursor = backR.Contains(e.Location) || quitR.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }
        var wa = Screen.PrimaryScreen.WorkingArea;
        awayForm.Location = new Point(wa.X + (wa.Width - awayForm.Width) / 2, wa.Y + (wa.Height - awayForm.Height) / 2);
        awayForm.Show();
        System.Media.SystemSounds.Exclamation.Play();
        tick.Start();
        bar.Invalidate(); list.Invalidate();
    }

    void EndAway(bool resume)
    {
        if (!away) return;
        away = false;
        if (resume) { focusEnd += DateTime.Now - awayStart; pausedSec += (DateTime.Now - awayStart).TotalSeconds; } // paused time doesn't count
        if (awayForm != null) awayForm.Hide();
        if (resume) Note("돌아왔어요 · 집중 이어서", false);
        bar.Invalidate();
    }

    void PaintAway(object o, PaintEventArgs e)
    {
        var f = (Form)o; var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Bg);
        using (var p = Round(new Rectangle(0, 0, f.Width - 1, f.Height - 1), (int)(11 * s)))
        using (var pen = new Pen(RedFill, 2.4f * s)) g.DrawPath(pen, p);
        var cf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, "⏸  집중 중이에요", midFont, new Rectangle(0, (int)(14 * s), f.Width, (int)(28 * s)), RedDeep, cf);
        double left = AwaySec - (DateTime.Now - awayStart).TotalSeconds;
        using (var big = new Font(bigFont.FontFamily, 26f))
            TextRenderer.DrawText(g, Clock(left), big, new Rectangle(0, (int)(42 * s), f.Width, (int)(50 * s)), Ink, cf);
        TextRenderer.DrawText(g, "1분 안에 돌아가면 타이머가 이어져요", Font, new Rectangle(0, (int)(92 * s), f.Width, (int)(22 * s)), Muted, cf);
        int bw = (int)(120 * s), bh = (int)(26 * s), y = f.Height - bh - (int)(14 * s), gap = (int)(10 * s);
        backR = new Rectangle(f.Width / 2 - bw - gap / 2, y, bw, bh);
        quitR = new Rectangle(f.Width / 2 + gap / 2, y, bw, bh);
        Pill(g, backR, GreenFill, GreenDeep, "↩ 돌아가기", ageFont);
        Pill(g, quitR, Color.FromArgb(235, 232, 240), GrayDeep, "■ 그만하기", ageFont);
    }

    // stop (reset) the focus timer; the time spent focusing doesn't count toward the other items' age
    void StopFocus(string note, bool warn, string outcome = "중지")
    {
        if (!focusing) return;
        LogSession(outcome); // before clearing away state: time away isn't focus time
        if (away) EndAway(false);
        TimeSpan el = DateTime.Now - freezeFrom;
        foreach (Item it in list.Items)
            if (!InFocus(it)) it.Created = it.Created < freezeFrom ? it.Created + el : DateTime.Now;
        focusing = false;
        focusItem = null; warnedItem = null;
        focusWins = new HashSet<IntPtr>();
        if (note != null) Note(note, warn);
        Save();
        bar.Invalidate(); list.Invalidate();
    }

    void Note(string text, bool warn)
    {
        barNote = text; barWarn = warn;
        barNoteUntil = DateTime.Now.AddSeconds(4);
        tick.Start();
        bar.Invalidate();
    }

    void OnTick(object o, EventArgs e)
    {
        var now = DateTime.Now;
        if (away)
        {
            if ((now - awayStart).TotalSeconds >= AwaySec) StopFocus("1분 안에 안 돌아와서 집중 초기화", true, "이탈");
            else if (awayForm != null) awayForm.Invalidate();
        }
        if (focusing && !away && now >= focusEnd)
        {
            foreach (var ft in FocusTask()) ft.Sessions = Math.Max(ft.Sessions, focusTier + 1); // next round for the whole task
            lastFocusItem = focusItem;
            doneWins = focusWins; doneItem = focusItem; doneTier = focusTier;
            doneStart = focusStart; doneEnd = now;
            StopFocus(null, false, "완료");
            barItem = null; UpdateBarItem();
            StartBreak();
        }
        if (breakForms != null && breakHidden)
        {
            if (now >= breakEnd) EndBreak();
            else if ((breakEnd - now).TotalSeconds <= SoonSec && !soonShown) ShowSoon();
            else if (soonForm != null && soonForm.Visible) soonForm.Invalidate();
        }
        else if (breakForms != null)
        {
            if (now >= breakEnd) EndBreak();
            else foreach (var f in breakForms)
                {
                    SetWindowPos(f.Handle, (IntPtr)(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // stay on top
                    f.Invalidate();
                }
        }
        bar.Invalidate();
        if (!focusing && breakForms == null && now >= barNoteUntil) tick.Stop();
    }

    void BarMouseDown(object o, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && breakForms != null && breakHidden) { ShowBreak(); return; } // bring the break screen back
        if (e.Button != MouseButtons.Left || breakForms != null) return;
        if (focusing) { if (stopR.Contains(e.Location)) StopFocus("집중 중지", false); return; }
        if (minusR.Contains(e.Location)) StepTier(-1);
        else if (plusR.Contains(e.Location)) StepTier(1);
        else if (goR.Contains(e.Location))
        {
            StartFocus();
            if (focusing && (ModifierKeys & Keys.Control) != 0) { focusEnd = focusStart.AddSeconds(10); plannedMin = 0; } // Ctrl+click = 10s test run
        }
    }

    static string Clock(double secs)
    {
        int t = (int)Math.Ceiling(Math.Max(0, secs));
        return (t / 60) + ":" + (t % 60).ToString("00");
    }

    void Pill(Graphics g, Rectangle r, Color fill, Color fg, string text, Font f)
    {
        using (var p = Round(r, r.Height / 2))
        using (var b = new SolidBrush(fill)) g.FillPath(b, p);
        TextRenderer.DrawText(g, text, f, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    void PaintBar(object o, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Bg);
        var r = CardRect(new Rectangle(0, (int)(4 * s), bar.Width, bar.Height - (int)(4 * s)));
        minusR = plusR = goR = stopR = Rectangle.Empty;
        bool note = DateTime.Now < barNoteUntil;
        var lf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        int ph = (int)(20 * s), py = r.Y + (r.Height - ph) / 2;
        Color lav = Color.FromArgb(232, 226, 246), lavDeep = Color.FromArgb(125, 105, 170);

        using (var card = Round(r, (int)(9 * s)))
        {
            if (breakForms != null)
            {
                using (var b = new SolidBrush(lav)) g.FillPath(b, card);
                TextRenderer.DrawText(g, "휴식 중  " + Clock((breakEnd - DateTime.Now).TotalSeconds) + (breakHidden ? "  · 클릭: 휴식 화면" : ""), ageFont,
                    new Rectangle(r.X + doneW, r.Y, r.Width - doneW * 2, r.Height), lavDeep, lf);
                return;
            }
            if (focusing)
            {
                double total = (focusEnd - focusStart).TotalSeconds, left = (focusEnd - (away ? awayStart : DateTime.Now)).TotalSeconds;
                float done = (float)Math.Max(0, Math.Min(1, 1 - left / total));
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, card);
                var clip = g.Clip;
                g.SetClip(card);
                using (var b = new SolidBrush(GreenFill)) g.FillRectangle(b, r.X, r.Y, (int)(r.Width * done), r.Height + 1);
                g.Clip = clip;
                using (var pen = new Pen((barWarn && note) || away ? RedDeep : GreenDeep, 1.4f * s)) g.DrawPath(pen, card);
                int sw = (int)(54 * s);
                stopR = new Rectangle(r.Right - sw - (int)(6 * s), py, sw, ph);
                Pill(g, stopR, Color.FromArgb(235, 232, 240), GrayDeep, "■ 그만", ageFont);
                string txt = note ? barNote : away ? "⏸ 일시정지 · " + Clock(AwaySec - (DateTime.Now - awayStart).TotalSeconds) + " 안에 복귀" : "집중  " + Clock(left) + "  남음";
                TextRenderer.DrawText(g, txt, ageFont, new Rectangle(r.X + doneW, r.Y, stopR.X - r.X - doneW - (int)(4 * s), r.Height),
                    (note && barWarn) || away ? RedDeep : GreenDeep, lf);
                return;
            }
            // idle: [-] 30분 [+]      [▶ 집중 시작]
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, card);
            using (var pen = new Pen(Color.FromArgb(215, 208, 222), 1f * s)) g.DrawPath(pen, card);
            int gw = (int)(58 * s);
            goR = new Rectangle(r.Right - gw - (int)(6 * s), py, gw, ph);
            Pill(g, goR, GreenFill, GreenDeep, "▶ 시작", ageFont);
            if (note)
            {
                TextRenderer.DrawText(g, barNote, ageFont, new Rectangle(r.X + doneW, r.Y, goR.X - r.X - doneW - (int)(4 * s), r.Height),
                    barWarn ? RedDeep : GreenDeep, lf);
                return;
            }
            int bs = ph, x = r.X + (int)(8 * s);
            minusR = new Rectangle(x, py, bs, bs);
            Pill(g, minusR, Color.FromArgb(240, 236, 244), selTier > 0 ? GrayDeep : Color.FromArgb(205, 200, 212), "<", ageFont);
            int lw = (int)(44 * s);
            TextRenderer.DrawText(g, TierFocus[selTier] + "분", Font, new Rectangle(minusR.Right, r.Y, lw, r.Height), Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            plusR = new Rectangle(minusR.Right + lw, py, bs, bs);
            // which session of this task + its break length
            TextRenderer.DrawText(g, (selTier + 1) + "회차 · 휴식 " + TierBreak[selTier] + "분",
                ageFont, new Rectangle(plusR.Right + (int)(6 * s), r.Y, goR.X - plusR.Right - (int)(8 * s), r.Height), Muted, lf);
            Pill(g, plusR, Color.FromArgb(240, 236, 244), selTier < TierFocus.Length - 1 ? GrayDeep : Color.FromArgb(205, 200, 212), ">", ageFont);
        }
    }

    // ---------- break overlay ----------

    void StartBreak()
    {
        breakStart = DateTime.Now;
        breakEnd = breakStart.AddMinutes(breakMin);
        breakForms = new List<Overlay>();
        foreach (var sc in Screen.AllScreens)
        {
            var f = new Overlay { Bounds = sc.Bounds, BackColor = Color.FromArgb(52, 46, 68) }; // opaque: actually rest
            if (sc.Primary)
            {
                f.Paint += PaintBreak;
                f.MouseDoubleClick += (o, e) => { if (skipR.Contains(e.Location)) EndBreak(); }; // emergency exit
                f.MouseDown += (o, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (extR.Contains(e.Location)) ExtendFocus();
                    else if (bgR.Contains(e.Location)) HideBreak();
                };
                f.MouseMove += (o, e) => f.Cursor = skipR.Contains(e.Location) || extR.Contains(e.Location) || bgR.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            }
            breakForms.Add(f);
            f.Show();
            f.Bounds = sc.Bounds;
        }
        System.Media.SystemSounds.Asterisk.Play();
        tick.Start();
        bar.Invalidate();
    }

    void EndBreak() { EndBreak(false); }

    void EndBreak(bool quiet)
    {
        if (breakForms == null) return;
        var fs = breakForms;
        breakForms = null;
        breakHidden = false; soonShown = false;
        if (soonForm != null) soonForm.Hide();
        foreach (var f in fs) { f.AllowClose = true; f.Close(); f.Dispose(); }
        if (!quiet)
        {
            System.Media.SystemSounds.Asterisk.Play();
            Note("휴식 끝! 다시 시작해볼까요", false);
        }
        BackToWork();
    }

    // from the break screen: same task, same round, 5 more minutes; then the break comes back
    HashSet<IntPtr> doneWins; Item doneItem; int doneTier;
    const int ExtendMin = 5;
    Rectangle extR;

    void ExtendFocus()
    {
        if (breakForms == null || doneWins == null) return;
        EndBreak(true);
        focusWins = doneWins;
        focusItem = doneItem != null && list.Items.Contains(doneItem) ? doneItem : null;
        focusTier = doneTier;
        breakMin = TierBreak[focusTier];
        focusing = true;
        var nowX = DateTime.Now;
        focusStart = nowX - (doneEnd - doneStart); // keep the time already focused on the bar
        focusEnd = nowX.AddMinutes(ExtendMin);
        freezeFrom = nowX; pausedSec = 0; plannedMin = ExtendMin; extension = true;
        warnedItem = null;
        tick.Start();
        Note("+" + ExtendMin + "분 연장 · 끝나면 다시 휴식", false);
        bar.Invalidate(); list.Invalidate();
    }

    // break keeps counting with the cover hidden; a small card pops up 10 s before it ends
    bool breakHidden, soonShown; Toast soonForm; Rectangle bgR;
    const int SoonSec = 10;

    void HideBreak()
    {
        if (breakForms == null) return;
        breakHidden = true;
        foreach (var f in breakForms) f.Hide();
        if ((breakEnd - DateTime.Now).TotalSeconds <= SoonSec) ShowSoon();
        Note("백그라운드로 휴식 중 · 끝나기 10초 전에 알려줄게요", false);
        tick.Start();
    }

    void ShowBreak()
    {
        if (breakForms == null || !breakHidden) return;
        breakHidden = false;
        if (soonForm != null) soonForm.Hide();
        foreach (var f in breakForms) f.Show();
        bar.Invalidate();
    }

    void ShowSoon()
    {
        soonShown = true;
        if (soonForm == null)
        {
            soonForm = new Toast { BackColor = Bg, Size = new Size((int)(300 * s), (int)(128 * s)) };
            IntPtr rgn = CreateRoundRectRgn(0, 0, soonForm.Width + 1, soonForm.Height + 1, (int)(22 * s), (int)(22 * s));
            soonForm.Region = Region.FromHrgn(rgn); DeleteObject(rgn);
            soonForm.Paint += PaintSoon;
            soonForm.MouseDown += (o, e) => soonForm.Hide();
        }
        var wa = Screen.PrimaryScreen.WorkingArea;
        soonForm.Location = new Point(wa.X + (wa.Width - soonForm.Width) / 2, wa.Y + (wa.Height - soonForm.Height) / 2);
        soonForm.Show();
        System.Media.SystemSounds.Exclamation.Play();
    }

    void PaintSoon(object o, PaintEventArgs e)
    {
        var f = (Form)o; var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Bg);
        Color lav = Color.FromArgb(180, 160, 220), lavDeep = Color.FromArgb(125, 105, 170);
        using (var p = Round(new Rectangle(0, 0, f.Width - 1, f.Height - 1), (int)(11 * s)))
        using (var pen = new Pen(lav, 2.4f * s)) g.DrawPath(pen, p);
        var cf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, "휴식이 곧 끝나요", midFont, new Rectangle(0, (int)(12 * s), f.Width, (int)(28 * s)), lavDeep, cf);
        double left = Math.Max(0, (breakEnd - DateTime.Now).TotalSeconds);
        using (var big = new Font(bigFont.FontFamily, 26f))
            TextRenderer.DrawText(g, Clock(left), big, new Rectangle(0, (int)(40 * s), f.Width, (int)(50 * s)), Ink, cf);
        TextRenderer.DrawText(g, "자리로 돌아올 준비 · 클릭하면 닫혀요", ageFont, new Rectangle(0, (int)(94 * s), f.Width, (int)(22 * s)), Muted, cf);
    }

    void PaintBreak(object o, PaintEventArgs e)
    {
        var f = (Form)o;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int cw = (int)(420 * s), ch = (int)(292 * s);
        var c = new Rectangle((f.ClientSize.Width - cw) / 2, (f.ClientSize.Height - ch) / 2, cw, ch);
        using (var p = Round(c, (int)(24 * s)))
        using (var b = new SolidBrush(Bg)) g.FillPath(b, p);
        var cf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        Color lavDeep = Color.FromArgb(125, 105, 170);
        TextRenderer.DrawText(g, "휴식 시간", midFont, new Rectangle(c.X, c.Y + (int)(26 * s), c.Width, (int)(30 * s)), lavDeep, cf);
        double left = (breakEnd - DateTime.Now).TotalSeconds;
        TextRenderer.DrawText(g, Clock(left), bigFont, new Rectangle(c.X, c.Y + (int)(62 * s), c.Width, (int)(80 * s)), Ink, cf);
        TextRenderer.DrawText(g, "화면에서 눈을 떼고, 일어나서 물 한 잔 + 스트레칭", Font,
            new Rectangle(c.X, c.Y + (int)(150 * s), c.Width, (int)(24 * s)), Muted, cf);
        // "5 more minutes" button
        // "5 more minutes" + "keep resting in the background" buttons
        int ebw = (int)(150 * s), ebh = (int)(30 * s), egap = (int)(12 * s), ey = c.Y + (int)(188 * s);
        if (doneWins == null)
        {
            extR = Rectangle.Empty;
            bgR = new Rectangle(c.X + (c.Width - ebw) / 2, ey, ebw, ebh);
        }
        else
        {
            extR = new Rectangle(c.X + c.Width / 2 - ebw - egap / 2, ey, ebw, ebh);
            bgR = new Rectangle(c.X + c.Width / 2 + egap / 2, ey, ebw, ebh);
            Pill(g, extR, GreenFill, GreenDeep, "+" + ExtendMin + "분 더 집중", Font);
        }
        Pill(g, bgR, Color.FromArgb(235, 232, 240), GrayDeep, "백그라운드로 쉬기", Font);
        // progress
        var pr = new Rectangle(c.X + (int)(50 * s), c.Bottom - (int)(40 * s), c.Width - (int)(100 * s), (int)(8 * s));
        float done = (float)Math.Max(0, Math.Min(1, 1 - left / (breakMin * 60.0)));
        using (var p = Round(pr, pr.Height / 2))
        using (var b = new SolidBrush(Color.FromArgb(232, 226, 246))) g.FillPath(b, p);
        if (done > 0.01f)
        {
            var pd = new Rectangle(pr.X, pr.Y, Math.Max(pr.Height, (int)(pr.Width * done)), pr.Height);
            using (var p = Round(pd, pr.Height / 2))
            using (var b = new SolidBrush(Color.FromArgb(180, 160, 220))) g.FillPath(b, p);
        }
        // small emergency exit at the bottom of the screen
        string skip = "급한 일이면 여기를 더블클릭해서 건너뛰기";
        var font = ageFont;
        Size sz = TextRenderer.MeasureText(skip, font);
        skipR = new Rectangle((f.ClientSize.Width - sz.Width) / 2 - (int)(10 * s), f.ClientSize.Height - (int)(70 * s), sz.Width + (int)(20 * s), (int)(28 * s));
        TextRenderer.DrawText(g, skip, font, skipR, Color.FromArgb(120, 112, 140), cf);
    }

    // ---------- opacity ----------

    ToolStripMenuItem OpacityMenu()
    {
        var m = new ToolStripMenuItem("투명도");
        foreach (int p in new[] { 100, 90, 80, 70, 60, 50, 40 })
        {
            int pp = p;
            m.DropDownItems.Add(p + "%", null, (o, e) => SetOpacity(pp / 100.0));
        }
        m.DropDownOpening += (o, e) =>
        {
            foreach (ToolStripMenuItem x in m.DropDownItems)
                x.Checked = x.Text == (int)Math.Round(Opacity * 100) + "%";
        };
        return m;
    }

    void SetOpacity(double v)
    {
        v = Math.Max(0.3, Math.Min(1.0, Math.Round(v, 2)));
        Opacity = v;
        try { File.WriteAllText(settingsPath, v.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
    }

    double LoadOpacity()
    {
        try
        {
            double v;
            if (double.TryParse(File.ReadAllText(settingsPath).Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return Math.Max(0.3, Math.Min(1.0, v));
        }
        catch { }
        return 0.95;
    }

    void Fit()
    {
        int n = list.Items.Count;
        int ghost = ghostShown ? row : 0;
        int max = Screen.PrimaryScreen.WorkingArea.Height * 2 / 3;
        int total = 0;
        for (int i = 0; i < n; i++) total += RowHeight(i);
        int listH = Math.Min(total, max - header.Height - ghost - bar.Height - Padding.Vertical); // scrolls if very long
        list.Height = Math.Max(0, listH);
        Height = header.Height + list.Height + ghost + bar.Height + Padding.Vertical;
    }

    // ---------- persistence: ticks \t hwnd \t proc \t title \t text ----------

    const char US = '\x1f'; // separates multiple linked windows inside a column
    static string Clean(string x) { return (x ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace(US, ' '); }

    void LoadItems()
    {
        try // section names: id \t name
        {
            if (File.Exists(groupsPath))
                foreach (var gl in File.ReadAllLines(groupsPath, Encoding.UTF8))
                {
                    var gf = gl.Split('\t'); int gid;
                    if (gf.Length >= 2 && int.TryParse(gf[0], out gid)) groupNames[gid] = gf[1];
                }
        }
        catch { }
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var f = line.Split('\t');
            long ticks;
            if (f.Length < 2 || !long.TryParse(f[0], out ticks)) continue;
            var it = new Item { Created = new DateTime(ticks) };
            if (f.Length >= 5)
            {
                // columns 1..3 hold one entry per linked window, separated by \x1f (old files: single entry)
                var hs = f[1].Split(US); var ps = f[2].Split(US); var ts = f[3].Split(US);
                for (int k = 0; k < hs.Length; k++)
                {
                    long hw; long.TryParse(hs[k], out hw);
                    if (hw == 0 && (k >= ps.Length || ps[k] == "")) continue;
                    it.Wins.Add(new Win { H = (IntPtr)hw, Proc = k < ps.Length ? ps[k] : "", Title = k < ts.Length ? ts[k] : "" });
                }
                int sess;
                if (f.Length >= 6 && int.TryParse(f[5], out sess)) // 6th col: sessions done, 7th: group id
                {
                    it.Sessions = sess; it.Text = f[4];
                    int gid; if (f.Length >= 7 && int.TryParse(f[6], out gid)) it.GroupId = gid;
                }
                else it.Text = string.Join(" ", f, 4, f.Length - 4);
            }
            else it.Text = string.Join(" ", f, 1, f.Length - 1); // oldest 2-column format
            ResolveAll(it); // re-link to the live windows after a restart
            list.Items.Add(it);
        }
    }

    void Save()
    {
        var sb = new StringBuilder();
        foreach (Item it in list.Items)
        {
            var hs = new List<string>(); var ps = new List<string>(); var ts = new List<string>();
            foreach (var w in it.Wins) { hs.Add(w.H.ToInt64().ToString()); ps.Add(Clean(w.Proc)); ts.Add(Clean(w.Title)); }
            string sep = US.ToString();
            sb.Append(it.Created.Ticks).Append('\t').Append(string.Join(sep, hs)).Append('\t')
              .Append(string.Join(sep, ps)).Append('\t').Append(string.Join(sep, ts)).Append('\t')
              .Append(Clean(it.Text)).Append('\t').Append(it.Sessions).Append('\t').Append(it.GroupId).Append('\n');
        }
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        // names of sections still in use
        var used = new HashSet<int>();
        foreach (Item it in list.Items) if (it.GroupId != 0) used.Add(it.GroupId);
        var gs = new StringBuilder();
        foreach (var kv in groupNames) if (used.Contains(kv.Key)) gs.Append(kv.Key).Append('\t').Append(Clean(kv.Value)).Append('\n');
        try { File.WriteAllText(groupsPath, gs.ToString(), Encoding.UTF8); } catch { }
    }

    // ---------- stats window ----------

    class Rec
    {
        public DateTime Start; public double Min; public string Task = "", Group = "", Kind = "", Outcome = "";
        public int Round, Planned;
    }

    static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var f = new StringBuilder(); bool q = false;
        text = text.TrimStart('﻿');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { f.Append('"'); i++; } else q = false; }
                else f.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { row.Add(f.ToString()); f.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(f.ToString()); f.Clear();
                if (row.Exists(x => x.Length > 0)) rows.Add(row.ToArray());
                row = new List<string>();
            }
            else f.Append(c);
        }
        if (f.Length > 0 || row.Count > 0) { row.Add(f.ToString()); if (row.Exists(x => x.Length > 0)) rows.Add(row.ToArray()); }
        return rows;
    }

    static string FmtMin(double m)
    {
        int v = (int)Math.Round(m);
        return v < 60 ? v + "분" : (v / 60) + "시간" + (v % 60 > 0 ? " " + (v % 60) + "분" : "");
    }

    class StatsForm : Form
    {
        readonly PQForm o;
        readonly float s;
        readonly Font fTitle, fBig, fText, fSmall;
        List<Rec> all = new List<Rec>();
        string range = "7";
        Rectangle closeR, sheetR;
        readonly List<KeyValuePair<string, Rectangle>> chips = new List<KeyValuePair<string, Rectangle>>();
        readonly List<KeyValuePair<Rectangle, string>> hover = new List<KeyValuePair<Rectangle, string>>();
        readonly ToolTip tt = new ToolTip { InitialDelay = 0, ReshowDelay = 0 };
        string tipShown;
        static readonly Color Card = Color.White, Line = Color.FromArgb(235, 229, 238), Grid = Color.FromArgb(240, 235, 242);
        static readonly Color Bar = Color.FromArgb(90, 168, 116), Ink2 = Color.FromArgb(107, 100, 120);

        public StatsForm(PQForm owner)
        {
            o = owner; s = owner.s;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Bg;
            Text = "집중 기록";
            StartPosition = FormStartPosition.Manual;
            Size = new Size((int)(760 * s), (int)(640 * s));
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
            IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, (int)(24 * s), (int)(24 * s));
            Region = Region.FromHrgn(rgn); DeleteObject(rgn);
            fTitle = Pick(14f, "Pretendard SemiBold", "Noto Sans KR Medium", "Malgun Gothic");
            fBig = Pick(18f, "Pretendard SemiBold", "Noto Sans KR Medium", "Malgun Gothic");
            fText = owner.Font;
            fSmall = Pick(8.5f, "Pretendard", "Noto Sans KR", "Malgun Gothic");
            KeyDown += (x, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseLeave += (x, e) => { tt.Hide(this); tipShown = null; };
            Reload();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // not in Alt+Tab
        }

        public void Reload()
        {
            all.Clear();
            try
            {
                if (File.Exists(o.logPath))
                    foreach (var r in ParseCsv(File.ReadAllText(o.logPath, Encoding.UTF8)))
                    {
                        DateTime st; double m;
                        if (r.Length < 9 || !DateTime.TryParse(r[0], out st)) continue;
                        double.TryParse(r[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out m);
                        int rd, pl; int.TryParse(r[5], out rd); int.TryParse(r[6], out pl);
                        all.Add(new Rec { Start = st, Min = m, Task = r[3].Length > 0 ? r[3] : "(할 일 없음)", Group = r[4], Round = rd, Planned = pl, Kind = r[7], Outcome = r[8] });
                    }
            }
            catch { }
            Invalidate();
        }

        List<Rec> InRange()
        {
            var today = DateTime.Today;
            var res = new List<Rec>();
            foreach (var r in all)
                if (range == "all" || (range == "today" ? r.Start >= today : r.Start >= today.AddDays(1 - int.Parse(range)))) res.Add(r);
            return res;
        }

        int S(float v) { return (int)(v * s); }

        void CardAt(Graphics g, Rectangle r)
        {
            using (var p = Round(r, S(14)))
            {
                using (var b = new SolidBrush(Card)) g.FillPath(b, p);
                using (var pen = new Pen(Line, 1f * s)) g.DrawPath(pen, p);
            }
        }

        static readonly TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        static readonly TextFormatFlags Rt = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        static readonly TextFormatFlags C = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Bg);
            hover.Clear(); chips.Clear();
            int W = ClientSize.Width, H = ClientSize.Height, M = S(24);
            var rows = InRange();
            double total = 0; int done = 0, ext = 0, left = 0;
            var days = new HashSet<DateTime>();
            foreach (var r in rows)
            {
                total += r.Min; days.Add(r.Start.Date);
                if (r.Outcome == "완료") done++;
                if (r.Kind == "연장") ext++;
                if (r.Outcome == "이탈") left++;
            }

            // header: title, range chips, close
            string label = range == "today" ? "오늘" : range == "7" ? "최근 7일" : range == "30" ? "최근 30일" : "전체 기간";
            TextRenderer.DrawText(g, "집중 기록", fTitle, new Rectangle(M, S(16), S(300), S(28)), Ink, L);
            TextRenderer.DrawText(g, label + " · 세션 " + rows.Count + "개", fSmall, new Rectangle(M, S(42), S(300), S(18)), Muted, L);
            closeR = new Rectangle(W - M - S(28), S(20), S(28), S(28));
            using (var p = Round(closeR, closeR.Height / 2))
            using (var b = new SolidBrush(Color.FromArgb(240, 236, 244))) g.FillPath(b, p);
            TextRenderer.DrawText(g, "✕", fText, closeR, GrayDeep, C);
            int cx = closeR.X - S(14);
            foreach (var kv in new[] { new[] { "all", "전체" }, new[] { "30", "30일" }, new[] { "7", "7일" }, new[] { "today", "오늘" } })
            {
                int cw = S(kv[1].Length > 2 ? 48 : 44);
                var cr = new Rectangle(cx - cw, S(22), cw, S(24));
                cx -= cw + S(6);
                bool on = range == kv[0];
                using (var p = Round(cr, cr.Height / 2))
                using (var b = new SolidBrush(on ? Ink : Color.FromArgb(240, 236, 244))) g.FillPath(b, p);
                TextRenderer.DrawText(g, kv[1], fSmall, cr, on ? Color.White : Ink2, C);
                chips.Add(new KeyValuePair<string, Rectangle>(kv[0], cr));
            }

            // KPI tiles
            int ky = S(72), kh = S(78), gap = S(10), kw = (W - M * 2 - gap * 3) / 4;
            string[][] k = {
                new[] { "총 집중", rows.Count > 0 ? FmtMin(total) : "–", days.Count > 1 ? "하루 평균 " + FmtMin(total / days.Count) : "" },
                new[] { "세션", rows.Count > 0 ? rows.Count.ToString() : "–", ext > 0 ? "연장 " + ext + "번 포함" : "" },
                new[] { "끝까지 완료", rows.Count > 0 ? (done * 100 / rows.Count) + "%" : "–", rows.Count > 0 ? done + " / " + rows.Count + " 세션" : "" },
                new[] { "평균 세션", rows.Count > 0 ? FmtMin(total / rows.Count) : "–", left > 0 ? "1분 넘게 이탈 " + left + "번" : "" } };
            for (int i = 0; i < 4; i++)
            {
                var r = new Rectangle(M + i * (kw + gap), ky, kw, kh);
                CardAt(g, r);
                TextRenderer.DrawText(g, k[i][0], fSmall, new Rectangle(r.X + S(14), r.Y + S(8), r.Width - S(20), S(18)), Ink2, L);
                TextRenderer.DrawText(g, k[i][1], fBig, new Rectangle(r.X + S(12), r.Y + S(26), r.Width - S(20), S(30)), Ink, L);
                TextRenderer.DrawText(g, k[i][2], fSmall, new Rectangle(r.X + S(14), r.Y + S(54), r.Width - S(20), S(18)), Muted, L);
            }

            // chart: one series (single hue, no legend; the title names it)
            int ry = ky + kh + gap, rh = S(236);
            var chartR = new Rectangle(M, ry, (int)((W - M * 2 - gap) * 0.58), rh);
            var taskR = new Rectangle(chartR.Right + gap, ry, W - M - chartR.Right - gap, rh);
            CardAt(g, chartR); CardAt(g, taskR);
            DrawChart(g, chartR, rows);
            DrawTasks(g, taskR, rows);

            // recent sessions
            var tr = new Rectangle(M, ry + rh + gap, W - M * 2, H - (ry + rh + gap) - M);
            CardAt(g, tr);
            DrawTable(g, tr, rows);
        }

        void DrawChart(Graphics g, Rectangle c, List<Rec> rows)
        {
            var names = new List<string>(); var ticks = new List<string>(); var vals = new List<double>();
            string title;
            if (range == "today")
            {
                title = "시간대별 집중 (오늘)";
                for (int h = 0; h < 24; h++) { names.Add(h + "시"); ticks.Add(h % 4 == 0 ? h + "시" : ""); vals.Add(0); }
                foreach (var r in rows) vals[r.Start.Hour] += r.Min;
            }
            else
            {
                title = "일별 집중 시간";
                int n = range == "all" ? 14 : int.Parse(range);
                if (range == "all" && all.Count > 0)
                {
                    DateTime first = DateTime.Today; foreach (var r in all) if (r.Start < first) first = r.Start;
                    n = Math.Max(7, Math.Min(90, (int)(DateTime.Today - first.Date).TotalDays + 1));
                }
                int every = n > 14 ? (int)Math.Ceiling(n / 7.0) : 1;
                var idx = new Dictionary<DateTime, int>();
                for (int i = n - 1; i >= 0; i--)
                {
                    var d = DateTime.Today.AddDays(-i); int j = n - 1 - i;
                    idx[d] = names.Count;
                    names.Add(d.Month + "/" + d.Day + " (" + "일월화수목금토"[(int)d.DayOfWeek] + ")");
                    ticks.Add(j % every == 0 || i == 0 ? d.Month + "/" + d.Day : "");
                    vals.Add(0);
                }
                foreach (var r in rows) { int j; if (idx.TryGetValue(r.Start.Date, out j)) vals[j] += r.Min; }
            }
            TextRenderer.DrawText(g, title, fText, new Rectangle(c.X + S(14), c.Y + S(8), c.Width - S(28), S(24)), Ink, L);
            int L0 = c.X + S(44), R0 = c.Right - S(14), T0 = c.Y + S(42), B0 = c.Bottom - S(28);
            double max = 30; foreach (var v in vals) max = Math.Max(max, v);
            int step = max > 240 ? 120 : max > 120 ? 60 : 30;
            double top = Math.Ceiling(max / step) * step;
            using (var gp = new Pen(Grid, 1f * s))
                for (int v = 0; v <= top; v += step)
                {
                    int y = B0 - (int)((B0 - T0) * v / top);
                    g.DrawLine(gp, L0, y, R0, y);
                    TextRenderer.DrawText(g, v >= 60 && v % 60 == 0 ? (v / 60) + "h" : v + "m", fSmall, new Rectangle(c.X, y - S(9), L0 - c.X - S(6), S(18)), Muted, Rt);
                }
            float cw = (R0 - L0) / (float)vals.Count, bw = Math.Max(2f, Math.Min(26f * s, cw - 2f * s));
            for (int i = 0; i < vals.Count; i++)
            {
                float x = L0 + i * cw + (cw - bw) / 2;
                int h = (int)((B0 - T0) * vals[i] / top);
                if (h > 0)
                {
                    var br = new Rectangle((int)x, B0 - h, (int)bw, h);
                    int rad = (int)Math.Min(4 * s, Math.Min(bw / 2, h));
                    using (var p = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        p.AddLine(br.X, br.Bottom, br.X, br.Y + rad);
                        if (rad > 0) p.AddArc(br.X, br.Y, rad * 2, rad * 2, 180, 90);
                        if (rad > 0) p.AddArc(br.Right - rad * 2, br.Y, rad * 2, rad * 2, 270, 90);
                        p.AddLine(br.Right, br.Y + rad, br.Right, br.Bottom);
                        p.CloseFigure();
                        using (var b = new SolidBrush(Bar)) g.FillPath(b, p);
                    }
                }
                if (ticks[i].Length > 0)
                    TextRenderer.DrawText(g, ticks[i], fSmall, new Rectangle((int)(x + bw / 2) - S(24), B0 + S(4), S(48), S(18)), Muted, C);
                hover.Add(new KeyValuePair<Rectangle, string>(new Rectangle((int)(L0 + i * cw), T0, (int)Math.Ceiling(cw), B0 - T0),
                    names[i] + " · " + (vals[i] > 0 ? FmtMin(vals[i]) : "기록 없음")));
            }
        }

        void DrawTasks(Graphics g, Rectangle c, List<Rec> rows)
        {
            TextRenderer.DrawText(g, "할 일별 집중", fText, new Rectangle(c.X + S(14), c.Y + S(8), c.Width - S(28), S(24)), Ink, L);
            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, "아직 기록이 없어요\n▶ 시작으로 집중해 보세요", fSmall, c, Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            var by = new Dictionary<string, double[]>(); var meta = new Dictionary<string, string[]>();
            foreach (var r in rows)
            {
                string key = r.Task + "\u0001" + r.Group;
                if (!by.ContainsKey(key)) { by[key] = new double[2]; meta[key] = new[] { r.Task, r.Group }; }
                by[key][0] += r.Min; by[key][1]++;
            }
            var keys = new List<string>(by.Keys);
            keys.Sort((a, b) => by[b][0].CompareTo(by[a][0]));
            var list = new List<string[]>(); var vals = new List<double>(); var cnt = new List<int>();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i == 5 && keys.Count > 6)
                {
                    double rest = 0; int rn = 0;
                    for (int j = 5; j < keys.Count; j++) { rest += by[keys[j]][0]; rn += (int)by[keys[j]][1]; }
                    list.Add(new[] { "기타 " + (keys.Count - 5) + "개", "" }); vals.Add(rest); cnt.Add(rn);
                    break;
                }
                list.Add(meta[keys[i]]); vals.Add(by[keys[i]][0]); cnt.Add((int)by[keys[i]][1]);
            }
            double max = 0; foreach (var v in vals) max = Math.Max(max, v);
            int y = c.Y + S(40), rowH = S(31), x = c.X + S(14), w = c.Width - S(28);
            for (int i = 0; i < list.Count; i++)
            {
                string val = FmtMin(vals[i]);
                int vw = TextRenderer.MeasureText(val, fSmall).Width;
                string name = list[i][0];
                int nw = Math.Min(TextRenderer.MeasureText(name, fText).Width, w - vw - S(8));
                TextRenderer.DrawText(g, name, fText, new Rectangle(x - S(3), y, nw + S(4), S(18)), Ink, L);
                if (list[i][1].Length > 0)
                    TextRenderer.DrawText(g, list[i][1], fSmall, new Rectangle(x + nw + S(2), y, Math.Max(0, w - vw - nw - S(8)), S(18)), Muted, L);
                TextRenderer.DrawText(g, val, fSmall, new Rectangle(x, y, w, S(18)), Ink2, Rt);
                var track = new Rectangle(x, y + S(20), w, S(6));
                using (var p = Round(track, track.Height / 2))
                using (var b = new SolidBrush(Grid)) g.FillPath(b, p);
                var fill = new Rectangle(x, track.Y, Math.Max(track.Height, (int)(w * vals[i] / max)), track.Height);
                using (var p = Round(fill, track.Height / 2))
                using (var b = new SolidBrush(Bar)) g.FillPath(b, p);
                hover.Add(new KeyValuePair<Rectangle, string>(new Rectangle(x, y, w, rowH),
                    name + " · " + FmtMin(vals[i]) + " · 세션 " + cnt[i] + "개"));
                y += rowH;
            }
        }

        void DrawTable(Graphics g, Rectangle c, List<Rec> rows)
        {
            var sorted = new List<Rec>(rows);
            sorted.Sort((a, b) => b.Start.CompareTo(a.Start));
            TextRenderer.DrawText(g, "최근 세션", fText, new Rectangle(c.X + S(14), c.Y + S(8), S(200), S(24)), Ink, L);
            string link = "기록 시트 열기 ↗";
            int lw = TextRenderer.MeasureText(link, fSmall).Width + S(8);
            sheetR = new Rectangle(c.Right - S(14) - lw, c.Y + S(10), lw, S(20));
            TextRenderer.DrawText(g, link, fSmall, sheetR, GreenDeep, C);
            int x = c.X + S(14), w = c.Width - S(28);
            int[] cols = { 0, S(92), S(330), S(470), S(560) };
            string[] heads = { "시작", "할 일", "묶음", "집중", "결과" };
            int y = c.Y + S(38);
            for (int i = 0; i < heads.Length; i++)
                TextRenderer.DrawText(g, heads[i], fSmall, new Rectangle(x + cols[i], y, S(120), S(20)), Muted, L);
            using (var pen = new Pen(Line, 1f * s)) g.DrawLine(pen, x, y + S(22), x + w, y + S(22));
            y += S(26);
            int rowH = S(24), maxRows = Math.Max(0, (c.Bottom - S(8) - y) / rowH);
            if (sorted.Count == 0)
            {
                TextRenderer.DrawText(g, "이 기간에는 기록이 없어요", fSmall, new Rectangle(x, y, w, S(40)), Muted, C);
                return;
            }
            for (int i = 0; i < Math.Min(maxRows, sorted.Count); i++)
            {
                var r = sorted[i];
                string[] v = {
                    r.Start.Month + "/" + r.Start.Day + " " + r.Start.ToString("HH:mm"), r.Task, r.Group,
                    r.Min.ToString("0.0") + "분" + (r.Planned > 0 ? " / " + r.Planned : ""),
                    r.Outcome + (r.Kind == "연장" ? " · 연장" : "") };
                for (int j = 0; j < v.Length; j++)
                {
                    int cw2 = (j + 1 < cols.Length ? cols[j + 1] : w) - cols[j] - S(8);
                    Color col = j == 4 ? (r.Outcome == "완료" ? GreenDeep : r.Outcome == "이탈" ? RedDeep : Ink2) : j == 0 || j == 3 ? Ink2 : Ink;
                    TextRenderer.DrawText(g, v[j], j == 1 ? fText : fSmall, new Rectangle(x + cols[j] - S(3), y, cw2, rowH), col, L);
                }
                y += rowH;
            }
        }

        void OnDown(object x, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (closeR.Contains(e.Location)) { Close(); return; }
            if (sheetR.Contains(e.Location)) { o.OpenLogSheet(); return; }
            foreach (var kv in chips) if (kv.Value.Contains(e.Location)) { range = kv.Key; Invalidate(); return; }
            if (e.Y < S(64)) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } // drag by the header
        }

        void OnMove(object x, MouseEventArgs e)
        {
            bool hand = closeR.Contains(e.Location) || sheetR.Contains(e.Location);
            foreach (var kv in chips) if (kv.Value.Contains(e.Location)) hand = true;
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            string tip = null;
            foreach (var kv in hover) if (kv.Key.Contains(e.Location)) { tip = kv.Value; break; }
            if (tip == tipShown) return;
            tipShown = tip;
            if (tip == null) tt.Hide(this); else tt.Show(tip, this, e.X + S(12), e.Y - S(28), 4000);
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--snap") // debug: which windows would be linked from <hwnd>
        {
            SetProcessDPIAware();
            var sb = new StringBuilder();
            foreach (var w in SnapGroup((IntPtr)long.Parse(args[1]), IntPtr.Zero)) sb.Append(w.ToInt64()).Append('\t').Append(TitleOf(w)).Append('\n');
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "pq_snap.txt"), sb.ToString(), Encoding.UTF8);
            return;
        }
        bool created;
        using (var mtx = new System.Threading.Mutex(true, "ADHD-PQ-singleton", out created))
        {
            if (!created) // already running: ask it to come back on screen
            {
                try { System.Threading.EventWaitHandle.OpenExisting(args.Length > 0 && args[0] == "--stats" ? "ADHD-PQ-stats" : "ADHD-PQ-show").Set(); } catch { }
                return;
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PQForm());
        }
    }
}
