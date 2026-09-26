"use strict";

const demoGroups = [
  { id: "browser", icon: "◉", zh: "浏览器", en: "Browser", windows: ["browser-1", "browser-2", "browser-3"] },
  { id: "documents", icon: "▤", zh: "文档", en: "Documents", windows: ["document-1", "document-2"] },
  { id: "files", icon: "▣", zh: "文件夹", en: "Folders", windows: ["files-1"] },
  { id: "notes", icon: "✦", zh: "笔记", en: "Notes", windows: ["notes-1"] }
];

const demoWindows = [
  { id: "browser-1", group: "browser", zh: "浏览器 · 设计参考", en: "Browser · Design references", x: "31%", focusX: "22px", y: "17%", minimized: false, kind: "browser" },
  { id: "browser-2", group: "browser", zh: "浏览器 · 搜索结果", en: "Browser · Search results", x: "38%", focusX: "40px", y: "20%", minimized: true, kind: "browser" },
  { id: "browser-3", group: "browser", zh: "浏览器 · 项目文档", en: "Browser · Project docs", x: "35%", focusX: "31px", y: "23%", minimized: true, kind: "browser" },
  { id: "document-1", group: "documents", zh: "文档 · 工作计划", en: "Document · Work plan", x: "30%", focusX: "22px", y: "22%", minimized: true, kind: "document" },
  { id: "document-2", group: "documents", zh: "文档 · 会议记录", en: "Document · Meeting notes", x: "42%", focusX: "34px", y: "15%", minimized: true, kind: "document" },
  { id: "files-1", group: "files", zh: "文件夹 · 项目资料", en: "Folder · Project files", x: "36%", focusX: "24px", y: "16%", minimized: true, kind: "files" },
  { id: "notes-1", group: "notes", zh: "笔记 · 今天", en: "Notes · Today", x: "39%", focusX: "43px", y: "18%", minimized: true, kind: "notes" }
];

const messages = {
  zh: {
    choose: "点击窗口卡片切换；悬停或点击多窗口主卡展开。",
    expanded: "子卡片向下展开；点击 FIX 可保持展开。",
    pinnedGroup: "这个分组已固定展开，点击 FIXED 才会取消。",
    focus: "Focus 模式为侧栏留出空间；点击左下箭头可暂时收起。",
    coexist: "普通模式下，卡片切换不会移动其他示例窗口。",
    hidden: "侧栏已隐藏。点击左边缘的箭头，或把鼠标移过去唤出。",
    revealed: "侧栏已唤出。点击图钉可保持显示。",
    pinnedSidebar: "图钉已固定侧栏；再次点击可恢复移开后收起。",
    unpinnedSidebar: "已取消固定；鼠标离开后侧栏会收起。",
    fullscreen: "模拟全屏已开启。移动到左边缘仍可唤出侧栏。",
    restored: "示例窗口已恢复。",
    minimized: "已最小化示例窗口；再点一次卡片恢复。",
    reset: "演示已重置。",
    allExpanded: "全部多窗口分组已展开，点击 FIXED 取消。",
    allCollapsed: "已取消全部展开；单独固定的分组仍保留。",
    allMinimized: "所有示例窗口已最小化；再点底部按钮恢复。",
    allRestored: "所有示例窗口已按原状态恢复。",
    fullscreenButton: "模拟全屏",
    exitFullscreen: "退出模拟全屏",
    minimize: "最小化",
    active: "正在使用",
    windows: "个窗口",
    versionError: "版本信息暂不可用 · 请到 GitHub 查看最新发布",
    fileSize: "Windows x64 · {size} MB · 单文件 EXE",
    hash: "SHA-256：{hash}",
    downloadReady: "{version} · Windows x64 · {size} MB · 免费下载",
    allRestoreLabel: "恢复所有示例窗口",
    allMinimizeLabel: "最小化所有示例窗口",
    edgeLabel: "唤出侧栏",
    pinLabel: "固定侧栏",
    unpinLabel: "取消侧栏固定",
    groupPin: "固定{group}分组",
    groupUnpin: "取消固定{group}分组"
  },
  en: {
    choose: "Click a window card to switch; hover or click a group to expand it.",
    expanded: "Child cards flow down. Click FIX to keep the group open.",
    pinnedGroup: "This group is pinned open. Click FIXED to release it.",
    focus: "Focus keeps a space for the sidebar. Use the arrow below to hide its cards.",
    coexist: "Coexist mode leaves your other sample windows where they are.",
    hidden: "The sidebar is hidden. Click its edge handle or move your mouse there to reveal it.",
    revealed: "Sidebar revealed. Use the pushpin to keep it in view.",
    pinnedSidebar: "Sidebar pinned open. Click the pushpin again to release it.",
    unpinnedSidebar: "Unpinned. The sidebar hides after the pointer leaves.",
    fullscreen: "Full-screen simulation is on. Move to the left edge to reveal the sidebar.",
    restored: "Sample window restored.",
    minimized: "Sample window minimized. Click its card again to restore it.",
    reset: "Demo reset.",
    allExpanded: "All multi-window groups are open. Click FIXED to release them.",
    allCollapsed: "Expand all is off. Individually pinned groups remain open.",
    allMinimized: "All sample windows minimized. Click the bottom button again to restore them.",
    allRestored: "Sample windows restored to their previous states.",
    fullscreenButton: "Simulate full screen",
    exitFullscreen: "Exit full screen",
    minimize: "MINIMIZED",
    active: "ACTIVE",
    windows: " windows",
    versionError: "Release information is temporarily unavailable · Open GitHub Releases",
    fileSize: "Windows x64 · {size} MB · single-file EXE",
    hash: "SHA-256: {hash}",
    downloadReady: "{version} · Windows x64 · {size} MB · Free download",
    allRestoreLabel: "Restore all sample windows",
    allMinimizeLabel: "Minimize all sample windows",
    edgeLabel: "Reveal sidebar",
    pinLabel: "Pin sidebar",
    unpinLabel: "Unpin sidebar",
    groupPin: "Pin {group} group",
    groupUnpin: "Unpin {group} group"
  }
};

