/*
 * pq_core.c - implementation of pq_core.h (see the header for the contract).
 * Plain C11; builds with MSVC (cl /utf-8), clang and gcc.
 * The rules are a port of PQ.cs (PQForm / StatsForm); comments name the original method.
 */
#if defined(__linux__) && !defined(_POSIX_C_SOURCE)
#  define _POSIX_C_SOURCE 200809L
#endif
#ifdef _WIN32
#  define _CRT_SECURE_NO_WARNINGS
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#  include <wchar.h>
#  include <direct.h>
#else
#  include <sys/stat.h>
#  include <sys/types.h>
#  include <unistd.h>
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>

#include "pq_core.h"
#include "pq_strings.h"

/* ====================================================================================== */
/* small helpers                                                                           */
/* ====================================================================================== */

static const int TIER_FOCUS[3] = { 15, 30, 60 };
static const int TIER_BREAK[3] = { 3, 5, 15 };
#define TIERS 3
#define AWAY_SEC 60.0
#define SOON_SEC 10.0
#define EXTEND_MIN 5
#define NOTE_SEC 4.0
#define WARN_SEC 6.0
#define US '\x1f' /* separates multiple linked windows inside a column of items.tsv */

static char* xstrdup(const char* s)
{
    size_t n;
    char* p;
    if (!s) s = "";
    n = strlen(s);
    p = (char*)malloc(n + 1);
    if (p) memcpy(p, s, n + 1);
    return p;
}

static void xset(char** dst, const char* s) { char* p = xstrdup(s); free(*dst); *dst = p; }

/* growable string */
typedef struct { char* p; size_t n, cap; } sb;

static void sb_reserve(sb* b, size_t extra)
{
    if (b->n + extra + 1 > b->cap)
    {
        size_t cap = b->cap ? b->cap : 64;
        char* np;
        while (b->n + extra + 1 > cap) cap *= 2;
        np = (char*)realloc(b->p, cap);
        if (!np) return;
        b->p = np; b->cap = cap;
    }
}
static void sb_addn(sb* b, const char* s, size_t n)
{
    sb_reserve(b, n);
    if (b->n + n + 1 > b->cap) return;
    memcpy(b->p + b->n, s, n);
    b->n += n;
    b->p[b->n] = 0;
}
static void sb_add(sb* b, const char* s) { if (s) sb_addn(b, s, strlen(s)); }
static void sb_addc(sb* b, char c) { sb_addn(b, &c, 1); }
static void sb_addll(sb* b, long long v) { char t[32]; snprintf(t, sizeof t, "%lld", v); sb_add(b, t); }
static const char* sb_str(sb* b) { if (!b->p) { sb_reserve(b, 0); if (b->p) b->p[0] = 0; } return b->p ? b->p : ""; }
static void sb_clear(sb* b) { b->n = 0; if (b->p) b->p[0] = 0; }
static void sb_free(sb* b) { free(b->p); b->p = NULL; b->n = b->cap = 0; }

/* text for a file column: no tabs / newlines / separators */
static void clean_into(sb* b, const char* s)
{
    for (; s && *s; s++)
    {
        char c = *s;
        sb_addc(b, (c == '\t' || c == '\r' || c == '\n' || c == US) ? ' ' : c);
    }
}

/* invariant number formatting / parsing (independent of the C locale) */
static void fmt1(char* out, size_t sz, double v) /* "0.0" */
{
    long long t = llround(v * 10.0);
    const char* sign = "";
    if (t < 0) { sign = "-"; t = -t; }
    snprintf(out, sz, "%s%lld.%lld", sign, t / 10, t % 10);
}

static int parse_ll(const char* s, long long* out)
{
    long long v = 0;
    int neg = 0, any = 0;
    while (*s == ' ') s++;
    if (*s == '-' || *s == '+') { neg = *s == '-'; s++; }
    for (; *s >= '0' && *s <= '9'; s++) { v = v * 10 + (*s - '0'); any = 1; }
    while (*s == ' ') s++;
    if (!any || *s) return 0;
    *out = neg ? -v : v;
    return 1;
}

static int parse_int(const char* s, int* out)
{
    long long v;
    if (!parse_ll(s, &v) || v < -2147483647LL || v > 2147483647LL) return 0;
    *out = (int)v;
    return 1;
}

static double parse_num(const char* s) /* "12.5" -> 12.5, garbage -> 0 */
{
    double v = 0, f = 0.1;
    int neg = 0;
    while (*s == ' ') s++;
    if (*s == '-' || *s == '+') { neg = *s == '-'; s++; }
    for (; *s >= '0' && *s <= '9'; s++) v = v * 10 + (*s - '0');
    if (*s == '.') for (s++; *s >= '0' && *s <= '9'; s++) { v += (*s - '0') * f; f /= 10; }
    return neg ? -v : v;
}

/* ====================================================================================== */
/* files (UTF-8 paths; wide APIs on Windows)                                                */
/* ====================================================================================== */

