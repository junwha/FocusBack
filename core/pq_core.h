/*
 * pq_core - the platform-independent core of FocusBack (task queue + focus timer).
 *
 * The core owns all state and rules: the task queue and groups, the files in the data folder,
 * focus rounds, the focus / away (1-minute grace) / break state machine, the focus log,
 * stats, and every UI string in English and Korean.
 *
 * A frontend (Windows: C# WinForms, macOS: Swift/AppKit) only does what is platform-specific:
 * tracking which window is in front, drawing, input, timers and showing windows.
 * It reports what happens (pq_activate, pq_start, pq_tick, ...) and draws what the core says.
 *
 * Conventions
 *  - Strings are UTF-8. Returned `const char*` stay valid until the next call that modifies
 *    the same object or the next call of the same function; copy them right away.
 *  - Time is `double now` = seconds since the Unix epoch (wall clock). The core never reads
 *    the clock itself, so behaviour is testable. Local-time conversion uses the C runtime.
 *  - Items are addressed by a stable positive id (never reused while the process runs).
 *    Index = position in the queue, 0-based. 0 means "no item".
 *  - Window ids (`pq_win`) are opaque to the core: HWND on Windows, CGWindowID on macOS.
 *    0 = no window (on macOS also "the whole app" when per-window tracking is off).
 *  - Every mutating call saves to disk when it changes persistent data.
 */
#ifndef PQ_CORE_H
#define PQ_CORE_H

#include <stdint.h>

#ifdef _WIN32
#  define PQ_API __declspec(dllexport)
#else
#  define PQ_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define PQ_API_VERSION 2

typedef struct pq pq;
typedef int64_t pq_win;

/* ---------- lifecycle ---------- */

PQ_API int         pq_api_version(void);                 /* == PQ_API_VERSION the core was built with */

/* Opens (creates) the data folder: loads items.tsv, groups.tsv, lang.txt, opacity.txt and converts
 * an old Korean-header focus_log.csv once (backup focus_log.ko.bak.csv). Copies items.tsv to
 * items.bak.tsv before anything is written. If items.tsv exists but cannot be read, the core never
 * overwrites it (all saves fail silently). system_is_korean = the OS display language is Korean;
 * used for the default language when lang.txt doesn't exist. Returns NULL only on allocation failure. */
PQ_API pq*         pq_open(const char* data_dir, int system_is_korean, double now);
/* Logs a running focus segment with outcome "quit", saves, frees. */
PQ_API void        pq_close(pq* c, double now);
PQ_API const char* pq_data_dir(pq* c);
PQ_API const char* pq_log_path(pq* c);                    /* <data_dir>/focus_log.csv (created with header if missing) */

/* ---------- settings ---------- */

PQ_API int         pq_lang_ko(pq* c);                     /* 1 = Korean UI, 0 = English */
PQ_API void        pq_set_lang_ko(pq* c, int ko);         /* saved to lang.txt ("en" / "ko") */
PQ_API double      pq_opacity(pq* c);                     /* 0.3 .. 1.0, default 0.95 */
PQ_API void        pq_set_opacity(pq* c, double v);       /* clamped, rounded to 0.01, saved to opacity.txt */

/* ---------- strings (see pq_strings.c) ---------- */

/* Localized text for a key in the current language, e.g. pq_tr(c, "bar.start") -> "▶ Start".
 * Templates contain {0}, {1}, ... ; fill them with pq_fmt. Unknown key -> the key itself. */