const desktop = document.querySelector("#demo-desktop");
const sidebar = document.querySelector("#demo-sidebar");
const stageList = document.querySelector("#stage-list");
const windowLayer = document.querySelector("#demo-windows");
const help = document.querySelector("#demo-help");
const languageButton = document.querySelector("#language-toggle");
let language = readLanguage();
let state = initialState();
let leaveTimer = 0;
let collapseTimer = 0;
let lastMessage = "choose";
let recentHover = { group: null, at: 0 };

function readLanguage() {
  try { return localStorage.getItem("stage-lai-site-language") === "en" ? "en" : "zh"; }
  catch { return "zh"; }
}

function initialState() {
  return {
    mode: "coexist", size: 80, sidebarVisible: true, manualCollapse: false,
    sidebarPinned: false, fullscreen: false, visibleBeforeFullscreen: true,
    expandedGroup: null, pinnedGroup: null, expandAll: false,
    activeWindow: "browser-1", z: 1, restoreAll: null,
    windows: Object.fromEntries(demoWindows.map(window => [window.id, { minimized: window.minimized, z: window.id === "browser-1" ? 2 : 1 }]))
  };
}

function tr(key, values = {}) {
  let value = messages[language][key] ?? key;
  for (const [name, replacement] of Object.entries(values)) value = value.replace(`{${name}}`, replacement);
  return value;
}

function label(item) { return item[language]; }

function setMessage(key) { lastMessage = key; help.textContent = tr(key); }

function translatePage() {
  document.documentElement.lang = language === "zh" ? "zh-CN" : "en";
  document.title = language === "zh" ? "Stage Manager Lai · 让窗口各就其位" : "Stage Manager Lai · Give every window its place";
  document.querySelectorAll("[data-zh][data-en]").forEach(node => { node.textContent = node.dataset[language]; });
  document.querySelectorAll("[data-zh-label][data-en-label]").forEach(node => { node.setAttribute("aria-label", node.dataset[`${language}Label`]); });
  languageButton.textContent = language === "zh" ? "English" : "中文";
  languageButton.setAttribute("aria-label", language === "zh" ? "Switch to English" : "切换到中文");
  render();
  applyRelease();
  setMessage(lastMessage);
}

