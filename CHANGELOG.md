# Changelog

## 0.5.2

### Features

- Scope the history to one account. The account list on the left drives the history list as well as the dashboard: clicking an account shows only that account's tasks and 全部账户 shows all of them, with the same result feeding the list, the provider chips' counts, a badge above the list and the count on each account row. Both pages share the one selection, so the account chosen on one page is the account chosen on the other. The dashboard keeps its aggregate view — it already routes the balance ruler, the quota card and the account table per account, and narrowing that whole page to one account would answer a smaller question than the page asks.
- Give a pay-as-you-go key a gauge instead of a percentage nobody can define. The bar measures what is left of the last top-up: the denominator is the balance observed right after the balance last rose, and the current balance is the numerator. The plugin watches for that rise itself — the host records balance decreases only, so it cannot supply top-ups — and keeps up to 64 observations per account in `usage-analytics-balances.v1.json`. A rise of at least `max(0.5, 1% of the previous reading)` counts as a top-up; a balance at or above the baseline becomes the new baseline and draws full, never past it; fractions are clamped to 0..1 and accounts in different currencies are never compared. With no recorded top-up the bar is not drawn at all and the row says 未记录充值 · 仅显示余额.

### Improvements

- Draw the plan allowance from one reading. The balance ruler and the account row resolved different fields (`quota_windows` on one side, `quota_limit` on the other), so one account could show a percentage in the card and nothing at all in the list. `QuotaReading` is now the only place that resolves an allowance, the row holds a reference to it instead of a copy of its numbers, and `quota_windows` wins with `quota_limit` plus `quota_reset_day` as the fallback for settings written by older builds.
- Let the click choose the highlighted account. The account list is built when the data changes, not on every refresh, so nothing rebuilt it when the selection moved and the highlight stayed on the first row. `ApplyAccountSelection` is called from the click path now, rows are reused rather than replaced, and the three colour channels cross-fade over 250 ms.
- Fold the subscription allowance in place. The container carried an explicit `Height=0`, so the expand animated zero to zero and the card never opened. An outer element clips the height while an inner one carries the content, and the expanded state is remembered per account.
- Keep the provider filter's transition. The chip row was rebuilt at the end of every filter change, which destroyed the animation that had just started; the chips are reused when the provider set and its order are unchanged, so only the counters change.
- Build a row's detail panel when the row is opened, not while the list is being filled. Every ledger row constructed its whole sub-request panel and measured it as it was built, which is invisible until the tasks carry sub-requests: the Codex filter listed 365 tasks carrying 2,008 sub-requests and took 2.9 s to paint, while the DeepSeek Harness filter listed 971 tasks carrying none and took 0.4 s — the slower filter was the one with fewer rows. The panel is built on the first expand now and the same filter paints in 0.12 s.
- Leave the list alone when nothing about it changed. `Refresh()` assigned `ItemsSource` unconditionally, and that assignment drops all 64 realized containers, so switching accounts rebuilt a list the switch does not affect. The rebuild is keyed to a signature of the account filter, the provider filter and the visible event ids now: 全部账户 went from 1.2 s to 0.09 s and DeepSeek Harness from 0.5 s to 0.12 s.

### Fixes

- Stop drawing a cost share for a cost nobody reported. Rows whose cost the host never sent drew a 0% bar, which reads as "this model was free" rather than "this number is unknown"; they read 未上报 and draw no bar.
- Give the 口径 column room for its own value. The value was right-aligned in a 98 px column while the text needed about 131 px, so its left 33 px were cut off and `Cache Read / Input` rendered as `re Read / Input`. The label and the value are both shorter now, and a paired reading that still overflows ellipsises with a tooltip carrying the full string.
- Write the subscription branch's figure. The 26 px reading on a subscription account was never assigned: it showed 已用 暂无数据 at launch, and the balance of the previous account after switching over from a pay-as-you-go key. It is summed from the cycle's events now, so the three figures on that card agree.
- Say 等待重置 instead of 0 小时后重置. A reset instant that has already passed is waiting for the next window, not zero hours away.
- Name the product instead of the internals. User-facing strings that said 主程序 now say BalancePet, and the line that explains a missing ceiling reads 额度上限未上报 · 这里只显示已用；BalancePet 会记录累计用量 rather than waiting for a record the host never writes.

## 0.5.1

### Fixes

- Let the click decide which account is highlighted. The hover handlers wrote the row background directly, so passing the pointer down the account list repainted whichever row was under it and the all-accounts row kept the highlight no matter which account was selected. A selected row now ignores hover and keeps the background its selection gave it.

### Improvements

