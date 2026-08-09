# Stage_Manager_Lai_v1.1

> **Personal modified fork:** This repository is maintained by **Frank Lai** and is based on
> [Stage Manager for Windows](https://github.com/awaescher/StageManager), originally created by
> [Andreas Wäscher](https://github.com/awaescher). It is not presented as an original project by
> the fork maintainer. The original copyright notice and MIT License are retained.

## Lai v1 changes

- anchors the sidebar to the physical left edge of the leftmost Windows display;
- keeps windows from different applications visible simultaneously;
- protects Windows Explorer, the taskbar, desktop icons, and Tencent Yuanbao translation popups;
- removes a redundant low-level mouse hook and releases WinEvent hooks cleanly;
- fixes a startup mouse-event race that could terminate the application;
- uses compact, clipped sidebar preview cards so adjacent apps such as WeChat and Codex do not overlap.
- removes the six-card display limit and automatically scales cards, previews, icons, and spacing to
  fit the available sidebar height; very large scene lists remain accessible by mouse-wheel scrolling.

The published Windows build is identified as `Stage_Manager_Lai_v1.1`. See [NOTICE.md](NOTICE.md)
for authorship and derivative-work attribution.

## About the upstream project

This is an experimental approach to bring the macOS [Stage Manager](https://support.apple.com/en-us/HT213315) to Microsoft Windows.

> **Important:** This is a prototype and a feasibility study - I am not actively developing this project at the moment but I'd be happy to review and merge pull requests. 

![Stage Manager](media/StageManager%20Basics.gif)

This prototype groups applications by their process. By switching between so called "scenes" on the left, Stage Manager hides other windows and the desktop icons, helping you to focus.

Windows can be moved from one scene to another by dragging them onto scenes on the left.

## Usage

Download and run the executable from the [Releases tab](https://github.com/awaescher/StageManager/releases/) or 
 - clone this repository
 - cd into the repository directory
 - run `dotnet run --project StageManager`

To quit, find the app's tray icon (Windows might move it into the overflow menu) and use its context menu to close the app.
 
### Requirements
 - Windows 10 version 2004 or newer
 - [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download)

## To do

This is an experimental fun project. I don't have any idea whether or not this is going to be a final product one day. 

|Topic|State|
|-|-|
|**Experimental stage**||
|initial windows grouping by process|✅|
|3D display of opened windows (static)|✅|
|hide/show windows of given scenes|✅|
|hide/show desktop icons|✅|
|scene management with drag&drop|✅|
|restore windows on quit/restart|✅|
|auto hide & fly-in scenes for maximized windows|✅|
|full screenshots for windows that were minimized on startup|✅|
|drag windows from other scenes into the current one|✅|
|place screenshots in relative size of the desktop|⬜|
|limit maximum scenes (like 6 for macOS?)|✅|
|limit window count per scene (like newest 5)|⬜|
|tray icon to start & stop|✅|
|start with Windows|✅|
|**Product stage**||
|virtual desktop support (pin window)|⬜|
|multi-monitor support|⬜|
|visual feedback when dragging windows from other scenes|⬜|
|feature parity with macOS Stage Manager|⬜|
|**Polishing stage**||
|window animations|⬜|
|live dwm thumbnails|✅|
|adjust 3D angle according to screen position|⬜|
|flyover sidebar in desktop view mode if icons are close to the left|⬜|

Contributions very welcome :heart:

---

Stage Manager is using a few code files to handle window tracking from [workspacer](https://github.com/workspacer/workspacer), an amazing open source project by [Rick Button](https://github.com/rickbutton).
