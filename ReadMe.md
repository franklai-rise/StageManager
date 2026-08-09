# Stage_Manager_Lai v2.0

Stage_Manager_Lai is Frank Lai's personal Windows adaptation of
[Stage Manager for Windows](https://github.com/awaescher/StageManager), originally created by
[Andreas Wäscher](https://github.com/awaescher). It remains a derivative work under the upstream MIT
license and is not presented as an original project by the fork maintainer.

`Stage_Manager_Lai_v1.1` remains tagged as the stable pre-v2 baseline.

## What v2 adds

- Runtime task stages can contain windows from several applications.
- **Coexist mode** is the default: selecting a stage brings its windows forward without hiding other apps.
- **Focus mode** minimizes only managed inactive stages and restores their exact position and state later.
- A stage can span multiple displays; the sidebar stays on the physical left edge of the leftmost display.
- Up to three live DWM previews form a stacked stage card, with an overflow badge for additional windows.
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
