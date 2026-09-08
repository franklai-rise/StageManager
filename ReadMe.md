# Stage_Manager_Lai v4.4.7

v4.4.7 adds a desktop-icons card directly below the minimize / restore card.
It reads the real Windows desktop-icon setting, toggles the native Explorer
desktop view without restarting Explorer, and follows changes made from the
desktop context menu. The card can be enabled or disabled in Settings.

## v4.4.6

v4.4.6 makes Focus enhanced mode persistent across long idle and exclusive
full-screen sessions. A hidden Focus sidebar now keeps a lightweight foreground
check running, restores immediately when full-screen ends, and rejects stale
idle or delayed hide requests unless an exclusive full-screen window is active.

## v4.4.5

v4.4.5 fixes the minimize/restore shortcut for applications that become hidden
while processing minimize. Restore now applies to every surviving window from
the recorded desktop session, validates that each handle still belongs to its
original process, and explicitly reapplies its normal or maximized placement.
The asynchronous minimize state is no longer sampled to decide whether a
window should be skipped. Exiting while the shortcut is active first restores
the recorded desktop session.

v4.4.4 keeps the column anchored to its collapsed layout when groups expand by
hover or click. The main card and everything above stay in place; child windows
and subsequent groups extend downward, with scrolling available for overflow.
150 ms ease-out motion shares its geometry with hit testing. Toolbar layout and
visual ordering are reused when unchanged, and preview uploads wait for motion
to finish. Recently hidden child previews are retained for up to eight seconds,
with at most eight surfaces and a 4 MiB estimated double-buffer pixel budget.

v4.4.3 restyles the minimize/restore shortcut to match the File Explorer shortcut:
the same adaptive card width, compact height, translucent charcoal background, rounded
corners and perspective. A centered blue desktop icon changes to stacked windows when
the desktop is shown, with a small active indicator and matching hover/press feedback.
The existing minimize/restore behavior is preserved.

v4.4.2 redesigns the Show Desktop control as a card-aligned rounded glass rectangle with a layered-window glyph and persistent active-state indicator.

v4.4.1 replaces the Windows Shell Show Desktop toggle used briefly in v4.4.0.
Stage Manager now records and minimizes only visible application windows while explicitly
excluding its own process, desktop, taskbars, tool windows and cloaked system windows. A
second click restores the recorded window states and foreground window, while the sidebar
remains visible. The glass control now uses a restrained dark translucent finish and a clean
outlined-monitor glyph instead of the former bright solid icon.

v4.4.0 adds an optional circular Show Desktop control below the window cards. Its
translucent glass layers, rim, highlight, shadow and compact monitor glyph provide hover,
press and active feedback without allocating another capture surface. The first click uses
the Windows Shell Show Desktop command; the next restores the same desktop session.

The hidden-icons card is now part of the main scrolling card column instead of a separately
positioned fixed layer. Its obsolete drag handle and independent vertical offset are removed;
refresh remains available from its context menu. Hidden icons and Show Desktop have separate
bilingual switches in Settings. Both follow the last window card and scroll with the column.

v4.3.8 responds as soon as a window-card click is released. The system double-click
interval no longer delays single clicks: a double-click now counts as ONE single click,
with its second press ignored even if window activation resets native click reporting.
Clicking a background window preserves its size, clicking the foreground window minimizes
it, and a later single click restores it. Double-clicking does not perform an additional
maximize, resize, restore, or minimize action. Press feedback and drag/capture-loss
cancellation remain, and separate cards can be clicked in quick succession.

v4.3.7 adds immediate, perspective-aligned click feedback: a subtle blue press highlight,
a lighter pending outline, and a 140 ms release fade. Window-card actions require a release
on the original card; dragging, scrolling, double-clicking, opening a menu, or hiding the
sidebar cancels pending input. Confirmed clicks retain the system double-click interval
and the originally selected activate/minimize intent. Explicit menu and keyboard activation
never minimize a foreground window.

Hover hints now appear after a 500 ms dwell beside the card. Bilingual hints reflect the
current window action and group pin/expand state, shorten long titles, update on state
changes, and disappear on leaving or clicking. Removed double-click commands are no
longer advertised. Press feedback uses small Composition shapes without new capture surfaces.