PQ_API const char* pq_tr(pq* c, const char* key);
/* pq_tr + replace {0} {1} {2} with the given arguments (NULL = unused). */
PQ_API const char* pq_fmt(pq* c, const char* key, const char* a0, const char* a1, const char* a2);
PQ_API const char* pq_fmt_min(pq* c, double minutes);    /* "45 min" / "1h 30m"  |  "45분" / "1시간 30분" */
PQ_API const char* pq_fmt_clock(double seconds);          /* "m:ss", negative -> "0:00" */
PQ_API const char* pq_fmt_age(pq* c, double minutes);     /* badge: "7m" / "2h13" */
PQ_API const char* pq_outcome_label(pq* c, const char* outcome); /* "done" -> "done" / "완료" */
PQ_API const char* pq_weekday(pq* c, double t);           /* "Mon" / "월" (local time) */
PQ_API const char* pq_fmt_stamp(double t);                /* "11/19 13:22" (local time) */

/* ---------- queue ---------- */

PQ_API int         pq_count(pq* c);
PQ_API int         pq_id_at(pq* c, int index);            /* 0 if out of range */
PQ_API int         pq_index_of(pq* c, int id);            /* -1 if unknown */
PQ_API const char* pq_text(pq* c, int id);
PQ_API double      pq_created(pq* c, int id);             /* start of the item's clock */
PQ_API int         pq_sessions(pq* c, int id);            /* completed focus sessions (per item, see pq_tier) */
PQ_API int         pq_group(pq* c, int id);               /* 0 = not in a group */

PQ_API int         pq_win_count(pq* c, int id);
PQ_API pq_win      pq_win_id(pq* c, int id, int k);
PQ_API const char* pq_win_proc(pq* c, int id, int k);     /* Windows: process name; macOS: bundle id */
PQ_API const char* pq_win_title(pq* c, int id, int k);
PQ_API pq_win      pq_last_win(pq* c, int id);            /* the item's window you used last (0 = none) */
/* After a restart the stored window ids are stale: the frontend re-finds each window (by id, then
 * title, then process) and writes the live id back. Not saved until the next real change. */
PQ_API void        pq_set_win_id(pq* c, int id, int k, pq_win w);

/* Add a task at the end (or the top) of the queue; returns its id. Not linked to a window yet. */
PQ_API int         pq_add(pq* c, const char* text, int at_top, double now);
/* Link a window to the item. replace=1 first removes all its windows (re-link). Duplicates ignored. */
PQ_API void        pq_link(pq* c, int id, pq_win w, const char* proc, const char* title, int replace);
PQ_API void        pq_remove(pq* c, int id);              /* = done */
PQ_API void        pq_rename(pq* c, int id, const char* text);
PQ_API void        pq_reset_age(pq* c, int id, double now);
/* Move to a position. live=1 while dragging (no group fix-up, not saved);
 * live=0 = final drop: dropped between two members of a group -> joins it; a member left without
 * neighbours of its group -> leaves the group; then saved. */
PQ_API void        pq_move(pq* c, int id, int to_index, int live);
/* Drop `id` onto `target`: both end up in one group (target's, or a new one), id placed right after
 * the target's group. Returns the group id; *created = 1 if a new group was made (ask for its name). */
PQ_API int         pq_join(pq* c, int id, int target, int* created);
PQ_API void        pq_leave(pq* c, int id);               /* placed just below its old group */

PQ_API const char* pq_group_name(pq* c, int g);           /* localized default ("Group" / "묶음") if unnamed */
PQ_API void        pq_set_group_name(pq* c, int g, const char* name);
PQ_API int         pq_group_size(pq* c, int g);
PQ_API int         pq_first_of_group(pq* c, int index);   /* row starts a section (draw its header) */
PQ_API int         pq_last_of_group(pq* c, int index);    /* row ends a section */

/* ---------- the window you're in ---------- */

#define PQ_EV_CHANGED      0x01  /* queue display changed (ages reset, titles) -> redraw */
#define PQ_EV_AWAY_START   0x02  /* left the focused task: show the 1-minute grace card */
#define PQ_EV_AWAY_END     0x04  /* came back in time: hide the card, focus continues */
#define PQ_EV_FOCUS_DONE   0x08  /* focus segment finished: a break started, show the break screen */
#define PQ_EV_AWAY_TIMEOUT 0x10  /* didn't come back within 1 minute: focus was reset */
#define PQ_EV_BREAK_SOON   0x20  /* break hidden and <= 10 s left: show the "almost over" card (once) */
#define PQ_EV_BREAK_END    0x40  /* break finished: close the break screen */

