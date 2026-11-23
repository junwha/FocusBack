/*
 * Unit tests for pq_core: files (round trip, old formats, backup, unreadable file, log migration),
 * the focus / away / break state machine, groups, stats and string formatting.
 *
 * Usage: test_core <empty-temp-dir>
 *   macOS/Linux: cc -std=c11 -o test_core core/tests/test_core.c core/pq_core.c core/pq_strings.c -lm
 *                ./test_core "$(mktemp -d)"
 *   Windows:     cl /nologo /utf-8 /W3 core\tests\test_core.c core\pq_core.c core\pq_strings.c /Fe:test_core.exe
 *                mkdir %TEMP%\pq_test & test_core.exe %TEMP%\pq_test
 * Exit code 0 = all passed.
 */
#ifdef _WIN32
#  define _CRT_SECURE_NO_WARNINGS
#  include <direct.h>
#  define MKDIR(p) _mkdir(p)
#else
#  include <sys/stat.h>
#  define MKDIR(p) mkdir(p, 0755)
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>
#include "../pq_core.h"

static int failures = 0, checks = 0;
#define CHECK(cond) do { checks++; if (!(cond)) { failures++; printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); } } while (0)
#define CHECK_STR(a, b) do { const char* a_ = (a); const char* b_ = (b); checks++; \
    if (!a_ || strcmp(a_, b_) != 0) { failures++; printf("FAIL %s:%d: \"%s\" != \"%s\"\n", __FILE__, __LINE__, a_ ? a_ : "(null)", b_); } } while (0)
#define CHECK_NEAR(a, b, eps) do { double a_ = (a), b_ = (b); checks++; \
    if (fabs(a_ - b_) > (eps)) { failures++; printf("FAIL %s:%d: %f != %f\n", __FILE__, __LINE__, a_, b_); } } while (0)

static char base_dir[1024];

static const char* dir(const char* name)
{
    static char buf[1200];
    snprintf(buf, sizeof buf, "%s/%s", base_dir, name);
    MKDIR(buf);
    return buf;
}

static const char* path(const char* d, const char* file)
{
    static char buf[1400];
    snprintf(buf, sizeof buf, "%s/%s", d, file);
    return buf;
}

static char* slurp(const char* p)
{
    FILE* f = fopen(p, "rb");
    long n;
    char* s;
    if (!f) return NULL;
    fseek(f, 0, SEEK_END); n = ftell(f); fseek(f, 0, SEEK_SET);
    s = (char*)malloc((size_t)n + 1);
    if (!s) { fclose(f); return NULL; }
    n = (long)fread(s, 1, (size_t)n, f);
    s[n] = 0;
    fclose(f);
    return s;
}

static void spit(const char* p, const char* text)
{
    FILE* f = fopen(p, "wb");
    if (!f) return;
    fputs(text, f);
    fclose(f);
}

static int exists(const char* p) { FILE* f = fopen(p, "rb"); if (f) { fclose(f); return 1; } return 0; }

static int count_lines(const char* p)
{
    char* s = slurp(p);
    int n = 0;
    char* q;
    if (!s) return 0;
    for (q = s; *q; q++) if (*q == '\n') n++;
    free(s);
    return n;
}

/* a fixed local time: 2026-03-04 (Wednesday) at h:m */
static double local_time(int y, int mo, int d, int h, int mi)
{
    struct tm tm;
    memset(&tm, 0, sizeof tm);
    tm.tm_year = y - 1900; tm.tm_mon = mo - 1; tm.tm_mday = d;
    tm.tm_hour = h; tm.tm_min = mi; tm.tm_isdst = -1;
    return (double)mktime(&tm);
}

static void stamp(char* out, size_t sz, double t)
{
    time_t tt = (time_t)t;
    struct tm* tm = localtime(&tt);
    snprintf(out, sz, "%04d-%02d-%02d %02d:%02d:%02d", tm->tm_year + 1900, tm->tm_mon + 1, tm->tm_mday, tm->tm_hour, tm->tm_min, tm->tm_sec);
}

