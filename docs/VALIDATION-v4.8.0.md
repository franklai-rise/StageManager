# v4.8.0 Outlook calendar — local trial

## Scope

- Optional calendar card above AI usage, independent of the AI switch. Default off.
- Date-only translucent dark card, sized like a quick-launch button. The taller 420 × 740 DIP native month/agenda popup shares AI usage's palette, rounded outer shape and actual button renderer. Calendar data never changes card dimensions.
- Default connection: Codex's already-connected Outlook plugin, via its public App Server and read-only tool calls. Explicit manual refresh only; no model turn, automatic query, scheduled task or copied login token. Direct MSAL personal-account login remains an optional alternative.
- Event completion is a **local checklist**, not an Outlook event update or Microsoft To Do task. Tick/untick from the agenda; completed titles are muted and struck through. The compact card contains only a date and has no invisible completion click targets.
- Local completion records contain SHA-256 identity keys and modification times, not event titles, descriptions, account names or raw event IDs. Records are bounded to 10,000 entries and 90 days.
- Codex calendar content is cached using DPAPI current-user encryption, at most three six-week ranges. Only a user click on Refresh invokes the connector; cached date navigation and cache loading do not. Direct MSAL tokens remain encrypted for the current Windows user. No telemetry.

## Default Codex connection

The read-only bridge has been verified against the local installed Codex CLI and the real connected personal Outlook account. A manual six-week refresh returned 19 events, including 6 today; the encrypted cache then reloaded with identical events and update time while using an intentionally nonexistent CLI path. No `turn/start` or model generation was issued. The calendar is still opt-in: Settings → Calendar → Show calendar card; keep “Use Codex Outlook plugin · manual refresh” checked. Refresh synchronizes the visible month. The CLI path follows the existing Codex CLI setting. A missing CLI, disconnected Outlook plugin, required approval, timeout or incomplete scan is an error, never a successful empty calendar.

The bridge calls only `list_calendars`, `search_events` and `fetch_events_batch` (plus discovery); no calendar mutation tools are permitted. Public protocol reference: https://learn.chatgpt.com/docs/app-server

## Optional direct Microsoft connection

No maintainer Client ID has been supplied. This optional alternative explicitly shows **Not configured** until registered. It does not use another application's identity or embedded client secret. The default Codex mode does not need a Client ID.

The maintainer must register an application in Microsoft Entra that supports personal Microsoft accounts, add the desktop/mobile `http://localhost` redirect, and configure the Microsoft Graph delegated `Calendars.Read` permission. No client secret is needed. Enter the public Application (client) ID in Settings → Calendar, then use **Connect account** and complete browser sign-in/consent personally.

Official references:
- https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app
- https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization

Direct-mode primary calendar ownership supports Microsoft-account sign-in aliases. Direct-mode non-primary calendars are conservatively included only when the returned owner matches the signed-in username; alias-owned secondary calendars need real-account verification. Codex mode matches secondary calendars against the primary calendar's owner. Shared/delegated calendars are intentionally excluded.

## Automated verification

Run `dotnet build StageManager.Tests -c Release`, then the test executable. `--calendar-tests` runs the dedicated calendar checks, including disabled/missing configuration, singleflight, cancellation/generation isolation, stale data/backoff, month-cache eviction, six-week grids, midnight/timezone changes, safe URLs, all-day dates, settings migration, local completion persistence/privacy, popup timing and four-edge projected click targets at 100/125/150/200% DPI.

Native preview is generated from synthetic events only; it is not evidence of account integration. The preview command is `--calendar-preview <absolute-output.png>`.

## Remaining acceptance gates

2026-10-25 local result: Release build succeeded with 0 warnings/errors; the complete `StageManager.Tests` executable finished with `All Stage_Manager_Lai tests passed.` Synthetic native preview was inspected. Self-contained, non-compressed trial EXE generated (190,922,138 bytes), SHA-256 `3F02FA3B9E634B94D4F346A70A062560E8FD0D3719F196038DCF82E7CEC7415A`. Current v4.7.3 executable hash, live settings hash, desktop target and Startup target remain unchanged.

- Optional direct-mode maintainer registration and login/consent.
- Further real-account comparison for calendar selection, recurring exceptions and nonlocal all-day/DST edge cases.
- Live four-edge/mixed-display hover corridor, full-screen/Focus behavior and screen-reader checks.
- Long-running memory/network/resume behavior with a real account.

## Deployment boundary

