# v4.8.1 release validation

## Scope and boundaries
- Add leftmost / primary / specific monitor selection to the canonical Card3DPrototype production entry; schema 23.
- Preserve the prior unpublished calendar/settings/AI fixes when publishing the current source, rather than reverting to v4.6.6.
- User explicitly requested GitHub push and immediate Release, with no installed binary, desktop link or startup replacement. Do not launch the new production entry against the user's settings.
- This is an expedited release. The exact CI EXE has not completed a 24-hour soak or physical hotplug/adapter/mixed-DPI acceptance. Do not claim otherwise or fabricate a trial start.

## Coverage
- Pure display-policy cases: automatic leftmost, primary, explicit target, case-insensitive monitor identity, changed GDI number, disconnected target fallback, single-display fallback and reconnect.
- Persistence: migration backs up exact schema-22 bytes, retains existing calendar/AI/card options; schema-23 selection round trip, malformed IDs/enums, fallback does not mutate saved preference.
- UI: bilingual dropdown, connected monitor labels, unsaved choice retention, disconnected placeholder, Save vs Cancel, resizable continuous settings and control-wheel routing.
- Integration: monitor selection feeds the same host bounds, edge activation, popup positioning and Focus reservation paths; old monitor reservation is removed before switching. Native enumeration is read-only via EnumDisplayDevicesW (EDD_GET_DEVICE_INTERFACE_NAME).
- Existing full regression includes four-edge/DPI layout, window clicks, independent FIX/OPEN, capture workers, AI schedule, calendar and updater behavior.

## Results
- Local Release build: 0 warnings / 0 errors.
- Full sequential StageManager.Tests completed with `All Stage_Manager_Lai tests passed.`, including the new monitor tests and schema-23 migration. Settings/UI tests repeated after final hint and introduction text changes.
- Native settings previews inspected at normal/narrow dimensions and light/dark themes; the disconnected dropdown uses a synthetic monitor ID, not this PC's real monitor identifiers.
- Website JavaScript syntax and Gemini quota parser tests passed locally.
- Remote CI, generated manifest/hash and attestation verification must still succeed before draft publication. CI logs and final Release assets are the authoritative remote record.

## Privacy
Only source, synthetic tests, public release notes and validation records are committed. User settings, monitor identities from this PC, account caches, API keys, private calendar content and local EXEs are excluded.