/* ---------------------------------------------------------------------------------------- */

static void test_strings(void)
{
    const char* d = dir("strings");
    pq* c = pq_open(d, 0, 0);
    CHECK_STR(pq_tr(c, "bar.start"), "▶ Start");
    CHECK_STR(pq_tr(c, "no.such.key"), "no.such.key");
    CHECK_STR(pq_fmt(c, "bar.round", "2", "5", NULL), "R2 · 5m rest");
    CHECK_STR(pq_fmt_min(c, 45), "45 min");
    CHECK_STR(pq_fmt_min(c, 90), "1h 30m");
    CHECK_STR(pq_fmt_min(c, 120), "2h");
    CHECK_STR(pq_fmt_clock(65.2), "1:06");
    CHECK_STR(pq_fmt_clock(-3), "0:00");
    CHECK_STR(pq_fmt_age(c, 7.9), "7m");
    CHECK_STR(pq_fmt_age(c, 133), "2h13");
    CHECK_STR(pq_outcome_label(c, "quit"), "closed");
    CHECK_STR(pq_weekday(c, local_time(2026, 3, 4, 12, 0)), "Wed");
    CHECK_STR(pq_fmt_stamp(local_time(2026, 3, 4, 9, 5)), "3/4 09:05");
    CHECK_STR(pq_group_name(c, 7), "Group");
    pq_set_lang_ko(c, 1);
    CHECK(pq_lang_ko(c) == 1);
    CHECK_STR(pq_fmt_min(c, 90), "1시간 30분");
    CHECK_STR(pq_fmt(c, "bar.round", "2", "5", NULL), "2회차 · 휴식 5분");
    CHECK_STR(pq_weekday(c, local_time(2026, 3, 4, 12, 0)), "수");
    CHECK_STR(pq_outcome_label(c, "done"), "완료");
    CHECK(pq_opacity(c) > 0.94 && pq_opacity(c) < 0.96);
    pq_set_opacity(c, 0.123);
    CHECK_NEAR(pq_opacity(c), 0.3, 1e-9);
    pq_set_opacity(c, 0.8);
    pq_close(c, 0);
    c = pq_open(d, 0, 0); /* lang.txt / opacity.txt win over the system language */
    CHECK(pq_lang_ko(c) == 1);
    CHECK_NEAR(pq_opacity(c), 0.8, 1e-9);
    pq_close(c, 0);
    c = pq_open(dir("strings_ko"), 1, 0);
    CHECK(pq_lang_ko(c) == 1);
    pq_close(c, 0);
}

