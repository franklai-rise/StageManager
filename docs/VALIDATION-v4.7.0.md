# v4.7.0 local settings trial — 2026-10-21

- Production and test build: 0 warnings, 0 errors.
- Full regression runner: 95 groups passed; log `artifacts/v4.7.0-regression.txt`.
- Settings checks: seven-page navigation, Chinese/English switching, resizable viewports, fixed save controls, AI deep link, Focus idle-hide constraint, preference saving and canceled theme-preview isolation.
- Preview artifacts: `artifacts/v4.7.0-settings/` (test data, no account credentials). Inspected light settings and AI page plus dark/large preferences.
- Automatic update tests: disabled requests, 45-second initial delay, repeated setting saves, 24-hour cadence, single-flight, cancellation/generation race, notification deferral/deduplication across restart, failed request, schema migration and persistence.
- Installed executable: `../Releases/v4.7.0-Settings/Stage_Manager_Lai_v4.7.0.exe`; 189498825 bytes.
- SHA-256: `f5fcf065e2b18bfeaf969dc4aea1cc5cb7500bd21ae1e810ea53abe37ede0a5e`.
- Exact same bytes deployed to `../Releases/Current/Stage_Manager_Lai.exe`; version 4.7.0.0, responding, independently launched by Explorer.
- Verified existing desktop and Startup shortcuts still point to that stable entry.
- Backup: `../Releases/Backup-before-v4.7.0-20261021-130957/` contains the previous EXE and settings.
- Live settings migrated 20 → 21 while retaining Simplified Chinese, Focus, left dock, card scale 0.58 and startup enabled.
- First real metadata check succeeded at 2026-10-21T13:10:51Z; no asset download or install occurred. Current local version exceeds the public v4.6.6 release.
- Existing renderer error log remained unchanged since 2026-10-18T11:29:09Z.

No public push or Release in this task. Full physical mixed-DPI hotplug testing, a real future-release notification and 24-hour stability observation remain unverified. Time-based scheduling was verified with a controlled clock rather than waiting a day.