function render() {
  desktop.dataset.mode = state.mode;
  desktop.dataset.fullscreen = String(state.fullscreen);
  desktop.classList.toggle("sidebar-hidden", !state.sidebarVisible);
  desktop.style.setProperty("--card-scale", String(Math.min(1, state.size / 100)));
  desktop.style.setProperty("--card-height", `${Math.round(88 * state.size / 80)}px`);
  document.querySelector("#card-size").value = String(state.size);
  document.querySelector("#size-value").value = `${state.size}%`;
  document.querySelectorAll("[data-mode]").forEach(button => {
    if (!button.matches(".segmented button")) return;
    button.setAttribute("aria-pressed", String(button.dataset.mode === state.mode));
  });
  const expandAllButton = document.querySelector("#expand-all");
  expandAllButton.setAttribute("aria-pressed", String(state.expandAll));
  document.querySelector("#expand-all-legend").textContent = state.expandAll ? "FIXED" : "FIX";
  const pinButton = document.querySelector("#sidebar-pin");
  pinButton.disabled = state.mode !== "focus" || (!state.manualCollapse && !state.sidebarPinned) || state.fullscreen;
  pinButton.setAttribute("aria-pressed", String(state.sidebarPinned));
  pinButton.setAttribute("aria-label", tr(state.sidebarPinned ? "unpinLabel" : "pinLabel"));
  const minimizeButton = document.querySelector("#minimize-all");
  minimizeButton.setAttribute("aria-pressed", String(state.restoreAll !== null));
  minimizeButton.setAttribute("aria-label", tr(state.restoreAll ? "allRestoreLabel" : "allMinimizeLabel"));
  document.querySelector("#taskbar-status").textContent = `${demoGroups.length} APPS`;
  renderCards();
  renderWindows();
}

function isOpen(group) { return state.expandAll || state.expandedGroup === group.id || state.pinnedGroup === group.id; }

function cardMarkup(windowId, child) {
  const window = demoWindows.find(entry => entry.id === windowId);
  const current = state.windows[windowId];
  const active = state.activeWindow === windowId && !current.minimized;
  return `<button type="button" class="stage-card ${child ? "child-card" : ""} ${current.minimized ? "minimized" : ""} ${active ? "active" : ""}" data-window="${windowId}" aria-label="${label(window)}"><span class="card-surface" aria-hidden="true"></span><span class="card-icon" aria-hidden="true">${demoGroups.find(group => group.id === window.group).icon}</span><span class="card-title">${label(window)}</span>${current.minimized ? `<span class="card-badge">${tr("minimize")}</span>` : ""}</button>`;
}

function renderCards() {
  stageList.innerHTML = demoGroups.map(group => {
    if (group.windows.length === 1) return `<div class="stage-group" data-group="${group.id}">${cardMarkup(group.windows[0], false)}</div>`;
    const open = isOpen(group);
    const children = group.windows.map((windowId, index) => `<div style="--delay:${index * 45}ms">${cardMarkup(windowId, true)}</div>`).join("");
    return `<div class="stage-group ${open ? "open" : ""}" data-group="${group.id}"><button type="button" class="stage-card group-primary" data-primary="${group.id}" aria-expanded="${open}" aria-label="${label(group)} · ${group.windows.length}${tr("windows")}"><span class="primary-mark" aria-hidden="true">${group.icon}</span><span class="card-title">${label(group)}</span><span class="card-badge">${group.windows.length}</span></button><button type="button" class="group-pin" data-pin="${group.id}" aria-label="${tr(state.pinnedGroup === group.id ? "groupUnpin" : "groupPin", { group: label(group) })}" aria-pressed="${state.pinnedGroup === group.id}">${state.pinnedGroup === group.id ? "FIXED" : "FIX"}</button><div class="stage-children">${children}</div></div>`;
  }).join("");
}

function windowBody(window) {
  const name = label(window);
  if (window.kind === "files") return `<h3>${name}</h3><div class="fake-tile-row"><div class="fake-tile"></div><div class="fake-tile"></div><div class="fake-tile"></div></div><div class="fake-line"></div><div class="fake-line medium"></div>`;
  if (window.kind === "document") return `<h3>${name}</h3><div class="fake-line"></div><div class="fake-line"></div><div class="fake-line medium"></div><div class="fake-line"></div><div class="fake-line short"></div>`;
  if (window.kind === "notes") return `<h3>${name}</h3><p>${language === "zh" ? "记录想法，让每个任务都有自己的位置。" : "Capture ideas and give each task a place of its own."}</p><div class="fake-line"></div><div class="fake-line short"></div>`;
  return `<h3>${name}</h3><div class="fake-line"></div><div class="fake-line short"></div><div class="fake-tile-row"><div class="fake-tile"></div><div class="fake-tile"></div><div class="fake-tile"></div></div>`;
}

function renderWindows() {
  windowLayer.innerHTML = demoWindows.map(window => {
    const current = state.windows[window.id];
    const active = state.activeWindow === window.id && !current.minimized;
    return `<article class="sample-window ${window.kind}-window ${current.minimized ? "is-minimized" : ""} ${active ? "is-active" : ""}" data-window-id="${window.id}" ${current.minimized ? 'inert aria-hidden="true"' : ""} style="left:${window.x};--x-focus:${window.focusX};top:${window.y};z-index:${current.z}"><div class="sample-titlebar"><strong>${label(window)}</strong><button type="button" class="fullscreen-toggle" data-fullscreen-button="${window.id}" aria-label="${tr(state.fullscreen && active ? "exitFullscreen" : "fullscreenButton")}">${state.fullscreen && active ? "❐" : "□"}</button></div><div class="sample-body">${windowBody(window)}</div></article>`;
  }).join("");
}