static void test_round_trip(void)
{
    const char* d = dir("roundtrip");
    double t0 = local_time(2026, 3, 4, 10, 0) + 0.25;
    int a, b, g, created = 0;
    pq* c = pq_open(d, 0, t0);
    char* s;
    a = pq_add(c, "Task A", 0, t0);
    pq_link(c, a, 101, "app.one", "Window\tone", 1);
    pq_link(c, a, 102, "app.two", "Window two", 0);
    pq_link(c, a, 102, "app.two", "dup", 0); /* duplicate ignored */
    b = pq_add(c, "Task B", 1, t0 + 60);
    CHECK(pq_index_of(c, b) == 0);
    g = pq_join(c, b, a, &created);
    CHECK(created == 1 && g > 0);
    CHECK(pq_index_of(c, a) == 0 && pq_index_of(c, b) == 1);
    pq_set_group_name(c, g, "Example group");
    pq_set_group_name(c, 99, "Unused group");
    pq_close(c, t0 + 120);

    s = slurp(path(d, "groups.tsv"));
    CHECK(s && (unsigned char)s[0] == 0xEF);
    CHECK(s && strstr(s, "Example group") && !strstr(s, "Unused group"));
    free(s);
    s = slurp(path(d, "items.tsv"));
    CHECK(s && (unsigned char)s[0] == 0xEF && strstr(s, "101\x1f" "102") && strstr(s, "app.one\x1f" "app.two") && strstr(s, "Window one"));
    free(s);

    c = pq_open(d, 0, t0 + 200);
    CHECK(exists(path(d, "items.bak.tsv")));
    CHECK(pq_count(c) == 2);
    a = pq_id_at(c, 0); b = pq_id_at(c, 1);
    CHECK_STR(pq_text(c, a), "Task A");
    CHECK_STR(pq_text(c, b), "Task B");
    CHECK_NEAR(pq_created(c, a), t0, 1e-3);
    CHECK(pq_win_count(c, a) == 2);
    CHECK(pq_win_id(c, a, 0) == 101 && pq_win_id(c, a, 1) == 102);
    CHECK_STR(pq_win_proc(c, a, 1), "app.two");
    CHECK_STR(pq_win_title(c, a, 0), "Window one");
    CHECK(pq_group(c, a) == pq_group(c, b) && pq_group(c, a) != 0);
    CHECK_STR(pq_group_name(c, pq_group(c, a)), "Example group");
    CHECK(pq_first_of_group(c, 0) && !pq_last_of_group(c, 0) && pq_last_of_group(c, 1));
    pq_set_win_id(c, a, 0, 555);
    CHECK(pq_win_id(c, a, 0) == 555);
    pq_close(c, t0 + 300);
}

static void test_old_formats(void)
{
    const char* d = dir("oldformats");
    pq* c;
    char buf[512];
    double t0 = local_time(2026, 3, 4, 10, 0);
    long long ticks = 639082152000000000LL; /* .NET ticks of the local wall clock 2026-03-04 10:00:00 */
    snprintf(buf, sizeof buf,
             "\xEF\xBB\xBF%lld\tOld two column text\n"
             "%lld\t123\tapp.old\tOld title\tFive column text\n"
             "garbage line\n"
             "%lld\t0\t\t\tUnlinked\t2\t0\r\n",
             ticks, ticks, ticks);
    spit(path(d, "items.tsv"), buf);
    c = pq_open(d, 0, t0);
    CHECK(pq_count(c) == 3);
    CHECK_STR(pq_text(c, pq_id_at(c, 0)), "Old two column text");
    CHECK_STR(pq_text(c, pq_id_at(c, 1)), "Five column text");
    CHECK(pq_win_count(c, pq_id_at(c, 1)) == 1 && pq_win_id(c, pq_id_at(c, 1), 0) == 123);
    CHECK_STR(pq_win_proc(c, pq_id_at(c, 1), 0), "app.old");
    CHECK(pq_win_count(c, pq_id_at(c, 2)) == 0 && pq_sessions(c, pq_id_at(c, 2)) == 2);
    CHECK_NEAR(pq_created(c, pq_id_at(c, 0)), t0, 1.0);
    pq_close(c, t0);
}

static void test_unreadable(void)
{
    const char* d = dir("unreadable");
    pq* c;
    MKDIR(path(d, "items.tsv")); /* exists, but is not a readable file */
    c = pq_open(d, 0, 0);
    pq_add(c, "Should not be written", 0, 0);
    pq_close(c, 0);
    CHECK(!exists(path(d, "items.tsv.tmp")));
    {
        FILE* f = fopen(path(path(d, "items.tsv"), "x"), "wb"); /* still a directory */
        CHECK(f != NULL);
        if (f) fclose(f);
    }
}