/* Report the window now in front (the frontend filters out its own windows, the desktop, taskbar...).
 * title = its current title (kept fresh for re-finding after a restart). Returns PQ_EV_* flags. */
PQ_API int         pq_activate(pq* c, pq_win w, const char* title, double now);
PQ_API pq_win      pq_current(pq* c);                     /* last reported window */
PQ_API int         pq_item_of(pq* c, pq_win w);           /* first item linked to w, 0 = none */
PQ_API int         pq_has(pq* c, int id, pq_win w);       /* w is one of the item's windows */
PQ_API int         pq_ghas(pq* c, int id, pq_win w);      /* ... or of any item in its group */
PQ_API int         pq_active(pq* c, int id);              /* = pq_ghas(id, current): draw as "working" */
PQ_API int         pq_needs_entry(pq* c);                 /* current window has no item: show the ghost entry */

/* Badge minutes (frozen at the focus start for items outside the focused task). */
PQ_API double      pq_age_min(pq* c, int id, double now);
/* Redness 0..6 (one step per 10 minutes); 0 while frozen or active. */
PQ_API int         pq_heat(pq* c, int id, double now);

/* ---------- focus timer ---------- */

#define PQ_IDLE   0
#define PQ_FOCUS  1
#define PQ_AWAY   2   /* focusing, but in the 1-minute grace */
#define PQ_BREAK  3

PQ_API int         pq_state(pq* c);
PQ_API int         pq_tier_count(void);                   /* 3 */
PQ_API int         pq_tier_focus(int tier);               /* 15, 30, 60 minutes */
PQ_API int         pq_tier_break(int tier);               /* 3, 5, 15 minutes */
PQ_API int         pq_tier(pq* c, int id);                /* a task's next round: max sessions in its group, capped */
PQ_API int         pq_bar_item(pq* c);                    /* task the bar is about (0 = none) */
PQ_API int         pq_sel_tier(pq* c);                    /* round selected on the bar */
PQ_API void        pq_step_tier(pq* c, int delta);        /* < > ; remembered for the bar's task (whole group) */
PQ_API void        pq_reset_rounds(pq* c, int id);        /* back to round 1 for the item's task */

/* Start focusing in the current window. wins = the current window plus windows snapped beside it
 * (wins[0] must be the current window). test=1: a 10-second run (planned 0 min). Returns 0 if there
 * is no current window. */
PQ_API int         pq_start(pq* c, const pq_win* wins, int n, int test, double now);
PQ_API void        pq_stop(pq* c, double now);            /* user stop, logged "stopped" */
PQ_API int         pq_in_focus(pq* c, int id);            /* item belongs to the task being focused on */
PQ_API int         pq_owns(pq* c, pq_win w);              /* window belongs to it */
/* Grace card "part of this task": the current window joins the focused task (saved with its item) and
 * the focus continues as if you never left. Returns PQ_EV_* flags, 0 if not away. */
PQ_API int         pq_adopt(pq* c, const char* proc, const char* title, double now);
/* Clicking another item while focusing: returns 1 = go ahead and switch, 0 = the core set the warning
 * note ("note.focus_warn"); a second click on the same item within 6 s returns 1. */
PQ_API int         pq_try_switch(pq* c, int id, double now);
PQ_API pq_win      pq_away_home(pq* c);                   /* window to go back to from the grace card */

/* Drive the clock (call ~4x per second while pq_needs_tick). Returns PQ_EV_* flags. */
PQ_API int         pq_tick(pq* c, double now);
PQ_API int         pq_needs_tick(pq* c, double now);      /* focusing, away, on a break, or a note showing */