function activateWindow(id, toggleCurrent = true) {
  const current = state.windows[id];
  if (state.fullscreen && state.activeWindow !== id) stopFullscreen();
  if (state.activeWindow === id && !current.minimized && toggleCurrent) {
    current.minimized = true;
    state.activeWindow = null;
    if (state.fullscreen) stopFullscreen();
    setMessage("minimized");
  } else {
    current.minimized = false;
    current.z = ++state.z + 10;
    state.activeWindow = id;
    setMessage("restored");
  }
  state.restoreAll = null;
  render();
}

function openGroup(id, fromHover = false) {
  if (state.pinnedGroup && state.pinnedGroup !== id) return;
  if (state.pinnedGroup === id) return;
  if (fromHover && (state.expandAll || state.expandedGroup === id)) return;
  if (state.expandAll) return;
  state.expandedGroup = state.expandedGroup === id && !fromHover ? null : id;
  if (fromHover && state.expandedGroup) recentHover = { group: id, at: performance.now() };
  setMessage(state.expandedGroup ? "expanded" : "choose");
  renderCards();
}

function scheduleCollapse() {
  clearTimeout(collapseTimer);
  if (state.expandAll || state.pinnedGroup || !state.expandedGroup) return;
  collapseTimer = setTimeout(() => {
    state.expandedGroup = null;
    renderCards();
  }, 550);
}

function revealSidebar() {
  clearTimeout(leaveTimer);
  if (state.sidebarVisible) return;
  state.sidebarVisible = true;
  setMessage("revealed");
  render();
}

function scheduleSidebarHide() {
  clearTimeout(leaveTimer);
  if ((!state.manualCollapse && !state.fullscreen) || (state.sidebarPinned && !state.fullscreen) || !state.sidebarVisible) return;
  leaveTimer = setTimeout(() => {
    state.sidebarVisible = false;
    render();
    setMessage("hidden");
  }, 530);
}

function startFullscreen(id) {
  if (state.activeWindow !== id || state.windows[id].minimized) activateWindow(id, false);
  state.visibleBeforeFullscreen = state.sidebarVisible;
  state.fullscreen = true;
  state.sidebarVisible = false;
  setMessage("fullscreen");
  render();
}

function stopFullscreen() {
  state.fullscreen = false;
  state.sidebarVisible = state.visibleBeforeFullscreen;
  setMessage("restored");
  render();
}

stageList.addEventListener("click", event => {
  const pin = event.target.closest("[data-pin]");
  if (pin) {
    const id = pin.dataset.pin;
    state.pinnedGroup = state.pinnedGroup === id ? null : id;
    state.expandedGroup = state.pinnedGroup ?? id;
    setMessage(state.pinnedGroup ? "pinnedGroup" : "expanded");
    renderCards();
    return;
  }
  const primary = event.target.closest("[data-primary]");
  if (primary) {
    const id = primary.dataset.primary;
    if (recentHover.group === id && performance.now() - recentHover.at < 500) return;
    openGroup(id);
    return;
  }
  const card = event.target.closest("[data-window]");
  if (card) activateWindow(card.dataset.window);
});

stageList.addEventListener("pointerover", event => {
  if (event.pointerType === "touch") return;
  const primary = event.target.closest("[data-primary]");
  if (primary) { clearTimeout(collapseTimer); openGroup(primary.dataset.primary, true); }
});

desktop.addEventListener("pointermove", event => {
  if (event.pointerType === "touch") return;
  if (!state.sidebarVisible && event.clientX - desktop.getBoundingClientRect().left < 16) revealSidebar();
  const group = event.target.closest(".stage-group");
  if (group && group.dataset.group === state.expandedGroup) clearTimeout(collapseTimer);
  else scheduleCollapse();
  if (state.sidebarVisible && (state.manualCollapse || state.fullscreen)) {
    const withinSidebar = event.target.closest("#demo-sidebar") || event.target.closest("#edge-reveal");
    if (withinSidebar) clearTimeout(leaveTimer); else scheduleSidebarHide();
  }
});

desktop.addEventListener("pointerleave", () => { scheduleCollapse(); scheduleSidebarHide(); });
document.querySelector("#edge-reveal").addEventListener("click", revealSidebar);