#ifdef _WIN32
static wchar_t* widen(const char* s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    wchar_t* w = (wchar_t*)malloc((size_t)(n > 0 ? n : 1) * sizeof(wchar_t));
    if (!w) return NULL;
    if (n <= 0) { w[0] = 0; return w; }
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
    return w;
}
static FILE* u8_fopen(const char* path, const char* mode)
{
    wchar_t* p = widen(path); wchar_t* m = widen(mode);
    FILE* f = (p && m) ? _wfopen(p, m) : NULL;
    free(p); free(m);
    return f;
}
static int u8_kind(const char* path) /* 0 = missing, 1 = file, 2 = directory */
{
    wchar_t* p = widen(path);
    DWORD a = p ? GetFileAttributesW(p) : INVALID_FILE_ATTRIBUTES;
    free(p);
    if (a == INVALID_FILE_ATTRIBUTES) return 0;
    return (a & FILE_ATTRIBUTE_DIRECTORY) ? 2 : 1;
}
static void u8_mkdir(const char* path) { wchar_t* p = widen(path); if (p) _wmkdir(p); free(p); }
static int u8_copy(const char* src, const char* dst)
{
    wchar_t* a = widen(src); wchar_t* b = widen(dst);
    int ok = a && b && CopyFileW(a, b, FALSE);
    free(a); free(b);
    return ok;
}
static int u8_replace(const char* tmp, const char* dst)
{
    wchar_t* a = widen(tmp); wchar_t* b = widen(dst);
    int ok = a && b && MoveFileExW(a, b, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
    free(a); free(b);
    return ok;
}
static void u8_remove(const char* path) { wchar_t* p = widen(path); if (p) _wremove(p); free(p); }
#  define PQ_SEP "\\"
#else
static FILE* u8_fopen(const char* path, const char* mode) { return fopen(path, mode); }
static int u8_kind(const char* path)
{
    struct stat st;
    if (stat(path, &st) != 0) return 0;
    return S_ISDIR(st.st_mode) ? 2 : 1;
}
static void u8_mkdir(const char* path) { mkdir(path, 0755); }
static int u8_copy(const char* src, const char* dst)
{
    FILE* a = fopen(src, "rb");
    FILE* b;
    char buf[8192];
    size_t n;
    int ok = 1;
    if (!a) return 0;
    b = fopen(dst, "wb");
    if (!b) { fclose(a); return 0; }
    while ((n = fread(buf, 1, sizeof buf, a)) > 0) if (fwrite(buf, 1, n, b) != n) { ok = 0; break; }
    if (ferror(a)) ok = 0;
    fclose(a);
    if (fclose(b) != 0) ok = 0;
    return ok;
}
static int u8_replace(const char* tmp, const char* dst) { return rename(tmp, dst) == 0; }
static void u8_remove(const char* path) { remove(path); }
#  define PQ_SEP "/"
#endif

/* create the folder and its parents */
static void make_dirs(const char* path)
{
    char* p = xstrdup(path);
    size_t i, n;
    if (!p) return;
    n = strlen(p);
    for (i = 1; i < n; i++)
    {
        if (p[i] == '/' || p[i] == '\\')
        {
            char c = p[i];
            if (i > 0 && p[i - 1] == ':') continue; /* "C:\" */
            p[i] = 0;
            if (u8_kind(p) == 0) u8_mkdir(p);
            p[i] = c;
        }
    }
    if (u8_kind(p) == 0) u8_mkdir(p);
    free(p);
}

/* whole file; returns NULL if it can't be read. *len excludes a UTF-8 BOM, which is skipped */
static char* read_file(const char* path, size_t* len)
{
    FILE* f = u8_fopen(path, "rb");
    char* buf = NULL;
    size_t cap = 0, n = 0, r;
    if (!f) return NULL;
    for (;;)
    {
        if (n + 4096 + 1 > cap)
        {
            size_t nc = cap ? cap * 2 : 8192;
            char* nb;
            while (n + 4096 + 1 > nc) nc *= 2;
            nb = (char*)realloc(buf, nc);
            if (!nb) { free(buf); fclose(f); return NULL; }
            buf = nb; cap = nc;
        }
        r = fread(buf + n, 1, 4096, f);
        n += r;
        if (r < 4096) break;
    }
    if (ferror(f)) { free(buf); fclose(f); return NULL; }
    fclose(f);
    buf[n] = 0;
    if (n >= 3 && (unsigned char)buf[0] == 0xEF && (unsigned char)buf[1] == 0xBB && (unsigned char)buf[2] == 0xBF)
    {
        memmove(buf, buf + 3, n - 2);
        n -= 3;
    }
    if (len) *len = n;
    return buf;
}

/* write tmp, then replace the target in one step */
static int write_atomic(const char* path, const char* text, int bom)
{
    sb tmp = { 0 };
    FILE* f;
    int ok;
    sb_add(&tmp, path);
    sb_add(&tmp, ".tmp");
    f = u8_fopen(sb_str(&tmp), "wb");
    if (!f) { sb_free(&tmp); return 0; }
    ok = 1;
    if (bom && fwrite("\xEF\xBB\xBF", 1, 3, f) != 3) ok = 0;
    if (ok && text[0] && fwrite(text, 1, strlen(text), f) != strlen(text)) ok = 0;
    if (fclose(f) != 0) ok = 0;
    if (ok) ok = u8_replace(sb_str(&tmp), path);
    if (!ok) u8_remove(sb_str(&tmp));
    sb_free(&tmp);
    return ok;
}

static int append_file(const char* path, const char* text)
{
    FILE* f = u8_fopen(path, "ab");
    int ok;
    if (!f) return 0;
    ok = fwrite(text, 1, strlen(text), f) == strlen(text);
    if (fclose(f) != 0) ok = 0;
    return ok;
}

/* ====================================================================================== */
/* local time                                                                              */
/* ====================================================================================== */

static int local_tm(double t, struct tm* out)
{
    time_t tt = (time_t)floor(t);
#ifdef _WIN32
    return localtime_s(out, &tt) == 0;
#else
    return localtime_r(&tt, out) != NULL;
#endif
}

/* days since 1970-01-01 for a civil date (proleptic Gregorian) */
static long long days_from_civil(long long y, int m, int d)
{
    long long era, yoe, doy, doe;
    y -= m <= 2;
    era = (y >= 0 ? y : y - 399) / 400;
    yoe = y - era * 400;
    doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468;
}

static void civil_from_days(long long z, int* y, int* m, int* d)
{
    long long era, doe, yoe, yy, doy, mp;
    z += 719468;
    era = (z >= 0 ? z : z - 146096) / 146097;
    doe = z - era * 146097;
    yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    yy = yoe + era * 400;
    doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    mp = (5 * doy + 2) / 153;
    *d = (int)(doy - (153 * mp + 2) / 5 + 1);
    *m = (int)(mp < 10 ? mp + 3 : mp - 9);
    *y = (int)(yy + (*m <= 2));
}

/* wall-clock seconds of t in the local time zone, counted like UTC seconds */
static double local_sec(double t)
{
    struct tm tm;
    if (!local_tm(t, &tm)) return t;
    return (double)days_from_civil(tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday) * 86400.0
        + tm.tm_hour * 3600.0 + tm.tm_min * 60.0 + tm.tm_sec + (t - floor(t));
}

/* inverse of local_sec */
static double from_local(double ls)
{
    double g = ls - (local_sec(ls) - ls);
    return ls - (local_sec(g) - g);
}

static long long day_of(double t) { return (long long)floor(local_sec(t) / 86400.0); }

static int weekday_of_day(long long day) { long long w = (day + 4) % 7; return (int)(w < 0 ? w + 7 : w); } /* 0 = Sunday */

/* .NET DateTime ticks of the local wall clock (items.tsv) */
#define TICKS_EPOCH 621355968000000000LL
static long long to_ticks(double t) { return TICKS_EPOCH + llround(local_sec(t) * 1e7); }
static double from_ticks(long long ticks) { return from_local((double)(ticks - TICKS_EPOCH) / 1e7); }

static void stamp(char* out, size_t sz, double t) /* yyyy-MM-dd HH:mm:ss */
{
    struct tm tm;
    if (!local_tm(t, &tm)) { snprintf(out, sz, "1970-01-01 00:00:00"); return; }
    snprintf(out, sz, "%04d-%02d-%02d %02d:%02d:%02d", tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday, tm.tm_hour, tm.tm_min, tm.tm_sec);
}

static int parse_stamp(const char* s, double* out)
{
    int y, mo, d, h = 0, mi = 0, se = 0, n;
    n = sscanf(s, "%d-%d-%d %d:%d:%d", &y, &mo, &d, &h, &mi, &se);
    if (n < 3 || mo < 1 || mo > 12 || d < 1 || d > 31) return 0;
    *out = from_local((double)days_from_civil(y, mo, d) * 86400.0 + h * 3600.0 + mi * 60.0 + se);
    return 1;
}

/* ====================================================================================== */
/* state                                                                                    */
/* ====================================================================================== */

typedef struct { pq_win id; char* proc; char* title; } win;

typedef struct
{
    int id;
    char* text;
    double created;
    win* wins; int nw;
    pq_win last;
    int sessions;
    int group;
} item;

typedef struct { int g; char* name; } gname;

typedef struct
{
    double start, min;
    char *task, *group, *kind, *outcome;
    int round, planned;
} rec;

typedef struct { char *name, *group; double min; int sessions; } task_row;

struct pq
{
    char *dir, *items_path, *groups_path, *log_path, *lang_path, *opacity_path, *bak_path;
    item** v; int n, cap;
    int next_id;
    gname* gn; int ngn;
    int ko;
    double opacity;
    int locked; /* items.tsv exists but couldn't be read: never write it */

    pq_win current;

    /* focus */
    int focusing, away, on_break;
    int sel_tier, focus_tier, bar_item, last_focus_item, focus_item;
    double focus_start, focus_end, freeze_from, paused_sec;
    int planned_min, extension;
    pq_win* fw; int nfw;
    int warned_item; double warn_time;
    double away_start; pq_win away_home;
    double break_start, break_end; int break_min; int break_hidden, soon_shown;
    int has_done; pq_win* dw; int ndw; int done_item, done_tier; double done_start, done_end;

    /* bar note */
    char* note; int note_warn; double note_until;

    /* stats */
    int st_days_arg, st_hourly;
    double st_total; int st_sessions, st_done, st_ext, st_left, st_days;
    double* bk_min; char** bk_label; char** bk_tick; int nbk;
    task_row* tasks; int ntasks;
    rec* rows; int nrows;

    /* returned strings */
    sb b_fmt, b_fmt_min, b_age, b_tmp;
};

/* ---------- strings ---------- */

const char* pq_tr(pq* c, const char* key)
{
    int i;
    if (!key) return "";
    for (i = 0; i < PQ_STRING_COUNT; i++)
        if (strcmp(PQ_STRINGS[i].key, key) == 0) return (c && c->ko) ? PQ_STRINGS[i].ko : PQ_STRINGS[i].en;
    return key;
}

static void fill(sb* out, const char* tpl, const char* a0, const char* a1, const char* a2)
{
    const char* p;
    sb_clear(out);
    for (p = tpl; *p; p++)
    {
        if (p[0] == '{' && p[1] >= '0' && p[1] <= '2' && p[2] == '}')
        {
            const char* a = p[1] == '0' ? a0 : p[1] == '1' ? a1 : a2;
            sb_add(out, a ? a : "");
            p += 2;
        }
        else sb_addc(out, *p);
    }
}

const char* pq_fmt(pq* c, const char* key, const char* a0, const char* a1, const char* a2)
{
    if (!c) return pq_tr(c, key);
    fill(&c->b_fmt, pq_tr(c, key), a0, a1, a2);
    return sb_str(&c->b_fmt);
}

/* FmtMin (C# Math.Round rounds half to even, like rint) */
static void fmt_min_into(pq* c, sb* out, double minutes)
{
    long long v = (long long)rint(minutes);
    char a[32], b[32];
    if (v < 60) { snprintf(a, sizeof a, "%lld", v); fill(out, pq_tr(c, "time.min"), a, NULL, NULL); return; }
    snprintf(a, sizeof a, "%lld", v / 60);
    snprintf(b, sizeof b, "%lld", v % 60);
    fill(out, pq_tr(c, v % 60 > 0 ? "time.hm" : "time.h"), a, b, NULL);
}

const char* pq_fmt_min(pq* c, double minutes)
{
    if (!c) return "";
    fmt_min_into(c, &c->b_fmt_min, minutes);
    return sb_str(&c->b_fmt_min);
}

const char* pq_fmt_clock(double seconds)
{
    static char buf[32]; /* no pq* here: one shared buffer */
    long long t = (long long)ceil(seconds > 0 ? seconds : 0);
    snprintf(buf, sizeof buf, "%lld:%02lld", t / 60, t % 60);
    return buf;
}

const char* pq_fmt_age(pq* c, double minutes)
{
    long long m = (long long)(minutes > 0 ? minutes : 0);
    char t[48];
    if (!c) return "";
    if (m < 60) snprintf(t, sizeof t, "%lldm", m);
    else snprintf(t, sizeof t, "%lldh%02lld", m / 60, m % 60);
    sb_clear(&c->b_age);
    sb_add(&c->b_age, t);
    return sb_str(&c->b_age);
}

const char* pq_outcome_label(pq* c, const char* outcome)
{
    if (!outcome) return "";
    if (!strcmp(outcome, "done")) return pq_tr(c, "outcome.done");
    if (!strcmp(outcome, "stopped")) return pq_tr(c, "outcome.stopped");
    if (!strcmp(outcome, "left")) return pq_tr(c, "outcome.left");
    if (!strcmp(outcome, "quit")) return pq_tr(c, "outcome.quit");
    return outcome;
}

static const char* WD_EN[7] = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
static const char* WD_KO[7] = { "일", "월", "화", "수", "목", "금", "토" };

const char* pq_weekday(pq* c, double t)
{
    int w = weekday_of_day(day_of(t));
    return (c && c->ko) ? WD_KO[w] : WD_EN[w];
}

const char* pq_fmt_stamp(double t)
{
    static char buf[32]; /* no pq* here: one shared buffer */
    struct tm tm;
    if (!local_tm(t, &tm)) return "";
    snprintf(buf, sizeof buf, "%d/%d %02d:%02d", tm.tm_mon + 1, tm.tm_mday, tm.tm_hour, tm.tm_min);
    return buf;
}

/* ---------- note ---------- */

void pq_set_note(pq* c, const char* text, int warn, double now)
{
    if (!c) return;
    xset(&c->note, text ? text : "");
    c->note_warn = warn;
    c->note_until = now + NOTE_SEC;
}

const char* pq_note(pq* c, double now) { return (c && c->note && now < c->note_until) ? c->note : NULL; }
int pq_note_warn(pq* c) { return c ? c->note_warn : 0; }

/* ====================================================================================== */
/* queue basics                                                                             */
/* ====================================================================================== */

static item* by_id(pq* c, int id)
{
    int i;
    if (!c || id <= 0) return NULL;
    for (i = 0; i < c->n; i++) if (c->v[i]->id == id) return c->v[i];
    return NULL;
}

static int idx_of(pq* c, int id)
{
    int i;
    for (i = 0; i < c->n; i++) if (c->v[i]->id == id) return i;
    return -1;
}

static void free_item(item* it)
{
    int k;
    if (!it) return;
    for (k = 0; k < it->nw; k++) { free(it->wins[k].proc); free(it->wins[k].title); }
    free(it->wins);
    free(it->text);
    free(it);
}

static void insert_at(pq* c, int at, item* it)
{
    if (c->n == c->cap)
    {
        int nc = c->cap ? c->cap * 2 : 16;
        item** nv = (item**)realloc(c->v, (size_t)nc * sizeof(item*));
        if (!nv) return;
        c->v = nv; c->cap = nc;
    }
    if (at < 0) at = 0;
    if (at > c->n) at = c->n;
    memmove(c->v + at + 1, c->v + at, (size_t)(c->n - at) * sizeof(item*));
    c->v[at] = it;
    c->n++;
}

static item* take_at(pq* c, int at)
{
    item* it = c->v[at];
    memmove(c->v + at, c->v + at + 1, (size_t)(c->n - at - 1) * sizeof(item*));
    c->n--;
    return it;
}

static void add_win(item* it, pq_win w, const char* proc, const char* title)
{
    win* nw = (win*)realloc(it->wins, (size_t)(it->nw + 1) * sizeof(win));
    if (!nw) return;
    it->wins = nw;
    it->wins[it->nw].id = w;
    it->wins[it->nw].proc = xstrdup(proc);
    it->wins[it->nw].title = xstrdup(title);
    it->nw++;
}

static int has(item* it, pq_win w)
{
    int k;
    if (!it || w == 0) return 0;
    for (k = 0; k < it->nw; k++) if (it->wins[k].id == w) return 1;
    return 0;
}

static int same_group(item* a, item* b) { return a && b && a->group != 0 && a->group == b->group; }

/* GHas: w is one of the item's windows, or of any item in its group */
static int ghas(pq* c, item* it, pq_win w)
{
    int i;
    if (!it) return 0;
    if (has(it, w)) return 1;
    if (it->group == 0 || w == 0) return 0;
    for (i = 0; i < c->n; i++) if (c->v[i] != it && c->v[i]->group == it->group && has(c->v[i], w)) return 1;
    return 0;
}

static item* item_of(pq* c, pq_win w)
{
    int i;
    for (i = 0; i < c->n; i++) if (has(c->v[i], w)) return c->v[i];
    return NULL;
}

static int first_of_group(pq* c, int i)
{
    item* it = c->v[i];
    return it->group != 0 && (i == 0 || !same_group(c->v[i - 1], it));
}

static int last_of_group(pq* c, int i)
{
    item* it = c->v[i];
    return it->group != 0 && (i + 1 >= c->n || !same_group(c->v[i + 1], it));
}

/* ====================================================================================== */
/* persistence                                                                              */
/* ====================================================================================== */

static const char* gname_get(pq* c, int g)
{
    int i;
    for (i = 0; i < c->ngn; i++) if (c->gn[i].g == g) return c->gn[i].name;
    return NULL;
}

static void gname_set(pq* c, int g, const char* name)
{
    int i;
    gname* ng;
    for (i = 0; i < c->ngn; i++) if (c->gn[i].g == g) { xset(&c->gn[i].name, name); return; }
    ng = (gname*)realloc(c->gn, (size_t)(c->ngn + 1) * sizeof(gname));
    if (!ng) return;
    c->gn = ng;
    c->gn[c->ngn].g = g;
    c->gn[c->ngn].name = xstrdup(name);
    c->ngn++;
}

/* split s at `sep` into a NULL-terminated array of new strings */
static char** split(const char* s, char sep, int* count)
{
    int n = 1, k = 0;
    const char* p;
    char** out;
    for (p = s; *p; p++) if (*p == sep) n++;
    out = (char**)calloc((size_t)n + 1, sizeof(char*));
    if (!out) { *count = 0; return NULL; }
    p = s;
    for (;;)
    {
        const char* e = strchr(p, sep);
        size_t len = e ? (size_t)(e - p) : strlen(p);
        out[k] = (char*)malloc(len + 1);
        if (out[k]) { memcpy(out[k], p, len); out[k][len] = 0; }
        k++;
        if (!e) break;
        p = e + 1;
    }
    *count = k;
    return out;
}

static void free_split(char** a, int n)
{
    int i;
    if (!a) return;
    for (i = 0; i < n; i++) free(a[i]);
    free(a);
}

static void join_cols(sb* out, char** f, int from, int to)
{
    int i;
    sb_clear(out);
    for (i = from; i < to; i++) { if (i > from) sb_addc(out, ' '); sb_add(out, f[i]); }
}

/* next line of a buffer (handles \n and \r\n); returns NULL at the end */
static char* next_line(char** cursor)
{
    char* s = *cursor;
    char* e;
    if (!s || !*s) return NULL;
    e = strchr(s, '\n');
    if (e) { *e = 0; *cursor = e + 1; } else *cursor = s + strlen(s);
    if (*s && s[strlen(s) - 1] == '\r') s[strlen(s) - 1] = 0;
    return s;
}

static void load_groups(pq* c)
{
    char* text = read_file(c->groups_path, NULL);
    char* cur = text;
    char* line;
    if (!text) return;
    while ((line = next_line(&cur)) != NULL)
    {
        int nf, g;
        char** f = split(line, '\t', &nf);
        if (f && nf >= 2 && parse_int(f[0], &g)) gname_set(c, g, f[1]);
        free_split(f, nf);
    }
    free(text);
}

/* LoadItems: current 7-column format, plus the older 5- and 2-column ones */
static void load_items(pq* c)
{
    char* text;
    char* cur;
    char* line;
    sb tmp = { 0 };
    int kind = u8_kind(c->items_path);
    if (kind == 0) return;
    text = kind == 1 ? read_file(c->items_path, NULL) : NULL;
    if (!text) { c->locked = 1; return; }
    cur = text;
    while ((line = next_line(&cur)) != NULL)
    {
        int nf;
        long long ticks;
        char** f = split(line, '\t', &nf);
        item* it;
        if (!f || nf < 2 || !parse_ll(f[0], &ticks)) { free_split(f, nf); continue; }
        it = (item*)calloc(1, sizeof(item));
        if (!it) { free_split(f, nf); break; }
        it->id = c->next_id++;
        it->created = from_ticks(ticks);
        if (nf >= 5)
        {
            int nh, np, nt, k, sess, g;
            char** hs = split(f[1], US, &nh);
            char** ps = split(f[2], US, &np);
            char** ts = split(f[3], US, &nt);
            for (k = 0; k < nh; k++)
            {
                long long hw = 0;
                const char* p = k < np ? ps[k] : "";
                if (!parse_ll(hs[k], &hw)) hw = 0;
                if (hw == 0 && p[0] == 0) continue;
                add_win(it, (pq_win)hw, p, k < nt ? ts[k] : "");
            }
            free_split(hs, nh); free_split(ps, np); free_split(ts, nt);
            if (nf >= 6 && parse_int(f[5], &sess)) /* 6th column: sessions done, 7th: group id */
            {
                it->sessions = sess;
                it->text = xstrdup(f[4]);
                if (nf >= 7 && parse_int(f[6], &g)) it->group = g;
            }
            else { join_cols(&tmp, f, 4, nf); it->text = xstrdup(sb_str(&tmp)); }
        }
        else { join_cols(&tmp, f, 1, nf); it->text = xstrdup(sb_str(&tmp)); } /* oldest 2-column format */
        insert_at(c, c->n, it);
        free_split(f, nf);
    }
    sb_free(&tmp);
    free(text);
}

static void save(pq* c)
{
    sb s = { 0 };
    int i, k;
    if (!c) return;
    if (!c->locked)
    {
        for (i = 0; i < c->n; i++)
        {
            item* it = c->v[i];
            sb_addll(&s, to_ticks(it->created));
            sb_addc(&s, '\t');
            for (k = 0; k < it->nw; k++) { if (k) sb_addc(&s, US); sb_addll(&s, (long long)it->wins[k].id); }
            sb_addc(&s, '\t');
            for (k = 0; k < it->nw; k++) { if (k) sb_addc(&s, US); clean_into(&s, it->wins[k].proc); }
            sb_addc(&s, '\t');
            for (k = 0; k < it->nw; k++) { if (k) sb_addc(&s, US); clean_into(&s, it->wins[k].title); }
            sb_addc(&s, '\t');
            clean_into(&s, it->text);
            sb_addc(&s, '\t');
            sb_addll(&s, it->sessions);
            sb_addc(&s, '\t');
            sb_addll(&s, it->group);
            sb_addc(&s, '\n');
        }
        write_atomic(c->items_path, sb_str(&s), 1);
    }
    /* names of sections still in use */
    sb_clear(&s);
    for (i = 0; i < c->ngn; i++)
    {
        int used = 0;
        for (k = 0; k < c->n && !used; k++) used = c->v[k]->group == c->gn[i].g;
        if (!used) continue;
        sb_addll(&s, c->gn[i].g);
        sb_addc(&s, '\t');
        clean_into(&s, c->gn[i].name);
        sb_addc(&s, '\n');
    }
    write_atomic(c->groups_path, sb_str(&s), 1);
    sb_free(&s);
}

/* ---------- focus log ---------- */

static const char* LOG_HEADER = "start,end,minutes,task,group,round,planned_min,kind,outcome";

static void csv_into(sb* out, const char* v)
{
    const char* p;
    if (!v) v = "";
    if (!strpbrk(v, ",\"\n\r")) { sb_add(out, v); return; }
    sb_addc(out, '"');
    for (p = v; *p; p++) { if (*p == '"') sb_addc(out, '"'); sb_addc(out, *p); }
    sb_addc(out, '"');
}

typedef struct { char*** rows; int* ncols; int n; } csv;

static void csv_free(csv* t)
{
    int i;
    for (i = 0; i < t->n; i++) free_split(t->rows[i], t->ncols[i]);
    free(t->rows); free(t->ncols);
    t->rows = NULL; t->ncols = NULL; t->n = 0;
}

static void csv_push(csv* t, char** row, int nc)
{
    int i, any = 0;
    for (i = 0; i < nc; i++) if (row[i] && row[i][0]) any = 1;
    if (!any) { free_split(row, nc); return; }
    t->rows = (char***)realloc(t->rows, (size_t)(t->n + 1) * sizeof(char**));
    t->ncols = (int*)realloc(t->ncols, (size_t)(t->n + 1) * sizeof(int));
    t->rows[t->n] = row; t->ncols[t->n] = nc; t->n++;
}

/* ParseCsv: quotes, "" escapes, \n / \r\n / \r line ends, blank rows skipped */
static csv parse_csv(const char* text)
{
    csv t = { 0 };
    char** row = NULL;
    int nc = 0, q = 0;
    sb f = { 0 };
    size_t i, n = strlen(text);
#define PUSH_FIELD() do { row = (char**)realloc(row, (size_t)(nc + 1) * sizeof(char*)); row[nc++] = xstrdup(sb_str(&f)); sb_clear(&f); } while (0)
    for (i = 0; i < n; i++)
    {
        char ch = text[i];
        if (q)
        {
            if (ch == '"') { if (i + 1 < n && text[i + 1] == '"') { sb_addc(&f, '"'); i++; } else q = 0; }
            else sb_addc(&f, ch);
        }
        else if (ch == '"') q = 1;
        else if (ch == ',') PUSH_FIELD();
        else if (ch == '\n' || ch == '\r')
        {
            if (ch == '\r' && i + 1 < n && text[i + 1] == '\n') i++;
            PUSH_FIELD();
            csv_push(&t, row, nc);
            row = NULL; nc = 0;
        }
        else sb_addc(&f, ch);
    }
    if (f.n > 0 || nc > 0) { PUSH_FIELD(); csv_push(&t, row, nc); row = NULL; }
#undef PUSH_FIELD
    free(row);
    sb_free(&f);
    return t;
}

/* values written by old Korean versions */
static const char* code(const char* v)
{
    static const char* map[][2] = { { "기본", "focus" }, { "연장", "extension" }, { "완료", "done" },
                                    { "중지", "stopped" }, { "이탈", "left" }, { "종료", "quit" } };
    int i;
    for (i = 0; i < 6; i++) if (!strcmp(v, map[i][0])) return map[i][1];
    return v;
}

static void ensure_log(pq* c)
{
    sb s = { 0 };
    if (u8_kind(c->log_path) != 0) return;
    sb_add(&s, LOG_HEADER);
    sb_add(&s, "\r\n");
    write_atomic(c->log_path, sb_str(&s), 1); /* BOM: spreadsheet apps read non-ASCII task names */
    sb_free(&s);
}

/* MigrateLog: logs from older versions used Korean headers/values; rewrite once (backup kept) */
static void migrate_log(pq* c)
{
    char* text = read_file(c->log_path, NULL);
    csv t;
    sb s = { 0 }, bak = { 0 };
    int i, j;
    if (!text) return;
    if (strncmp(text, "시작,", strlen("시작,")) != 0) { free(text); return; }
    sb_add(&bak, c->dir); sb_add(&bak, PQ_SEP "focus_log.ko.bak.csv");
    if (!u8_copy(c->log_path, sb_str(&bak))) { free(text); sb_free(&bak); return; }
    t = parse_csv(text);
    sb_add(&s, LOG_HEADER); sb_add(&s, "\r\n");
    for (i = 1; i < t.n; i++)
    {
        char** r = t.rows[i];
        for (j = 0; j < t.ncols[i]; j++)
        {
            const char* v = r[j];
            if (t.ncols[i] >= 9 && (j == 7 || j == 8)) v = code(v);
            if (j) sb_addc(&s, ',');
            csv_into(&s, v);
        }
        sb_add(&s, "\r\n");
    }
    write_atomic(c->log_path, sb_str(&s), 1);
    csv_free(&t);
    sb_free(&s); sb_free(&bak);
    free(text);
}

/* ====================================================================================== */
/* lifecycle and settings                                                                   */
/* ====================================================================================== */

int pq_api_version(void) { return PQ_API_VERSION; }

static char* path_in(const char* dir, const char* name)
{
    sb s = { 0 };
    char* r;
    sb_add(&s, dir);
    if (s.n > 0 && s.p[s.n - 1] != '/' && s.p[s.n - 1] != '\\') sb_add(&s, PQ_SEP);
    sb_add(&s, name);
    r = xstrdup(sb_str(&s));
    sb_free(&s);
    return r;
}

static void update_bar_item(pq* c);
static void normalize_groups(pq* c, item* dropped);

pq* pq_open(const char* data_dir, int system_is_korean, double now)
{
    pq* c = (pq*)calloc(1, sizeof(pq));
    char* t;
    (void)now;
    if (!c) return NULL;
    c->dir = xstrdup(data_dir ? data_dir : ".");
    make_dirs(c->dir);
    c->items_path = path_in(c->dir, "items.tsv");
    c->groups_path = path_in(c->dir, "groups.tsv");
    c->log_path = path_in(c->dir, "focus_log.csv");
    c->lang_path = path_in(c->dir, "lang.txt");
    c->opacity_path = path_in(c->dir, "opacity.txt");
    c->bak_path = path_in(c->dir, "items.bak.tsv");
    c->next_id = 1;
    c->break_min = TIER_BREAK[0];

    /* keep a copy of the queue before anything is written */
    if (u8_kind(c->items_path) == 1)
    {
        size_t len = 0;
        char* probe = read_file(c->items_path, &len);
        if (probe && len > 0) u8_copy(c->items_path, c->bak_path);
        free(probe);
    }

    t = read_file(c->lang_path, NULL);
    if (t)
    {
        char* p = t;
        while (*p == ' ' || *p == '\t') p++;
        c->ko = !strncmp(p, "ko", 2) ? 1 : !strncmp(p, "en", 2) ? 0 : (system_is_korean != 0);
        free(t);
    }
    else c->ko = system_is_korean != 0;

    c->opacity = 0.95;
    t = read_file(c->opacity_path, NULL);
    if (t)
    {
        double v = parse_num(t);
        if (v > 0) c->opacity = v < 0.3 ? 0.3 : v > 1.0 ? 1.0 : v;
        free(t);
    }

    migrate_log(c);
    load_groups(c);
    load_items(c);
    normalize_groups(c, NULL); /* LoadItems(); NormalizeGroups(); */
    return c;
}

static void stop_focus(pq* c, const char* note_key, int warn, const char* outcome, double now);
static void free_stats(pq* c);

void pq_close(pq* c, double now)
{
    int i;
    if (!c) return;
    stop_focus(c, NULL, 0, "quit", now); /* log a running session when the widget closes */
    save(c);
    for (i = 0; i < c->n; i++) free_item(c->v[i]);
    free(c->v);
    for (i = 0; i < c->ngn; i++) free(c->gn[i].name);
    free(c->gn);
    free(c->fw); free(c->dw);
    free(c->note);
    free_stats(c);
    free(c->dir); free(c->items_path); free(c->groups_path); free(c->log_path);
    free(c->lang_path); free(c->opacity_path); free(c->bak_path);
    sb_free(&c->b_fmt); sb_free(&c->b_fmt_min); sb_free(&c->b_age); sb_free(&c->b_tmp);
    free(c);
}

const char* pq_data_dir(pq* c) { return c ? c->dir : ""; }
const char* pq_log_path(pq* c) { if (!c) return ""; ensure_log(c); return c->log_path; }

int pq_lang_ko(pq* c) { return c ? c->ko : 0; }

void pq_set_lang_ko(pq* c, int ko)
{
    if (!c) return;
    c->ko = ko != 0;
    write_atomic(c->lang_path, c->ko ? "ko" : "en", 0);
}

double pq_opacity(pq* c) { return c ? c->opacity : 1.0; }

void pq_set_opacity(pq* c, double v)
{
    char t[32];
    if (!c) return;
    v = floor(v * 100.0 + 0.5) / 100.0;
    if (v < 0.3) v = 0.3;
    if (v > 1.0) v = 1.0;
    c->opacity = v;
    {
        long long h = llround(v * 100);
        snprintf(t, sizeof t, "%lld.%02lld", h / 100, h % 100);
    }
    write_atomic(c->opacity_path, t, 0);
}

/* ====================================================================================== */
/* queue API                                                                                */
/* ====================================================================================== */

int pq_count(pq* c) { return c ? c->n : 0; }
int pq_id_at(pq* c, int index) { return (c && index >= 0 && index < c->n) ? c->v[index]->id : 0; }
int pq_index_of(pq* c, int id) { return c ? idx_of(c, id) : -1; }
const char* pq_text(pq* c, int id) { item* it = by_id(c, id); return it ? it->text : ""; }
double pq_created(pq* c, int id) { item* it = by_id(c, id); return it ? it->created : 0; }
int pq_sessions(pq* c, int id) { item* it = by_id(c, id); return it ? it->sessions : 0; }
int pq_group(pq* c, int id) { item* it = by_id(c, id); return it ? it->group : 0; }
int pq_win_count(pq* c, int id) { item* it = by_id(c, id); return it ? it->nw : 0; }
pq_win pq_win_id(pq* c, int id, int k) { item* it = by_id(c, id); return (it && k >= 0 && k < it->nw) ? it->wins[k].id : 0; }
const char* pq_win_proc(pq* c, int id, int k) { item* it = by_id(c, id); return (it && k >= 0 && k < it->nw) ? it->wins[k].proc : ""; }
const char* pq_win_title(pq* c, int id, int k) { item* it = by_id(c, id); return (it && k >= 0 && k < it->nw) ? it->wins[k].title : ""; }
pq_win pq_last_win(pq* c, int id) { item* it = by_id(c, id); return it ? it->last : 0; }

void pq_set_win_id(pq* c, int id, int k, pq_win w)
{
    item* it = by_id(c, id);
    if (!it || k < 0 || k >= it->nw) return;
    if (it->last == it->wins[k].id) it->last = w;
    it->wins[k].id = w;
}

/* Changed(): save + the bar follows the queue */
static void changed(pq* c) { save(c); update_bar_item(c); }

int pq_add(pq* c, const char* text, int at_top, double now)
{
    item* it;
    sb t = { 0 };
    if (!c) return 0;
    it = (item*)calloc(1, sizeof(item));
    if (!it) return 0;
    clean_into(&t, text);
    it->id = c->next_id++;
    it->text = xstrdup(sb_str(&t));
    sb_free(&t);
    it->created = now;
    insert_at(c, at_top ? 0 : c->n, it); /* Shift+Enter = top */
    changed(c);
    return it->id;
}

void pq_link(pq* c, int id, pq_win w, const char* proc, const char* title, int replace)
{
    item* it = by_id(c, id);
    int k;
    if (!it) return;
    if (replace)
    {
        for (k = 0; k < it->nw; k++) { free(it->wins[k].proc); free(it->wins[k].title); }
        it->nw = 0;
        it->last = 0;
    }
    if (w == 0 || has(it, w)) { changed(c); return; }
    add_win(it, w, proc, title);
    if (it->last == 0) it->last = w; /* Attach: the window you were in gets focus on click */
    changed(c);
}

void pq_remove(pq* c, int id)
{
    int i = c ? idx_of(c, id) : -1;
    if (i < 0) return;
    free_item(take_at(c, i));
    if (c->bar_item == id) c->bar_item = 0;
    if (c->focus_item == id) c->focus_item = 0;
    if (c->last_focus_item == id) c->last_focus_item = 0;
    if (c->done_item == id) c->done_item = 0;
    if (c->warned_item == id) c->warned_item = 0;
    changed(c);
}

void pq_rename(pq* c, int id, const char* text)
{
    item* it = by_id(c, id);
    sb t = { 0 };
    if (!it || !text) return;
    clean_into(&t, text);
    if (t.n > 0) xset(&it->text, sb_str(&t));
    sb_free(&t);
    changed(c);
}

void pq_reset_age(pq* c, int id, double now)
{
    item* it = by_id(c, id);
    if (!it) return;
    it->created = now;
    changed(c);
}

/* NormalizeGroups: dropped between two members -> joins them; a member with no neighbour in its group -> leaves */
static void normalize_groups(pq* c, item* dropped)
{
    int i;
    if (dropped)
    {
        int at = -1;
        for (i = 0; i < c->n; i++) if (c->v[i] == dropped) at = i;
        if (at >= 0)
        {
            item* a = at > 0 ? c->v[at - 1] : NULL;
            item* b = at + 1 < c->n ? c->v[at + 1] : NULL;
            if (same_group(a, b)) dropped->group = a->group;
        }
    }
    {
        int* lone = (int*)calloc((size_t)(c->n + 1), sizeof(int));
        if (!lone) return;
        for (i = 0; i < c->n; i++)
        {
            item* it = c->v[i];
            int up, down;
            if (it->group == 0) continue;
            up = i > 0 && same_group(c->v[i - 1], it);
            down = i + 1 < c->n && same_group(c->v[i + 1], it);
            if (!up && !down) lone[i] = 1;
        }
        for (i = 0; i < c->n; i++) if (lone[i]) c->v[i]->group = 0;
        free(lone);
    }
}

void pq_move(pq* c, int id, int to_index, int live)
{
    int i = c ? idx_of(c, id) : -1;
    item* it;
    if (i < 0) return;
    if (to_index < 0) to_index = 0;
    if (to_index > c->n - 1) to_index = c->n - 1;
    if (to_index != i)
    {
        it = take_at(c, i);
        insert_at(c, to_index, it);
    }
    if (live) return;
    normalize_groups(c, c->v[to_index]);
    changed(c);
}

/* JoinGroup + NormalizeGroups(dropped) */
int pq_join(pq* c, int id, int target, int* created)
{
    item* it = by_id(c, id);
    item* tg = by_id(c, target);
    int i, at, made = 0;
    if (created) *created = 0;
    if (!it || !tg || it == tg) return it ? it->group : 0;
    if (tg->group == 0)
    {
        int mx = 0;
        for (i = 0; i < c->n; i++) if (c->v[i]->group > mx) mx = c->v[i]->group;
        tg->group = mx + 1;
        made = 1;
    }
    it->group = tg->group;
    take_at(c, idx_of(c, id));
    at = idx_of(c, target);
    while (at + 1 < c->n && same_group(c->v[at + 1], tg)) at++;
    insert_at(c, at + 1, it);
    normalize_groups(c, it);
    changed(c);
    if (created) *created = made && it->group != 0;
    return it->group;
}

void pq_leave(pq* c, int id)
{
    item* it = by_id(c, id);
    int g, i, last = -1;
    if (!it || it->group == 0) return;
    g = it->group;
    it->group = 0;
    /* move it just below its old group so the group stays in one piece */
    take_at(c, idx_of(c, id));
    for (i = 0; i < c->n; i++) if (c->v[i]->group == g) last = i;
    insert_at(c, last < 0 ? 0 : last + 1, it);
    normalize_groups(c, NULL);
    changed(c);
}

const char* pq_group_name(pq* c, int g)
{
    const char* n;
    if (!c) return "";
    n = gname_get(c, g);
    if (n && n[0]) return n;
    return pq_tr(c, "group.default");
}

void pq_set_group_name(pq* c, int g, const char* name)
{
    sb t = { 0 };
    if (!c || !name) return;
    clean_into(&t, name);
    if (t.n > 0) gname_set(c, g, sb_str(&t));
    sb_free(&t);
    save(c);
}

int pq_group_size(pq* c, int g)
{
    int i, n = 0;
    if (!c || g == 0) return 0;
    for (i = 0; i < c->n; i++) if (c->v[i]->group == g) n++;
    return n;
}

int pq_first_of_group(pq* c, int index) { return (c && index >= 0 && index < c->n) ? first_of_group(c, index) : 0; }
int pq_last_of_group(pq* c, int index) { return (c && index >= 0 && index < c->n) ? last_of_group(c, index) : 0; }

/* ====================================================================================== */
/* focus task                                                                               */
/* ====================================================================================== */

static int in_list(const pq_win* a, int n, pq_win w)
{
    int i;
    if (w == 0) return 0;
    for (i = 0; i < n; i++) if (a[i] == w) return 1;
    return 0;
}

/* FocusTask(): computed live so items/groups added after Start count too: the focus item, any item
 * linked to a window you started in, and every member of their groups. mark[i] for queue index i. */
static void focus_task(pq* c, char* mark)
{
    int i, k;
    memset(mark, 0, (size_t)c->n + 1);
    if (!c->focusing) return;
    for (i = 0; i < c->n; i++)
    {
        item* it = c->v[i];
        int root = it->id == c->focus_item;
        for (k = 0; k < it->nw && !root; k++) if (in_list(c->fw, c->nfw, it->wins[k].id)) root = 1;
        if (root) mark[i] = 1;
    }
    for (i = 0; i < c->n; i++)
    {
        int j;
        if (mark[i] != 1 || c->v[i]->group == 0) continue;
        for (j = 0; j < c->n; j++) if (!mark[j] && c->v[j]->group == c->v[i]->group) mark[j] = 2;
    }
}

static int in_focus(pq* c, item* it)
{
    int i, r = 0;
    char* mark;
    if (!it || !c->focusing) return 0;
    mark = (char*)malloc((size_t)c->n + 1);
    if (!mark) return 0;
    focus_task(c, mark);
    for (i = 0; i < c->n; i++) if (c->v[i] == it) r = mark[i] != 0;
    free(mark);
    return r;
}

/* FocusOwns: is window w part of the task being focused on? */
static int owns(pq* c, pq_win w)
{
    int i, r = 0;
    char* mark;
    if (w == 0) return 0;
    if (in_list(c->fw, c->nfw, w)) return 1;
    mark = (char*)malloc((size_t)c->n + 1);
    if (!mark) return 0;
    focus_task(c, mark);
    for (i = 0; i < c->n && !r; i++) if (mark[i] && has(c->v[i], w)) r = 1;
    free(mark);
    return r;
}

/* a group is one task: its round is the highest of its members */
static int tier_of(pq* c, item* it)
{
    int i, n;
    if (!it) return 0;
    n = it->sessions;
    for (i = 0; i < c->n; i++) if (same_group(c->v[i], it) && c->v[i]->sessions > n) n = c->v[i]->sessions;
    return n < 0 ? 0 : n > TIERS - 1 ? TIERS - 1 : n;
}

static void set_task_sessions(pq* c, item* it, int v)
{
    int i;
    if (!it) return;
    for (i = 0; i < c->n; i++) if (c->v[i] == it || same_group(c->v[i], it)) c->v[i]->sessions = v;
}

/* UpdateBarItem: the window you're in changed -> the bar shows that task's next default again */
static void update_bar_item(pq* c)
{
    item* lf;
    item* it;
    if (c->focusing) return;
    lf = by_id(c, c->last_focus_item);
    it = (lf && ghas(c, lf, c->current)) ? lf : item_of(c, c->current);
    if ((it ? it->id : 0) != c->bar_item)
    {
        c->bar_item = it ? it->id : 0;
        c->sel_tier = tier_of(c, it);
    }
}

static void set_note_key(pq* c, const char* key, int warn, double now) { pq_set_note(c, pq_tr(c, key), warn, now); }

static void set_wins(pq_win** dst, int* n, const pq_win* src, int count)
{
    free(*dst);
    *dst = NULL; *n = 0;
    if (count <= 0) return;
    *dst = (pq_win*)malloc((size_t)count * sizeof(pq_win));
    if (!*dst) return;
    memcpy(*dst, src, (size_t)count * sizeof(pq_win));
    *n = count;
}

static void push_win(pq_win** dst, int* n, pq_win w)
{
    pq_win* nw;
    if (w == 0 || in_list(*dst, *n, w)) return;
    nw = (pq_win*)realloc(*dst, (size_t)(*n + 1) * sizeof(pq_win));
    if (!nw) return;
    *dst = nw;
    (*dst)[(*n)++] = w;
}

/* ---------- log ---------- */

/* LogSession: one row per focus segment: when, how long (paused time excluded), which task, how it ended */
static void log_session(pq* c, const char* outcome, double now)
{
    double end = c->away ? c->away_start : now;
    double min = ((end - c->freeze_from) - c->paused_sec) / 60.0;
    item* it;
    sb s = { 0 };
    char a[32], b[32], m[32];
    if (min < 0.5) return; /* ignore accidental starts */
    it = by_id(c, c->focus_item);
    if (!it)
    {
        char* mark = (char*)malloc((size_t)c->n + 1);
        int i;
        if (mark)
        {
            focus_task(c, mark);
            for (i = 0; i < c->n && !it; i++) if (mark[i]) it = c->v[i];
            free(mark);
        }
    }
    ensure_log(c);
    stamp(a, sizeof a, c->freeze_from);
    stamp(b, sizeof b, end);
    fmt1(m, sizeof m, min);
    sb_add(&s, a); sb_addc(&s, ',');
    sb_add(&s, b); sb_addc(&s, ',');
    sb_add(&s, m); sb_addc(&s, ',');
    csv_into(&s, it ? it->text : ""); sb_addc(&s, ',');
    csv_into(&s, (it && it->group) ? pq_group_name(c, it->group) : ""); sb_addc(&s, ',');
    sb_addll(&s, c->focus_tier + 1); sb_addc(&s, ',');
    sb_addll(&s, c->planned_min); sb_addc(&s, ',');
    sb_add(&s, c->extension ? "extension" : "focus"); sb_addc(&s, ',');
    sb_add(&s, outcome);
    sb_add(&s, "\r\n");
    append_file(c->log_path, sb_str(&s));
    sb_free(&s);
}

/* ---------- away ---------- */

static void start_away(pq* c, pq_win from, double now)
{
    c->away = 1;
    c->away_start = now;
    c->away_home = (from != 0 && owns(c, from)) ? from : (c->nfw > 0 ? c->fw[0] : 0);
}

static void end_away(pq* c, int resume, double now)
{
    if (!c->away) return;
    c->away = 0;
    if (resume)
    {
        double d = now - c->away_start; /* paused time doesn't count */
        c->focus_end += d;
        c->paused_sec += d;
        set_note_key(c, "note.back", 0, now);
    }
}

/* StopFocus: the time spent focusing doesn't count toward the other items' age */
static void stop_focus(pq* c, const char* note_key, int warn, const char* outcome, double now)
{
    int i;
    double el;
    char* mark;
    if (!c->focusing) return;
    log_session(c, outcome, now); /* before clearing away state: time away isn't focus time */
    if (c->away) end_away(c, 0, now);
    el = now - c->freeze_from;
    mark = (char*)malloc((size_t)c->n + 1);
    if (mark)
    {
        focus_task(c, mark);
        for (i = 0; i < c->n; i++)
        {
            item* it = c->v[i];
            if (mark[i]) continue;
            it->created = it->created < c->freeze_from ? it->created + el : now;
        }
        free(mark);
    }
    c->focusing = 0;
    c->focus_item = 0;
    c->warned_item = 0;
    set_wins(&c->fw, &c->nfw, NULL, 0);
    if (note_key) set_note_key(c, note_key, warn, now);
    save(c);
}

/* ---------- break ---------- */

static void start_break(pq* c, double now)
{
    c->on_break = 1;
    c->break_start = now;
    c->break_end = now + c->break_min * 60.0;
    c->break_hidden = 0;
    c->soon_shown = 0;
}

static void end_break(pq* c, int quiet, double now)
{
    if (!c->on_break) return;
    c->on_break = 0;
    c->break_hidden = 0;
    c->soon_shown = 0;
    if (!quiet) set_note_key(c, "note.break_over", 0, now);
}

/* ====================================================================================== */
/* the window you're in                                                                     */
/* ====================================================================================== */

/* Track() */
int pq_activate(pq* c, pq_win w, const char* title, double now)
{
    pq_win prev;
    int ev = PQ_EV_CHANGED, i, k, dirty = 0;
    if (!c || w == 0 || w == c->current) return 0;
    prev = c->current;
    c->current = w;
    if (c->focusing)
    {
        if (!owns(c, w)) { if (!c->away) { start_away(c, prev, now); ev |= PQ_EV_AWAY_START; } } /* left the task: 1-minute grace */
        else if (c->away) { end_away(c, 1, now); ev |= PQ_EV_AWAY_END; }                         /* came back in time: resume */
    }
    for (i = 0; i < c->n; i++)
    {
        item* it = c->v[i];
        if (has(it, w))
        {
            it->last = w;
            for (k = 0; k < it->nw; k++)
                if (it->wins[k].id == w && title && title[0] && strcmp(it->wins[k].title, title) != 0)
                    xset(&it->wins[k].title, title); /* keep the title fresh for restarts */
        }
        /* entering or leaving the task (or its group) restarts its clock; moving inside the group doesn't */
        if (ghas(c, it, prev) != ghas(c, it, w)) { it->created = now; dirty = 1; }
    }
    if (dirty) save(c);
    update_bar_item(c);
    return ev;
}

pq_win pq_current(pq* c) { return c ? c->current : 0; }
int pq_item_of(pq* c, pq_win w) { item* it = c ? item_of(c, w) : NULL; return it ? it->id : 0; }
int pq_has(pq* c, int id, pq_win w) { return has(by_id(c, id), w); }
int pq_ghas(pq* c, int id, pq_win w) { item* it = by_id(c, id); return it ? ghas(c, it, w) : 0; }
int pq_active(pq* c, int id) { return c ? pq_ghas(c, id, c->current) : 0; }

int pq_needs_entry(pq* c)
{
    if (!c) return 1;
    return c->n == 0 || item_of(c, c->current) == NULL;
}

static int frozen(pq* c, item* it) { return c->focusing && !ghas(c, it, c->current) && !in_focus(c, it); }

double pq_age_min(pq* c, int id, double now)
{
    item* it = by_id(c, id);
    double t;
    if (!it) return 0;
    t = frozen(c, it) ? (it->created < c->freeze_from ? c->freeze_from : it->created) : now;
    return (t - it->created) / 60.0;
}

int pq_heat(pq* c, int id, double now)
{
    item* it = by_id(c, id);
    double m;
    int h;
    if (!it || ghas(c, it, c->current) || frozen(c, it)) return 0;
    m = (now - it->created) / 60.0;
    h = (int)floor(m / 10.0);
    return h < 0 ? 0 : h > 6 ? 6 : h;
}

/* ====================================================================================== */
/* focus timer API                                                                          */
/* ====================================================================================== */

int pq_state(pq* c)
{
    if (!c) return PQ_IDLE;
    if (c->on_break) return PQ_BREAK;
    if (c->focusing) return c->away ? PQ_AWAY : PQ_FOCUS;
    return PQ_IDLE;
}

int pq_tier_count(void) { return TIERS; }
int pq_tier_focus(int tier) { return TIER_FOCUS[tier < 0 ? 0 : tier >= TIERS ? TIERS - 1 : tier]; }
int pq_tier_break(int tier) { return TIER_BREAK[tier < 0 ? 0 : tier >= TIERS ? TIERS - 1 : tier]; }
int pq_tier(pq* c, int id) { return c ? tier_of(c, by_id(c, id)) : 0; }
int pq_bar_item(pq* c) { return (c && by_id(c, c->bar_item)) ? c->bar_item : 0; }
int pq_sel_tier(pq* c) { return c ? c->sel_tier : 0; }

/* StepTier: < > move between rounds; remembered for the task */
void pq_step_tier(pq* c, int delta)
{
    item* b;
    if (!c) return;
    c->sel_tier += delta;
    if (c->sel_tier < 0) c->sel_tier = 0;
    if (c->sel_tier > TIERS - 1) c->sel_tier = TIERS - 1;
    b = by_id(c, c->bar_item);
    if (b) { set_task_sessions(c, b, c->sel_tier); save(c); }
}

void pq_reset_rounds(pq* c, int id)
{
    item* it = by_id(c, id);
    if (!it) return;
    set_task_sessions(c, it, 0);
    if (it->id == c->bar_item || same_group(it, by_id(c, c->bar_item))) c->sel_tier = 0;
    changed(c);
}

/* StartFocus */
int pq_start(pq* c, const pq_win* wins, int n, int test, double now)
{
    item* fi;
    int i, k;
    if (!c || c->focusing || c->on_break || !wins || n <= 0 || wins[0] == 0) return 0;
    set_wins(&c->fw, &c->nfw, wins, n); /* the window you were in + windows snapped beside it */
    fi = item_of(c, wins[0]);
    c->focus_item = fi ? fi->id : 0;
    if (fi) /* the whole group counts as "the task": moving between its windows keeps the timer */
    {
        for (i = 0; i < c->n; i++)
            if (c->v[i] == fi || same_group(c->v[i], fi))
                for (k = 0; k < c->v[i]->nw; k++) push_win(&c->fw, &c->nfw, c->v[i]->wins[k].id);
        c->last_focus_item = fi->id;
    }
    c->focusing = 1;
    c->away = 0;
    c->focus_start = now;
    c->focus_tier = c->sel_tier;
    c->focus_end = now + TIER_FOCUS[c->focus_tier] * 60.0;
    c->freeze_from = now;
    c->paused_sec = 0;
    c->planned_min = TIER_FOCUS[c->focus_tier];
    c->extension = 0;
    c->break_min = TIER_BREAK[c->focus_tier];
    c->warned_item = 0;
    c->note_until = 0;
    if (test) { c->focus_end = now + 10.0; c->planned_min = 0; } /* 10-second test run */
    return 1;
}

void pq_stop(pq* c, double now) { if (c) stop_focus(c, "note.stopped", 0, "stopped", now); }
int pq_in_focus(pq* c, int id) { return c ? in_focus(c, by_id(c, id)) : 0; }
int pq_owns(pq* c, pq_win w) { return c && c->focusing ? owns(c, w) : 0; }

/* Adopt: "this window is part of the task" from the grace card. The current window joins the focus
 * (and the focused item, so it stays part of the task next time); the time spent there counts as focus. */
int pq_adopt(pq* c, const char* proc, const char* title, double now)
{
    item* it;
    pq_win w;
    if (!c || !c->focusing || !c->away || c->current == 0) return 0;
    w = c->current;
    it = item_of(c, c->away_home);
    if (!it) it = by_id(c, c->focus_item);
    if (it && !has(it, w)) add_win(it, w, proc ? proc : "", title ? title : "");
    push_win(&c->fw, &c->nfw, w);
    end_away(c, 0, now);
    set_note_key(c, "note.adopted", 0, now);
    save(c);
    return PQ_EV_AWAY_END | PQ_EV_CHANGED;
}

/* TrySwitch: warn once, second click (within 6 s) goes */
int pq_try_switch(pq* c, int id, double now)
{
    item* it = by_id(c, id);
    if (!c || !it) return 0;
    if (c->focusing && !in_focus(c, it))
    {
        if (c->warned_item != id || now - c->warn_time > WARN_SEC)
        {
            c->warned_item = id;
            c->warn_time = now;
            set_note_key(c, "note.focus_warn", 1, now);
            return 0;
        }
    }
    return 1;
}

pq_win pq_away_home(pq* c) { return c ? c->away_home : 0; }

/* OnTick */
int pq_tick(pq* c, double now)
{
    int ev = 0, i;
    if (!c) return 0;
    if (c->focusing && c->away && now - c->away_start >= AWAY_SEC)
    {
        stop_focus(c, "note.timeout", 1, "left", now);
        ev |= PQ_EV_AWAY_TIMEOUT;
    }
    if (c->focusing && !c->away && now >= c->focus_end)
    {
        char* mark = (char*)malloc((size_t)c->n + 1);
        if (mark) /* next round for the whole task */
        {
            focus_task(c, mark);
            for (i = 0; i < c->n; i++)
                if (mark[i] && c->v[i]->sessions < c->focus_tier + 1) c->v[i]->sessions = c->focus_tier + 1;
            free(mark);
        }
        c->last_focus_item = c->focus_item;
        set_wins(&c->dw, &c->ndw, c->fw, c->nfw);
        c->done_item = c->focus_item;
        c->done_tier = c->focus_tier;
        c->done_start = c->focus_start;
        c->done_end = now;
        c->has_done = 1;
        stop_focus(c, NULL, 0, "done", now);
        c->bar_item = 0;
        update_bar_item(c);
        start_break(c, now);
        ev |= PQ_EV_FOCUS_DONE;
    }
    if (c->on_break)
    {
        if (now >= c->break_end) { end_break(c, 0, now); ev |= PQ_EV_BREAK_END; }
        else if (c->break_hidden && c->break_end - now <= SOON_SEC && !c->soon_shown) { c->soon_shown = 1; ev |= PQ_EV_BREAK_SOON; }
    }
    return ev;
}

int pq_needs_tick(pq* c, double now)
{
    if (!c) return 0;
    return c->focusing || c->on_break || (c->note && now < c->note_until);
}

double pq_focus_left(pq* c, double now)
{
    if (!c || !c->focusing) return 0;
    return c->focus_end - (c->away ? c->away_start : now);
}

double pq_focus_progress(pq* c, double now)
{
    double total, left, d;
    if (!c || !c->focusing) return 0;
    total = c->focus_end - c->focus_start;
    left = c->focus_end - (c->away ? c->away_start : now);
    if (total <= 0) return 1;
    d = 1 - left / total;
    return d < 0 ? 0 : d > 1 ? 1 : d;
}

double pq_away_left(pq* c, double now) { return (c && c->away) ? AWAY_SEC - (now - c->away_start) : 0; }
int pq_is_extension(pq* c) { return c ? c->extension : 0; }

/* ---------- break ---------- */

double pq_break_left(pq* c, double now) { return (c && c->on_break) ? c->break_end - now : 0; }

double pq_break_progress(pq* c, double now)
{
    double d;
    if (!c || !c->on_break || c->break_min <= 0) return 0;
    d = 1 - (c->break_end - now) / (c->break_min * 60.0);
    return d < 0 ? 0 : d > 1 ? 1 : d;
}

int pq_break_hidden(pq* c) { return c ? c->break_hidden : 0; }

/* the break keeps counting with the cover hidden; a small card pops up 10 s before it ends */
void pq_break_hide(pq* c, double now)
{
    if (!c || !c->on_break) return;
    c->break_hidden = 1;
    set_note_key(c, "note.background", 0, now);
}

void pq_break_show(pq* c) { if (c && c->on_break) c->break_hidden = 0; }
void pq_break_skip(pq* c, double now) { if (c) end_break(c, 1, now); }
int pq_can_extend(pq* c) { return c && c->on_break && c->has_done; }

/* ExtendFocus: same task, same round, 5 more minutes; then the break comes back */
void pq_extend(pq* c, double now)
{
    char t[16];
    if (!pq_can_extend(c)) return;
    end_break(c, 1, now);
    set_wins(&c->fw, &c->nfw, c->dw, c->ndw);
    c->focus_item = by_id(c, c->done_item) ? c->done_item : 0;
    c->focus_tier = c->done_tier;
    c->break_min = TIER_BREAK[c->focus_tier];
    c->focusing = 1;
    c->away = 0;
    c->focus_start = now - (c->done_end - c->done_start); /* keep the time already focused on the bar */
    c->focus_end = now + EXTEND_MIN * 60.0;
    c->freeze_from = now;
    c->paused_sec = 0;
    c->planned_min = EXTEND_MIN;
    c->extension = 1;
    c->warned_item = 0;
    snprintf(t, sizeof t, "%d", EXTEND_MIN);
    pq_set_note(c, pq_fmt(c, "note.extended", t, NULL, NULL), 0, now);
}

/* ====================================================================================== */
/* stats (StatsForm)                                                                        */
/* ====================================================================================== */

static void free_stats(pq* c)
{
    int i;
    for (i = 0; i < c->nbk; i++) { free(c->bk_label[i]); free(c->bk_tick[i]); }
    free(c->bk_min); free(c->bk_label); free(c->bk_tick);
    c->bk_min = NULL; c->bk_label = NULL; c->bk_tick = NULL; c->nbk = 0;
    for (i = 0; i < c->ntasks; i++) { free(c->tasks[i].name); free(c->tasks[i].group); }
    free(c->tasks); c->tasks = NULL; c->ntasks = 0;
    for (i = 0; i < c->nrows; i++) { free(c->rows[i].task); free(c->rows[i].group); free(c->rows[i].kind); free(c->rows[i].outcome); }
    free(c->rows); c->rows = NULL; c->nrows = 0;
}

static void add_bucket(pq* c, const char* label, const char* tick)
{
    c->bk_min = (double*)realloc(c->bk_min, (size_t)(c->nbk + 1) * sizeof(double));
    c->bk_label = (char**)realloc(c->bk_label, (size_t)(c->nbk + 1) * sizeof(char*));
    c->bk_tick = (char**)realloc(c->bk_tick, (size_t)(c->nbk + 1) * sizeof(char*));
    c->bk_min[c->nbk] = 0;
    c->bk_label[c->nbk] = xstrdup(label);
    c->bk_tick[c->nbk] = xstrdup(tick);
    c->nbk++;
}

/* stable sort helpers (insertion sort; the lists are small) */
static void sort_rows_newest(rec* r, int n)
{
    int i, j;
    for (i = 1; i < n; i++)
    {
        rec x = r[i];
        for (j = i - 1; j >= 0 && r[j].start < x.start; j--) r[j + 1] = r[j];
        r[j + 1] = x;
    }
}

void pq_stats(pq* c, int days, double now)
{
    char* text;
    csv t = { 0 };
    rec* all = NULL;
    int nall = 0, i, j;
    long long today, first_day;
    long long* seen = NULL;
    int nseen = 0;
    if (!c) return;
    free_stats(c);
    c->st_days_arg = days;
    c->st_total = 0; c->st_sessions = c->st_done = c->st_ext = c->st_left = c->st_days = 0;

    text = read_file(c->log_path, NULL);
    if (text) { t = parse_csv(text); free(text); }
    for (i = 0; i < t.n; i++)
    {
        char** r = t.rows[i];
        double st;
        rec x;
        if (t.ncols[i] < 9 || !parse_stamp(r[0], &st)) continue;
        x.start = st;
        x.min = parse_num(r[2]);
        x.task = xstrdup(r[3]);
        x.group = xstrdup(r[4]);
        if (!parse_int(r[5], &x.round)) x.round = 0;
        if (!parse_int(r[6], &x.planned)) x.planned = 0;
        x.kind = xstrdup(code(r[7]));
        x.outcome = xstrdup(code(r[8]));
        all = (rec*)realloc(all, (size_t)(nall + 1) * sizeof(rec));
        all[nall++] = x;
    }
    csv_free(&t);

    today = day_of(now);
    first_day = today;
    for (i = 0; i < nall; i++) { long long d = day_of(all[i].start); if (d < first_day) first_day = d; }

    /* InRange */
    for (i = 0; i < nall; i++)
    {
        long long d = day_of(all[i].start);
        int in = days == PQ_RANGE_ALL || (days == PQ_RANGE_TODAY ? d >= today : d >= today - (days - 1));
        if (!in)
        {
            free(all[i].task); free(all[i].group); free(all[i].kind); free(all[i].outcome);
            continue;
        }
        c->rows = (rec*)realloc(c->rows, (size_t)(c->nrows + 1) * sizeof(rec));
        c->rows[c->nrows++] = all[i];
    }
    free(all);

    for (i = 0; i < c->nrows; i++)
    {
        rec* r = &c->rows[i];
        long long d = day_of(r->start);
        int dup = 0;
        c->st_total += r->min;
        for (j = 0; j < nseen; j++) if (seen[j] == d) dup = 1;
        if (!dup) { seen = (long long*)realloc(seen, (size_t)(nseen + 1) * sizeof(long long)); seen[nseen++] = d; }
        if (!strcmp(r->outcome, "done")) c->st_done++;
        if (!strcmp(r->kind, "extension")) c->st_ext++;
        if (!strcmp(r->outcome, "left")) c->st_left++;
    }
    free(seen);
    c->st_sessions = c->nrows;
    c->st_days = nseen;

    /* chart */
    if (days == PQ_RANGE_TODAY)
    {
        c->st_hourly = 1;
        for (i = 0; i < 24; i++)
        {
            char h[16];
            snprintf(h, sizeof h, "%d", i);
            fill(&c->b_tmp, pq_tr(c, "stats.hour"), h, NULL, NULL);
            add_bucket(c, sb_str(&c->b_tmp), i % 4 == 0 ? sb_str(&c->b_tmp) : "");
        }
        for (i = 0; i < c->nrows; i++)
        {
            struct tm tm;
            if (local_tm(c->rows[i].start, &tm)) c->bk_min[tm.tm_hour] += c->rows[i].min;
        }
    }
    else
    {
        int n = days == PQ_RANGE_ALL ? 14 : days, every;
        c->st_hourly = 0;
        if (days == PQ_RANGE_ALL && first_day < today + 1 && c->nrows > 0)
        {
            long long span = today - first_day + 1;
            n = (int)(span < 7 ? 7 : span > 90 ? 90 : span);
        }
        if (n < 1) n = 1;
        every = n > 14 ? (n + 6) / 7 : 1;
        for (i = n - 1; i >= 0; i--)
        {
            long long d = today - i;
            int jj = n - 1 - i, y, mo, dd;
            char label[64], tick[32];
            civil_from_days(d, &y, &mo, &dd);
            snprintf(label, sizeof label, "%d/%d (%s)", mo, dd, c->ko ? WD_KO[weekday_of_day(d)] : WD_EN[weekday_of_day(d)]);
            snprintf(tick, sizeof tick, "%d/%d", mo, dd);
            add_bucket(c, label, (jj % every == 0 || i == 0) ? tick : "");
        }
        for (i = 0; i < c->nrows; i++)
        {
            long long k = day_of(c->rows[i].start) - (today - (n - 1));
            if (k >= 0 && k < n) c->bk_min[k] += c->rows[i].min;
        }
    }

    /* tasks, most focus first */
    {
        task_row* by = NULL;
        int nb = 0;
        for (i = 0; i < c->nrows; i++)
        {
            rec* r = &c->rows[i];
            int f = -1;
            for (j = 0; j < nb && f < 0; j++) if (!strcmp(by[j].name, r->task) && !strcmp(by[j].group, r->group)) f = j;
            if (f < 0)
            {
                by = (task_row*)realloc(by, (size_t)(nb + 1) * sizeof(task_row));
                by[nb].name = xstrdup(r->task);
                by[nb].group = xstrdup(r->group);
                by[nb].min = 0;
                by[nb].sessions = 0;
                f = nb++;
            }
            by[f].min += r->min;
            by[f].sessions++;
        }
        for (i = 1; i < nb; i++) /* stable: most minutes first */
        {
            task_row x = by[i];
            for (j = i - 1; j >= 0 && by[j].min < x.min; j--) by[j + 1] = by[j];
            by[j + 1] = x;
        }
        for (i = 0; i < nb; i++)
        {
            if (i == 5 && nb > 6)
            {
                double rest = 0;
                int rn = 0;
                char cnt[16];
                for (j = 5; j < nb; j++) { rest += by[j].min; rn += by[j].sessions; }
                snprintf(cnt, sizeof cnt, "%d", nb - 5);
                fill(&c->b_tmp, pq_tr(c, "stats.tasks.others"), cnt, NULL, NULL);
                c->tasks = (task_row*)realloc(c->tasks, (size_t)(c->ntasks + 1) * sizeof(task_row));
                c->tasks[c->ntasks].name = xstrdup(sb_str(&c->b_tmp));
                c->tasks[c->ntasks].group = xstrdup("");
                c->tasks[c->ntasks].min = rest;
                c->tasks[c->ntasks].sessions = rn;
                c->ntasks++;
                break;
            }
            c->tasks = (task_row*)realloc(c->tasks, (size_t)(c->ntasks + 1) * sizeof(task_row));
            c->tasks[c->ntasks].name = xstrdup(by[i].name);
            c->tasks[c->ntasks].group = xstrdup(by[i].group);
            c->tasks[c->ntasks].min = by[i].min;
            c->tasks[c->ntasks].sessions = by[i].sessions;
            c->ntasks++;
        }
        for (i = 0; i < nb; i++) { free(by[i].name); free(by[i].group); }
        free(by);
    }

    sort_rows_newest(c->rows, c->nrows);
}

double pq_st_total(pq* c) { return c ? c->st_total : 0; }
int pq_st_sessions(pq* c) { return c ? c->st_sessions : 0; }
int pq_st_done(pq* c) { return c ? c->st_done : 0; }
int pq_st_extensions(pq* c) { return c ? c->st_ext : 0; }
int pq_st_left(pq* c) { return c ? c->st_left : 0; }
int pq_st_days(pq* c) { return c ? c->st_days : 0; }
int pq_st_hourly(pq* c) { return c ? c->st_hourly : 0; }
int pq_st_bucket_count(pq* c) { return c ? c->nbk : 0; }
double pq_st_bucket_min(pq* c, int i) { return (c && i >= 0 && i < c->nbk) ? c->bk_min[i] : 0; }
const char* pq_st_bucket_label(pq* c, int i) { return (c && i >= 0 && i < c->nbk) ? c->bk_label[i] : ""; }
const char* pq_st_bucket_tick(pq* c, int i) { return (c && i >= 0 && i < c->nbk) ? c->bk_tick[i] : ""; }
int pq_st_task_count(pq* c) { return c ? c->ntasks : 0; }

/* an empty task name (focus with no task) shows as "(no task)" */
static const char* task_label(pq* c, const char* t) { return (t && t[0]) ? t : pq_tr(c, "stats.no_task"); }

const char* pq_st_task_name(pq* c, int i) { return (c && i >= 0 && i < c->ntasks) ? task_label(c, c->tasks[i].name) : ""; }
const char* pq_st_task_group(pq* c, int i) { return (c && i >= 0 && i < c->ntasks) ? c->tasks[i].group : ""; }
double pq_st_task_min(pq* c, int i) { return (c && i >= 0 && i < c->ntasks) ? c->tasks[i].min : 0; }
int pq_st_task_sessions(pq* c, int i) { return (c && i >= 0 && i < c->ntasks) ? c->tasks[i].sessions : 0; }
int pq_st_row_count(pq* c) { return c ? c->nrows : 0; }
double pq_st_row_start(pq* c, int i) { return (c && i >= 0 && i < c->nrows) ? c->rows[i].start : 0; }
const char* pq_st_row_task(pq* c, int i) { return (c && i >= 0 && i < c->nrows) ? task_label(c, c->rows[i].task) : ""; }
const char* pq_st_row_group(pq* c, int i) { return (c && i >= 0 && i < c->nrows) ? c->rows[i].group : ""; }
double pq_st_row_min(pq* c, int i) { return (c && i >= 0 && i < c->nrows) ? c->rows[i].min : 0; }
int pq_st_row_planned(pq* c, int i) { return (c && i >= 0 && i < c->nrows) ? c->rows[i].planned : 0; }
const char* pq_st_row_kind(pq* c, int i) { return (c && i >= 0 && i < c->nrows) ? c->rows[i].kind : ""; }
const char* pq_st_row_outcome(pq* c, int i) { return (c && i >= 0 && i < c->nrows) ? c->rows[i].outcome : ""; }
