# Settings polish — local v4.8.0 trial, 2026-10-29

## Scope
- Retain the continuous settings document, section navigation, all existing sidebar behavior and saved accounts.
- Compact header, sections and rows; use Windows Microsoft YaHei UI; preserve large action targets and light/dark themes.
- Double-buffer the settings viewport/document, batch width fitting, avoid height-triggered width passes and unchanged navigation invalidation. Long-distance navigation jumps directly instead of animating hundreds of native controls across the whole document.
- Add AI refresh interval (30–3600 seconds; existing/default 300 seconds). The service is the single deadline authority; a one-second host pulse cannot postpone a slightly early deadline by another five minutes. Only due queries start; manual refresh retains its ten-second cooldown. Interval-only edits preserve current snapshots. Trend sampling remains five-minute buckets.
- Add asynchronous CLI discovery, display the result, preserve automatic mode rather than pin a version folder. Search local PATH, npm, standalone install folders and at most 32 desktop CLI version folders; do not search whole disks or copy credentials.
- CLI location help verified against https://learn.chatgpt.com/docs/config-file/environment-variables . The install location is an example, not a guaranteed path.

## Verification
- Release build: zero warnings/errors.
- Full StageManager.Tests regression passed, including quota schedule/cancellation/history, calendar, Focus, independent FIX, click/scroll behavior, capture isolation and startup behavior.
- Subsequent header-only adjustment revalidated by settings preview/UI tests.
- Settings previews inspected: AI, general, narrow viewport, dark/large text. Narrow layout search clipping found during testing was corrected before deployment.
- Synthetic 80-step wheel dispatch: median 0.36 ms / p95 0.56 ms; document dimensions stayed unchanged. This is an off-screen regression harness, not a claim of measured physical display frame rate.
- Added interval default/bounds/save and near-deadline refresh tests. Existing configuration stays schema 22; the additive missing field defaults to 300 seconds.
- Native installed process responding, parent Explorer; desktop/startup shortcuts both still target the unchanged installation path.
- No GitHub push, Release, website update or real API-key change. User needs to judge physical scroll smoothness and visual preference during use.

## Local delivery
- EXE: `Releases/v4.8.0-Calendar-Compact/Stage_Manager_Lai_v4.8.0.exe`
- SHA256: `2D54CD155EF61F2BEAA42194446C4F83370DFD64E358D1CBFD368A285AFE9B58`
- Previous EXE, settings and hash manifest: `Releases/Backup-before-SettingsPolish-20261029-103852/`
- Settings hash unchanged immediately after deployment. API keys were not read or copied into the build.

## Follow-up: Codex connection and wheel routing (10:50 local)
- Configured root `OpenAI/Codex/bin/codex.exe` exists but exits immediately during the app-server query (IOException). Versioned desktop CLI succeeds using the same read-only client: Connected, 50% at 10:47 local. This is not evidence of an account sign-out.
- Backed up configuration and cleared only CodexCliPath to enable automatic discovery. Native versioned desktop directories now take precedence over the stale root binary. Invalid paths are rejected on settings save; early CLI exit has actionable bilingual text. No credential changes.
- Added a form-scoped WM_MOUSEWHEEL message filter, active only for this settings form and its own hovered controls. All ordinary controls route to the main document; dropdowns retain their expanded list handling. No global hook. Filter is removed at disposal.
- Document stack height now uses flow preferred size, excluding hidden controls' old bounds. Tests verify hidden direct-calendar configuration shrinks and gaps between modules stay within 24 DIP.
- Tests route wheel over all text boxes, numeric editors, sliders, toggles, labels and buttons, asserting page movement and no numeric setting mutation.
- Full regression passed on final sequential run. An earlier run concurrent with publishing encountered an existing capture-worker MainModule race in its test; the subsequent run passed (2.17 s timeout/recovery). No unrelated capture implementation was changed.
- Latest deployed SHA256: `B99D0E24081113F4CB9E04D64884FC6F7EC53D8BF9C3CDE840C54DDB0B95BB8A`.
- Rollback: `Releases/Backup-before-SettingsPolish-20261029-105040/`.