windowLayer.addEventListener("click", event => {
  const fullButton = event.target.closest("[data-fullscreen-button]");
  if (fullButton) {
    if (state.fullscreen && state.activeWindow === fullButton.dataset.fullscreenButton) stopFullscreen();
    else startFullscreen(fullButton.dataset.fullscreenButton);
    return;
  }
  const sample = event.target.closest("[data-window-id]");
  if (sample) activateWindow(sample.dataset.windowId, false);
});

document.querySelectorAll(".segmented button[data-mode]").forEach(button => button.addEventListener("click", () => {
  state.mode = button.dataset.mode;
  state.sidebarPinned = false;
  state.manualCollapse = false;
  state.sidebarVisible = !state.fullscreen;
  setMessage(state.mode === "focus" ? "focus" : "coexist");
  render();
}));

document.querySelector("#card-size").addEventListener("input", event => {
  state.size = Number(event.target.value);
  render();
});

document.querySelector("#reset-demo").addEventListener("click", () => {
  clearTimeout(leaveTimer); clearTimeout(collapseTimer);
  state = initialState();
  setMessage("reset");
  render();
});

document.querySelector("#expand-all").addEventListener("click", () => {
  state.expandAll = !state.expandAll;
  if (!state.expandAll) state.expandedGroup = null;
  setMessage(state.expandAll ? "allExpanded" : "allCollapsed");
  render();
});

document.querySelector("#explorer-card").addEventListener("click", () => activateWindow("files-1", false));

document.querySelector("#minimize-all").addEventListener("click", () => {
  if (state.restoreAll) {
    for (const [id, previous] of Object.entries(state.restoreAll.windows)) state.windows[id] = { ...previous };
    state.activeWindow = state.restoreAll.activeWindow;
    state.restoreAll = null;
    setMessage("allRestored");
  } else {
    state.restoreAll = { windows: structuredClone(state.windows), activeWindow: state.activeWindow };
    for (const value of Object.values(state.windows)) value.minimized = true;
    state.activeWindow = null;
    if (state.fullscreen) stopFullscreen();
    setMessage("allMinimized");
  }
  render();
});

document.querySelector("#collapse-sidebar").addEventListener("click", () => {
  clearTimeout(leaveTimer);
  state.manualCollapse = true;
  state.sidebarPinned = false;
  state.sidebarVisible = false;
  setMessage("hidden");
  render();
});

document.querySelector("#sidebar-pin").addEventListener("click", () => {
  state.sidebarPinned = !state.sidebarPinned;
  setMessage(state.sidebarPinned ? "pinnedSidebar" : "unpinnedSidebar");
  render();
  if (!state.sidebarPinned) scheduleSidebarHide();
});

languageButton.addEventListener("click", () => {
  language = language === "zh" ? "en" : "zh";
  try { localStorage.setItem("stage-lai-site-language", language); } catch { /* Private browsing can block storage. */ }
  translatePage();
});

let releaseData = null;
function applyRelease() {
  if (!releaseData) return;
  const { version, asset } = releaseData;
  const size = (asset.size_bytes / 1_000_000).toFixed(1);
  document.querySelector("#hero-release").textContent = tr("downloadReady", { version, size });
  document.querySelector("#download-meta").textContent = `${version} · ${tr("fileSize", { size })}`;
  document.querySelector("#download-checksum").textContent = tr("hash", { hash: asset.sha256.toUpperCase() });
  document.querySelector("#hero-download").href = asset.url;
  document.querySelector("#download-exe").href = asset.url;
  document.querySelector("#release-page").href = releaseData.release_url;
}

async function loadRelease() {
  try {
    const response = await fetch("data/release.json", { cache: "no-cache" });
    if (!response.ok) throw new Error(`Release data HTTP ${response.status}`);
    const data = await response.json();
    if (!/^v\d+\.\d+\.\d+$/.test(data.version) || !/^https:\/\/github\.com\/franklai-rise\/StageManager\/releases\/download\//.test(data.asset.url) ||
      !/^https:\/\/github\.com\/franklai-rise\/StageManager\/releases\/tag\//.test(data.release_url) || !/^[a-f0-9]{64}$/i.test(data.asset.sha256) ||
      !Number.isSafeInteger(data.asset.size_bytes) || data.asset.size_bytes <= 0) throw new Error("Invalid release data");
    releaseData = data;
    applyRelease();
  } catch (error) {
    document.querySelector("#hero-release").textContent = tr("versionError");
    document.querySelector("#download-meta").textContent = tr("versionError");
    console.warn("Release metadata unavailable:", error);
  }
}

translatePage();
loadRelease();