static void test_state_machine(void)
{
    const char* d = dir("focus");
    double t0 = local_time(2026, 3, 4, 10, 0);
    double t, end;
    pq_win one[1] = { 1 };
    int a, b, ev, lines;
    pq* c = pq_open(d, 0, t0);
    a = pq_add(c, "Task A", 0, t0); pq_link(c, a, 1, "app.a", "A", 1);
    b = pq_add(c, "Task B", 0, t0); pq_link(c, b, 2, "app.b", "B", 1);
    CHECK(pq_needs_entry(c));
    ev = pq_activate(c, 1, "A", t0);
    CHECK(ev & PQ_EV_CHANGED);
    CHECK(pq_activate(c, 1, "A", t0) == 0); /* same window: ignored */
    CHECK(!pq_needs_entry(c));
    CHECK(pq_bar_item(c) == a && pq_sel_tier(c) == 0);
    CHECK(pq_active(c, a) && !pq_active(c, b));
    CHECK(pq_heat(c, b, t0 + 25 * 60) == 2 && pq_heat(c, a, t0 + 25 * 60) == 0);
    CHECK(pq_heat(c, b, t0 + 600 * 60) == 6);

    /* focus 15 min with a 30 s trip away */
    t = t0 + 600; /* B is 10 min old */
    CHECK(pq_start(c, one, 1, 0, t) == 1);
    CHECK(pq_state(c) == PQ_FOCUS && pq_needs_tick(c, t));
    CHECK_NEAR(pq_focus_left(c, t), 900, 1e-6);
    CHECK_NEAR(pq_age_min(c, b, t + 300), 10, 1e-6);   /* frozen */
    CHECK(pq_heat(c, b, t + 3000) == 0);
    CHECK(pq_in_focus(c, a) && !pq_in_focus(c, b));
    CHECK(pq_try_switch(c, b, t + 10) == 0);
    CHECK(pq_note(c, t + 10) != NULL && pq_note_warn(c));
    CHECK(pq_try_switch(c, b, t + 12) == 1);
    CHECK(pq_try_switch(c, a, t + 13) == 1);
    ev = pq_activate(c, 2, "B", t + 60);
    CHECK(ev & PQ_EV_AWAY_START);
    CHECK(pq_state(c) == PQ_AWAY && pq_away_home(c) == 1);
    CHECK_NEAR(pq_away_left(c, t + 70), 50, 1e-6);
    CHECK_NEAR(pq_focus_left(c, t + 80), 840, 1e-6);   /* paused */
    ev = pq_activate(c, 1, "A", t + 90);
    CHECK(ev & PQ_EV_AWAY_END);
    CHECK_STR(pq_note(c, t + 90), "Welcome back · focus continues");
    CHECK_NEAR(pq_focus_left(c, t + 90), 840, 1e-6);
    CHECK(pq_tick(c, t + 929) == 0);
    lines = count_lines(pq_log_path(c));
    ev = pq_tick(c, t + 930);
    CHECK(ev & PQ_EV_FOCUS_DONE);
    CHECK(pq_state(c) == PQ_BREAK && pq_can_extend(c));
    CHECK(pq_sessions(c, a) == 1 && pq_sessions(c, b) == 0);
    CHECK(pq_sel_tier(c) == 1 && pq_bar_item(c) == a);
    CHECK(count_lines(pq_log_path(c)) == lines + 1);
    {
        char* s = slurp(pq_log_path(c));
        CHECK(s && strstr(s, ",15.0,Task A,,1,15,focus,done\r\n"));
        free(s);
    }
    /* B was visited during the focus (its clock restarted then), so StopFocus resets it to now */
    CHECK_NEAR(pq_age_min(c, b, t + 930), 0, 1e-6);
    CHECK_NEAR(pq_age_min(c, b, t + 990), 1, 1e-6);
    CHECK_NEAR(pq_break_left(c, t + 930), 180, 1e-6);

    /* rest in background: the "almost over" card once, then the end */
    end = t + 930 + 180;
    pq_break_hide(c, end - 30);
    CHECK(pq_break_hidden(c));
    CHECK_STR(pq_note(c, end - 30), "Resting in the background · alert 10 s before the end");
    CHECK(pq_tick(c, end - 20) == 0);
    CHECK(pq_tick(c, end - 10) & PQ_EV_BREAK_SOON);
    CHECK(!(pq_tick(c, end - 5) & PQ_EV_BREAK_SOON));
    CHECK(pq_tick(c, end) & PQ_EV_BREAK_END);
    CHECK(pq_state(c) == PQ_IDLE);
    CHECK_STR(pq_note(c, end + 1), "Break over! Ready to start again?");

    /* 10-second test run, then +5 min */
    t = end + 100;
    CHECK(pq_start(c, one, 1, 1, t) == 1);
    CHECK_NEAR(pq_focus_left(c, t), 10, 1e-6);
    CHECK(pq_tick(c, t + 10) & PQ_EV_FOCUS_DONE);
    CHECK(pq_sessions(c, a) == 2);
    CHECK_NEAR(pq_break_left(c, t + 10), 300, 1e-6); /* round 2: 5 min break */
    pq_extend(c, t + 20);
    CHECK(pq_state(c) == PQ_FOCUS && pq_is_extension(c));
    CHECK_STR(pq_note(c, t + 20), "+5 min · break comes back after");
    CHECK_NEAR(pq_focus_progress(c, t + 20), 10.0 / 310.0, 1e-9);
    CHECK_NEAR(pq_focus_left(c, t + 20), 300, 1e-6);
    pq_stop(c, t + 80);
    CHECK(pq_state(c) == PQ_IDLE);
    CHECK_STR(pq_note(c, t + 80), "Focus stopped");
    {
        char* s = slurp(pq_log_path(c));
        CHECK(s && strstr(s, ",1.0,Task A,,2,5,extension,stopped\r\n"));
        free(s);
    }

    /* short sessions are not logged */
    t += 200;
    lines = count_lines(pq_log_path(c));
    pq_start(c, one, 1, 0, t);
    pq_stop(c, t + 20);
    CHECK(count_lines(pq_log_path(c)) == lines);

    /* didn't come back within 1 minute */
    t += 100;
    pq_start(c, one, 1, 0, t);
    pq_activate(c, 2, "B", t + 120);
    CHECK(pq_tick(c, t + 179) == 0);
    ev = pq_tick(c, t + 180);
    CHECK(ev & PQ_EV_AWAY_TIMEOUT);
    CHECK(pq_state(c) == PQ_IDLE && pq_note_warn(c));
    CHECK_STR(pq_note(c, t + 180), "Didn't come back within 1 min · focus reset");
    {
        char* s = slurp(pq_log_path(c));
        CHECK(s && strstr(s, ",2.0,Task A,,3,60,focus,left\r\n"));
        free(s);
    }

    /* skip: no "break over" note */
    pq_activate(c, 1, "A", t + 300);
    pq_start(c, one, 1, 1, t + 400);
    CHECK(pq_tick(c, t + 410) & PQ_EV_FOCUS_DONE);
    pq_break_skip(c, t + 415);
    CHECK(pq_state(c) == PQ_IDLE && pq_note(c, t + 415) == NULL);

    /* rounds */
    pq_reset_rounds(c, a);
    CHECK(pq_sessions(c, a) == 0 && pq_sel_tier(c) == 0);
    pq_step_tier(c, 1); pq_step_tier(c, 5);
    CHECK(pq_sel_tier(c) == 2 && pq_tier(c, a) == 2);
    CHECK(pq_start(c, NULL, 0, 0, t + 500) == 0);

    /* closing while focusing logs "quit" */
    pq_start(c, one, 1, 0, t + 600);
    pq_close(c, t + 700);
    {
        char* s = slurp(path(d, "focus_log.csv"));
        CHECK(s && (unsigned char)s[0] == 0xEF && !strncmp(s + 3, "start,end,minutes,task,group,round,planned_min,kind,outcome\r\n", 61));
        CHECK(s && strstr(s, ",focus,quit\r\n"));
        free(s);
    }
}

