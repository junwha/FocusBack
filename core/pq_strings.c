/* Every UI string, English and Korean. Keys are used by both frontends via pq_tr / pq_fmt.
 * {0} {1} {2} are filled by pq_fmt. Keep the two columns in step when adding a string. */
#include "pq_strings.h"

const pq_string PQ_STRINGS[] = {
    /* widget */
    { "header.title",        "To-do",                                   "할 일" },
    { "entry.placeholder",   "+ task for this window",                  "+ 이 창의 할 일" },
    { "item.working",        "● working",                               "● 작업 중" },
    { "item.focusing",       "● focusing",                              "● 집중 중" },
    { "item.no_window",      "(no linked window)",                      "(연결된 창 없음)" },
    { "group.default",       "Group",                                   "묶음" },

    /* menu */
    { "menu.go",             "Go to this window (click / Enter)",       "이 창으로 이동 (클릭 / Enter)" },
    { "menu.done",           "Done (Shift+click / Del)",                "완료 (Shift+클릭 / Del)" },
    { "menu.done_mac",       "Done (Shift+click / Delete)",             "완료 (Shift+클릭 / Delete)" },
    { "menu.link_prev",      "+ Also link the previous window",         "+ 직전 창도 이 할 일에 연결" },
    { "menu.relink",         "Re-link windows (previous + neighbor)",   "창 연결 다시 하기 (직전 창 + 옆 창)" },
    { "menu.leave_group",    "Remove from group",                       "묶음에서 빼기" },
    { "menu.to_top",         "Move to top",                             "맨 위로" },
    { "menu.reset_timer",    "Reset timer",                             "타이머 리셋" },
    { "menu.reset_rounds",   "Restart focus rounds (from 15 min)",      "집중 회차 처음으로 (15분부터)" },
    { "menu.stats",          "Stats",                                   "통계 보기" },
    { "menu.open_log",       "Open log sheet (CSV)",                    "기록 시트 열기 (CSV)" },
    { "menu.opacity",        "Opacity",                                 "투명도" },
    { "menu.language",       "Language",                                "언어" },
    { "menu.quit",           "Quit",                                    "종료" },

    /* focus bar */
    { "bar.start",           "▶ Start",                                 "▶ 시작" },
    { "bar.stop",            "■ Stop",                                  "■ 그만" },
    { "bar.minutes",         "{0} min",                                 "{0}분" },
    { "bar.round",           "R{0} · {1}m rest",                        "{0}회차 · 휴식 {1}분" },
    { "bar.focus_left",      "Focus  {0}  left",                        "집중  {0}  남음" },
    { "bar.paused",          "⏸ Paused · back within {0}",              "⏸ 일시정지 · {0} 안에 복귀" },
    { "bar.break",           "Break  {0}",                              "휴식 중  {0}" },
    { "bar.break_hidden",    "Break  {0}  · click: break screen",       "휴식 중  {0}  · 클릭: 휴식 화면" },

    /* notes on the bar */
    { "note.focus_warn",     "⚠ Focusing! Click again to switch (1 min grace)", "⚠ 집중 중! 한 번 더 누르면 이동 (1분 유예)" },
    { "note.stopped",        "Focus stopped",                           "집중 중지" },
    { "note.back",           "Welcome back · focus continues",          "돌아왔어요 · 집중 이어서" },
    { "note.adopted",        "Window added to the task · focus continues", "이 창을 작업에 넣었어요 · 집중 이어서" },
    { "note.timeout",        "Didn't come back within 1 min · focus reset", "1분 안에 안 돌아와서 집중 초기화" },
    { "note.extended",       "+{0} min · break comes back after",       "+{0}분 연장 · 끝나면 다시 휴식" },
    { "note.background",     "Resting in the background · alert 10 s before the end", "백그라운드로 휴식 중 · 끝나기 10초 전에 알려줄게요" },
    { "note.break_over",     "Break over! Ready to start again?",       "휴식 끝! 다시 시작해볼까요" },

    /* 1-minute grace card */
    { "away.title",          "⏸  You're focusing",                      "⏸  집중 중이에요" },
    { "away.body",           "Come back within 1 min to keep the timer", "1분 안에 돌아가면 타이머가 이어져요" },
    { "away.back",           "↩ Go back",                               "↩ 돌아가기" },
    { "away.adopt",          "+ Part of task",                          "+ 작업에 포함" },
    { "away.stop",           "■ Stop",                                  "■ 그만하기" },

    /* break screen */
    { "break.title",         "Break time",                              "휴식 시간" },
    { "break.body",          "Look away from the screen, get up, drink water, stretch", "화면에서 눈을 떼고, 일어나서 물 한 잔 + 스트레칭" },
    { "break.extend",        "+{0} min focus",                          "+{0}분 더 집중" },
    { "break.background",    "Rest in background",                      "백그라운드로 쉬기" },
    { "break.skip",          "Urgent? Double-click here to skip the break", "급한 일이면 여기를 더블클릭해서 건너뛰기" },
    { "soon.title",          "Break is almost over",                    "휴식이 곧 끝나요" },
    { "soon.body",           "Get ready to come back · click to close", "자리로 돌아올 준비 · 클릭하면 닫혀요" },

    /* stats window */
    { "stats.title",         "Focus log",                               "집중 기록" },
    { "stats.subtitle",      "{0} · {1} sessions",                      "{0} · 세션 {1}개" },
    { "stats.range.today",   "Today",                                   "오늘" },
    { "stats.range.7",       "Last 7 days",                             "최근 7일" },
    { "stats.range.30",      "Last 30 days",                            "최근 30일" },
    { "stats.range.all",     "All time",                                "전체 기간" },
    { "stats.chip.today",    "Today",                                   "오늘" },
    { "stats.chip.7",        "7d",                                      "7일" },
    { "stats.chip.30",       "30d",                                     "30일" },
    { "stats.chip.all",      "All",                                     "전체" },
    { "stats.kpi.total",     "Total focus",                             "총 집중" },
    { "stats.kpi.avg_day",   "avg/day {0}",                             "하루 평균 {0}" },
    { "stats.kpi.sessions",  "Sessions",                                "세션" },
    { "stats.kpi.ext",       "incl. {0} extensions",                    "연장 {0}번 포함" },
    { "stats.kpi.completed", "Completed",                               "끝까지 완료" },
    { "stats.kpi.done_of",   "{0} / {1} sessions",                      "{0} / {1} 세션" },
    { "stats.kpi.avg",       "Avg session",                             "평균 세션" },
    { "stats.kpi.left",      "left > 1 min: {0}",                       "1분 넘게 이탈 {0}번" },
    { "stats.chart.hourly",  "Focus by hour (today)",                   "시간대별 집중 (오늘)" },
    { "stats.chart.daily",   "Focus per day",                           "일별 집중 시간" },
    { "stats.chart.none",    "no focus",                                "기록 없음" },
    { "stats.hour",          "{0}:00",                                  "{0}시" },
    { "stats.tasks",         "Focus by task",                           "할 일별 집중" },
    { "stats.tasks.empty",   "No sessions yet\n▶ Start to focus",       "아직 기록이 없어요\n▶ 시작으로 집중해 보세요" },
    { "stats.tasks.others",  "{0} others",                              "기타 {0}개" },
    { "stats.tasks.tip",     "{0} · {1} · {2} sessions",                "{0} · {1} · 세션 {2}개" },
    { "stats.recent",        "Recent sessions",                         "최근 세션" },
    { "stats.open_log",      "Open log sheet ↗",                        "기록 시트 열기 ↗" },
    { "stats.col.start",     "Start",                                   "시작" },
    { "stats.col.task",      "Task",                                    "할 일" },
    { "stats.col.group",     "Group",                                   "묶음" },
    { "stats.col.focus",     "Focus",                                   "집중" },
    { "stats.col.result",    "Result",                                  "결과" },
    { "stats.row.min",       "{0} / {1} min",                           "{0}분 / {1}" },
    { "stats.row.ext",       " · ext.",                                 " · 연장" },
    { "stats.empty",         "No sessions in this period",              "이 기간에는 기록이 없어요" },
    { "stats.no_task",       "(no task)",                               "(할 일 없음)" },
    { "log.sheet",           "Log sheet",                               "기록 시트" },

    /* log outcomes */
    { "outcome.done",        "done",                                    "완료" },
    { "outcome.stopped",     "stopped",                                 "중지" },
    { "outcome.left",        "left",                                    "이탈" },
    { "outcome.quit",        "closed",                                  "종료" },

    /* time formats (pq_fmt_min) */
    { "time.min",            "{0} min",                                 "{0}분" },
    { "time.hm",             "{0}h {1}m",                               "{0}시간 {1}분" },
    { "time.h",              "{0}h",                                    "{0}시간" },

    /* macOS only */
    { "mac.ax_notice",       "Allow Accessibility to link tasks to windows", "창 단위 연결을 하려면 손쉬운 사용을 허용하세요" },
};

const int PQ_STRING_COUNT = (int)(sizeof(PQ_STRINGS) / sizeof(PQ_STRINGS[0]));