- Ease the transitions that used to jump. The account list hover, the Dashboard/History switch and the content-area swap now run on a `PowerEase` (`EaseInOut`, power 2) over 200–300 ms, so the navigation background and label colours cross-fade instead of snapping and an account row lights up progressively rather than in one step. The content swap changes `Visibility` only after the outgoing view has finished fading, which is what removes the flicker.
- Animate a brush of its own rather than the shared one. A frozen resource brush cannot carry an animation, so each transition stops any animation already running and animates the colour of a fresh `SolidColorBrush` it assigns to the element.

## 0.5.0

### Features

- Redraw the window from the approved prototype. A full-width balance ruler at the top, one shared column head over four reading columns, then the model and account tables — with one 26 px figure in the whole window and every reading set in a monospace face so decimal points line up down a column.
- Route the balance ruler by account type. A subscription gets a share of its plan's allowance and the date it renews; a pay-as-you-go key gets a balance, the pace today's spend implies, and the user's own warning line highlighted in the cost colour. A key with no ceiling is never given a percentage: an invented denominator is a made-up number wearing a progress bar.
- Open a task row in place. A row is one task; clicking it grows that row, raises its own background, rounds its own corners and grows a 2 px accent bar down its left edge, while the task's sub-requests fade in inside it. One row at a time, and the full-window detail view is still there behind the button in the opened row.
- Read the plan allowance where the account declares one. `quota_limit`, `quota_reset_day` and `warning_below` are read from the account settings the host already writes; with no ceiling the reading says 未上报 rather than 0%.

### Fixes

- Never print an unreported counter as zero. Every figure now carries whether the host reported it: a measured zero stays `0`, an absent reading is 未上报 in the stale tint, and the cache hit rate — the one place the two were printed as the same thing — reads 未上报 when no input was reported at all.
- Keep a stale reading rather than hiding it. A balance document older than an hour draws its figures desaturated while still showing the last value the host published.

## 0.4.0

### Features

- Declare an account subscription-billed. A subscription has no per-request price at all, so "no cost reported" is the expected state rather than a gap in the data. The account settings gain a switch that says so, and the account directory carries the declaration through to the dashboard.
- Read the plan the client reports. The host now forwards the subscription plan it observed for a session. The two signals are independent and both count: the declaration covers subscriptions whose client never reports a plan, and a reported plan covers accounts the user never labelled.
- Say what the money cards mean on a subscription account. Balance, today's usage and total usage each read 订阅制 · 不计费 or 订阅制 · 无余额 with a line explaining why, instead of showing a zero that looks like a failed reading.

### Fixes

- Tell an absent cost apart for official accounts and relays. A relay that reports no cost is missing data; an official API account under a subscription genuinely has none. The row text no longer reads the same for both.
- Stop clipping the official-account cost text. It was drawn on one line in a fixed-height box, so the longer explanation was cut off mid-sentence.
- Default `EventRow.AccountLabel` to empty rather than null. The constructor returns early when the host reported no account, and `ProviderLine` and `HasAccount` both read the value without a null check, so a record with no account id could throw while being rendered.

### Notes

- Requires BalancePet core `1.4.0` or newer. The reported plan arrives on a field the core only publishes from that version, and on an older core the subscription features stay hidden rather than showing figures that are wrong.

## 0.3.22

### Features

- Show which account each record billed to, next to the client: `DeepSeek Harness · 何意味`. Names come from the host settings; an unknown id falls back to a shortened form.
- Split the detail view by where the cost came from. A relay-matched cost keeps the per-request layout; a balance-derived one labels itself 任务总消耗, explains that it is the account balance change across the task rather than a per-request charge, and hides the three relay panel — cost timeline, token trend and relay request list — instead of showing empty placeholders.

### Notes

- An official API account publishes no per-request billing log, so the host reports a task-level figure from the balance drop. It is a real charge, including any peak/off-peak discount, but it has polling granularity: tasks shorter than the account refresh interval can measure zero.

## 0.3.21

### Fixes

- Line up the navigation rows. The base button template hard-coded a centred content presenter and ignored `HorizontalContentAlignment`, so `NavButton`'s `Left` never applied and each row was centred; the shorter label pushed its icon out of line. The template now honours the property, and the default Centre is stated explicitly so ordinary buttons are unaffected.
- Make the three navigation glyphs one family: the folder was stroked while the other two were filled, and its bounding box differed, so `Stretch="Uniform"` normalised them differently. All three now share a filled `(2,2)-(12,12)` box.
- Correct the toolbar refresh glyph. Its geometry is an open arc, but it was being filled, which rendered as a blob rather than an arrow; it is now stroked with round caps.

### Improvements