/* grace card "part of this task": the window joins the focused item, focus goes on, away time counts */
static void test_adopt(void)
{
    const char* d = dir("adopt");
    double t0 = local_time(2026, 3, 4, 10, 0), t = t0;
    pq_win one[1] = { 1 };
    int a, ev;
    pq* c = pq_open(d, 0, t0);
    a = pq_add(c, "Task A", 0, t0); pq_link(c, a, 1, "app.a", "A", 1);
    pq_activate(c, 1, "A", t);
    CHECK(pq_adopt(c, "app.x", "X", t) == 0);            /* not focusing */
    CHECK(pq_start(c, one, 1, 0, t) == 1);
    CHECK(pq_adopt(c, "app.a", "A", t + 5) == 0);        /* not away */
    ev = pq_activate(c, 7, "Docs", t + 10);              /* a window the task didn't know */
    CHECK((ev & PQ_EV_AWAY_START) && pq_state(c) == PQ_AWAY && !pq_owns(c, 7));
    ev = pq_adopt(c, "app.x", "Docs", t + 20);
    CHECK((ev & PQ_EV_AWAY_END) && (ev & PQ_EV_CHANGED));
    CHECK(pq_state(c) == PQ_FOCUS && pq_owns(c, 7));
    CHECK(pq_has(c, a, 7) && pq_win_count(c, a) == 2);   /* linked for next time */
    CHECK_STR(pq_note(c, t + 20), "Window added to the task · focus continues");
    CHECK_NEAR(pq_focus_left(c, t + 20), 900 - 20, 1e-6); /* the 10 s there counted as focus */
    CHECK(pq_activate(c, 1, "A", t + 30) == PQ_EV_CHANGED); /* moving between them: no grace card */
    CHECK(pq_activate(c, 7, "Docs", t + 40) == PQ_EV_CHANGED);
    pq_close(c, t + 45);
    c = pq_open(d, 0, t + 50);                           /* the link was saved */
    CHECK(pq_win_count(c, pq_id_at(c, 0)) == 2);
    pq_close(c, t + 50);
}

