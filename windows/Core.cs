// C# bindings for the shared core (core/pq_core.h, loaded from pq_core.dll next to PQ.exe).
// One process = one core instance; strings cross the boundary as UTF-8.
using System;
using System.Runtime.InteropServices;
using System.Text;

static class Core
{
    const string D = "pq_core.dll";
    const CallingConvention CC = CallingConvention.Cdecl;
    public const int ApiVersion = 1;

    // state
    public const int Idle = 0, Focus = 1, Away = 2, Break = 3;
    // events from Activate / Tick
    public const int EvChanged = 0x01, EvAwayStart = 0x02, EvAwayEnd = 0x04, EvFocusDone = 0x08,
                     EvAwayTimeout = 0x10, EvBreakSoon = 0x20, EvBreakEnd = 0x40;
    public const int RangeToday = 0, RangeAll = -1;

    static IntPtr c;

    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static double Now { get { return (DateTime.UtcNow - Epoch).TotalSeconds; } }
    public static DateTime Local(double t) { return Epoch.AddSeconds(t).ToLocalTime(); }

    static byte[] U(string s) { return Encoding.UTF8.GetBytes((s ?? "") + "\0"); }
    static string S(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        int n = 0;
        while (Marshal.ReadByte(p, n) != 0) n++;
        var b = new byte[n];
        Marshal.Copy(p, b, 0, n);
        return Encoding.UTF8.GetString(b);
    }