- Rebuild the toolbar Refresh button on the shared palette at a 36 px pointer target, matching the account selector beside it, instead of the cramped ad-hoc accent pill.
- Add motion: navigation icons scale slightly on hover, and the refresh icon completes one turn per click so the press is visibly acknowledged.

## 0.3.20

### Improvements

- Give the history list a bounded height so it virtualizes and recycles its rows. Switching the client filter took seconds with a few hundred records and now measures around 0.17 s regardless of how many are shown.
- Make a client filter change re-render only the list instead of re-reading every file, rebuilding the report and redrawing the dashboard charts.

### Changes

- Remove the 60-second auto-refresh and its countdown badge. The host writes a file for every new record, so the file watchers already keep the view current and the timer was duplicate work. A Refresh button in the header replaces it, re-reading the data directory on demand.
- Signal the host to refresh once when the window opens, so opening it shows current balances without polling.

## 0.3.19

### Features

- Add client filter chips to the usage history, one per reporting provider with an event count, so Codex, DeepSeek Harness and any other client can be viewed separately instead of interleaved.
- Add an account selector to the dashboard header. Every balance, usage and today figure now follows the selected account, and the combined view sums them, so the three numbers always describe the same scope.
- Resolve opaque account ids to the names chosen in the host settings, reading only `monitors[].id` and `monitors[].name`.

### Notes

- The dashboard previously mixed scopes: the balance snapshot was summed across accounts while the usage total came from the host's selected account alone. The combined entry is now the default and applies to both.

## 0.3.18

### Improvements

- Keep the Token trend hover surface active across the full plot area, including translucent filled regions.
- Position the tooltip near the cursor so it remains visible while moving through the chart.

## 0.3.17

### Improvements

- Make the Token trend tooltip follow the mouse position across the chart instead of snapping the guide to a data point.
- Add translucent area fills beneath the Input, Output, Cache Creation, and Cache Read series.

## 0.3.16

### Improvements

- Add hover details to the dashboard Token trend chart, including date, token counters, cache hit rate, and request count.
- Add a vertical guide line so the hovered time bucket is easy to follow.

## 0.3.15

### Improvements

- Move the dashboard time-range selector into a dedicated analysis filter bar.

## 0.3.14

### Improvements

- Reorganize the dashboard into a two-row, four-column metric layout inspired by relay dashboards.
- Move balance, cumulative spending, and server daily usage into a dedicated account-and-site summary panel.
- Keep provider/model breakdowns and token trends in the lower analytical area for a clearer hierarchy.

## 0.3.13

### New features

- Clarify the dashboard's local balance-change estimate versus the server's daily usage summary.
- Add cumulative balance-ledger spending to the total-balance card.

## 0.3.12

### New features

- Add a per-request Token trend chart to usage-record details for input, cache-read, and output counters.
- Leave gaps for counters that were not reported instead of rendering missing data as zero.

## 0.3.11

### New features

- Replace inline expandable request rows with a full usage-record detail view opened by clicking the entire record card.
- Add Token composition bars, a per-request cost timeline, and a linked request detail table with back/Esc navigation.

## 0.3.10

- Continue consuming the host-written per-request cost and reasoning details without reading provider credentials.
- Refresh usage rows when the main program completes browser-session based cost reconciliation.

## 0.3.9

- Increase the usage-event line limit so tasks with many per-request details remain readable.
- Keep the task summary and expandable request details compatible with the new main-program usage pipeline.

## 0.3.8

- Display the reasoning effort captured by the host for Codex tasks and matched relay requests.

## 0.3.7

- Avoid reparsing unchanged usage-event files on every refresh; only changed or newly rotated files are rescanned.
- Keep the existing report totals and event de-duplication behavior while reducing repeated disk I/O.

## 0.3.6

- Add selectable Token trend ranges: 24 hours, 7 days, 30 days, 90 days, and all time.
- Switch the chart bucket size to hour, day, week, or month for readable labels at each range.

## 0.3.5

- Show relay-reported reasoning intensity under each task model.
- Expand a task row to inspect its matched relay request details while keeping one summary row per task.
- Display the summed relay cost for tasks whose individual requests can be reconciled safely.

## 0.3.4

- Add the server-reported daily `/v1/usage` cost summary to the dashboard without attributing aggregate charges to individual requests.
- Keep the extension credential-free; the main application publishes only a sanitized local summary snapshot.

## 0.3.3

- 左侧导航移除“提供方与模型”入口；提供方与模型统计仍保留在仪表盘右侧内容中。
- 修复仪表盘滚动后错误高亮“使用记录”的问题；高亮现在跟随当前打开的页面。

