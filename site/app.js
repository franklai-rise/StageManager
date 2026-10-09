"use strict";

const demoGroups = [
  { id: "browser", icon: "◉", zh: "浏览器", en: "Browser", windows: ["browser-1", "browser-2", "browser-3"] },
  { id: "documents", icon: "▤", zh: "文档", en: "Documents", windows: ["document-1", "document-2"] },
  { id: "files", icon: "▣", zh: "文件夹", en: "Folders", windows: ["files-1"] },
  { id: "notes", icon: "✦", zh: "笔记", en: "Notes", windows: ["notes-1"] }
];
const demoWindows = [
  { id: "browser-1", group: "browser", zh: "浏览器 · 设计参考", en: "Browser · Design references", x: "31%", focusX: "18px", y: "10%", minimized: false, kind: "browser" },
  { id: "browser-2", group: "browser", zh: "浏览器 · 搜索结果", en: "Browser · Search results", x: "35%", focusX: "30px", y: "14%", minimized: true, kind: "browser" },
  { id: "browser-3", group: "browser", zh: "浏览器 · 项目文档", en: "Browser · Project docs", x: "33%", focusX: "24px", y: "12%", minimized: true, kind: "browser" },
  { id: "document-1", group: "documents", zh: "文档 · 工作计划", en: "Document · Work plan", x: "29%", focusX: "18px", y: "15%", minimized: true, kind: "document" },
  { id: "document-2", group: "documents", zh: "文档 · 会议记录", en: "Document · Meeting notes", x: "36%", focusX: "34px", y: "8%", minimized: true, kind: "document" },
  { id: "files-1", group: "files", zh: "文件夹 · 项目资料", en: "Folder · Project files", x: "34%", focusX: "23px", y: "12%", minimized: true, kind: "files" },
  { id: "notes-1", group: "notes", zh: "笔记 · 今天", en: "Notes · Today", x: "32%", focusX: "28px", y: "11%", minimized: true, kind: "notes" }
];
const messages = {
  zh: {
    choose: "悬停或点击浏览器卡片，展开三个窗口。",
    expanded: "子卡片向下流出；点击 FIX，让这个分组保持展开。",
    pinnedGroup: "分组已固定。其他分组仍可展开；点击 FIXED 取消固定。",
    unpinnedGroup: "已取消固定；鼠标离开后，这个分组会收起。",
    focus: "左侧留给卡片，右侧留给工作。Focus 模式下侧栏保持显示。",
    coexist: "普通模式：自由摆放窗口，切换卡片不挪动其他窗口。",
    hidden: "侧栏已收起。移到左边缘，或点击边缘箭头唤出。",
    revealed: "侧栏已唤出。点击底部图钉，让它保持显示。",
    pinnedSidebar: "侧栏已固定。再次点击图钉，恢复移开后收起。",
    unpinnedSidebar: "已取消固定；鼠标离开后，临时唤出的侧栏会收起。",
    fullscreen: "正在模拟全屏。试着靠近桌面的左边缘，唤出卡片。",
    restored: "已切换到选中的窗口；再点同一卡片可最小化。",
    minimized: "窗口已最小化。再点它的卡片即可恢复。",
    reset: "演示已重置。可以重新尝试任意场景。",
    allExpanded: "所有分组已展开。点击 FIXED 取消；单独固定的分组仍会保留。",
    allCollapsed: "已取消全部展开。单独固定的分组保持不变。",
    allMinimized: "桌面清空了。再次点击底部窗口按钮，恢复之前的窗口。",
    allRestored: "窗口已恢复到一键最小化前的状态。",
    fullscreenButton: "模拟全屏", exitFullscreen: "退出模拟全屏",
    minimizeButton: "最小化这个示例窗口", minimize: "最小化", active: "当前窗口",
    windows: "个窗口", taskbar: "{apps} 个应用 / {windows} 个窗口",
    versionError: "版本信息暂不可用 · 可前往 GitHub Releases 下载",
    downloadReady: "{version} · Windows x64 · {size} MB · 免费",
    fileSize: "{version} · Windows x64 · {size} MB · 单文件 EXE",
    published: "发布于 {date}", hash: "SHA-256：{hash}",
    allRestoreLabel: "恢复所有示例窗口", allMinimizeLabel: "最小化所有示例窗口",
    allCollapseLabel: "取消全部固定展开", allExpandLabel: "展开并固定所有分组",
    pinLabel: "固定唤出的侧栏", unpinLabel: "取消侧栏固定",
    groupPin: "固定{group}分组", groupUnpin: "取消固定{group}分组",
    copyLink: "下载链接已复制，可以发送给朋友。", copyHash: "SHA-256 已复制。",
    copyError: "未能复制，请长按或右键复制下载按钮的链接。",
    idea: "留一点空间，给下一次灵感。",
    reference: "为下一个想法，找一点灵感。",
    referenceLead: "把喜欢的颜色、形状和留白，收在一起。",
    coast: "海岸", mountain: "山脊", forest: "森林",
    task1: "整理设计参考", task2: "写下今天的工作计划", task3: "给下一步留一点空间",
    meeting: "想法清楚，下一步也清楚。", file: "项目文件", docs: "说明文档", count: "{count} 项",
    search1: "让工作区更有秩序", search2: "在窗口之间从容切换", search3: "属于自己的桌面节奏",
    docsHeading: "一个简单的开始", docsLead: "选择一个卡片，再把注意力留给眼前的任务。",
    docsCode: "打开 → 展开 → FIX → 专注",
    sampleNote: "先把想法写下来。其余的，慢慢来。"
  },
  en: {
    choose: "Hover or click the Browser card to unfold its three windows.",
    expanded: "Windows flow downward. Click FIX to keep this group open.",
    pinnedGroup: "Group pinned. Other groups still expand; click FIXED to unpin.",
    unpinnedGroup: "Unpinned. This group will fold when the pointer leaves.",
    focus: "Cards on the left, work on the right. Focus keeps the sidebar in view.",
    coexist: "Coexist: arrange freely. Switching cards leaves other windows in place.",
    hidden: "Sidebar hidden. Move to the left edge, or click its handle to reveal it.",
    revealed: "Sidebar revealed. Use the bottom pushpin to keep it visible.",
    pinnedSidebar: "Sidebar pinned. Click the pushpin again to release it.",
    unpinnedSidebar: "Unpinned. The temporarily revealed sidebar hides after you leave.",
    fullscreen: "Full-screen simulation. Move to the desktop’s left edge to find your cards.",
    restored: "Selected window in front. Click the same card again to minimize it.",
    minimized: "Window minimized. Click its card to restore it.",
    reset: "Demo reset. Try any of the scenarios again.",
    allExpanded: "All groups pinned open. Click FIXED to release; individual pins stay.",
    allCollapsed: "Expand all is off. Individually pinned groups stay open.",
    allMinimized: "A clear desktop. Click the bottom window button to restore your windows.",
    allRestored: "Windows restored to their states before minimizing all.",
    fullscreenButton: "Simulate full screen", exitFullscreen: "Exit full screen",
    minimizeButton: "Minimize this sample window", minimize: "MINIMIZED", active: "Current window",
    windows: " windows", taskbar: "{apps} APPS / {windows} WINDOWS",
    versionError: "Release info unavailable · Download from GitHub Releases",
    downloadReady: "{version} · Windows x64 · {size} MB · Free",
    fileSize: "{version} · Windows x64 · {size} MB · Single-file EXE",
    published: "Released {date}", hash: "SHA-256: {hash}",
    allRestoreLabel: "Restore all sample windows", allMinimizeLabel: "Minimize all sample windows",
    allCollapseLabel: "Release expand all", allExpandLabel: "Expand and pin all groups",
    pinLabel: "Pin the revealed sidebar", unpinLabel: "Unpin sidebar",
    groupPin: "Pin {group} group", groupUnpin: "Unpin {group} group",
    copyLink: "Download link copied. Share it with a friend.", copyHash: "SHA-256 copied.",
    copyError: "Could not copy. Right-click or long-press the download link to copy it.",
    idea: "A little room for your next idea.",
    reference: "A little inspiration for what’s next.",
    referenceLead: "Collect the colors, shapes and spaces you love.",
    coast: "Coast", mountain: "Ridge", forest: "Forest",
    task1: "Collect design references", task2: "Write today’s work plan", task3: "Make room for what’s next",
    meeting: "Clear ideas. Clear next steps.", file: "Project files", docs: "Documentation", count: "{count} items",
    search1: "A workspace with room to breathe", search2: "Move calmly between windows", search3: "Find your desktop rhythm",
    docsHeading: "A simple place to start", docsLead: "Choose a card, then bring your attention to the task in front of you.",
    docsCode: "Open → Expand → FIX → Focus",
    sampleNote: "Put the idea on paper first. The rest can wait."
  }
};
const $ = selector => document.querySelector(selector);
const desktop = $("#demo-desktop");
const sidebar = $("#demo-sidebar");
const stageList = $("#stage-list");
const windowLayer = $("#demo-windows");
let language = readLanguage();
let state = initialState();
let lastMessage = "choose";
let leaveTimer = 0;
let collapseTimer = 0;
let collapseGroup = null;
let toastTimer = 0;
let keyboardNavigation = false;
let hoveredGroup = null;
let recentHover = { group: null, at: 0 };
let releaseData = null;

