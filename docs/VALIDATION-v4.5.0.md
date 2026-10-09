# v4.5.0 validation record

## Automated local checks

- Release solution build: passed with zero warnings and errors.
- Full local regression suite: passed, including live same-EXE capture worker tests.
- A stalled owned worker was timed out, terminated and replaced; timeout plus recovery
  measured approximately 2.15 seconds. An owned unresponsive window returned safely.
  Blocking inside native PrintWindow specifically was not deterministically reproduced
  (Windows can use its compositor cache); do not claim that exact path was observed.
- Permission and operation state-machine checks: 61 passed (including cache expiry,
  owner reuse, integrity-query failures, cooperative tray transitions and stopping
  retries after new input).
- Launcher migration, ordering, Shell commands, cancellation, icon ownership,
  stalled-launch admission and renderer hit-testing: thirteen groups passed.
- Website JavaScript syntax and Python compilation: passed.

GitHub CI must also pass at the recorded release commit. Automated checks alone
do not satisfy the manual/local-trial gate below.

## Manual/local trial gate (pending)

- [ ] Download CI draft and verify its SHA-256 and artifact attestation.
- [ ] Use the same EXE for at least 24 hours; review private memory, CPU and handles.
- [ ] UI stall does not delay mouse activity in other applications.
- [ ] Normal and elevated Terminal: correct target or clear refusal, no forced elevation.
- [ ] VPN/Clash: activation, minimize-to-tray and restore at the original maximized size.
- [ ] WeChat, Zotero, PowerPoint and Explorer retain their window placement.
- [ ] Focus remains configured across long fullscreen sessions; FIXED/expansion/scroll retained.
- [ ] Mixed-DPI screens and monitor changes retain correct wheel/hit regions.
- [ ] Quick-launch picker works in Chinese and English, including missing paths and cancellation.
- [ ] Independent app parent/startup behavior does not depend on Codex.

The observer writes local numeric process statistics only, never window titles.
It does not publish releases or treat uptime alone as acceptance.