## 0.3.2

- 自动同步间隔调整为 60 秒，事件文件变化仍会立即刷新界面。
- 使用记录支持同一事件的延迟补齐，Codex 在任务结束后才写入的 Token 数据会更新原记录，不再产生重复请求。
- 按日志文件写入时间合并轮转文件，避免补齐记录被旧文件覆盖。

## 0.3.1

- 移除无实际接口刷新能力的手动刷新按钮，改为文件监听、5 秒定时同步和主程序刷新调度信号三重更新机制。
- “使用记录”改为独立页面，并逐条标注输入给模型、模型输出、缓存读取、缓存写入和消耗额度。
- Usage Event v1 增加可选 `cost` 与 `currency` 字段；未上报额度时明确显示“未上报”，不根据 Token 猜测金额。
- 窗口圆角和控件圆角按 Windows 11 / Windows 10 自动调整。

## 0.3.0

- Add a total balance card sourced from the host's credential-free Balance Usage v1 snapshot.
- Sum configured account balances only when currencies match and keep the existing estimated-spending semantics.

## 0.2.12

- Replace the unavailable TTFT overview card with today's spending from the
  host's credential-free `balance-usage.v1.json` summary.
- Follow the selected BalancePet account and refresh when the host summary changes.
- Document the balance-usage summary and its estimate-only semantics.

## 0.2.11

- Rename the first-token card to average time to first token (TTFT).
- Show `暂无数据 / No data` when no client has reported TTFT instead of implying a zero or parser failure.
- Keep the average restricted to valid, explicitly reported `time_to_first_token_ms` samples.

## 0.2.10

- Rewrite the README as a Chinese/English bilingual usage, privacy, compatibility, and build guide.
- Document the release-note structure and link directly to the Feature Extension API v1 contract.

## 0.2.9

- Align cache-hit calculations with relay dashboards: cache read is divided by the complete input total.
- Add a multi-series seven-day trend for uncached input, output, cache creation, cache read, and cache-hit rate.
- Use the BalancePet application icon for the usage window and taskbar instead of the generic Windows placeholder.

## 0.2.8

- Read Token and cache counters emitted by BalancePet 0.7.7 for Codex tasks.
- Keep unavailable counters explicit when the client does not expose usage data.

## 0.2.7

- Display missing token counters as unreported instead of zero.
- Keep the dashboard and recent-list scrollbars integrated with the dark theme.
- Format long task durations as minutes or hours for easier scanning.

## 0.2.6

- Unified dashboard and recent-list scrollbars with the dark theme and removed the bright system focus outline.

## 0.2.5

- Fixed the dashboard startup crash caused by a read-only model percentage binding being treated as two-way.

## 0.2.4

- Added a borderless integrated title bar and styled scrollbars for a cohesive dashboard window.
- Added a live seconds-until-refresh countdown in the header.
- Broadened compatible usage counter aliases for client hook payloads.

## 0.2.3

- Improve navigation reliability after refresh or window resize.
- Explain lifecycle-only data and the local full-usage event sender.
- Treat omitted counters as “not reported” in the bundled test sender.

## 0.2.2

- Added the extension-owned GitHub Release update URL to the manifest.
- Kept the executable and Usage Event v1 contract unchanged; this package can
  be installed independently from BalancePet core updates.

## 0.2.1

- Sidebar entries now navigate to the dashboard, recent usage, and provider/model sections.
- Added more lifecycle payload aliases and session fallbacks so compatible clients
  can populate model, duration, and token fields instead of producing count-only events.
- Clarified that lifecycle-only events cannot provide token totals without client counters.

## 0.2.0

- Replaced the compact report window with a dark dashboard layout inspired by
  provider consoles.
- Added overview cards, provider breakdown, model distribution, seven-day token
  trend, and a richer recent-usage list.
- Metrics remain metadata-only; unavailable token or cost fields are shown as
  unavailable instead of being inferred.

## 0.1.3

- Clarified the empty-data status so missing host events are easier to diagnose.

## 0.1.2

- Enlarged the dashboard window and made it resizable.
- Refined the header, summary cards, recent-request panel, buttons, and refresh status.

## 0.1.1

- Refresh automatically when usage event files change.
- Refresh at least once per minute as a fallback when file notifications are unavailable.

## 0.1.0

- Initial read-only dashboard for BalancePet Usage Event v1.
- Shows 5-hour, 24-hour, 7-day, and all-time token totals.
- Shows cache hit rate, request count, success rate, average first-token time,
  output throughput, and recent request metadata.
# 0.3.9

- 提高明细事件行的读取上限，支持主程序保存更多逐次 API 请求记录。
