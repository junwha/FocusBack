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
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = row,
            BorderStyle = BorderStyle.None,
            BackColor = Bg,
            IntegralHeight = false,
            AllowDrop = true,
        };
        list.DrawItem += DrawItem;
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
        menu.Items.Add("맨 위로", null, (o, e) => MoveSelected(-list.SelectedIndex));
        menu.Items.Add("타이머 리셋", null, (o, e) => ResetSelected());
        menu.Items.Add("집중 회차 처음으로 (15분부터)", null, (o, e) =>
        {
            var it = list.SelectedItem as Item;
            if (it != null) { it.Sessions = 0; if (it == barItem) selTier = 0; Changed(); bar.Invalidate(); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(OpacityMenu());
        menu.Items.Add("종료", null, (o, e) => Close());
        var hmenu = new ContextMenuStrip { Font = menu.Font };
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
        Fit();
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - (int)(10 * s), wa.Top + (int)(44 * s)); // below title-bar buttons of maximized windows

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
        if (focusing && !focusWins.Contains(h)) StopFocus("다른 창으로 이동 → 집중 초기화", true); // left the task
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
            // entering or leaving the task restarts its clock; moving between its own windows doesn't
            if (inPrev != inNow) { it.Created = DateTime.Now; changed = true; }
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
            // live reorder: the lifted card jumps to the row under the cursor
            int n = list.Items.Count;
            int to = e.Y < 0 ? 0 : list.IndexFromPoint(new Point(list.Width / 2, e.Y));
            if (to < 0) to = n - 1;
            int cur = list.Items.IndexOf(dragItem);
            if (to != cur)
            {
                list.BeginUpdate();
                list.Items.RemoveAt(cur);
                list.Items.Insert(to, dragItem);
                list.SelectedIndex = to;
                list.EndUpdate();
            }
            return;
        }
        if (dragItem != null) { dragItem = null; list.Cursor = Cursors.Default; Changed(); } // button released elsewhere
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
            dragItem = null;
            dragIndex = -1;
            dropTick = Environment.TickCount;
            list.Cursor = Cursors.Default;
            Changed();
            return;
        }
        int i = list.IndexFromPoint(e.Location);
        bool click = dragIndex >= 0 && i == dragIndex;
        dragIndex = -1;
        if (!click) return;
        if ((ModifierKeys & Keys.Shift) != 0) { list.SelectedIndex = i; RemoveSelected(); } // Shift+click = done
        else if ((ModifierKeys & Keys.Control) == 0) TrySwitch((Item)list.Items[i]);      // Ctrl+click = just select
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
        bool active = it.Has(lastExternal);
        // while focusing, the other items' clocks are frozen and they stay white
        bool frozen = focusing && !active && !InFocus(it);
        DateTime now = frozen ? (it.Created < focusStart ? focusStart : it.Created) : DateTime.Now;
        double m = (now - it.Created).TotalMinutes;

        // active window's item = green; others white, one step redder per 10 min (max 6 steps)
        float t = frozen ? 0 : Math.Min(6, (int)(m / 10)) / 6f;
        Color fill = active ? GreenFill : Lerp(Color.White, RedFill, t);
        Color deep = active ? GreenDeep : Lerp(GrayDeep, RedDeep, t);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = CardRect(e.Bounds);
        bool sel = (e.State & DrawItemState.Selected) != 0;
        bool lifted = it == dragItem;
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

    void Changed() { Save(); UpdateGhost(); UpdateBarItem(); Fit(); list.Invalidate(); }

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

    bool InFocus(Item it)
    {
        if (!focusing || it == null) return false;
        if (it == focusItem) return true;
        foreach (var w in it.Wins) if (focusWins.Contains(w.H)) return true;
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
                Note("⚠ 집중 중! 한 번 더 누르면 이동 + 초기화", true);
                System.Media.SystemSounds.Exclamation.Play();
                return;
            }
            StopFocus("이동해서 집중 초기화", true);
        }
        SwitchTo(it);
    }

    // < > : move between rounds (15/3, 30/5, 60/15); remembered for the task
    void StepTier(int d)
    {
        selTier = Math.Max(0, Math.Min(TierFocus.Length - 1, selTier + d));
        if (barItem != null) { barItem.Sessions = selTier; Save(); }
        bar.Invalidate();
    }

    static int Tier(Item it) { return it == null ? 0 : Math.Min(TierFocus.Length - 1, it.Sessions); }

    Item ItemOf(IntPtr h)
    {
        foreach (Item it in list.Items) if (it.Has(h)) return it;
        return null;
    }

    // the window you're in changed: the bar shows that task's next default again
    void UpdateBarItem()
    {
        if (focusing) return;
        var it = ItemOf(lastExternal);
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
            if (it.Has(h)) { focusItem = it; foreach (var w in it.Wins) if (w.H != IntPtr.Zero) focusWins.Add(w.H); break; }
        focusing = true;
        focusStart = DateTime.Now;
        focusTier = selTier;
        focusEnd = focusStart.AddMinutes(TierFocus[focusTier]);
        breakMin = TierBreak[focusTier];
        warnedItem = null;
        barNoteUntil = DateTime.MinValue;
        tick.Start();
        bar.Invalidate(); list.Invalidate();
        BackToWork();
    }

    // stop (reset) the focus timer; the time spent focusing doesn't count toward the other items' age
    void StopFocus(string note, bool warn)
    {
        if (!focusing) return;
        TimeSpan el = DateTime.Now - focusStart;
        foreach (Item it in list.Items)
            if (!InFocus(it)) it.Created = it.Created < focusStart ? it.Created + el : DateTime.Now;
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
        if (focusing && now >= focusEnd)
        {
            if (focusItem != null) focusItem.Sessions = Math.Max(focusItem.Sessions, focusTier + 1); // next round for this task
            StopFocus(null, false);
            barItem = null; UpdateBarItem();
            StartBreak();
        }
        if (breakForms != null)
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
        if (e.Button != MouseButtons.Left || breakForms != null) return;
        if (focusing) { if (stopR.Contains(e.Location)) StopFocus("집중 중지", false); return; }
        if (minusR.Contains(e.Location)) StepTier(-1);
        else if (plusR.Contains(e.Location)) StepTier(1);
        else if (goR.Contains(e.Location))
        {
            StartFocus();
            if (focusing && (ModifierKeys & Keys.Control) != 0) focusEnd = focusStart.AddSeconds(10); // Ctrl+click = 10s test run
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
                TextRenderer.DrawText(g, "휴식 중  " + Clock((breakEnd - DateTime.Now).TotalSeconds), ageFont,
                    new Rectangle(r.X + doneW, r.Y, r.Width - doneW * 2, r.Height), lavDeep, lf);
                return;
            }
            if (focusing)
            {
                double total = (focusEnd - focusStart).TotalSeconds, left = (focusEnd - DateTime.Now).TotalSeconds;
                float done = (float)Math.Max(0, Math.Min(1, 1 - left / total));
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, card);
                var clip = g.Clip;
                g.SetClip(card);
                using (var b = new SolidBrush(GreenFill)) g.FillRectangle(b, r.X, r.Y, (int)(r.Width * done), r.Height + 1);
                g.Clip = clip;
                using (var pen = new Pen(barWarn && note ? RedDeep : GreenDeep, 1.4f * s)) g.DrawPath(pen, card);
                int sw = (int)(54 * s);
                stopR = new Rectangle(r.Right - sw - (int)(6 * s), py, sw, ph);
                Pill(g, stopR, Color.FromArgb(235, 232, 240), GrayDeep, "■ 그만", ageFont);
                string txt = note ? barNote : "집중  " + Clock(left) + "  남음";
                TextRenderer.DrawText(g, txt, ageFont, new Rectangle(r.X + doneW, r.Y, stopR.X - r.X - doneW - (int)(4 * s), r.Height),
                    note && barWarn ? RedDeep : GreenDeep, lf);
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
                f.MouseMove += (o, e) => f.Cursor = skipR.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            }
            breakForms.Add(f);
            f.Show();
            f.Bounds = sc.Bounds;
        }
        System.Media.SystemSounds.Asterisk.Play();
        tick.Start();
        bar.Invalidate();
    }

    void EndBreak()
    {
        if (breakForms == null) return;
        var fs = breakForms;
        breakForms = null;
        foreach (var f in fs) { f.AllowClose = true; f.Close(); f.Dispose(); }
        System.Media.SystemSounds.Asterisk.Play();
        Note("휴식 끝! 다시 시작해볼까요", false);
        BackToWork();
    }

    void PaintBreak(object o, PaintEventArgs e)
    {
        var f = (Form)o;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int cw = (int)(420 * s), ch = (int)(250 * s);
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
        // progress
        var pr = new Rectangle(c.X + (int)(50 * s), c.Bottom - (int)(46 * s), c.Width - (int)(100 * s), (int)(8 * s));
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
        int listH = Math.Min(n * row, max - header.Height - ghost - bar.Height - Padding.Vertical); // scrolls if very long
        list.Height = Math.Max(0, listH);
        Height = header.Height + list.Height + ghost + bar.Height + Padding.Vertical;
    }

    // ---------- persistence: ticks \t hwnd \t proc \t title \t text ----------

    const char US = '\x1f'; // separates multiple linked windows inside a column
    static string Clean(string x) { return (x ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace(US, ' '); }

    void LoadItems()
    {
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
                if (f.Length >= 6 && int.TryParse(f[5], out sess)) { it.Sessions = sess; it.Text = f[4]; } // 6th col: sessions done
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
              .Append(Clean(it.Text)).Append('\t').Append(it.Sessions).Append('\n');
        }
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
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
            if (!created) return; // already running
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PQForm());
        }
    }
}