v4.3.6 makes double-clicking a window card a true no-op. The first click is held for
the Windows double-click interval and runs only when no second click arrives; a second
click cancels the pending activation/minimize action. This prevents double-click races
from restoring applications into an invalid tiny normal-window rectangle.

v4.3.5 restores the intended foreground-window toggle without reintroducing the
maximize-to-normal bug. A background window is brought forward without changing its
placement; clicking the current foreground window minimizes it; clicking that minimized
card restores its previous Windows state. The restore path now performs one native restore
instead of racing two asynchronous restore commands, preserving restore-to-maximized state.

v4.3.4 makes a card's primary click a placement-preserving activation. Clicking a
normal or maximized window only brings that exact window forward; it no longer minimizes
the current foreground window or issues a restore command that could unmaximize it. Only
a window confirmed by Windows to be minimized is restored. Size and position commands
remain explicit actions in the card's context menu.

v4.3.3 refines wheel scrolling: each notch moves one quarter of the former distance,
precision-wheel deltas accumulate without losing steps, and the main column gains up to
40 logical pixels of extra travel at each end. A short damped spring provides smooth
direction changes and a bounded edge rebound. Scrolling translates the main visual layer
and its cached hit region; the hidden-icons layer remains fixed. The animation timer stops
at rest, and turning off animations uses immediate scrolling without bounce.

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
v2.5.1 makes left-edge reveal independent of the currently focused application, including maximized and full-screen
windows, without leaving the sidebar permanently topmost.

v2.5.2 keeps every expanded child-window card in a stable slot. Clicking, minimizing, restoring, or retitling a child
window no longer changes the list order, and newly opened windows are appended.

v2.5.3 adds a centered title to empty preview cards. The title is shortened with `...`; Chinese runs use STZhongsong
(华文中宋) while Latin runs use Times New Roman, with a serif fallback when either font is unavailable.

v2.5.4 remembers each window's first observed normal size, position, and maximized state for its current lifetime.
Card right-click menus can restore that initial layout, move the selected window or application group directly to
the current display center, or close it through the application's normal close path so save prompts remain intact.

v2.5.5 explicitly closes sidebar context menus before deferring window actions, associates manually opened menus with
the sidebar owner, and restores normal outside-click dismissal. Ignoring an application now explains how to restore it
from Settings > Ignored applications. Settings now include a persisted English / Simplified Chinese interface switch
that also updates tray menus, card actions, and tooltips. The small idle-status line below the footer arrow has been
removed and its reserved space returned to the card list.

v2.5.6 adds a short 350 ms dwell gesture for collapsed application cards with two or more windows: pause over the
primary card and its vertical child list opens without a click. The list still stays open until the primary card is
clicked. Settings now show an explicit in-page English / Simplified Chinese selector in addition to the compact toggle,
and the settings window can be resized or maximized with a safe minimum size and adaptive horizontal controls.

v2.5.7 fixes the v2.5.6 hover trigger for real multi-window application groups. Those primary cards are synthetic
white app-logo cards without a window handle, so they had been incorrectly excluded from the dwell test. They now
expand reliably after a 350 ms pointer pause just like the visible application stack.

v4.0.1 is the formal release designation for the current personal build. It includes the stable multi-window hover
expansion fix, configurable card sizing, resizable bilingual settings, static low-frequency previews, click-through
transparent space, and the existing dual-screen left-edge behavior.

v4.1.0 adds an optional Focus enhanced mode. While enabled, the visible card column is registered as a Windows desktop
work-area reservation on the physical leftmost display, so maximized, snapped, newly opened, and selected normal windows
stay in the remaining area to its right. The sidebar remains visible and ignores idle auto-hide. A true exclusive
full-screen window may cover the column; moving the pointer to the left edge temporarily raises the cards above it, and
clicking the card for that foreground full-screen window minimizes it. Leaving Focus enhanced mode immediately restores
the original Windows work area. Other displays, Explorer, the taskbar, and desktop icons are never moved or controlled.