function readLanguage() {
  try { return localStorage.getItem("stage-lai-site-language") === "en" ? "en" : "zh"; }
  catch { return "zh"; }
}
function initialState() {
  return {
    mode: "coexist", size: 100, sidebarVisible: true, manualCollapse: false,
    sidebarPinned: false, fullscreen: false, visibleBeforeFullscreen: true,
    expandedGroup: null, pinnedGroups: new Set(), expandAll: false,
    activeWindow: "browser-1", z: 2, restoreAll: null, scenario: null,
    windows: Object.fromEntries(demoWindows.map(item => [item.id, { minimized: item.minimized, z: item.id === "browser-1" ? 2 : 1 }]))
  };
}
function tr(key, values = {}) {
  let value = messages[language][key] ?? key;
  for (const [name, replacement] of Object.entries(values)) value = value.replaceAll("{" + name + "}", replacement);
  return value;
}
function label(item) { return item[language]; }
function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[char]));
}
function setMessage(key) {
  if (lastMessage === key && $("#demo-help").textContent === tr(key)) return;
  lastMessage = key;
  $("#demo-help").textContent = tr(key);
}
function isOpen(group) { return state.expandAll || state.expandedGroup === group.id || state.pinnedGroups.has(group.id); }

function previewMarkup(item) {
  let content = '<div class="preview-lines"><i></i><i></i><i></i></div>';
  if (item.kind === "files" || item.id === "browser-1") content = '<div class="preview-tiles"><i></i><i></i><i></i></div><div class="preview-lines"><i></i><i></i></div>';
  if (item.kind === "notes") content = '<div class="preview-note">A little room<br>for big ideas.</div>';
  return '<span class="card-preview ' + item.kind + '" aria-hidden="true"><span class="preview-bar"></span><span class="preview-content">' + content + '</span></span>';
}
function cardMarkup(id, child) {
  const item = demoWindows.find(window => window.id === id);
  const group = demoGroups.find(group => group.id === item.group);
  return '<button type="button" class="stage-card ' + (child ? "child-card" : "") + '" data-window="' + id + '">' + previewMarkup(item) + '<span class="card-icon" aria-hidden="true">' + group.icon + '</span><span class="card-title"></span><span class="card-badge" hidden></span></button>';
}
function mountDemo() {
  stageList.innerHTML = demoGroups.map(group => {
    if (group.windows.length === 1) return '<div class="stage-group" data-group="' + group.id + '">' + cardMarkup(group.windows[0], false) + '</div>';
    const children = group.windows.map((id, index) => '<div class="child-row" style="--delay:' + index * 55 + 'ms">' + cardMarkup(id, true) + '</div>').join("");
    return '<div class="stage-group" data-group="' + group.id + '"><button type="button" class="stage-card group-primary" data-primary="' + group.id + '"><span class="primary-mark" aria-hidden="true">' + group.icon + '</span><span class="card-title"></span><span class="card-badge">' + group.windows.length + '</span></button><button type="button" class="group-pin" data-pin="' + group.id + '">FIX</button><div class="stage-children"><div class="children-inner">' + children + '</div></div></div>';
  }).join("");
  windowLayer.innerHTML = demoWindows.map(item => '<article class="sample-window ' + item.kind + '-window" data-window-id="' + item.id + '" style="left:' + item.x + ';--x-focus:' + item.focusX + ';top:' + item.y + '"><div class="sample-titlebar"><strong></strong><div class="sample-controls"><button type="button" data-minimize="' + item.id + '">−</button><button type="button" data-fullscreen-button="' + item.id + '">□</button></div></div><div class="sample-body"></div></article>').join("");
  $("#taskbar-apps").innerHTML = demoGroups.map(group => '<button type="button" data-taskbar="' + group.id + '"><span aria-hidden="true">' + group.icon + '</span></button>').join("");
}
function windowBody(item) {
  const name = escapeHtml(label(item));
  if (item.kind === "files") return '<span class="sample-eyebrow">YOUR FILES, TOGETHER.</span><h3>' + name + '</h3>' + ["file", "docs", "reference"].map((key, index) => '<div class="folder-row"><span class="folder-icon" aria-hidden="true"></span><span>' + escapeHtml(tr(key)) + '</span><span>' + tr("count", { count: [12, 4, 8][index] }) + '</span></div>').join("");
  if (item.kind === "document") return '<div class="document-body"><span class="document-label">' + (item.id === "document-1" ? "WORK / 01" : "MEETING / 02") + '</span><h3>' + name + '</h3><p>' + escapeHtml(tr(item.id === "document-1" ? "idea" : "meeting")) + '</p>' + ["task1", "task2", "task3"].map(key => '<div class="doc-check">' + escapeHtml(tr(key)) + '</div>').join("") + '<div class="fake-line"></div><div class="fake-line medium"></div></div>';
  if (item.kind === "notes") return '<span class="sample-eyebrow">NOTES TO SELF</span><h3>' + name + '</h3><div class="note-paper"><p>' + escapeHtml(tr("sampleNote")) + '</p><p>' + escapeHtml(tr("idea")) + '</p><div class="fake-line"></div><div class="fake-line short"></div></div>';
  const address = '<div class="browser-address">◇ &nbsp; stage.example / ' + (item.id === "browser-1" ? "inspiration" : item.id === "browser-2" ? "search" : "docs") + '</div>';
  if (item.id === "browser-2") return address + '<span class="sample-eyebrow">SEARCH / IDEAS</span><h3>' + name + '</h3>' + ["search1", "search2", "search3"].map(key => '<div class="search-row"><strong>' + escapeHtml(tr(key)) + '</strong><div class="fake-line medium"></div></div>').join("");
  if (item.id === "browser-3") return address + '<span class="sample-eyebrow">DOCUMENTATION</span><h3>' + escapeHtml(tr("docsHeading")) + '</h3><p>' + escapeHtml(tr("docsLead")) + '</p><div class="code-line">' + escapeHtml(tr("docsCode")) + '</div><div class="fake-line"></div><div class="fake-line medium"></div>';
  return address + '<span class="sample-eyebrow">A SMALL COLLECTION / 03</span><h3>' + escapeHtml(tr("reference")) + '</h3><p>' + escapeHtml(tr("referenceLead")) + '</p><div class="reference-grid"><div class="reference-tile"></div><div class="reference-tile"></div><div class="reference-tile"></div></div><div class="reference-labels"><span>' + tr("coast") + '</span><span>' + tr("mountain") + '</span><span>' + tr("forest") + '</span></div>';
}
function translatePage() {
  document.documentElement.lang = language === "zh" ? "zh-CN" : "en";
  document.title = language === "zh" ? "Stage Manager Lai · 给每个窗口，一个舞台" : "Stage Manager Lai · Every window, a place of its own";
  document.querySelectorAll("[data-zh][data-en]").forEach(node => { node.textContent = node.dataset[language]; });
  document.querySelectorAll("[data-zh-label][data-en-label]").forEach(node => { node.setAttribute("aria-label", node.dataset[language + "Label"]); });
  $("#language-toggle").textContent = language === "zh" ? "EN / 中" : "中 / EN";
  $("#language-toggle").setAttribute("aria-label", language === "zh" ? "Switch to English" : "切换到中文");
  for (const item of demoWindows) {
    const sample = windowLayer.querySelector('[data-window-id="' + item.id + '"]');
    sample.querySelector(".sample-titlebar strong").textContent = label(item);
    sample.querySelector(".sample-body").innerHTML = windowBody(item);
  }
  render();
  applyRelease();
  setMessage(lastMessage);
}
function render() {
  desktop.dataset.mode = state.mode;
  desktop.dataset.fullscreen = String(state.fullscreen);
  desktop.classList.toggle("sidebar-hidden", !state.sidebarVisible);
  desktop.style.setProperty("--card-scale", String(state.size / 100));
  sidebar.inert = !state.sidebarVisible;
  $("#edge-reveal").disabled = state.sidebarVisible;
  $("#edge-reveal").tabIndex = state.sidebarVisible ? -1 : 0;
  $("#card-size").value = String(state.size);
  $("#size-value").value = state.size + "%";
  $("#desktop-mode").textContent = state.fullscreen ? "FULL SCREEN" : state.mode.toUpperCase();
  document.querySelectorAll(".segmented button").forEach(button => button.setAttribute("aria-pressed", String(button.dataset.mode === state.mode)));
  document.querySelectorAll("[data-scenario]").forEach(button => button.setAttribute("aria-pressed", String(button.dataset.scenario === state.scenario)));
  $("#expand-all").setAttribute("aria-pressed", String(state.expandAll));
  $("#expand-all").setAttribute("aria-label", tr(state.expandAll ? "allCollapseLabel" : "allExpandLabel"));
  $("#expand-all-legend").textContent = state.expandAll ? "FIXED" : "FIX";
  const pin = $("#sidebar-pin");
  pin.disabled = (!state.manualCollapse && !state.sidebarPinned) || state.fullscreen;
  pin.setAttribute("aria-pressed", String(state.sidebarPinned));
  pin.setAttribute("aria-label", tr(state.sidebarPinned ? "unpinLabel" : "pinLabel"));
  $("#minimize-all").setAttribute("aria-pressed", String(state.restoreAll !== null));
  $("#minimize-all").setAttribute("aria-label", tr(state.restoreAll ? "allRestoreLabel" : "allMinimizeLabel"));
  $("#taskbar-status").textContent = tr("taskbar", { apps: demoGroups.length, windows: demoWindows.length });
  $("#desktop-empty").hidden = demoWindows.some(item => !state.windows[item.id].minimized);
  for (const group of demoGroups) {
    const node = stageList.querySelector('[data-group="' + group.id + '"]');
    const primary = node.querySelector("[data-primary]");
    if (primary) {
      const open = isOpen(group);
      node.classList.toggle("open", open);
      primary.setAttribute("aria-expanded", String(open));
      primary.setAttribute("aria-label", label(group) + " · " + group.windows.length + tr("windows"));
      primary.querySelector(".card-title").textContent = label(group);
      const children = node.querySelector(".children-inner");
      children.inert = !open;
      children.setAttribute("aria-hidden", String(!open));
      const pinButton = node.querySelector("[data-pin]");
      const pinned = state.pinnedGroups.has(group.id);
      pinButton.setAttribute("aria-pressed", String(pinned));
      pinButton.setAttribute("aria-label", tr(pinned ? "groupUnpin" : "groupPin", { group: label(group) }));
      pinButton.textContent = pinned ? "FIXED" : "FIX";
    }
    const taskbar = document.querySelector('[data-taskbar="' + group.id + '"]');
    taskbar.setAttribute("aria-label", label(group));
    taskbar.setAttribute("aria-pressed", String(group.windows.includes(state.activeWindow)));
  }
  for (const item of demoWindows) {
    const current = state.windows[item.id];
    const active = state.activeWindow === item.id && !current.minimized;
    const card = stageList.querySelector('[data-window="' + item.id + '"]');
    card.classList.toggle("active", active);
    card.classList.toggle("minimized", current.minimized);
    card.setAttribute("aria-label", label(item) + (current.minimized ? " · " + tr("minimize") : active ? " · " + tr("active") : ""));
    card.setAttribute("aria-pressed", String(active));
    card.querySelector(".card-title").textContent = label(item);
    const badge = card.querySelector(".card-badge");
    badge.hidden = !current.minimized;
    badge.textContent = tr("minimize");
    const sample = windowLayer.querySelector('[data-window-id="' + item.id + '"]');
    sample.classList.toggle("is-minimized", current.minimized);
    sample.classList.toggle("is-active", active);
    sample.inert = current.minimized;
    sample.setAttribute("aria-hidden", String(current.minimized));
    sample.style.zIndex = current.z;
    const fullButton = sample.querySelector("[data-fullscreen-button]");
    fullButton.setAttribute("aria-label", tr(state.fullscreen && active ? "exitFullscreen" : "fullscreenButton"));
    fullButton.textContent = state.fullscreen && active ? "❐" : "□";
    sample.querySelector("[data-minimize]").setAttribute("aria-label", tr("minimizeButton"));
  }
}
function stopFullscreen() {
  if (!state.fullscreen) return;
  state.fullscreen = false;
  state.sidebarVisible = state.visibleBeforeFullscreen;
  clearSidebarTimer();
}
function activateWindow(id, toggle = true) {
  const current = state.windows[id];
  if (state.fullscreen && state.activeWindow !== id) stopFullscreen();
  if (state.activeWindow === id && !current.minimized && toggle) {
    current.minimized = true;
    state.activeWindow = null;
    stopFullscreen();
    setMessage("minimized");
  } else {
    current.minimized = false;
    current.z = ++state.z;
    state.activeWindow = id;
    setMessage("restored");
  }
  state.restoreAll = null;
  render();
}
function openGroup(id, fromHover = false) {
  if (state.expandAll || state.pinnedGroups.has(id)) return;
  if (fromHover && state.expandedGroup === id) return;
  clearTimeout(collapseTimer); collapseTimer = 0;
  state.expandedGroup = state.expandedGroup === id && !fromHover ? null : id;
  if (fromHover) recentHover = { group: id, at: performance.now() };
  setMessage(state.expandedGroup ? "expanded" : "choose");
  render();
}
function scheduleCollapse() {
  if (state.expandAll || !state.expandedGroup || state.pinnedGroups.has(state.expandedGroup)) return;
  if (collapseTimer && collapseGroup === state.expandedGroup) return;
  clearTimeout(collapseTimer);
  const id = state.expandedGroup;
  collapseGroup = id;
  collapseTimer = setTimeout(() => {
    collapseTimer = 0;
    const group = stageList.querySelector('[data-group="' + id + '"]');
    if (hoveredGroup === id || (keyboardNavigation && group.contains(document.activeElement))) return;
    if (state.expandedGroup === id && !state.pinnedGroups.has(id)) {
      state.expandedGroup = null;
      render();
    }
  }, 550);
}
function clearSidebarTimer() { clearTimeout(leaveTimer); leaveTimer = 0; }
function revealSidebar() {
  clearSidebarTimer();
  if (state.sidebarVisible) return;
  state.sidebarVisible = true;
  setMessage("revealed");
  render();
}
function scheduleSidebarHide() {
  if (leaveTimer || (!state.manualCollapse && !state.fullscreen) || (state.sidebarPinned && !state.fullscreen) || !state.sidebarVisible || (keyboardNavigation && sidebar.contains(document.activeElement))) return;
  leaveTimer = setTimeout(() => {
    leaveTimer = 0;
    if (keyboardNavigation && sidebar.contains(document.activeElement)) return;
    state.sidebarVisible = false;
    render();
    setMessage("hidden");
  }, 600);
}
function startFullscreen(id) {
  if (state.fullscreen && state.activeWindow === id) return;
  if (state.activeWindow !== id || state.windows[id].minimized) activateWindow(id, false);
  state.visibleBeforeFullscreen = state.sidebarVisible;
  state.fullscreen = true;
  state.sidebarVisible = false;
  clearSidebarTimer();
  setMessage("fullscreen");
  render();
}
function clearDemoTimers() {
  clearSidebarTimer();
  clearTimeout(collapseTimer); collapseTimer = 0;
  hoveredGroup = null;
  recentHover = { group: null, at: 0 };
}
function resetDemo() {
  clearDemoTimers();
  state = initialState();
  stageList.scrollTop = 0;
  setMessage("reset");
  render();
}
document.addEventListener("pointerdown", () => { keyboardNavigation = false; }, { passive: true });
document.addEventListener("keydown", event => {
  if (["Tab", "ArrowUp", "ArrowDown", "Home", "End", "Enter", " "].includes(event.key)) keyboardNavigation = true;
});
stageList.addEventListener("click", event => {
  const pin = event.target.closest("[data-pin]");
  if (pin) {
    const id = pin.dataset.pin;
    clearTimeout(collapseTimer); collapseTimer = 0;
    if (state.pinnedGroups.has(id)) {
      state.pinnedGroups.delete(id); state.expandedGroup = id;
      setMessage("unpinnedGroup");
    } else {
      state.pinnedGroups.add(id);
      setMessage("pinnedGroup");
    }
    render();
    return;
  }
  const primary = event.target.closest("[data-primary]");
  if (primary) {
    if (event.detail > 0 && recentHover.group === primary.dataset.primary && performance.now() - recentHover.at < 450) return;
    openGroup(primary.dataset.primary);
    return;
  }
  const card = event.target.closest("[data-window]");
  if (card) activateWindow(card.dataset.window);
});
stageList.addEventListener("pointerover", event => {
  if (event.pointerType === "touch") return;
  const group = event.target.closest(".stage-group");
  if (!group) return;
  hoveredGroup = group.dataset.group;
  if (state.expandedGroup === hoveredGroup) { clearTimeout(collapseTimer); collapseTimer = 0; }
  const primary = event.target.closest("[data-primary]");
  if (primary) openGroup(primary.dataset.primary, true);
});
stageList.addEventListener("pointerout", event => {
  const group = event.target.closest(".stage-group");
  if (!group || group.contains(event.relatedTarget)) return;
  hoveredGroup = null;
  scheduleCollapse();
});
stageList.addEventListener("focusout", () => queueMicrotask(scheduleCollapse));
stageList.addEventListener("keydown", event => {
  if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
  const buttons = [...stageList.querySelectorAll("button")].filter(button => !button.closest("[inert]") && button.getClientRects().length);
  const index = buttons.indexOf(document.activeElement);
  if (index < 0 || !buttons.length) return;
  event.preventDefault();
  let next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 : (index + (event.key === "ArrowDown" ? 1 : -1) + buttons.length) % buttons.length;
  buttons[next].focus({ preventScroll: true });
  buttons[next].scrollIntoView({ block: "nearest", behavior: "instant" });
});
desktop.addEventListener("pointermove", event => {
  if (event.pointerType === "touch") return;
  if (!state.sidebarVisible && event.clientX - desktop.getBoundingClientRect().left < 23) revealSidebar();
  const group = event.target.closest(".stage-group");
  if (group && group.dataset.group === state.expandedGroup) {
    clearTimeout(collapseTimer); collapseTimer = 0;
  } else scheduleCollapse();
  if (event.target.closest("#demo-sidebar, #edge-reveal")) clearSidebarTimer();
  else scheduleSidebarHide();
});
desktop.addEventListener("pointerleave", () => { hoveredGroup = null; scheduleCollapse(); scheduleSidebarHide(); });
sidebar.addEventListener("focusin", clearSidebarTimer);
sidebar.addEventListener("focusout", () => queueMicrotask(scheduleSidebarHide));
$("#edge-reveal").addEventListener("click", revealSidebar);
desktop.addEventListener("keydown", event => {
  if (event.key !== "Escape") return;
  event.preventDefault();
  if (state.fullscreen) stopFullscreen();
  state.sidebarVisible = true;
  state.expandedGroup = null;
  clearDemoTimers();
  setMessage("revealed");
  render();
  $("#collapse-sidebar").focus({ preventScroll: true });
});
windowLayer.addEventListener("click", event => {
  const minimize = event.target.closest("[data-minimize]");
  if (minimize) {
    state.windows[minimize.dataset.minimize].minimized = true;
    if (state.activeWindow === minimize.dataset.minimize) { state.activeWindow = null; stopFullscreen(); }
    state.restoreAll = null; setMessage("minimized"); render(); return;
  }
  const full = event.target.closest("[data-fullscreen-button]");
  if (full) {
    if (state.fullscreen && state.activeWindow === full.dataset.fullscreenButton) { stopFullscreen(); setMessage("restored"); render(); }
    else startFullscreen(full.dataset.fullscreenButton);
    return;
  }
  const sample = event.target.closest("[data-window-id]");
  if (sample) activateWindow(sample.dataset.windowId, false);
});
document.querySelectorAll(".segmented button").forEach(button => button.addEventListener("click", () => {
  clearSidebarTimer();
  state.mode = button.dataset.mode;
  state.sidebarPinned = false; state.manualCollapse = false;
  state.sidebarVisible = !state.fullscreen;
  state.scenario = null;
  setMessage(state.mode === "focus" ? "focus" : "coexist"); render();
}));
$("#card-size").addEventListener("input", event => { state.size = Number(event.target.value); render(); });
$("#reset-demo").addEventListener("click", resetDemo);
$("#expand-all").addEventListener("click", () => {
  state.expandAll = !state.expandAll;
  if (!state.expandAll) state.expandedGroup = null;
  setMessage(state.expandAll ? "allExpanded" : "allCollapsed"); render();
});
$("#explorer-card").addEventListener("click", () => activateWindow("files-1", false));
$("#minimize-all").addEventListener("click", () => {
  if (state.restoreAll) {
    for (const [id, previous] of Object.entries(state.restoreAll.windows)) state.windows[id] = { ...previous };
    state.activeWindow = state.restoreAll.activeWindow; state.restoreAll = null;
    setMessage("allRestored");
  } else {
    const snapshot = { windows: structuredClone(state.windows), activeWindow: state.activeWindow };
    stopFullscreen();
    for (const current of Object.values(state.windows)) current.minimized = true;
    state.activeWindow = null; state.restoreAll = snapshot;
    setMessage("allMinimized");
  }
  render();
});
$("#collapse-sidebar").addEventListener("click", () => {
  clearSidebarTimer();
  state.manualCollapse = true; state.sidebarPinned = false; state.sidebarVisible = false;
  setMessage("hidden"); render();
  $("#edge-reveal").focus({ preventScroll: true });
});
$("#sidebar-pin").addEventListener("click", () => {
  state.sidebarPinned = !state.sidebarPinned;
  setMessage(state.sidebarPinned ? "pinnedSidebar" : "unpinnedSidebar"); render();
});
$("#taskbar-apps").addEventListener("click", event => {
  const button = event.target.closest("[data-taskbar]");
  if (!button) return;
  const group = demoGroups.find(group => group.id === button.dataset.taskbar);
  const visible = group.windows.filter(id => !state.windows[id].minimized).sort((a, b) => state.windows[b].z - state.windows[a].z);
  activateWindow(visible[0] ?? group.windows[0]);
});
document.querySelectorAll("[data-scenario]").forEach(button => button.addEventListener("click", () => {
  resetDemo();
  state.scenario = button.dataset.scenario;
  if (state.scenario === "groups") { state.expandedGroup = "browser"; setMessage("expanded"); }
  if (state.scenario === "focus") {
    state.mode = "focus";
    state.windows["document-1"].minimized = false;
    state.windows["document-1"].z = 3; state.z = 3; state.activeWindow = "document-1";
    setMessage("focus");
  }
  if (state.scenario === "fullscreen") { state.mode = "focus"; startFullscreen("browser-1"); }
  render();
}));
$("#language-toggle").addEventListener("click", () => {
  language = language === "zh" ? "en" : "zh";
  try { localStorage.setItem("stage-lai-site-language", language); } catch { /* Storage may be disabled. */ }
  translatePage();
});
function applyRelease() {
  if (!releaseData) return;
  const { version, asset } = releaseData;
  const size = (asset.size_bytes / 1_000_000).toFixed(1);
  $("#hero-release").textContent = tr("downloadReady", { version, size });
  $("#download-meta").textContent = tr("fileSize", { version, size });
  $("#download-checksum").textContent = tr("hash", { hash: asset.sha256.toUpperCase() });
  $("#hero-download").href = asset.url; $("#download-exe").href = asset.url;
  $("#release-page").href = releaseData.release_url;
  $("#copy-download").disabled = false; $("#copy-checksum").disabled = false;
  const date = new Date(releaseData.published_at);
  $("#release-date").textContent = Number.isNaN(date.getTime()) ? "" : tr("published", { date: new Intl.DateTimeFormat(language === "zh" ? "zh-CN" : "en", { year: "numeric", month: "short", day: "numeric" }).format(date) });
}
async function loadRelease() {
  try {
    const response = await fetch("data/release.json", { cache: "no-cache" });
    if (!response.ok) throw new Error("Release data HTTP " + response.status);
    const data = await response.json();
    if (!/^v\d+\.\d+\.\d+$/.test(data.version) || !/^https:\/\/github\.com\/franklai-rise\/StageManager\/releases\/download\//.test(data.asset.url) ||
        !/^https:\/\/github\.com\/franklai-rise\/StageManager\/releases\/tag\//.test(data.release_url) ||
        !/^[a-f0-9]{64}$/i.test(data.asset.sha256) || !Number.isSafeInteger(data.asset.size_bytes) || data.asset.size_bytes <= 0) throw new Error("Invalid release data");
    releaseData = data; applyRelease();
  } catch {
    $("#hero-release").textContent = tr("versionError");
    $("#download-meta").textContent = tr("versionError");
  }
}
function showToast(message) {
  clearTimeout(toastTimer);
  const toast = $("#site-toast");
  toast.textContent = message; toast.hidden = false;
  toastTimer = setTimeout(() => { toast.hidden = true; }, 3500);
}
async function copyText(value, success) {
  try {
    await navigator.clipboard.writeText(value);
    showToast(tr(success));
  } catch { showToast(tr("copyError")); }
}
$("#copy-download").addEventListener("click", () => { if (releaseData) copyText(releaseData.asset.url, "copyLink"); });
$("#copy-checksum").addEventListener("click", () => { if (releaseData) copyText(releaseData.asset.sha256.toUpperCase(), "copyHash"); });
function updateClock() {
  if (document.hidden) return;
  $("#demo-clock").textContent = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit" }).format(new Date());
}
document.addEventListener("visibilitychange", updateClock);
mountDemo();
translatePage();
updateClock();
setInterval(updateClock, 60000);
loadRelease();