    // ---------- raw imports ----------
    [DllImport(D, CallingConvention = CC)] static extern int pq_api_version();
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_open(byte[] dir, int ko, double now);
    [DllImport(D, CallingConvention = CC)] static extern void pq_close(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_log_path(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_lang_ko(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern void pq_set_lang_ko(IntPtr c, int ko);
    [DllImport(D, CallingConvention = CC)] static extern double pq_opacity(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern void pq_set_opacity(IntPtr c, double v);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_tr(IntPtr c, byte[] key);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_fmt(IntPtr c, byte[] key, byte[] a0, byte[] a1, byte[] a2);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_fmt_min(IntPtr c, double m);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_fmt_clock(double s);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_fmt_age(IntPtr c, double m);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_outcome_label(IntPtr c, byte[] o);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_fmt_stamp(double t);

    [DllImport(D, CallingConvention = CC)] static extern int pq_count(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_id_at(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern int pq_index_of(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_text(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern int pq_group(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern int pq_win_count(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern long pq_win_id(IntPtr c, int id, int k);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_win_proc(IntPtr c, int id, int k);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_win_title(IntPtr c, int id, int k);
    [DllImport(D, CallingConvention = CC)] static extern long pq_last_win(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern void pq_set_win_id(IntPtr c, int id, int k, long w);
    [DllImport(D, CallingConvention = CC)] static extern int pq_add(IntPtr c, byte[] text, int top, double now);
    [DllImport(D, CallingConvention = CC)] static extern void pq_link(IntPtr c, int id, long w, byte[] proc, byte[] title, int replace);
    [DllImport(D, CallingConvention = CC)] static extern void pq_remove(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern void pq_rename(IntPtr c, int id, byte[] text);
    [DllImport(D, CallingConvention = CC)] static extern void pq_reset_age(IntPtr c, int id, double now);
    [DllImport(D, CallingConvention = CC)] static extern void pq_move(IntPtr c, int id, int to, int live);
    [DllImport(D, CallingConvention = CC)] static extern int pq_join(IntPtr c, int id, int target, out int created);
    [DllImport(D, CallingConvention = CC)] static extern void pq_leave(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_group_name(IntPtr c, int g);
    [DllImport(D, CallingConvention = CC)] static extern void pq_set_group_name(IntPtr c, int g, byte[] name);
    [DllImport(D, CallingConvention = CC)] static extern int pq_group_size(IntPtr c, int g);
    [DllImport(D, CallingConvention = CC)] static extern int pq_first_of_group(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern int pq_last_of_group(IntPtr c, int i);

    [DllImport(D, CallingConvention = CC)] static extern int pq_activate(IntPtr c, long w, byte[] title, double now);
    [DllImport(D, CallingConvention = CC)] static extern int pq_item_of(IntPtr c, long w);
    [DllImport(D, CallingConvention = CC)] static extern int pq_has(IntPtr c, int id, long w);
    [DllImport(D, CallingConvention = CC)] static extern int pq_ghas(IntPtr c, int id, long w);
    [DllImport(D, CallingConvention = CC)] static extern int pq_active(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern int pq_needs_entry(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern double pq_age_min(IntPtr c, int id, double now);
    [DllImport(D, CallingConvention = CC)] static extern int pq_heat(IntPtr c, int id, double now);

    [DllImport(D, CallingConvention = CC)] static extern int pq_state(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_tier_count();
    [DllImport(D, CallingConvention = CC)] static extern int pq_tier_focus(int t);
    [DllImport(D, CallingConvention = CC)] static extern int pq_tier_break(int t);
    [DllImport(D, CallingConvention = CC)] static extern int pq_sel_tier(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern void pq_step_tier(IntPtr c, int d);
    [DllImport(D, CallingConvention = CC)] static extern void pq_reset_rounds(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern int pq_start(IntPtr c, long[] wins, int n, int test, double now);
    [DllImport(D, CallingConvention = CC)] static extern void pq_stop(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern int pq_in_focus(IntPtr c, int id);
    [DllImport(D, CallingConvention = CC)] static extern int pq_try_switch(IntPtr c, int id, double now);
    [DllImport(D, CallingConvention = CC)] static extern long pq_away_home(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_tick(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern int pq_needs_tick(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern double pq_focus_left(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern double pq_focus_progress(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern double pq_away_left(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern double pq_break_left(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern double pq_break_progress(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern int pq_break_hidden(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern void pq_break_hide(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern void pq_break_show(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern void pq_break_skip(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern int pq_can_extend(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern void pq_extend(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_note(IntPtr c, double now);
    [DllImport(D, CallingConvention = CC)] static extern int pq_note_warn(IntPtr c);

    [DllImport(D, CallingConvention = CC)] static extern void pq_stats(IntPtr c, int days, double now);
    [DllImport(D, CallingConvention = CC)] static extern double pq_st_total(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_sessions(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_done(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_extensions(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_left(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_days(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_hourly(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_bucket_count(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern double pq_st_bucket_min(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_st_bucket_label(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_st_bucket_tick(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_task_count(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_st_task_name(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_st_task_group(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern double pq_st_task_min(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_task_sessions(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_row_count(IntPtr c);
    [DllImport(D, CallingConvention = CC)] static extern double pq_st_row_start(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_st_row_task(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_st_row_group(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern double pq_st_row_min(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern int pq_st_row_planned(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_st_row_kind(IntPtr c, int i);
    [DllImport(D, CallingConvention = CC)] static extern IntPtr pq_st_row_outcome(IntPtr c, int i);

    // ---------- wrappers ----------
    public static string Open(string dir, bool systemKorean)
    {
        int v;
        try { v = pq_api_version(); }
        catch (DllNotFoundException) { return "pq_core.dll was not found next to PQ.exe."; }
        if (v != ApiVersion) return "pq_core.dll is version " + v + ", PQ.exe needs " + ApiVersion + ". Run build.bat again.";
        c = pq_open(U(dir), systemKorean ? 1 : 0, Now);
        return c == IntPtr.Zero ? "pq_core could not start." : null;
    }
    public static void Close() { if (c != IntPtr.Zero) { pq_close(c, Now); c = IntPtr.Zero; } }
    public static string LogPath { get { return S(pq_log_path(c)); } }
    public static bool Ko { get { return pq_lang_ko(c) != 0; } set { pq_set_lang_ko(c, value ? 1 : 0); } }
    public static double Opacity { get { return pq_opacity(c); } set { pq_set_opacity(c, value); } }

    public static string Tr(string key) { return S(pq_tr(c, U(key))); }
    public static string Fmt(string key, object a0, object a1 = null, object a2 = null)
    {
        return S(pq_fmt(c, U(key), Arg(a0), Arg(a1), Arg(a2)));
    }
    static byte[] Arg(object a) { return a == null ? null : U(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)); }
    public static string FmtMin(double m) { return S(pq_fmt_min(c, m)); }
    public static string Clock(double s) { return S(pq_fmt_clock(s)); }
    public static string Age(double m) { return S(pq_fmt_age(c, m)); }
    public static string OutcomeLabel(string o) { return S(pq_outcome_label(c, U(o))); }
    public static string Stamp(double t) { return S(pq_fmt_stamp(t)); }

    public static int Count { get { return pq_count(c); } }
    public static int IdAt(int i) { return pq_id_at(c, i); }
    public static int IndexOf(int id) { return pq_index_of(c, id); }
    public static string Text(int id) { return S(pq_text(c, id)); }
    public static int Group(int id) { return pq_group(c, id); }
    public static int WinCount(int id) { return pq_win_count(c, id); }
    public static IntPtr WinId(int id, int k) { return (IntPtr)pq_win_id(c, id, k); }
    public static string WinProc(int id, int k) { return S(pq_win_proc(c, id, k)); }
    public static string WinTitle(int id, int k) { return S(pq_win_title(c, id, k)); }
    public static IntPtr LastWin(int id) { return (IntPtr)pq_last_win(c, id); }
    public static void SetWinId(int id, int k, IntPtr w) { pq_set_win_id(c, id, k, w.ToInt64()); }
    public static int Add(string text, bool top) { return pq_add(c, U(text), top ? 1 : 0, Now); }
    public static void Link(int id, IntPtr w, string proc, string title, bool replace) { pq_link(c, id, w.ToInt64(), U(proc), U(title), replace ? 1 : 0); }
    public static void Remove(int id) { pq_remove(c, id); }
    public static void Rename(int id, string text) { pq_rename(c, id, U(text)); }
    public static void ResetAge(int id) { pq_reset_age(c, id, Now); }
    public static void Move(int id, int to, bool live) { pq_move(c, id, to, live ? 1 : 0); }
    public static int Join(int id, int target, out bool created) { int cr; int g = pq_join(c, id, target, out cr); created = cr != 0; return g; }
    public static void Leave(int id) { pq_leave(c, id); }
    public static string GroupName(int g) { return S(pq_group_name(c, g)); }
    public static void SetGroupName(int g, string name) { pq_set_group_name(c, g, U(name)); }
    public static int GroupSize(int g) { return pq_group_size(c, g); }
    public static bool FirstOfGroup(int i) { return pq_first_of_group(c, i) != 0; }
    public static bool LastOfGroup(int i) { return pq_last_of_group(c, i) != 0; }

    public static int Activate(IntPtr w, string title) { return pq_activate(c, w.ToInt64(), U(title), Now); }
    public static int ItemOf(IntPtr w) { return pq_item_of(c, w.ToInt64()); }
    public static bool Has(int id, IntPtr w) { return pq_has(c, id, w.ToInt64()) != 0; }
    public static bool GHas(int id, IntPtr w) { return pq_ghas(c, id, w.ToInt64()) != 0; }
    public static bool Active(int id) { return pq_active(c, id) != 0; }
    public static bool NeedsEntry { get { return pq_needs_entry(c) != 0; } }
    public static double AgeMin(int id) { return pq_age_min(c, id, Now); }
    public static int Heat(int id) { return pq_heat(c, id, Now); }

    public static int State { get { return pq_state(c); } }
    public static int TierCount { get { return pq_tier_count(); } }
    public static int TierFocus(int t) { return pq_tier_focus(t); }
    public static int TierBreak(int t) { return pq_tier_break(t); }
    public static int SelTier { get { return pq_sel_tier(c); } }
    public static void StepTier(int d) { pq_step_tier(c, d); }
    public static void ResetRounds(int id) { pq_reset_rounds(c, id); }
    public static bool Start(IntPtr[] wins, bool test)
    {
        var a = new long[wins.Length];
        for (int i = 0; i < a.Length; i++) a[i] = wins[i].ToInt64();
        return pq_start(c, a, a.Length, test ? 1 : 0, Now) != 0;
    }
    public static void Stop() { pq_stop(c, Now); }
    public static bool InFocus(int id) { return pq_in_focus(c, id) != 0; }
    public static bool TrySwitch(int id) { return pq_try_switch(c, id, Now) != 0; }
    public static IntPtr AwayHome { get { return (IntPtr)pq_away_home(c); } }
    public static int Tick() { return pq_tick(c, Now); }
    public static bool NeedsTick { get { return pq_needs_tick(c, Now) != 0; } }
    public static double FocusLeft { get { return pq_focus_left(c, Now); } }
    public static double FocusProgress { get { return pq_focus_progress(c, Now); } }
    public static double AwayLeft { get { return pq_away_left(c, Now); } }
    public static double BreakLeft { get { return pq_break_left(c, Now); } }
    public static double BreakProgress { get { return pq_break_progress(c, Now); } }
    public static bool BreakHidden { get { return pq_break_hidden(c) != 0; } }
    public static void BreakHide() { pq_break_hide(c, Now); }
    public static void BreakShow() { pq_break_show(c); }
    public static void BreakSkip() { pq_break_skip(c, Now); }
    public static bool CanExtend { get { return pq_can_extend(c) != 0; } }
    public static void Extend() { pq_extend(c, Now); }
    public static string Note { get { return S(pq_note(c, Now)); } }
    public static bool NoteWarn { get { return pq_note_warn(c) != 0; } }

    // stats
    public static void Stats(int days) { pq_stats(c, days, Now); }
    public static double StTotal { get { return pq_st_total(c); } }
    public static int StSessions { get { return pq_st_sessions(c); } }
    public static int StDone { get { return pq_st_done(c); } }
    public static int StExtensions { get { return pq_st_extensions(c); } }
    public static int StLeft { get { return pq_st_left(c); } }
    public static int StDays { get { return pq_st_days(c); } }
    public static bool StHourly { get { return pq_st_hourly(c) != 0; } }
    public static int StBucketCount { get { return pq_st_bucket_count(c); } }
    public static double StBucketMin(int i) { return pq_st_bucket_min(c, i); }
    public static string StBucketLabel(int i) { return S(pq_st_bucket_label(c, i)); }
    public static string StBucketTick(int i) { return S(pq_st_bucket_tick(c, i)); }
    public static int StTaskCount { get { return pq_st_task_count(c); } }
    public static string StTaskName(int i) { return S(pq_st_task_name(c, i)); }
    public static string StTaskGroup(int i) { return S(pq_st_task_group(c, i)); }
    public static double StTaskMin(int i) { return pq_st_task_min(c, i); }
    public static int StTaskSessions(int i) { return pq_st_task_sessions(c, i); }
    public static int StRowCount { get { return pq_st_row_count(c); } }
    public static double StRowStart(int i) { return pq_st_row_start(c, i); }
    public static string StRowTask(int i) { return S(pq_st_row_task(c, i)); }
    public static string StRowGroup(int i) { return S(pq_st_row_group(c, i)); }
    public static double StRowMin(int i) { return pq_st_row_min(c, i); }
    public static int StRowPlanned(int i) { return pq_st_row_planned(c, i); }
    public static string StRowKind(int i) { return S(pq_st_row_kind(c, i)); }
    public static string StRowOutcome(int i) { return S(pq_st_row_outcome(c, i)); }
}