v4.1.1 fixes a Focus-mode conflict where a maximized window could briefly match the physical display bounds and be
mistaken for an exclusive full-screen application. Maximized windows now always retain the reserved card column; only a
non-maximized, borderless window that truly fills the display can use the temporary left-edge reveal behavior.

v4.1.2 removes the bottom sidebar-collapse arrow completely while Focus enhanced mode is enabled. It is neither drawn
nor clickable and no longer reserves footer space; tray and shortcut controls remain available for an intentional manual
hide outside the focused workflow. Switching back to the normal mode restores the arrow automatically.

v4.1.3 adds an adjustable sidebar vertical position. The default is 80 pixels above the previous centered layout;
Settings > Appearance accepts values from -400 to 400 pixels, where negative values move the complete card column upward
and positive values move it downward. The normal-mode collapse arrow follows the column, while Focus enhanced mode keeps
the arrow disabled.

v4.1.4 fixes Focus-mode multi-window interaction. Hover-triggered expansion is now transient and collapses about 500 ms
after the pointer leaves the group, while an explicit click or child-window selection keeps the group expanded until the
primary card is clicked again. Moving directly to another multi-window group also re-arms its 350 ms hover expansion.

v4.2.0 adds an optional File Explorer quick button above the card column. It follows the sidebar's 3D angle, opens Windows
File Explorer with one click, and can be removed completely from **Settings > Appearance** without leaving unused top spacing.

v4.2.1 adds an optional pin button to the right of an expanded multi-window primary card in both Coexist and Focus modes.
A hover-expanded group stays open after pinning; clicking the pin again restores leave-to-collapse behavior, while clicking
the primary card still closes the group immediately. The pin can be disabled in **Settings > Appearance**.

v4.2.2 moves the expanded-card pin onto the primary card's upper-right edge and increases its contrast, preventing the
3D-projected button from being clipped or becoming difficult to see at scaled display settings.

v4.2.3 refines the pin interaction into a dedicated right-edge rail: it no longer covers preview content, uses a larger
invisible click target, shows a hand cursor and faster tooltip, and allows 750 ms before an unpinned hover expansion closes.
The pin is rendered at camera level so card clipping and z-order changes cannot hide it.

v4.2.4 replaces the edge control with a flat pin centered on the expanded primary card. It fades in over 150 ms, brightens
on hover, compresses while pressed, and changes to a blue-and-gold locked state. The enlarged invisible hit target and
camera-level rendering remain, so the centered control is both obvious and reliable.

v4.2.5 attaches the pin directly to the primary card visual. The button now inherits the card's real pivot, scale, Y-axis
tilt and perspective instead of approximating its screen position. A gold status dot and underline appear only while pinned,
making the selected state explicit in addition to the blue background and press animation.

v4.2.6 adds the segmented label `FIX` to the left of the pin icon. When the card is pinned, `ED` fades in on the right so
the complete button reads `FIX [pin] ED`; releasing the pin removes `ED`. The existing blue-and-gold state, confirmation
dot, underline and press animation remain for redundant visual feedback.

v4.2.10 permanently anchors the Focus host window to the physical left edge instead of the AppBar-reduced work area,
and adds a one-second invariant check that restores the sidebar after full-screen transitions move it aside.

v4.2.11 closes card and tray menus when the user clicks anywhere outside them, including another application's window.
Double-clicking a real window card now restores and maximizes that exact window; centering remains an explicit card-menu
command, and the same menu includes a new maximize command for individual windows or complete application groups.

v4.3.2 places compact controls outside the hidden-icons card: drag the vertical double-arrow to move only that card, or click the curved cycle arrow to refresh the hidden-icon mapping immediately. Their input paths are isolated: dragging can never invoke refresh, and refresh runs only after a complete press-and-release on its own button. Dragging updates only this card's transform and commits the full hit-test layout once on release, avoiding whole-sidebar work on every pointer pixel. The card position is persisted without moving the main application column. It also adds optional Google Chrome and Microsoft Edge quick-launch cards beneath File Explorer, using each browser's installed icon. Mouse-wheel scrolling now moves the whole visible main card column even when all cards already fit, works across transparent gaps, and leaves the separate hidden-icons card untouched; global wheel events are coalesced onto the UI queue so Windows cannot silently remove a slow hook. Double-click-specific card commands are disabled; maximize remains available from the card's context menu.

