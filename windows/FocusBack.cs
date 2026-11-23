// FocusBack for Windows - the WinForms frontend. All rules and state live in the shared core
// (core/pq_core.h via Core.cs); this file only tracks windows, draws, and handles input.
// Build: build.bat (no installs: the C# compiler ships with Windows, the core DLL is downloaded)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// flicker-free owner-drawn panel
class DBPanel : Panel
{
    public DBPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}

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

// the inline rename box is a child of the list; without WS_CLIPCHILDREN the list's repaint
// draws over it and only the caret is left (looks like a stray ' before the name)
class ClipListBox : ListBox
{
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.Style |= 0x02000000; return cp; } // WS_CLIPCHILDREN
    }
    // every row paints its own background, so skip the erase-to-blank that made the list flash on each repaint
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x14 /*WM_ERASEBKGND*/ && Items.Count > 0) { m.Result = (IntPtr)1; return; }
        base.WndProc(ref m);
    }
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
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    const int WM_HOTKEY = 0x0312;

    static string Tr(string key) { return Core.Tr(key); }

    // first installed font wins (Korean-friendly first)
    static Font Pick(float size, params string[] names)
    {
        foreach (var n in names)
        {
            try { var ff = new FontFamily(n); return new Font(ff, size); } catch { }
        }
        return new Font("Malgun Gothic", size);
    }

    readonly Label header;
    readonly TextBox input;
    readonly Panel inputBox;
    readonly Font ageFont, bigFont, midFont;
    readonly ContextMenuStrip menu, hmenu;
    readonly ListBox list;          // holds item ids, in the core's order
    readonly Timer timer;           // repaint every 20 s (ages)
    readonly Timer tick;            // 250 ms while the core needs ticking
    readonly ToolTip tip;
    readonly DBPanel bar;
    readonly float s;               // DPI scale
    readonly int row, doneW;

    int dragIndex = -1, tipIndex = -1, dropTick;
    Point dragStart;
    int dragItem;                   // id of the card currently lifted (0 = none)
    int mergeTarget;                // id of the card it would be grouped with if dropped now

    TextBox editBox;
    Action<string> editCommit;
    int[] heights = new int[0];     // row heights computed by Sync()
    int HdrH { get { return (int)(26 * s); } }   // section header above the first card
    int SecPad { get { return (int)(7 * s); } }  // section padding below the last card / at the sides

    // last window the user was in that is not this widget (event-driven, no polling)
    static readonly uint MyPid = (uint)Process.GetCurrentProcess().Id;
    IntPtr lastExternal;
    IntPtr hook;
    readonly WinEventProc hookProc; // keep delegate alive

    // pastel palette
    static readonly Color Bg = Color.FromArgb(250, 247, 242);
    static readonly Color Ink = Color.FromArgb(58, 54, 72);
    static readonly Color Muted = Color.FromArgb(160, 150, 170);
    static readonly Color InputBg = Color.White;
    static readonly Color GreenFill = Color.FromArgb(198, 238, 208), GreenDeep = Color.FromArgb(70, 155, 95);
    static readonly Color RedFill = Color.FromArgb(255, 168, 168), RedDeep = Color.FromArgb(210, 80, 85);
    static readonly Color GrayDeep = Color.FromArgb(150, 145, 160);
    static readonly Color[] GroupColors = {
        Color.FromArgb(170, 150, 215), Color.FromArgb(240, 160, 120), Color.FromArgb(110, 170, 220), Color.FromArgb(120, 190, 160) };
    static Color GroupColor(int g) { return GroupColors[(Math.Max(1, g) - 1) % GroupColors.Length]; }

    static Color Lerp(Color a, Color b, float t)
    {
        return Color.FromArgb(a.R + (int)((b.R - a.R) * t), a.G + (int)((b.G - a.G) * t), a.B + (int)((b.B - a.B) * t));
    }

    Rectangle minusR, plusR, goR, stopR, skipR, extR, bgR, backR, quitR;
    List<Overlay> breakForms;       // the break screen (one per monitor) while a break runs
    Toast awayForm, soonForm;

    PQForm()
    {
        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        row = (int)(34 * s);
        doneW = (int)(12 * s);
        ageFont = Pick(8.5f, "Pretendard SemiBold", "Noto Sans KR Medium", "Segoe UI Semibold");
        bigFont = Pick(40f, "Pretendard SemiBold", "Noto Sans KR Medium", "Segoe UI Semibold");
        midFont = Pick(13f, "Pretendard SemiBold", "Noto Sans KR Medium", "Malgun Gothic");

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        Opacity = Core.Opacity;
        Padding = new Padding((int)(10 * s), (int)(6 * s), (int)(10 * s), (int)(10 * s));
        Font = Pick(9.5f, "Pretendard Medium", "Pretendard", "Noto Sans KR Medium", "Malgun Gothic");
        Width = (int)(280 * s);

        header = new Label
        {
            Dock = DockStyle.Top,
            Height = (int)(22 * s),
            Text = HeaderText(),
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
        input = new TextBox { BorderStyle = BorderStyle.None, BackColor = InputBg, ForeColor = Ink, Font = Font };
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
        input.LostFocus += (o, e) => { forceGhost = false; BeginInvoke((Action)UpdateGhost); };
        input.KeyDown += InputKeyDown;
        input.HandleCreated += (o, e) => SetCue();

        list = new ClipListBox
        {
            Dock = DockStyle.Top,
            DrawMode = DrawMode.OwnerDrawVariable,
            BorderStyle = BorderStyle.None,
            BackColor = Bg,
            IntegralHeight = false,
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
            if (dragItem != 0 || Environment.TickCount - dropTick < 300) { dragIndex = -1; return; }
            dragIndex = e.Button == MouseButtons.Left ? list.IndexFromPoint(e.Location) : -1;
            dragStart = e.Location;
        };
        list.MouseMove += ListMouseMove;
        list.MouseUp += ListMouseUp;
        list.MouseLeave += (o, e) => { tip.Hide(list); tipIndex = -1; };
        list.KeyDown += ListKeyDown;

        tip = new ToolTip { InitialDelay = 400, ShowAlways = true };

        menu = new ContextMenuStrip { Font = Pick(9f, "Pretendard", "Noto Sans KR", "Segoe UI", "Malgun Gothic") };
        hmenu = new ContextMenuStrip { Font = menu.Font };
        header.ContextMenuStrip = hmenu;
        menu.Opening += (o, e) => { if (leaveItem != null) leaveItem.Visible = Core.Group(Selected) != 0; };
        BuildMenus();

        // focus timer bar at the bottom
        bar = new DBPanel { Dock = DockStyle.Top, Height = row + (int)(4 * s), BackColor = Bg };
        bar.Paint += PaintBar;
        bar.MouseDown += BarMouseDown;
        bar.MouseMove += (o, e) =>
        {
            bool hot = minusR.Contains(e.Location) || plusR.Contains(e.Location) || goR.Contains(e.Location) || stopR.Contains(e.Location);
            bar.Cursor = hot ? Cursors.Hand : Cursors.Default;
        };
        bar.MouseWheel += (o, e) => { if (Core.State == Core.Idle) { Core.StepTier(e.Delta > 0 ? 1 : -1); bar.Invalidate(); } };
        tick = new Timer { Interval = 250 };
        tick.Tick += OnTick;

        // all Dock=Top: last added ends up topmost -> header, list, ghost entry, focus bar
        Controls.Add(bar);
        Controls.Add(inputBox);
        Controls.Add(list);
        Controls.Add(header);

        for (int i = 0; i < Core.Count; i++) ResolveAll(Core.IdAt(i)); // re-find the windows after a restart
        Sync();
        PlaceDefault();
        // monitor unplugged / resolution changed: pull the widget back if it ended up off-screen
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (o, e) => BeginInvoke((Action)EnsureOnScreen);
        // running FocusBack.exe again brings this one back instead of doing nothing
        var showEvt = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "FocusBack-show" + InstanceTag);
        new System.Threading.Thread(() =>
        {
            while (true) { showEvt.WaitOne(); try { BeginInvoke((Action)(() => { PlaceDefault(); Show(); Activate(); })); } catch { return; } }
        }) { IsBackground = true }.Start();
        // "FocusBack.exe --stats" opens the stats window of the running widget
        var statsEvt = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "FocusBack-stats" + InstanceTag);
        new System.Threading.Thread(() =>
        {
            while (true) { statsEvt.WaitOne(); try { BeginInvoke((Action)OpenStats); } catch { return; } }
        }) { IsBackground = true }.Start();

        timer = new Timer { Interval = 20000 };
        timer.Tick += (o, e) => list.Invalidate();
        timer.Start();

        hookProc = OnForegroundChanged;
        hook = SetWinEventHook(3 /*EVENT_SYSTEM_FOREGROUND*/, 3, IntPtr.Zero, hookProc, 0, 0, 0 /*OUTOFCONTEXT*/);
        Track(GetForegroundWindow());
    }

    int Selected { get { return list.SelectedIndex >= 0 ? Core.IdAt(list.SelectedIndex) : 0; } }

    // ---------- window tracking ----------

    void OnForegroundChanged(IntPtr hk, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time) { Track(hwnd); }

    void Track(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        h = GetAncestor(h, 3 /*GA_ROOTOWNER: dialogs/popups count as their main window*/);
        if (h == IntPtr.Zero || h == Handle) return;
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid == MyPid) return; // our own menus/tooltips/cards
        string cls = ClassOf(h);
        if (cls == "Shell_TrayWnd" || cls == "Progman" || cls == "WorkerW" || cls == "Shell_SecondaryTrayWnd"
            || cls == "Windows.UI.Core.CoreWindow" || cls == "XamlExplorerHostIslandWindow") return; // taskbar/desktop/start/alt-tab
        if (h == lastExternal) return;
        IntPtr prev = lastExternal;
        lastExternal = h;
        OnCore(Core.Activate(h, TitleOf(h)));
        // came back to one window of a task -> bring its partners up too (just below it, no focus change)
        for (int i = 0; i < Core.Count; i++)
        {
            int id = Core.IdAt(i);
            if (!Core.Has(id, h) || Core.Has(id, prev)) continue;
            for (int k = 0; k < Core.WinCount(id); k++)
            {
                IntPtr w = Core.WinId(id, k);
                if (w == h || w == IntPtr.Zero || !IsWindow(w)) continue;
                if (IsIconic(w)) ShowWindow(w, 4 /*SW_SHOWNOACTIVATE*/);
                SetWindowPos(w, h, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x4000 /*NOSIZE|NOMOVE|NOACTIVATE|ASYNC*/);
            }
        }
        UpdateGhost();
        bar.Invalidate();
        list.Invalidate();
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

        var above = new List<Rectangle> { rh };
        long covered = Area(rh);
        bool past = false;
        EnumWindows((w, l) => // top -> bottom
        {
            if (w == h) { past = true; return true; }
            if (!IsAppWindow(w, self)) return true;
            Rectangle r = Rectangle.Intersect(RectOf(w), screen);
            if (Area(r) < Area(screen) * 15 / 100) { if (past) above.Add(r); return true; }
            if (!past) { above.Add(r); return true; }
            long hidden = 0;
            foreach (var a in above) hidden += Area(Rectangle.Intersect(a, r));
            if (hidden <= Area(r) / 10) { group.Add(w); covered += Area(r); }
            above.Add(r);
            return covered < Area(screen) * 95 / 100 && group.Count < 4;
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

    // link the item to the window you were in + whatever is snapped beside it
    void Attach(int id, IntPtr h)
    {
        if (id == 0 || h == IntPtr.Zero || !IsWindow(h)) return;
        bool first = true;
        foreach (var w in SnapGroup(h)) { Core.Link(id, w, ProcOf(w), TitleOf(w), first); first = false; }
    }

    void AddWindow(int id, IntPtr h)
    {
        if (id == 0 || h == IntPtr.Zero || !IsWindow(h) || Core.Has(id, h)) return;
        Core.Link(id, h, ProcOf(h), TitleOf(h), false);
        Changed();
    }

    // re-find windows: same handle if still alive, else (after restart) exact title, else same app.
    // never map two links onto the same window.
    void ResolveAll(int id)
    {
        var used = new HashSet<IntPtr>();
        var pending = new List<int>();
        int n = Core.WinCount(id);
        for (int k = 0; k < n; k++)
        {
            IntPtr w = Core.WinId(id, k);
            string proc = Core.WinProc(id, k);
            if (w != IntPtr.Zero && IsWindow(w) && !used.Contains(w) && (proc == "" || ProcOf(w) == proc)) used.Add(w);
            else pending.Add(k);
        }
        foreach (int k in pending)
        {
            string proc = Core.WinProc(id, k), title = Core.WinTitle(id, k);
            IntPtr found = IntPtr.Zero;
            if (proc != "")
            {
                IntPtr byTitle = IntPtr.Zero, byProc = IntPtr.Zero;
                EnumWindows((h, l) =>
                {
                    if (used.Contains(h) || !IsWindowVisible(h)) return true;
                    string t = TitleOf(h);
                    if (t.Length == 0 || ProcOf(h) != proc) return true;
                    if (t == title) { byTitle = h; return false; }
                    if (byProc == IntPtr.Zero) byProc = h;
                    return true;
                }, IntPtr.Zero);
                found = byTitle != IntPtr.Zero ? byTitle : byProc;
            }
            Core.SetWinId(id, k, found);
            if (found != IntPtr.Zero) used.Add(found);
        }
    }

    // bring all of the task's windows forward; focus the one you used last
    void SwitchTo(int id)
    {
        if (id == 0) return;
        ResolveAll(id);
        IntPtr last = Core.LastWin(id), focus = IntPtr.Zero;
        var wins = new List<IntPtr>();
        for (int k = 0; k < Core.WinCount(id); k++)
        {
            IntPtr w = Core.WinId(id, k);
            if (w == IntPtr.Zero) continue;
            wins.Add(w);
            if (IsIconic(w)) ShowWindow(w, 9 /*SW_RESTORE*/);
            if (focus == IntPtr.Zero || w == last) focus = w;
        }
        if (focus == IntPtr.Zero) { System.Media.SystemSounds.Beep.Play(); return; }
        foreach (var w in wins)
            if (w != focus) SetWindowPos(w, IntPtr.Zero /*HWND_TOP*/, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
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
        Core.Close(); // logs a running session as "quit" and saves
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

    // ---------- core events ----------

    // react to what the core reports (from Activate / Tick / button actions)
    void OnCore(int ev)
    {
        if ((ev & Core.EvAwayStart) != 0) ShowAway();
        if ((ev & (Core.EvAwayEnd | Core.EvAwayTimeout)) != 0 && awayForm != null) awayForm.Hide();
        if ((ev & Core.EvAwayTimeout) != 0) System.Media.SystemSounds.Exclamation.Play();
        if ((ev & Core.EvFocusDone) != 0) ShowBreakScreen();
        if ((ev & Core.EvBreakSoon) != 0) ShowSoon();
        if ((ev & Core.EvBreakEnd) != 0) { CloseBreakScreen(); System.Media.SystemSounds.Asterisk.Play(); BackToWork(); }
        if ((ev & (Core.EvChanged | Core.EvFocusDone | Core.EvAwayTimeout)) != 0) Sync();
        Kick(ev != 0);
    }

    // the 250 ms tick only moves the bar's countdown; the list repaints when something actually changed
    void Kick(bool list = true)
    {
        if (Core.NeedsTick) tick.Start();
        bar.Invalidate();
        if (list) this.list.Invalidate();
    }

    void OnTick(object o, EventArgs e)
    {
        OnCore(Core.Tick());
        if (Core.State == Core.Away && awayForm != null) awayForm.Invalidate();
        if (soonForm != null && soonForm.Visible) soonForm.Invalidate();
        if (breakForms != null && !Core.BreakHidden)
            foreach (var f in breakForms)
            {
                SetWindowPos(f.Handle, (IntPtr)(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // stay on top
                f.Invalidate();
            }
        if (!Core.NeedsTick) tick.Stop();
    }

    // ---------- input ----------

    void InputKeyDown(object o, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            string t = input.Text.Trim();
            if (t.Length == 0) return;
            int id = Core.Add(t, e.Shift); // Shift+Enter = top
            // while focusing, a task jotted down from the task's own window is a note for later:
            // linking it to that window would make it part of the focus (and show it as the focused one)
            if (!Core.Owns(lastExternal)) Attach(id, lastExternal);
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
        else if (e.KeyCode == Keys.Enter) { TrySwitch(Selected); e.Handled = true; }
        else if (e.Alt && e.KeyCode == Keys.Up) { MoveSelected(-1); e.Handled = true; }
        else if (e.Alt && e.KeyCode == Keys.Down) { MoveSelected(1); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { BackToWork(); e.Handled = true; }
    }

    void ListMouseMove(object o, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && (dragItem != 0 || dragIndex >= 0))
        {
            if (dragItem == 0)
            {
                if (Math.Abs(e.Y - dragStart.Y) < SystemInformation.DragSize.Height) return;
                dragItem = Core.IdAt(dragIndex); // lift
                dragIndex = -1;
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
            int cur = Core.IndexOf(dragItem);
            int merge = 0;
            if (!outside && to != cur)
            {
                var rr = list.GetItemRectangle(to);
                float frac = (e.Y - rr.Y) / (float)rr.Height;
                if (to > cur ? frac < 0.6f : frac > 0.4f) merge = Core.IdAt(to);
            }
            if (merge != mergeTarget) { mergeTarget = merge; list.Invalidate(); }
            if (merge == 0 && to != cur)
            {
                Core.Move(dragItem, to, true);
                Sync();
                list.SelectedIndex = to;
            }
            return;
        }
        if (dragItem != 0) { var d = dragItem; dragItem = 0; mergeTarget = 0; list.Cursor = Cursors.Default; Core.Move(d, Core.IndexOf(d), false); Changed(); } // released elsewhere
        int i = list.IndexFromPoint(e.Location);
        if (i == tipIndex) return;
        tipIndex = i;
        if (i < 0) { tip.Hide(list); return; }
        int id = Core.IdAt(i);
        var where = new StringBuilder();
        for (int k = 0; k < Core.WinCount(id); k++) where.Append("\n→ ").Append(Core.WinProc(id, k)).Append(" — ").Append(Core.WinTitle(id, k));
        if (Core.WinCount(id) == 0) where.Append("\n→ " + Tr("item.no_window"));
        tip.Show(Core.Text(id) + where, list, e.X + (int)(12 * s), e.Y + (int)(16 * s), 5000);
    }

    void ListMouseUp(object o, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            int ri = list.IndexFromPoint(e.Location);
            if (ri < 0) return;
            list.SelectedIndex = ri;
            menu.Show(list, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (dragItem != 0) // drop
        {
            int d = dragItem;
            dragItem = 0;
            bool created = false; int gid = 0;
            if (mergeTarget != 0) gid = Core.Join(d, mergeTarget, out created); // dropped onto a card -> group
            mergeTarget = 0;
            Core.Move(d, Core.IndexOf(d), false); // final drop: fix up groups, save
            dragIndex = -1;
            dropTick = Environment.TickCount;
            list.Cursor = Cursors.Default;
            Changed();
            int sel = Core.IndexOf(d);
            if (sel >= 0) list.SelectedIndex = sel;
            if (created && Core.Group(d) != 0) { int g = Core.Group(d); BeginInvoke((Action)(() => RenameGroup(g))); } // name the new section
            return;
        }
        int i = list.IndexFromPoint(e.Location);
        bool click = dragIndex >= 0 && i == dragIndex;
        dragIndex = -1;
        if (!click) return;
        int id = Core.IdAt(i);
        if ((ModifierKeys & Keys.Shift) != 0) { list.SelectedIndex = i; RemoveSelected(); }          // Shift+click = done
        else if ((ModifierKeys & Keys.Control) != 0) { }                                             // Ctrl+click = just select
        else if (Core.FirstOfGroup(i) && e.Y < list.GetItemRectangle(i).Top + HdrH) RenameGroup(Core.Group(id)); // section header
        else if (Core.Has(id, lastExternal)) RenameItem(i);   // the task you're already in -> rename it
        else TrySwitch(id);
    }

    // clicking another item while focusing: the core warns once (note on the bar), a second click goes
    void TrySwitch(int id)
    {
        if (id == 0) return;
        if (!Core.TrySwitch(id)) { System.Media.SystemSounds.Exclamation.Play(); Kick(); return; }
        SwitchTo(id); // leaving starts the 1-minute grace
    }

    void MoveSelected(int delta)
    {
        int i = list.SelectedIndex;
        if (i < 0) return;
        int to = Math.Max(0, Math.Min(list.Items.Count - 1, i + delta));
        if (to == i) return;
        int id = Core.IdAt(i);
        Core.Move(id, to, false);
        Changed();
        list.SelectedIndex = Core.IndexOf(id);
    }

    void RemoveSelected()
    {
        int i = list.SelectedIndex;
        if (i < 0) return;
        Core.Remove(Core.IdAt(i));
        tipIndex = -1; tip.Hide(list);
        Changed();
        if (list.Items.Count > 0) list.SelectedIndex = Math.Min(i, list.Items.Count - 1);
    }

    // ---------- drawing ----------

    void DrawItem(object o, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        using (var bgb = new SolidBrush(Bg)) g.FillRectangle(bgb, e.Bounds);
        if (e.Index < 0 || e.Index >= Core.Count) return;

        int id = Core.IdAt(e.Index);
        bool active = Core.Active(id);          // whole group shows "working"
        int heat = Core.Heat(id);               // one step redder per 10 min, 0 while frozen
        float t = Math.Min(6, heat) / 6f;
        Color fill = active ? GreenFill : Lerp(Color.White, RedFill, t);
        Color deep = active ? GreenDeep : Lerp(GrayDeep, RedDeep, t);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        bool lifted = id == dragItem;
        int gid = Core.Group(id);

        // section: one rounded tinted box around the whole group; each row paints its slice of it
        if (gid != 0)
        {
            Color gc = GroupColor(gid);
            var sr = SectionRect(e.Index);
            var clip = g.Clip;
            g.SetClip(e.Bounds);
            using (var p = Round(sr, (int)(13 * s)))
            {
                using (var b = new SolidBrush(Lerp(Bg, gc, 0.16f))) g.FillPath(b, p);
                using (var pen = new Pen(Lerp(Bg, gc, 0.45f), 1.2f * s)) g.DrawPath(pen, p);
            }
            if (Core.FirstOfGroup(e.Index))
            {
                var hr = new Rectangle(sr.X + (int)(12 * s), sr.Y + (int)(1 * s), sr.Width - (int)(24 * s), HdrH - (int)(4 * s));
                string name = Core.GroupName(gid);
                var hf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                if (editCommit == null || !editBox.Visible || editBox.Top > hr.Bottom || editBox.Bottom < hr.Top)
                {
                    TextRenderer.DrawText(g, name, ageFont, hr, Lerp(gc, Ink, 0.45f), hf);
                    int nw = TextRenderer.MeasureText(g, name, ageFont, Size.Empty, (hf & ~TextFormatFlags.EndEllipsis) | TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, "· " + Core.GroupSize(gid), ageFont, new Rectangle(hr.X + nw + (int)(9 * s), hr.Y, (int)(40 * s), hr.Height), Lerp(gc, Bg, 0.25f), hf);
                }
            }
            g.Clip = clip;
        }
        var r = CardRect(CardBounds(e.Index, e.Bounds));
        if (lifted)
        {
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
            if (id == mergeTarget) // "drop here to group"
            {
                Color mc = gid != 0 ? GroupColor(gid) : GroupColor(1);
                using (var pen = new Pen(mc, 2.4f * s) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) g.DrawPath(pen, path);
            }
        }

        // age badge on the right
        bool focusing = Core.State == Core.Focus || Core.State == Core.Away;
        string age = active ? Tr(focusing ? "item.focusing" : "item.working") : Core.Age(Core.AgeMin(id));
        var tf = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        Size asz = TextRenderer.MeasureText(g, age, ageFont, Size.Empty, tf);
        int bh = (int)(18 * s), bw = asz.Width + (int)(12 * s);
        var badge = new Rectangle(r.Right - bw - (int)(6 * s), r.Y + (r.Height - bh) / 2, bw, bh);
        using (var path = Round(badge, bh / 2))
        using (var b = new SolidBrush(Lerp(fill, deep, 0.12f))) g.FillPath(b, path);
        TextRenderer.DrawText(g, age, ageFont, badge, deep, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | tf);

        // index + text
        var txtR = new Rectangle(r.X + doneW, r.Y, badge.X - r.X - doneW - (int)(6 * s), r.Height);
        TextRenderer.DrawText(g, (e.Index + 1) + "   " + Core.Text(id), list.Font, txtR, Ink,
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
            if (sc.WorkingArea.Contains(new Point(me.X + me.Width / 2, me.Y + 10))) return;
        PlaceDefault();
    }

    void Changed() { Sync(); UpdateGhost(); Kick(); }

    // mirror the core's queue in the ListBox (ids in order) and re-measure rows when sections change
    void Sync()
    {
        int n = Core.Count;
        var ids = new object[n];
        var h = new int[n];
        for (int i = 0; i < n; i++)
        {
            ids[i] = Core.IdAt(i);
            h[i] = row + (Core.FirstOfGroup(i) ? HdrH : 0) + (Core.LastOfGroup(i) ? SecPad : 0);
        }
        bool same = n == list.Items.Count && h.Length == heights.Length;
        for (int i = 0; same && i < n; i++) same = h[i] == heights[i] && (int)list.Items[i] == (int)ids[i];
        if (!same)
        {
            heights = h;
            int selId = Selected, top = list.TopIndex;
            list.BeginUpdate();
            list.Items.Clear();
            list.Items.AddRange(ids);
            int si = selId != 0 ? Core.IndexOf(selId) : -1;
            if (si >= 0) list.SelectedIndex = si;
            if (n > 0) list.TopIndex = Math.Min(top, n - 1);
            list.EndUpdate();
        }
        Fit();
        list.Invalidate();
    }

    int RowHeight(int i) { return i >= 0 && i < heights.Length ? heights[i] : row; }

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

    bool forceGhost;       // hotkey: show the entry even in an already-linked window
    bool ghostShown = true;

    // ghost entry only when the window you're in isn't linked to any item (or nothing is queued yet)
    void UpdateGhost()
    {
        if (list == null || inputBox == null) return;
        bool show = forceGhost || input.Focused || input.TextLength > 0 || Core.NeedsEntry;
        if (ghostShown != show) { ghostShown = show; inputBox.Visible = show; Fit(); }
        LayoutGhost();
    }

    static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int rad)
    {
        int d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
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

    // section rectangle (list coordinates) of the group containing row i
    Rectangle SectionRect(int i)
    {
        int g = Core.Group(Core.IdAt(i));
        int a = i, b = i;
        while (a > 0 && Core.Group(Core.IdAt(a - 1)) == g) a--;
        while (b + 1 < Core.Count && Core.Group(Core.IdAt(b + 1)) == g) b++;
        Rectangle ra = list.GetItemRectangle(a), rb = list.GetItemRectangle(b);
        int top = ra.Top + (Core.FirstOfGroup(a) ? (int)(3 * s) : 0);
        return Rectangle.FromLTRB(0, top, list.Width - 1, rb.Bottom - (Core.LastOfGroup(b) ? (int)(1 * s) : 0));
    }

    // where the card sits inside its row (rows of a group leave room for the section frame)
    Rectangle CardBounds(int i, Rectangle b)
    {
        if (Core.Group(Core.IdAt(i)) == 0) return b;
        int top = Core.FirstOfGroup(i) ? HdrH : 0, bot = Core.LastOfGroup(i) ? SecPad : 0;
        int pad = SecPad;
        return new Rectangle(b.X + pad, b.Y + top, b.Width - pad * 2, b.Height - top - bot);
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
        int id = Core.IdAt(i);
        var r = CardRect(CardBounds(i, list.GetItemRectangle(i)));
        int x = r.X + doneW + TextRenderer.MeasureText((i + 1) + "   ", list.Font).Width - (int)(3 * s);
        BeginEdit(new Rectangle(x, r.Y, r.Right - x - (int)(70 * s), r.Height), Core.Text(id), GreenFill,
            t => { Core.Rename(id, t); Changed(); });
    }

    void RenameGroup(int g)
    {
        int first = -1;
        for (int i = 0; i < Core.Count; i++) if (Core.Group(Core.IdAt(i)) == g) { first = i; break; }
        if (first < 0) return;
        var sr = SectionRect(first);
        var rc = new Rectangle(sr.X + (int)(12 * s), sr.Y + (int)(2 * s), sr.Width - (int)(60 * s), HdrH - (int)(5 * s));
        BeginEdit(rc, Core.GroupName(g), Lerp(Bg, GroupColor(g), 0.16f), t => { Core.SetGroupName(g, t); list.Invalidate(); });
    }

    // ---------- focus bar ----------

    void StartFocus(bool test)
    {
        IntPtr h = lastExternal;
        if (h == IntPtr.Zero || !IsWindow(h) || !Core.Start(SnapGroup(h).ToArray(), test)) { System.Media.SystemSounds.Beep.Play(); return; }
        Kick();
        BackToWork();
    }

    void BarMouseDown(object o, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int st = Core.State;
        if (st == Core.Break) { if (Core.BreakHidden) { Core.BreakShow(); ShowBreakForms(); bar.Invalidate(); } return; } // bring the break screen back
        if (st == Core.Focus || st == Core.Away)
        {
            if (stopR.Contains(e.Location)) { Core.Stop(); if (awayForm != null) awayForm.Hide(); Sync(); Kick(); }
            return;
        }
        if (minusR.Contains(e.Location)) { Core.StepTier(-1); bar.Invalidate(); }
        else if (plusR.Contains(e.Location)) { Core.StepTier(1); bar.Invalidate(); }
        else if (goR.Contains(e.Location)) StartFocus((ModifierKeys & Keys.Control) != 0); // Ctrl+click = 10 s test run
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
        string note = Core.Note;
        bool warn = note != null && Core.NoteWarn;
        var lf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        int ph = (int)(20 * s), py = r.Y + (r.Height - ph) / 2;
        Color lav = Color.FromArgb(232, 226, 246), lavDeep = Color.FromArgb(125, 105, 170);
        int st = Core.State;

        using (var card = Round(r, (int)(9 * s)))
        {
            if (st == Core.Break)
            {
                using (var b = new SolidBrush(lav)) g.FillPath(b, card);
                string txt = Core.Fmt(Core.BreakHidden ? "bar.break_hidden" : "bar.break", Core.Clock(Core.BreakLeft));
                TextRenderer.DrawText(g, txt, ageFont, new Rectangle(r.X + doneW, r.Y, r.Width - doneW * 2, r.Height), lavDeep, lf);
                return;
            }
            if (st == Core.Focus || st == Core.Away)
            {
                bool away = st == Core.Away;
                float done = (float)Math.Max(0, Math.Min(1, Core.FocusProgress));
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, card);
                var clip = g.Clip;
                g.SetClip(card);
                using (var b = new SolidBrush(GreenFill)) g.FillRectangle(b, r.X, r.Y, (int)(r.Width * done), r.Height + 1);
                g.Clip = clip;
                using (var pen = new Pen(warn || away ? RedDeep : GreenDeep, 1.4f * s)) g.DrawPath(pen, card);
                int sw = (int)(54 * s);
                stopR = new Rectangle(r.Right - sw - (int)(6 * s), py, sw, ph);
                Pill(g, stopR, Color.FromArgb(235, 232, 240), GrayDeep, Tr("bar.stop"), ageFont);
                string txt = note ?? (away ? Core.Fmt("bar.paused", Core.Clock(Core.AwayLeft)) : Core.Fmt("bar.focus_left", Core.Clock(Core.FocusLeft)));
                TextRenderer.DrawText(g, txt, ageFont, new Rectangle(r.X + doneW, r.Y, stopR.X - r.X - doneW - (int)(4 * s), r.Height),
                    warn || away ? RedDeep : GreenDeep, lf);
                return;
            }
            // idle: [<] 30 min [>]  R1 · 3m rest   [▶ Start]
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, card);
            using (var pen = new Pen(Color.FromArgb(215, 208, 222), 1f * s)) g.DrawPath(pen, card);
            int gw = (int)(58 * s);
            goR = new Rectangle(r.Right - gw - (int)(6 * s), py, gw, ph);
            Pill(g, goR, GreenFill, GreenDeep, Tr("bar.start"), ageFont);
            if (note != null)
            {
                TextRenderer.DrawText(g, note, ageFont, new Rectangle(r.X + doneW, r.Y, goR.X - r.X - doneW - (int)(4 * s), r.Height),
                    warn ? RedDeep : GreenDeep, lf);
                return;
            }
            int tier = Core.SelTier;
            int bs = ph, x = r.X + (int)(8 * s);
            minusR = new Rectangle(x, py, bs, bs);
            Pill(g, minusR, Color.FromArgb(240, 236, 244), tier > 0 ? GrayDeep : Color.FromArgb(205, 200, 212), "<", ageFont);
            int lw = (int)(44 * s);
            TextRenderer.DrawText(g, Core.Fmt("bar.minutes", Core.TierFocus(tier)), Font, new Rectangle(minusR.Right, r.Y, lw, r.Height), Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            plusR = new Rectangle(minusR.Right + lw, py, bs, bs);
            TextRenderer.DrawText(g, Core.Fmt("bar.round", tier + 1, Core.TierBreak(tier)),
                ageFont, new Rectangle(plusR.Right + (int)(6 * s), r.Y, goR.X - plusR.Right - (int)(8 * s), r.Height), Muted, lf);
            Pill(g, plusR, Color.FromArgb(240, 236, 244), tier < Core.TierCount - 1 ? GrayDeep : Color.FromArgb(205, 200, 212), ">", ageFont);
        }
    }

    // ---------- 1-minute grace card ----------

    void ShowAway()
    {
        if (awayForm == null)
        {
            awayForm = new Toast { BackColor = Bg, Size = new Size((int)(340 * s), (int)(168 * s)) };
            IntPtr rgn = CreateRoundRectRgn(0, 0, awayForm.Width + 1, awayForm.Height + 1, (int)(22 * s), (int)(22 * s));
            awayForm.Region = Region.FromHrgn(rgn); DeleteObject(rgn);
            awayForm.Paint += PaintAway;
            awayForm.MouseDown += (o, e) =>
            {
                if (backR.Contains(e.Location)) { IntPtr h = Core.AwayHome; if (h != IntPtr.Zero && IsWindow(h)) SetForegroundWindow(h); }
                else if (quitR.Contains(e.Location)) { Core.Stop(); awayForm.Hide(); Sync(); Kick(); }
            };
            awayForm.MouseMove += (o, e) => awayForm.Cursor = backR.Contains(e.Location) || quitR.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }
        var wa = Screen.PrimaryScreen.WorkingArea;
        awayForm.Location = new Point(wa.X + (wa.Width - awayForm.Width) / 2, wa.Y + (wa.Height - awayForm.Height) / 2);
        awayForm.Show();
        awayForm.Invalidate();
        System.Media.SystemSounds.Exclamation.Play();
    }

    void PaintAway(object o, PaintEventArgs e)
    {
        var f = (Form)o; var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Bg);
        using (var p = Round(new Rectangle(0, 0, f.Width - 1, f.Height - 1), (int)(11 * s)))
        using (var pen = new Pen(RedFill, 2.4f * s)) g.DrawPath(pen, p);
        var cf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, Tr("away.title"), midFont, new Rectangle(0, (int)(14 * s), f.Width, (int)(28 * s)), RedDeep, cf);
        using (var big = new Font(bigFont.FontFamily, 26f))
            TextRenderer.DrawText(g, Core.Clock(Core.AwayLeft), big, new Rectangle(0, (int)(42 * s), f.Width, (int)(50 * s)), Ink, cf);
        TextRenderer.DrawText(g, Tr("away.body"), Font, new Rectangle(0, (int)(92 * s), f.Width, (int)(22 * s)), Muted, cf);
        int bw = (int)(120 * s), bh = (int)(26 * s), y = f.Height - bh - (int)(14 * s), gap = (int)(10 * s);
        backR = new Rectangle(f.Width / 2 - bw - gap / 2, y, bw, bh);
        quitR = new Rectangle(f.Width / 2 + gap / 2, y, bw, bh);
        Pill(g, backR, GreenFill, GreenDeep, Tr("away.back"), ageFont);
        Pill(g, quitR, Color.FromArgb(235, 232, 240), GrayDeep, Tr("away.stop"), ageFont);
    }

    // ---------- break screen ----------

    void ShowBreakScreen()
    {
        if (awayForm != null) awayForm.Hide();
        if (breakForms == null)
        {
            breakForms = new List<Overlay>();
            foreach (var sc in Screen.AllScreens)
            {
                var f = new Overlay { Bounds = sc.Bounds, BackColor = Color.FromArgb(52, 46, 68) }; // opaque: actually rest
                if (sc.Primary)
                {
                    f.Paint += PaintBreak;
                    f.MouseDoubleClick += (o, e) => { if (skipR.Contains(e.Location)) { Core.BreakSkip(); CloseBreakScreen(); BackToWork(); Kick(); } }; // emergency exit
                    f.MouseDown += (o, e) =>
                    {
                        if (e.Button != MouseButtons.Left) return;
                        if (extR.Contains(e.Location)) { Core.Extend(); CloseBreakScreen(); Kick(); BackToWork(); }
                        else if (bgR.Contains(e.Location))
                        {
                            Core.BreakHide();
                            foreach (var x in breakForms) x.Hide();
                            OnCore(Core.Tick()); // <= 10 s left: the "almost over" card right away
                        }
                    };
                    f.MouseMove += (o, e) => f.Cursor = skipR.Contains(e.Location) || extR.Contains(e.Location) || bgR.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
                }
                breakForms.Add(f);
            }
        }
        ShowBreakForms();
        System.Media.SystemSounds.Asterisk.Play();
        Kick();
    }

    void ShowBreakForms()
    {
        if (breakForms == null) return;
        if (soonForm != null) soonForm.Hide();
        foreach (var f in breakForms) { var b = f.Bounds; f.Show(); f.Bounds = b; }
    }

    void CloseBreakScreen()
    {
        if (soonForm != null) soonForm.Hide();
        if (breakForms == null) return;
        var fs = breakForms;
        breakForms = null;
        foreach (var f in fs) { f.AllowClose = true; f.Close(); f.Dispose(); }
    }

    void ShowSoon()
    {
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
        TextRenderer.DrawText(g, Tr("soon.title"), midFont, new Rectangle(0, (int)(12 * s), f.Width, (int)(28 * s)), lavDeep, cf);
        using (var big = new Font(bigFont.FontFamily, 26f))
            TextRenderer.DrawText(g, Core.Clock(Core.BreakLeft), big, new Rectangle(0, (int)(40 * s), f.Width, (int)(50 * s)), Ink, cf);
        TextRenderer.DrawText(g, Tr("soon.body"), ageFont, new Rectangle(0, (int)(94 * s), f.Width, (int)(22 * s)), Muted, cf);
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
        TextRenderer.DrawText(g, Tr("break.title"), midFont, new Rectangle(c.X, c.Y + (int)(26 * s), c.Width, (int)(30 * s)), lavDeep, cf);
        TextRenderer.DrawText(g, Core.Clock(Core.BreakLeft), bigFont, new Rectangle(c.X, c.Y + (int)(62 * s), c.Width, (int)(80 * s)), Ink, cf);
        TextRenderer.DrawText(g, Tr("break.body"), Font, new Rectangle(c.X, c.Y + (int)(150 * s), c.Width, (int)(24 * s)), Muted, cf);
        // "+5 min focus" + "rest in the background" buttons
        int ebw = (int)(150 * s), ebh = (int)(30 * s), egap = (int)(12 * s), ey = c.Y + (int)(188 * s);
        if (!Core.CanExtend)
        {
            extR = Rectangle.Empty;
            bgR = new Rectangle(c.X + (c.Width - ebw) / 2, ey, ebw, ebh);
        }
        else
        {
            extR = new Rectangle(c.X + c.Width / 2 - ebw - egap / 2, ey, ebw, ebh);
            bgR = new Rectangle(c.X + c.Width / 2 + egap / 2, ey, ebw, ebh);
            Pill(g, extR, GreenFill, GreenDeep, Core.Fmt("break.extend", 5), Font);
        }
        Pill(g, bgR, Color.FromArgb(235, 232, 240), GrayDeep, Tr("break.background"), Font);
        // progress
        var pr = new Rectangle(c.X + (int)(50 * s), c.Bottom - (int)(40 * s), c.Width - (int)(100 * s), (int)(8 * s));
        float done = (float)Math.Max(0, Math.Min(1, Core.BreakProgress));
        using (var p = Round(pr, pr.Height / 2))
        using (var b = new SolidBrush(Color.FromArgb(232, 226, 246))) g.FillPath(b, p);
        if (done > 0.01f)
        {
            var pd = new Rectangle(pr.X, pr.Y, Math.Max(pr.Height, (int)(pr.Width * done)), pr.Height);
            using (var p = Round(pd, pr.Height / 2))
            using (var b = new SolidBrush(Color.FromArgb(180, 160, 220))) g.FillPath(b, p);
        }
        // small emergency exit at the bottom of the screen
        string skip = Tr("break.skip");
        Size sz = TextRenderer.MeasureText(skip, ageFont);
        skipR = new Rectangle((f.ClientSize.Width - sz.Width) / 2 - (int)(10 * s), f.ClientSize.Height - (int)(70 * s), sz.Width + (int)(20 * s), (int)(28 * s));
        TextRenderer.DrawText(g, skip, ageFont, skipR, Color.FromArgb(120, 112, 140), cf);
    }

    // ---------- menus, language, opacity ----------

    static string HeaderText() { return "✿  " + Tr("header.title") + "    Ctrl+Alt+Space"; }

    void SetCue() { if (input.IsHandleCreated) SendMessage(input.Handle, 0x1501, (IntPtr)1, Tr("entry.placeholder")); }

    ToolStripMenuItem leaveItem;

    void BuildMenus()
    {
        menu.Items.Clear(); hmenu.Items.Clear();
        menu.Items.Add(Tr("menu.go"), null, (o, e) => TrySwitch(Selected));
        menu.Items.Add(Tr("menu.done"), null, (o, e) => RemoveSelected());
        menu.Items.Add(Tr("menu.link_prev"), null, (o, e) => AddWindow(Selected, lastExternal));
        menu.Items.Add(Tr("menu.relink"), null, (o, e) => { int id = Selected; if (id != 0) { Attach(id, lastExternal); Changed(); } });
        leaveItem = new ToolStripMenuItem(Tr("menu.leave_group"), null, (o, e) => { int id = Selected; if (id != 0) { Core.Leave(id); Changed(); } });
        menu.Items.Add(leaveItem);
        menu.Items.Add(Tr("menu.to_top"), null, (o, e) => MoveSelected(-list.SelectedIndex));
        menu.Items.Add(Tr("menu.reset_timer"), null, (o, e) => { int id = Selected; if (id != 0) { Core.ResetAge(id); Changed(); } });
        menu.Items.Add(Tr("menu.reset_rounds"), null, (o, e) => { int id = Selected; if (id != 0) { Core.ResetRounds(id); Changed(); } });
        menu.Items.Add(new ToolStripSeparator());
        foreach (var m in new[] { menu, hmenu })
        {
            m.Items.Add(Tr("menu.stats"), null, (o, e) => OpenStats());
            m.Items.Add(Tr("menu.open_log"), null, (o, e) => OpenLogSheet());
            m.Items.Add(OpacityMenu());
            var lm = new ToolStripMenuItem(Tr("menu.language"));
            lm.DropDownItems.Add(new ToolStripMenuItem("English", null, (o, e) => SetLang(false)) { Checked = !Core.Ko });
            lm.DropDownItems.Add(new ToolStripMenuItem("한국어", null, (o, e) => SetLang(true)) { Checked = Core.Ko });
            m.Items.Add(lm);
            m.Items.Add(Tr("menu.quit"), null, (o, e) => Close());
        }
    }

    void SetLang(bool ko)
    {
        Core.Ko = ko;
        header.Text = HeaderText();
        SetCue();
        BuildMenus();
        Invalidate(true); bar.Invalidate(); list.Invalidate();
        if (stats != null && !stats.IsDisposed) stats.Reload();
    }

    ToolStripMenuItem OpacityMenu()
    {
        var m = new ToolStripMenuItem(Tr("menu.opacity"));
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
        Core.Opacity = v;      // clamped and saved by the core
        Opacity = Core.Opacity;
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

    // ---------- log sheet + stats window ----------

    StatsForm stats;

    void OpenStats()
    {
        if (stats != null && !stats.IsDisposed) { stats.Reload(); stats.Activate(); return; }
        stats = new StatsForm(this);
        stats.Show();
    }

    void OpenLogSheet()
    {
        try { Process.Start(Core.LogPath); }
        catch (Exception ex) { MessageBox.Show(ex.Message, Tr("log.sheet")); }
    }

    class StatsForm : Form
    {
        readonly PQForm o;
        readonly float s;
        readonly Font fTitle, fBig, fText, fSmall;
        int range = 7; // days, Core.RangeToday or Core.RangeAll
        Rectangle closeR, sheetR;
        readonly List<KeyValuePair<int, Rectangle>> chips = new List<KeyValuePair<int, Rectangle>>();
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
            Text = Tr("stats.title");
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

        public void Reload() { Core.Stats(range); Text = Tr("stats.title"); Invalidate(); }

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
            int n = Core.StSessions, done = Core.StDone, ext = Core.StExtensions, left = Core.StLeft, days = Core.StDays;
            double total = Core.StTotal;

            // header: title, range chips, close
            string label = Tr(range == Core.RangeToday ? "stats.range.today" : range == 7 ? "stats.range.7" : range == 30 ? "stats.range.30" : "stats.range.all");
            TextRenderer.DrawText(g, Tr("stats.title"), fTitle, new Rectangle(M, S(16), S(300), S(28)), Ink, L);
            TextRenderer.DrawText(g, Core.Fmt("stats.subtitle", label, n), fSmall, new Rectangle(M, S(42), S(300), S(18)), Muted, L);
            closeR = new Rectangle(W - M - S(28), S(20), S(28), S(28));
            using (var p = Round(closeR, closeR.Height / 2))
            using (var b = new SolidBrush(Color.FromArgb(240, 236, 244))) g.FillPath(b, p);
            TextRenderer.DrawText(g, "✕", fText, closeR, GrayDeep, C);
            int cx = closeR.X - S(14);
            var chipDefs = new[] { new KeyValuePair<int, string>(Core.RangeAll, "stats.chip.all"), new KeyValuePair<int, string>(30, "stats.chip.30"),
                                   new KeyValuePair<int, string>(7, "stats.chip.7"), new KeyValuePair<int, string>(Core.RangeToday, "stats.chip.today") };
            foreach (var kv in chipDefs)
            {
                string t = Tr(kv.Value);
                int cw = Math.Max(S(44), TextRenderer.MeasureText(t, fSmall).Width + S(18));
                var cr = new Rectangle(cx - cw, S(22), cw, S(24));
                cx -= cw + S(6);
                bool on = range == kv.Key;
                using (var p = Round(cr, cr.Height / 2))
                using (var b = new SolidBrush(on ? Ink : Color.FromArgb(240, 236, 244))) g.FillPath(b, p);
                TextRenderer.DrawText(g, t, fSmall, cr, on ? Color.White : Ink2, C);
                chips.Add(new KeyValuePair<int, Rectangle>(kv.Key, cr));
            }

            // KPI tiles
            int ky = S(72), kh = S(78), gap = S(10), kw = (W - M * 2 - gap * 3) / 4;
            string[][] k = {
                new[] { Tr("stats.kpi.total"), n > 0 ? Core.FmtMin(total) : "–", days > 1 ? Core.Fmt("stats.kpi.avg_day", Core.FmtMin(total / days)) : "" },
                new[] { Tr("stats.kpi.sessions"), n > 0 ? n.ToString() : "–", ext > 0 ? Core.Fmt("stats.kpi.ext", ext) : "" },
                new[] { Tr("stats.kpi.completed"), n > 0 ? (done * 100 / n) + "%" : "–", n > 0 ? Core.Fmt("stats.kpi.done_of", done, n) : "" },
                new[] { Tr("stats.kpi.avg"), n > 0 ? Core.FmtMin(total / n) : "–", left > 0 ? Core.Fmt("stats.kpi.left", left) : "" } };
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
            DrawChart(g, chartR);
            DrawTasks(g, taskR);

            // recent sessions
            var tr = new Rectangle(M, ry + rh + gap, W - M * 2, H - (ry + rh + gap) - M);
            CardAt(g, tr);
            DrawTable(g, tr);
        }

        void DrawChart(Graphics g, Rectangle c)
        {
            string title = Tr(Core.StHourly ? "stats.chart.hourly" : "stats.chart.daily");
            TextRenderer.DrawText(g, title, fText, new Rectangle(c.X + S(14), c.Y + S(8), c.Width - S(28), S(24)), Ink, L);
            int nb = Core.StBucketCount;
            if (nb == 0) return;
            var vals = new double[nb];
            for (int i = 0; i < nb; i++) vals[i] = Core.StBucketMin(i);
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
            float cw = (R0 - L0) / (float)nb, bw = Math.Max(2f, Math.Min(26f * s, cw - 2f * s));
            for (int i = 0; i < nb; i++)
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
                string tick = Core.StBucketTick(i);
                if (!string.IsNullOrEmpty(tick))
                    TextRenderer.DrawText(g, tick, fSmall, new Rectangle((int)(x + bw / 2) - S(24), B0 + S(4), S(48), S(18)), Muted, C);
                hover.Add(new KeyValuePair<Rectangle, string>(new Rectangle((int)(L0 + i * cw), T0, (int)Math.Ceiling(cw), B0 - T0),
                    Core.StBucketLabel(i) + " · " + (vals[i] > 0 ? Core.FmtMin(vals[i]) : Tr("stats.chart.none"))));
            }
        }

        void DrawTasks(Graphics g, Rectangle c)
        {
            TextRenderer.DrawText(g, Tr("stats.tasks"), fText, new Rectangle(c.X + S(14), c.Y + S(8), c.Width - S(28), S(24)), Ink, L);
            int nt = Core.StTaskCount;
            if (nt == 0)
            {
                TextRenderer.DrawText(g, Tr("stats.tasks.empty"), fSmall, c, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            double max = 0;
            for (int i = 0; i < nt; i++) max = Math.Max(max, Core.StTaskMin(i));
            int y = c.Y + S(40), rowH = S(31), x = c.X + S(14), w = c.Width - S(28);
            for (int i = 0; i < nt; i++)
            {
                double m = Core.StTaskMin(i);
                string val = Core.FmtMin(m);
                int vw = TextRenderer.MeasureText(val, fSmall).Width;
                string name = Core.StTaskName(i), grp = Core.StTaskGroup(i);
                if (string.IsNullOrEmpty(name)) name = Tr("stats.no_task");
                int nw = Math.Min(TextRenderer.MeasureText(name, fText).Width, w - vw - S(8));
                TextRenderer.DrawText(g, name, fText, new Rectangle(x - S(3), y, nw + S(4), S(18)), Ink, L);
                if (!string.IsNullOrEmpty(grp))
                    TextRenderer.DrawText(g, grp, fSmall, new Rectangle(x + nw + S(2), y, Math.Max(0, w - vw - nw - S(8)), S(18)), Muted, L);
                TextRenderer.DrawText(g, val, fSmall, new Rectangle(x, y, w, S(18)), Ink2, Rt);
                var track = new Rectangle(x, y + S(20), w, S(6));
                using (var p = Round(track, track.Height / 2))
                using (var b = new SolidBrush(Grid)) g.FillPath(b, p);
                var fill = new Rectangle(x, track.Y, Math.Max(track.Height, (int)(w * m / Math.Max(max, 0.001))), track.Height);
                using (var p = Round(fill, track.Height / 2))
                using (var b = new SolidBrush(Bar)) g.FillPath(b, p);
                hover.Add(new KeyValuePair<Rectangle, string>(new Rectangle(x, y, w, rowH),
                    Core.Fmt("stats.tasks.tip", name, val, Core.StTaskSessions(i))));
                y += rowH;
            }
        }

        void DrawTable(Graphics g, Rectangle c)
        {
            TextRenderer.DrawText(g, Tr("stats.recent"), fText, new Rectangle(c.X + S(14), c.Y + S(8), S(200), S(24)), Ink, L);
            string link = Tr("stats.open_log");
            int lw = TextRenderer.MeasureText(link, fSmall).Width + S(8);
            sheetR = new Rectangle(c.Right - S(14) - lw, c.Y + S(10), lw, S(20));
            TextRenderer.DrawText(g, link, fSmall, sheetR, GreenDeep, C);
            int x = c.X + S(14), w = c.Width - S(28);
            int[] cols = { 0, S(92), S(330), S(470), S(560) };
            string[] heads = { Tr("stats.col.start"), Tr("stats.col.task"), Tr("stats.col.group"), Tr("stats.col.focus"), Tr("stats.col.result") };
            int y = c.Y + S(38);
            for (int i = 0; i < heads.Length; i++)
                TextRenderer.DrawText(g, heads[i], fSmall, new Rectangle(x + cols[i], y, S(120), S(20)), Muted, L);
            using (var pen = new Pen(Line, 1f * s)) g.DrawLine(pen, x, y + S(22), x + w, y + S(22));
            y += S(26);
            int rowH = S(24), maxRows = Math.Max(0, (c.Bottom - S(8) - y) / rowH);
            int nr = Core.StRowCount;
            if (nr == 0)
            {
                TextRenderer.DrawText(g, Tr("stats.empty"), fSmall, new Rectangle(x, y, w, S(40)), Muted, C);
                return;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < Math.Min(maxRows, nr); i++)
            {
                string outcome = Core.StRowOutcome(i), task = Core.StRowTask(i);
                int planned = Core.StRowPlanned(i);
                string mins = Core.StRowMin(i).ToString("0.0", inv);
                string[] v = {
                    Core.Stamp(Core.StRowStart(i)), string.IsNullOrEmpty(task) ? Tr("stats.no_task") : task, Core.StRowGroup(i),
                    planned > 0 ? Core.Fmt("stats.row.min", mins, planned) : mins,
                    Core.OutcomeLabel(outcome) + (Core.StRowKind(i) == "extension" ? Tr("stats.row.ext") : "") };
                for (int j = 0; j < v.Length; j++)
                {
                    int cw2 = (j + 1 < cols.Length ? cols[j + 1] : w) - cols[j] - S(8);
                    Color col = j == 4 ? (outcome == "done" ? GreenDeep : outcome == "left" ? RedDeep : Ink2) : j == 0 || j == 3 ? Ink2 : Ink;
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
            foreach (var kv in chips) if (kv.Value.Contains(e.Location)) { range = kv.Key; Reload(); return; }
            if (e.Y < S(64)) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } // drag by the header
        }

        void OnMove(object x, MouseEventArgs e)
        {
            bool hand = closeR.Contains(e.Location) || sheetR.Contains(e.Location);
            foreach (var kv in chips) if (kv.Value.Contains(e.Location)) hand = true;
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            string t = null;
            foreach (var kv in hover) if (kv.Key.Contains(e.Location)) { t = kv.Value; break; }
            if (t == tipShown) return;
            tipShown = t;
            if (t == null) tt.Hide(this); else tt.Show(t, this, e.X + S(12), e.Y - S(28), 4000);
        }
    }

    // ---------- startup ----------

    // data folder; `--data <dir>` runs a separate instance on another folder (e.g. a demo or a portable copy)
    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string DataDir = Path.Combine(AppData, "FocusBack");

    // versions before FocusBack kept their data in %APPDATA%\ADHD-PQ: move it over once
    static void MoveOldData()
    {
        string old = Path.Combine(AppData, "ADHD-PQ");
        try { if (!Directory.Exists(DataDir) && Directory.Exists(old)) Directory.Move(old, DataDir); }
        catch { if (!Directory.Exists(DataDir)) DataDir = old; } // in use: keep using the old folder this time
    }
    static string InstanceTag = "";

    [STAThread]
    static void Main(string[] args)
    {
        int di = Array.IndexOf(args, "--data");
        if (di >= 0 && di + 1 < args.Length)
        {
            DataDir = Path.GetFullPath(args[di + 1]);
            InstanceTag = "-" + ((uint)DataDir.ToLowerInvariant().GetHashCode()).ToString("x8");
        }
        bool wantStats = Array.IndexOf(args, "--stats") >= 0;
        bool created;
        using (var mtx = new System.Threading.Mutex(true, "FocusBack-singleton" + InstanceTag, out created))
        {
            if (!created) // already running: ask it to come back on screen (or open its stats)
            {
                try { System.Threading.EventWaitHandle.OpenExisting((wantStats ? "FocusBack-stats" : "FocusBack-show") + InstanceTag).Set(); } catch { }
                return;
            }
            if (di < 0) MoveOldData();
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string err = Core.Open(DataDir, System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko");
            if (err != null) { MessageBox.Show(err, "FocusBack"); return; }
            var form = new PQForm();
            if (wantStats) form.Shown += (o, e) => form.OpenStats();
            Application.Run(form);
        }
    }
}