static void test_groups(void)
{
    const char* d = dir("groups");
    double t0 = local_time(2026, 3, 4, 10, 0);
    pq* c = pq_open(d, 0, t0);
    int a = pq_add(c, "A", 0, t0), b = pq_add(c, "B", 0, t0), cc = pq_add(c, "C", 0, t0), dd = pq_add(c, "D", 0, t0);
    int created = 0, g;
    pq_win w[2] = { 30, 31 };
    pq_link(c, a, 10, "p", "a", 1);
    pq_link(c, cc, 30, "p", "c", 1);
    g = pq_join(c, b, a, &created);
    CHECK(created && pq_group_size(c, g) == 2);
    CHECK(pq_join(c, cc, a, &created) == g && !created);
    CHECK(pq_index_of(c, cc) == 2 && pq_group_size(c, g) == 3);
    pq_leave(c, b);
    CHECK(pq_group(c, b) == 0 && pq_index_of(c, b) == 2); /* A C B D */
    pq_move(c, dd, 1, 0);                                 /* A D C B: dropped inside the group */
    CHECK(pq_group(c, dd) == g && pq_group_size(c, g) == 3);
    pq_move(c, a, 3, 1);                                  /* live: no fix-up */
    CHECK(pq_group(c, a) == g);
    pq_move(c, a, 3, 0);                                  /* D C B A: A has no neighbour in the group */
    CHECK(pq_group(c, a) == 0 && pq_group_size(c, g) == 2);
    CHECK(pq_first_of_group(c, 0) && pq_last_of_group(c, 1));

    /* group = one task: windows of the group count, rounds are shared */
    pq_activate(c, 30, "c", t0);
    CHECK(pq_active(c, dd) && pq_ghas(c, dd, 30));
    CHECK(pq_start(c, w, 2, 0, t0 + 10));
    CHECK(pq_in_focus(c, dd) && pq_in_focus(c, cc) && !pq_in_focus(c, b));
    CHECK(pq_owns(c, 31) && pq_owns(c, 30) && !pq_owns(c, 10));
    pq_stop(c, t0 + 20);
    pq_remove(c, b);
    CHECK(pq_count(c) == 3 && pq_index_of(c, b) == -1);
    pq_rename(c, a, "A\trenamed");
    CHECK_STR(pq_text(c, a), "A renamed");
    pq_close(c, t0 + 30);
}