v4.3.1 rebuilds the optional bottom hidden-icons card around the real Windows 11 overflow panel instead of registry history
or executable-file icons. A short-lived isolated worker captures each native icon at its current system pixel size, while
the fixed square frame follows the same rounded `-7.5 degree` perspective as the main cards. Icons stay upright and are
placed on a clean adaptive grid without circular badges or forced resizing. Clicking a named icon invokes that exact
accessible tray item; the card menu can refresh the snapshot or open the native Windows panel.

v4.2.9 makes Focus persistent after long exclusive full-screen sessions, moves user-ignored applications to a
card-only filter so their windows remain launchable and usable, fixes a window-tracker disposal race, and uses a
Windows Startup-folder shortcut so Explorer launches the app independently from Codex or a terminal.

v4.2.8 keeps the Focus sidebar visible while Windows' Alt+Tab task switcher is active. System overlays are no longer
mistaken for managed exclusive full-screen applications, while real managed full-screen windows retain edge reveal.
It also adds a matching 3D button above File Explorer that expands every multi-window card vertically; pressing it
again collapses the groups, and an existing `FIXED` expansion is restored afterward. The button mirrors the card pin
language: it shows `FIX` at rest and reveals `ED` with the active colour when all groups are held open.

v4.2.7 makes `FIXED` a strict expansion lock. A fixed multi-window group cannot be replaced by another group's click or
hover, its primary card cannot collapse it, and temporarily hiding the sidebar preserves the fixed expansion. Only clicking
the centered `FIXED` control releases the lock. Every expanded group that is not fixed now closes automatically after the
pointer leaves, including groups originally opened by clicking rather than hovering.

## What the current 3D build includes

- One stable application group per app, with a synthetic logo card when that app has multiple windows.
- Click-expanded vertical child cards for selecting an exact window; larger groups support paging.
- macOS-inspired native Composition perspective, shadows, hover feedback, and card-shaped click-through.
- Cards scale from 55% to 125%; long lists scroll without overlap.
- The complete card column has an adjustable vertical offset from -400 to 400 pixels; the default is -80 pixels.
- Static window snapshots refresh on a configurable low-frequency schedule rather than continuously.
- The sidebar follows the physical leftmost display and supports edge reveal over maximized or full-screen apps.
- Optional Focus enhanced mode permanently reserves the visible card column for normal and maximized windows while
  retaining temporary edge reveal over true full-screen applications.
- Current-public-virtual-desktop filtering prevents windows from other desktops appearing in the sidebar.
- Settings, ignored applications, appearance, startup behavior, and shortcuts persist in
  `%LocalAppData%\Stage_Manager_Lai\settings.json`.
- Explorer folder windows are supported while the desktop, taskbar, notification area, and shell remain protected.

## Safety rules

Windows Explorer, the taskbar, desktop surfaces, and other Windows shell processes are permanently excluded.
Desktop icons are never hidden or toggled. Tencent Yuanbao is shown normally by default; if its selection-translation
overlay is distracting, select Yuanbao in Settings > Ignored applications to hide it.

## Controls

- Click a single-window card to bring it forward; click that foreground card again to minimize it.
- Click a multi-window application card to expand or collapse its vertical list, then click an exact child window.
- Double-click an off-screen window card to recover that window to the display under the pointer.
- Right-click a card to bring it forward, recover it, refresh its preview, or ignore its application.
- Click the bottom arrow or use the tray icon/global shortcut to hide or show the sidebar.
- Left-click the tray icon to toggle the sidebar; its menu also refreshes all previews and opens Settings.
- Enable `Focus enhanced mode (reserve the card column)` under Settings > Behavior to keep normal windows to the right
  of the visible cards. Manual hide/show controls continue to work in this mode.

Default shortcuts:

| Action | Shortcut |
|---|---|
| Show or hide sidebar | `Win+Alt+S` |
| Previous stage | `Win+Alt+[` |
| Next stage | `Win+Alt+]` |

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