PQ_API double      pq_focus_left(pq* c, double now);      /* seconds */
PQ_API double      pq_focus_progress(pq* c, double now);  /* 0..1 of the bar, extension keeps earlier time */
PQ_API double      pq_away_left(pq* c, double now);       /* seconds of grace left */
PQ_API int         pq_is_extension(pq* c);

/* ---------- break ---------- */

PQ_API double      pq_break_left(pq* c, double now);
PQ_API double      pq_break_progress(pq* c, double now);
PQ_API int         pq_break_hidden(pq* c);
PQ_API void        pq_break_hide(pq* c, double now);      /* "Rest in background"; may raise BREAK_SOON on next tick */
PQ_API void        pq_break_show(pq* c);                  /* clicking the bar brings the break screen back */
PQ_API void        pq_break_skip(pq* c, double now);      /* emergency skip (no "break over" note) */
PQ_API int         pq_can_extend(pq* c);                  /* the break came from a finished focus segment */
PQ_API void        pq_extend(pq* c, double now);          /* +5 min on the same task and round, then the break again */

/* ---------- bar note (short message on the bar, ~4 s) ----------
 * The core sets these itself on state changes (focus stopped, welcome back, didn't come back,
 * +5 min, resting in background, break over, the switch warning); the frontend only draws them. */

PQ_API const char* pq_note(pq* c, double now);            /* NULL when nothing to show */
PQ_API int         pq_note_warn(pq* c);                   /* show it in red */
PQ_API void        pq_set_note(pq* c, const char* text, int warn, double now);

/* ---------- stats (reads focus_log.csv) ---------- */

#define PQ_RANGE_TODAY 0
#define PQ_RANGE_ALL  -1
/* Recompute for the last `days` days (7, 30), PQ_RANGE_TODAY or PQ_RANGE_ALL. */
PQ_API void        pq_stats(pq* c, int days, double now);
PQ_API double      pq_st_total(pq* c);                    /* minutes */
PQ_API int         pq_st_sessions(pq* c);
PQ_API int         pq_st_done(pq* c);
PQ_API int         pq_st_extensions(pq* c);
PQ_API int         pq_st_left(pq* c);
PQ_API int         pq_st_days(pq* c);                     /* distinct days with focus */
/* chart: 24 hourly buckets for today, otherwise one per day (all time: 7..90 days) */
PQ_API int         pq_st_hourly(pq* c);
PQ_API int         pq_st_bucket_count(pq* c);
PQ_API double      pq_st_bucket_min(pq* c, int i);
PQ_API const char* pq_st_bucket_label(pq* c, int i);      /* tooltip: "11/19 (Wed)" / "14:00" */
PQ_API const char* pq_st_bucket_tick(pq* c, int i);       /* axis label or "" */
/* tasks, most focus first: top 5 + "N others" when there are more than 6 */
PQ_API int         pq_st_task_count(pq* c);
PQ_API const char* pq_st_task_name(pq* c, int i);
PQ_API const char* pq_st_task_group(pq* c, int i);
PQ_API double      pq_st_task_min(pq* c, int i);
PQ_API int         pq_st_task_sessions(pq* c, int i);
/* sessions, newest first */
PQ_API int         pq_st_row_count(pq* c);
PQ_API double      pq_st_row_start(pq* c, int i);
PQ_API const char* pq_st_row_task(pq* c, int i);
PQ_API const char* pq_st_row_group(pq* c, int i);
PQ_API double      pq_st_row_min(pq* c, int i);
PQ_API int         pq_st_row_planned(pq* c, int i);
PQ_API const char* pq_st_row_kind(pq* c, int i);          /* "focus" | "extension" */
PQ_API const char* pq_st_row_outcome(pq* c, int i);       /* "done" | "stopped" | "left" | "quit" */

#ifdef __cplusplus
}
#endif
#endif
