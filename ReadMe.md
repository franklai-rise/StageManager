# Stage_Manager_Lai v2.2.1

Stage_Manager_Lai is Frank Lai's personal Windows adaptation of
[Stage Manager for Windows](https://github.com/awaescher/StageManager), originally created by
[Andreas Wäscher](https://github.com/awaescher). It remains a derivative work under the upstream MIT
license and is not presented as an original project by the fork maintainer.

`Stage_Manager_Lai_v1.1` remains tagged as the stable pre-v2 baseline.

v2.0.1 makes the sidebar canvas fully transparent while retaining the individual live-preview cards.

v2.0.2 retries late-forming Office and packaged-app windows, and suppresses native previews while cards are
being reordered so stale DWM rectangles cannot overlap neighboring cards.

v2.0.3 uses 60% cards by default and prevents ordinary windows from triggering sidebar auto-hide.
Exclusive full-screen applications still suppress the sidebar, and the sidebar can still be hidden manually.

v2.0.4 removes persistent topmost behavior. The sidebar remains at the left edge but normal foreground windows
can cover it naturally.

v2.1.0 adds optional macOS-style 2.5D stage cards. Live previews remain rectangular and readable while
slanted backplates, depth rails, highlights, shadows, and MRU-based offsets create a perspective stack without
transforming the native DWM thumbnails. The effect is enabled by default and can be disabled in Settings.

v2.2.0 promotes the native Windows Composition 3D renderer to the formal personal build. It adds real
perspective-transformed window captures, subtle hover feedback, transparent crop-to-fill cards, stable card
slots, exact click-to-activate/minimize behavior, reliable click-through outside card shapes, and a persisted
55%–125% card-size control in the tray menu.

v2.2.1 restores the practical settings and global shortcuts used by the earlier builds. It replaces extended-
style click-through with a card-shaped native window region, adds a one-minute idle slide-away behavior with
left-edge wake-up, and keeps card size, animations, startup, shortcuts, and ignored applications configurable.

## What v2 adds

- Runtime task stages can contain windows from several applications.
- **Coexist mode** is the default: selecting a stage brings its windows forward without hiding other apps.
- **Focus mode** minimizes only managed inactive stages and restores their exact position and state later.
- A stage can span multiple displays; the sidebar stays on the physical left edge of the leftmost display.
- Up to three live DWM previews form a stacked stage card, with an overflow badge for additional windows.
- Cards use a macOS-inspired perspective stack by default, with a flat-card fallback in Settings.
- Cards shrink automatically down to 55%; larger lists scroll without overlapping.
- Public Windows virtual-desktop APIs keep recent stages separate per virtual desktop.
- Full-screen detection suppresses the sidebar over games and videos.
- Settings, ignored applications, appearance, startup behavior, and shortcuts persist in
  `%LocalAppData%\Stage_Manager_Lai\settings.json`.
- Window events are serialized and coalesced; DWM thumbnails and WinEvent hooks are released deterministically.
- Three unclean starts activate safe mode, which leaves only the tray controls active.

## Safety rules

Windows Explorer, the taskbar, desktop surfaces, and other Windows shell processes are permanently excluded.
Desktop icons are never hidden or toggled. Tencent Yuanbao is ignored by default so selection-translation
overlays cannot become stage cards; it can be removed from the user ignore list if desired.

## Controls

- Click a card to select its complete stage.
- Drag an application window onto a card to add it to that stage.
- Drag a card from the sidebar into the active area to merge it into the current stage.
- Right-click a card to split it, move it to the next display, arrange two or three windows, rebuild its
  previews, ignore its applications, or close its most recent window.
- Use the tray icon to switch modes, toggle auto-hide, open settings, inspect logs, or quit safely.

Default shortcuts:

| Action | Shortcut |
|---|---|
| Show or hide sidebar | `Win+Alt+S` |
| Previous stage | `Win+Alt+[` |
| Next stage | `Win+Alt+]` |
| Add/remove active window | `Win+Alt+G` |

Windows Game Bar can reserve `Win+Alt+G`. When that happens, Stage_Manager_Lai automatically registers
`Ctrl+Alt+Shift+G` for the current run; shortcuts can be changed in Settings.

## Build and verification

Requirements: Windows 10 version 2004 or newer and the .NET 8 SDK.

```powershell
dotnet restore StageManager.sln
dotnet build StageManager.sln -c Release
dotnet run --project StageManager.Tests\StageManager.Tests.csproj -c Release
```

`RuntimeProbe` is the local integration harness used for rapid window creation and multi-display restoration
tests. The repository's source remains on `net8.0-windows`.
