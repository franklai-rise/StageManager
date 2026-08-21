# Stage_Manager_Lai v3.1.1

Stage_Manager_Lai is Frank Lai's personal Windows adaptation of
[Stage Manager for Windows](https://github.com/awaescher/StageManager), originally created by
[Andreas Wäscher](https://github.com/awaescher). It remains a derivative work under the upstream MIT
license and is not presented as an original project by the fork maintainer.

`Stage_Manager_Lai_v2.5.1` is the installed stable hotfix, with the unchanged v2.5.0 executable retained as a rollback build while v3.1 completes its burn-in gate.

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

v2.4.0 introduces a quieter smart-preview engine and display-aware sidebar behavior. Preview captures now run one at a
time, skip `PrintWindow` entirely for minimized applications, can pause while the sidebar is hidden, and use a
user-configurable 1–60 minute refresh interval. The tray and each card provide an immediate manual refresh action.

The sidebar now follows the physical leftmost display when monitor topology changes without restarting the application.
Card right-click actions expose bring-to-front, off-screen recovery, preview refresh, and application ignore controls.
The footer hint reflects the actual configured idle delay, and left-clicking the tray icon toggles the sidebar.

v2.5.0 adds a low-memory rendering mode, enabled by default. Card surfaces are created only when visible, released
after the hidden sidebar has been idle, and rendered at a lower internal oversampling ratio. Window capture now scales
directly from the native DIB into the card bitmap and reuses pooled pixel buffers, avoiding multiple full-window copies
on the managed large-object heap. The low-memory renderer uses Windows' software composition path to avoid loading a
large vendor GPU driver into this small utility; it can be disabled in Settings if a particular machine prefers GPU
rendering. On the development machine, steady private memory fell from roughly 84–104 MB to about 37–40 MB.

v2.6 separates the event-driven core, the formal WinForms/Composition product, and the legacy WPF test shell. It adds
single-instance activation, crash-loop safe mode, privacy-redacted local diagnostics, deterministic shutdown, .NET 8
build/test automation, and a self-contained release size below 190 MB.

v2.7 replaces the frequent full-window scan with a bounded WinEvent queue, 33 ms event coalescing, and a 15-second
repair pass. Window identity now includes process lifetime and handle generation; foreground activation resolves modal
popups, verifies the result, and flashes the intended taskbar button when Windows refuses focus. Display selection uses
stable display identities, mixed-DPI changes rebuild card geometry, and each public Windows virtual desktop keeps its
own ordering and expansion state.

v2.8 isolates `PrintWindow` in a recyclable capture-worker mode of the same executable. Captures have a two-second
limit, a bounded/coalesced priority queue, a 16-megapixel source guard, and per-application Auto, Snapshot, or IconOnly
rules. A 16-surface/16-MB rendering pool replaces unbounded per-card resources; LowMemory, Balanced, and Performance
profiles choose WARP, the low-power adapter, or the high-performance adapter. Device-loss recovery rebuilds card
resources and falls back to placeholder cards after three consecutive failures.

v3.0 connects the formal 3D interface to real runtime stages. Different applications can share one stage, cards can be
dragged together, a child window can be dragged out, `Win+Alt+G` adds or removes the foreground window, and the last ten
stage changes can be undone. Coexist/Focus and AllAtOnce/OneAtATime now affect the formal build, including monitor-aware
layout restoration and per-virtual-desktop sessions.

v3.1 adds a searchable application/window switcher, runtime-only pinning to every stage, minimized/off-screen/other-
display/capture-failure card markers, high-contrast colors, system reduced-motion handling, and UI Automation names,
roles, states, window counts, and actions for the Composition sidebar. Preview scheduling is deadline-driven rather
than a 350 ms polling loop, rejected shell windows are cached between 15-second calibration passes, mixed-DPI moves
recreate card surfaces, and display-topology changes recover only genuinely off-screen windows without moving them
back when a monitor reconnects.

v3.1.1 moves hidden edge detection onto an independent low-power background timer. Reaching the physical left edge
now reveals the sidebar without first clicking the foreground window; the sidebar is raised only for that transient
interaction and is demoted again after it hides.

## What the current 3D build includes

- Runtime task stages that may contain windows from several different applications.
- Same-application groups retain the synthetic logo card and click-expanded vertical child list.
- Cross-application stages show up to three real window previews plus an overflow count.
- Click-expanded vertical child cards for selecting an exact window; larger groups support paging.
- macOS-inspired native Composition perspective, shadows, hover feedback, and card-shaped click-through.
- Cards scale from 55% to 125%; long lists scroll without overlap.
- Static window snapshots refresh on a configurable low-frequency schedule rather than continuously.
- The sidebar follows the physical leftmost display and supports edge reveal over maximized or full-screen apps.
- Current-public-virtual-desktop filtering prevents windows from other desktops appearing in the sidebar.
- Settings, ignored applications, appearance, startup behavior, and shortcuts persist in
  `%LocalAppData%\Stage_Manager_Lai\settings.json`.
- Explorer folder windows are supported while the desktop, taskbar, notification area, and shell remain protected.
- A searchable keyboard switcher selects a concrete window by application, title, stage, display, or state.
- Focus/Coexist and AllAtOnce/OneAtATime modes, drag-to-merge, drag-to-extract, ten-step undo, and runtime pinning.
- High contrast, reduced motion, card state markers, and screen-reader metadata.

## Safety rules

Windows Explorer's desktop/taskbar shell surfaces and other Windows shell processes are permanently excluded; normal
File Explorer folder windows remain supported as cards.
Desktop icons are never hidden or toggled. Tencent Yuanbao is shown normally by default; if its selection-translation
overlay is distracting, select Yuanbao in Settings > Ignored applications to hide it.

## Controls

- Click a single-window card to bring it forward; click that foreground card again to minimize it.
- Click a multi-window application card to expand or collapse its vertical list, then click an exact child window.
- Double-click an off-screen window card to recover that window to the display under the pointer.
- Right-click a card to bring it forward, recover it, refresh its preview, or ignore its application.
- Drag a stage card onto another stage to merge them; drag a child card out of the sidebar to split it into a new stage.
- Right-click a concrete window to pin it to all stages for the current run or to move it into its own stage.
- Click the bottom arrow or use the tray icon/global shortcut to hide or show the sidebar.
- Left-click the tray icon to toggle the sidebar; its menu also refreshes all previews and opens Settings.

Default shortcuts:

| Action | Shortcut |
|---|---|
| Show or hide sidebar | `Win+Alt+S` |
| Previous stage | `Win+Alt+[` |
| Next stage | `Win+Alt+]` |
| Add/remove foreground window from current stage | `Win+Alt+G` |
| Search applications and windows | `Win+Alt+Space` |

Windows or another application can reserve a shortcut. Stage_Manager_Lai reports that conflict without simulating
keyboard input; every shortcut can be changed or disabled in Settings.

## Build and verification

Requirements: Windows 10 version 2004 or newer and the .NET 8 SDK.

```powershell
dotnet restore StageManager.sln
dotnet build StageManager.sln -c Release
dotnet test StageManager.Tests\StageManager.Tests.csproj -c Release
```

`RuntimeProbe` is the local integration harness used for rapid window creation and multi-display restoration
tests. The repository's source remains on `net8.0-windows`.