Original trial EXE remains in the external project `StageManager/Releases/v4.8.0-Calendar` folder. The blue/Codex revision belongs in `StageManager/Releases/v4.8.0-Blue-Codex`. Preserve the original trial and v4.7.3/settings backup. `Releases/Current`, desktop and Startup shortcuts stay unchanged pending interactive acceptance. No GitHub push, Release or website update is authorized for this trial. Existing AI refresh issues are outside this change.

Blue/Codex revision local result (2026-10-25): build passed with 0 warnings/errors; the complete regression executable finished with `All Stage_Manager_Lai tests passed.` Native Chinese synthetic preview was inspected, including rounded selected dates and distinct button hover/press states. Trial EXE is 190,967,706 bytes; SHA-256 `26C0B5E502378CFC3D2EEA7B4270431C6DB8C31A99FEC6AEBB81099C19FD9ED3`. The previously running original v4.8.0 trial was closed normally and the blue/Codex revision launched through the Windows shell; the new main process responded. The user's current calendar-enabled settings were backed up without reverting their intervening schema/calendar changes. Stable `Current` EXE remains unchanged. Desktop still targets `Current`; Startup was found targeting the original calendar trial and was not rewritten to the blue revision. Neither entry is claimed to point to the new blue trial.

## Compact dark UI revision (2026-10-25)

- The date launcher now matches shortcut width/height/tilt, including the horizontal quick-launch row layout. It shows only a large local month/day; event refresh or completion no longer repaints it. Midnight still invalidates the date. Compact checklist hit targets were removed, not merely hidden.
- The flyout reuses the AI palette, native action-button implementation and 16-DIP outer corner radius. It is 420 × 740 DIP, with a longer independent agenda viewport; small work areas retain readable DPI-sized content via scrolling.
- The native accessible checkbox is painted as a 22-DIP rounded square, with hover/pressed states and a 140 ms checked transition. Reduced-animation preferences are respected; timers stop on completion/hide/disposal. Persistence echoes update controls in place instead of destroying the active checkbox, preserving animation and scroll position.
- Release build: 0 warnings/errors. Complete regression suite passed; calendar checks rerun after the final navigation-arrow/label polish. Four-edge tests cover 100/125/150/200% DPI, portrait/landscape, two card scales, 0/1/2/3/4/7 launchers, stable geometry and scrolled click targets. Native Chinese preview uses synthetic events only. No real account sync was triggered for this UI-only change.
- Single-file trial: `Releases/v4.8.0-Calendar-Compact/Stage_Manager_Lai_v4.8.0.exe`, 190,967,706 bytes; SHA-256 `AB6C301AD2F588EE7B976802BC5988847ECC6BA697E0BF303D7C10D230D13032`. Prior original-calendar, blue/Codex and v4.7.3 artifacts remain available. Stable Current and shortcut targets are not changed by this UI trial.
- Local rollout: the running blue/Codex process was closed normally after executable/HWND ownership checks; the compact revision was launched through the Windows shell and verified responding. Settings were backed up to `Releases/Backup-before-Calendar-Compact-20261025/settings.json`; their SHA-256 remained `EAD8342585BE044B44BF6AC12D872786E34728644C6C9E983A5200A95B3080BE`. Stable Current and both shortcut targets were rechecked unchanged. No GitHub mutation or public release.

## Date typography revision (2026-10-26)

- The date card previously relied on the bitmap Graphics default text-rendering hint and Segoe UI bold CJK fallback. Date-only rendering now explicitly uses grayscale antialiasing, Microsoft YaHei UI Regular for Chinese and Segoe UI Regular for English. ClearType RGB subpixels are not stored in the transformed alpha texture.
- Only the date texture is rendered at up to 2× device resolution and fitted to the unchanged card visual. Extra supersampling is bounded by 2048 pixels per dimension / 1,048,576 pixels; already-larger device textures remain native resolution. Date and hover reuse the existing texture. No continuous render work, new settings, font downloads or new runtime dependencies.
- Chinese/English dates including long month/day strings were checked at 100/125/150/200% DPI and the user's 57% card scale. Tests check real intermediate alpha coverage, padding, caller graphics-state preservation, bounded allocation, unchanged projected hits and texture reuse. The complete regression suite passed. A synthetic before/after comparison was inspected at native pixel size.
- Replaced the existing desktop-target EXE in `Releases/v4.8.0-Calendar-Compact` after a normal close and backup to `Releases/Backup-before-DateClarity-20261026`. Launched through the existing Explorer desktop automation object. Desktop target, startup target, settings and v4.7.3 Current remain untouched.
- New EXE: 190,971,802 bytes; SHA-256 `572A0970E22AE87F45CEA27B01B8093CD57D33A5EC62335EA98EF244783E68FD`. The pre-typography compact revision is preserved in the backup. No public release or website change.
