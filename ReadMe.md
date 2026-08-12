# Stage_Manager_Lai v2.3.8

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

v2.2.2 improves same-application multi-window cards. Clicking a stacked card opens a full downward child list:
each window keeps a separate full-size card, while a blue-gray connector line shows the parent/child relationship.
Hover only raises the pointed card slightly, and a second click selects that exact window. Single-window cards
remain direct one-click targets. The expanded native region follows every card so transparent space continues
to click through. Collapsed cards retain a small hover lift and angle response without opening automatically.

v2.2.3 fixes activation for visible background windows such as Zotero and WeChat by temporarily attaching the
relevant Windows input threads before raising the selected window. Tray-hidden, non-minimized windows such as
Nutstore's background WPF shell and WeChat's hidden main window are removed from the sidebar instead of being
forced into a stale black frame. Taskbar-minimized windows remain managed and restore normally.

v2.2.4 adds full-screen edge reveal on the physical leftmost display. While a full-screen application is active,
the sidebar stays out of the way, slides over the application when the pointer reaches the left edge, and hides
again shortly after the pointer leaves. It is raised to the topmost band only for this temporary reveal and is
demoted as soon as the hide animation completes; normal operation remains non-topmost.

v2.2.5 applies the same transient edge behavior when the foreground window is maximized on the sidebar's display.
Maximized windows on another monitor do not hide the left sidebar, and ordinary restored windows retain the normal
one-minute idle behavior.

v2.2.6 keeps a multi-window child list expanded after selecting a child window or moving the pointer away. The
expanded list now collapses only when its primary card is clicked again, while pagination and direct child-window
selection continue to work normally. Real File Explorer folder windows are also included as cards, while desktop,
taskbar, notification-area, and other Explorer shell surfaces remain protected.

v2.2.7 keeps the card that opened a multi-window group permanently visible at the top of every expanded page. The
primary card remains stable even when another child window receives focus or the user changes pages, so it is always
available as the explicit collapse control. Child cards continue below it and remain expanded after selection.

v2.3.0 replaces the window-backed primary card for multi-window applications with a synthetic application group card.
The group card uses a white background with the application logo centered on it and never activates or minimizes a
window. It only expands or collapses the group. Every real window now appears below it as a selectable child; groups
with more than five windows page only the child cards while the application card remains available at the top.

v2.3.1 changes live previews to static initial snapshots. Each real window is captured once when its card first becomes
visible, then the captured texture remains on the card. The renderer no longer calls PrintWindow or GDI capture on a
125/500ms refresh loop, reducing redraw pressure and preventing flicker in Abaqus and other hardware-accelerated apps.

v2.3.2 stops ignoring Tencent Yuanbao by default. Settings now show detected applications with their current window
count, so an application can be ignored or restored by checking a name in the list instead of finding its executable.
An advanced process-name field remains available for applications that are not currently running.

v2.3.3 keeps the low-frequency snapshot mode but refreshes each visible window card at most once every five minutes.
The renderer still avoids the former 125/500ms capture loop, so hardware-accelerated applications such as Abaqus are
not repeatedly captured while their cards remain on screen.

v2.3.4 adds a subtle flat left-arrow button at the bottom of the sidebar. Clicking it collapses the sidebar without
requiring the global shortcut; the button has a small hover and press response and remains part of the shaped
click-through region.

v2.3.5 pins the bottom arrow and idle hint to the flat foreground plane so perspective projection cannot push them
outside the visible work area. Double-clicking a concrete window card now checks all active display work areas; only a
window with no meaningful visible area is restored and centered on the display where the card was clicked. A normal
on-screen window keeps its position, and double-clicking it does not leave it minimized.

v2.3.6 darkens the sidebar collapse arrow and places the footer directly below the lowest visible card. The card layout
reserves footer space, while long lists keep the footer at the bottom of the visible sidebar without overlapping cards.

v2.3.7 corrects the collapse arrow direction and widens its button for easier targeting. Minimized windows and windows
whose preview cannot be captured now use a soft light-gray placeholder instead of an almost invisible transparent card.

v2.3.8 matches the collapse control width and 3D tilt to the window cards, aligns it with the card column, and changes
the arrow itself to a deep near-opaque black while retaining subtle hover and press feedback.

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
Desktop icons are never hidden or toggled. Tencent Yuanbao is shown normally by default; if its selection-translation
overlay is distracting, select Yuanbao in Settings > Ignored applications to hide it.

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