static void test_migration(void)
{
    const char* d = dir("migrate");
    pq* c;
    char* s;
    spit(path(d, "focus_log.csv"),
         "\xEF\xBB\xBF시작,끝,집중(분),할 일,묶음,회차,계획(분),종류,결과\r\n"
         "2026-03-01 10:00:00,2026-03-01 10:15:00,15.0,\"Task, with comma\",,1,15,기본,완료\r\n"
         "2026-03-01 11:00:00,2026-03-01 11:05:00,5.0,Task,G,1,5,연장,이탈\r\n");
    c = pq_open(d, 0, 0);
    pq_close(c, 0);
    s = slurp(path(d, "focus_log.csv"));
    CHECK(s && !strncmp(s, "\xEF\xBB\xBF" "start,end,minutes,task,group,round,planned_min,kind,outcome\r\n", 64));
    CHECK(s && strstr(s, "\"Task, with comma\",,1,15,focus,done\r\n"));
    CHECK(s && strstr(s, ",Task,G,1,5,extension,left\r\n"));
    free(s);
    CHECK(exists(path(d, "focus_log.ko.bak.csv")));
    c = pq_open(d, 0, 0); /* second open: nothing to do */
    pq_close(c, 0);
}

static void test_stats(void)
{
    const char* d = dir("stats");
    double now = local_time(2026, 3, 4, 18, 0);
    char text[8192], line[256], a[32], b[32];
    int i;
    pq* c;
    strcpy(text, "\xEF\xBB\xBFstart,end,minutes,task,group,round,planned_min,kind,outcome\r\n");
    /* today 10:00 and 10:30, Task 1 */
    stamp(a, sizeof a, local_time(2026, 3, 4, 10, 0)); stamp(b, sizeof b, local_time(2026, 3, 4, 10, 15));
    snprintf(line, sizeof line, "%s,%s,15.0,Task 1,,1,15,focus,done\r\n", a, b); strcat(text, line);
    stamp(a, sizeof a, local_time(2026, 3, 4, 10, 30)); stamp(b, sizeof b, local_time(2026, 3, 4, 10, 35));
    snprintf(line, sizeof line, "%s,%s,5.0,Task 1,,1,5,extension,done\r\n", a, b); strcat(text, line);
    /* earlier this week: tasks 2..8 (7 tasks with more focus than Task 1 for some) */
    for (i = 2; i <= 8; i++)
    {
        stamp(a, sizeof a, local_time(2026, 3, 4 - (i % 3) - 1, 9, 0)); stamp(b, sizeof b, local_time(2026, 3, 4 - (i % 3) - 1, 9, 30));
        snprintf(line, sizeof line, "%s,%s,%d.0,Task %d,%s,2,30,focus,%s\r\n", a, b, 10 + i, i, i == 2 ? "G" : "", i == 3 ? "left" : "stopped");
        strcat(text, line);
    }
    /* no task, 20 days ago */
    stamp(a, sizeof a, local_time(2026, 2, 12, 9, 0)); stamp(b, sizeof b, local_time(2026, 2, 12, 9, 10));
    snprintf(line, sizeof line, "%s,%s,10.0,,,1,15,focus,done\r\n", a, b); strcat(text, line);
    spit(path(d, "focus_log.csv"), text);

    c = pq_open(d, 0, now);
    pq_stats(c, PQ_RANGE_TODAY, now);
    CHECK(pq_st_hourly(c) && pq_st_bucket_count(c) == 24);
    CHECK(pq_st_sessions(c) == 2 && pq_st_done(c) == 2 && pq_st_extensions(c) == 1);
    CHECK_NEAR(pq_st_total(c), 20, 1e-9);
    CHECK_NEAR(pq_st_bucket_min(c, 10), 20, 1e-9);
    CHECK_STR(pq_st_bucket_label(c, 10), "10:00");
    CHECK_STR(pq_st_bucket_tick(c, 8), "8:00");
    CHECK_STR(pq_st_bucket_tick(c, 9), "");

    pq_stats(c, 7, now);
    CHECK(!pq_st_hourly(c) && pq_st_bucket_count(c) == 7);
    CHECK(pq_st_sessions(c) == 9 && pq_st_left(c) == 1 && pq_st_days(c) == 4);
    CHECK_STR(pq_st_bucket_label(c, 6), "3/4 (Wed)");
    CHECK_STR(pq_st_bucket_tick(c, 6), "3/4");
    CHECK_NEAR(pq_st_bucket_min(c, 6), 20, 1e-9);
    CHECK(pq_st_task_count(c) == 6);                      /* top 5 + "N others" */
    CHECK_STR(pq_st_task_name(c, 0), "Task 1");           /* 20 min */
    CHECK_STR(pq_st_task_name(c, 1), "Task 8");           /* 18 */
    CHECK_STR(pq_st_task_name(c, 5), "3 others");
    CHECK(pq_st_task_sessions(c, 5) == 3);
    CHECK_NEAR(pq_st_task_min(c, 5), 12 + 13 + 14, 1e-9);
    CHECK_STR(pq_st_row_task(c, 0), "Task 1");            /* newest first */
    CHECK_STR(pq_st_row_kind(c, 0), "extension");
    CHECK(pq_st_row_planned(c, 0) == 5);
    for (i = 1; i < pq_st_row_count(c); i++) CHECK(pq_st_row_start(c, i - 1) >= pq_st_row_start(c, i));

    pq_stats(c, PQ_RANGE_ALL, now);
    CHECK(pq_st_sessions(c) == 10 && pq_st_bucket_count(c) == 21); /* 2/12 .. 3/4 */
    CHECK_STR(pq_st_bucket_tick(c, 0), "2/12");
    CHECK_STR(pq_st_bucket_tick(c, 1), "");               /* ticks every ceil(21/7) = 3 days */
    CHECK_STR(pq_st_bucket_tick(c, 3), "2/15");
    CHECK_STR(pq_st_row_task(c, 9), "(no task)");
    pq_set_lang_ko(c, 1);
    pq_stats(c, PQ_RANGE_ALL, now);
    CHECK_STR(pq_st_row_task(c, 9), "(할 일 없음)");
    CHECK_STR(pq_st_bucket_label(c, 20), "3/4 (수)");
    pq_set_lang_ko(c, 0);

    pq_stats(c, 30, now);
    CHECK(pq_st_bucket_count(c) == 30 && pq_st_sessions(c) == 10);
    pq_close(c, now);

    /* all time is capped at 90 days, and at least 7 */
    spit(path(d, "focus_log.csv"), "start,end,minutes,task,group,round,planned_min,kind,outcome\r\n2025-01-01 09:00:00,2025-01-01 09:10:00,10.0,Old,,1,15,focus,done\r\n");
    c = pq_open(d, 0, now);
    pq_stats(c, PQ_RANGE_ALL, now);
    CHECK(pq_st_bucket_count(c) == 90);
    pq_stats(c, 7, now);
    CHECK(pq_st_sessions(c) == 0 && pq_st_task_count(c) == 0);
    pq_close(c, now);
}

int main(int argc, char** argv)
{
    if (argc < 2) { printf("usage: test_core <empty-temp-dir>\n"); return 2; }
    snprintf(base_dir, sizeof base_dir, "%s", argv[1]);
    MKDIR(base_dir);
    CHECK(pq_api_version() == PQ_API_VERSION);
    test_strings();
    test_round_trip();
    test_old_formats();
    test_unreadable();
    test_state_machine();
    test_adopt();
    test_groups();
    test_migration();
    test_stats();
    printf("%d checks, %d failed\n", checks, failures);
    return failures ? 1 : 0;
}
