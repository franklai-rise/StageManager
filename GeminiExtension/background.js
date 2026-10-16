import { readQuota } from './quota.mjs';
const host = 'com.franklai.stage_manager.gemini';
const origins = ['https://gemini.google.com/*'];
let running;
const native = message => chrome.runtime.sendNativeMessage(host, message);
const note = message => chrome.storage.local.set({ message });
async function refresh() {
  if (running) return running;
  running = (async () => {
    const state = await chrome.storage.local.get(['enabled', 'lastAttempt', 'session']);
    if (!state.enabled) return note('未连接。点击“连接 Gemini”。');
    if (Date.now() - (state.lastAttempt || 0) < 10000) return;
    if (!await chrome.permissions.contains({ origins })) return note('需要你授权 Gemini 网站访问权限。');
    let available;
    try { available = await native({ op: 'status' }); } catch { return note('本机桥接未安装或不可用，请先完成安装。'); }
    if (!available.enabled) return note('已暂停：请在 StageManager 设置中启用 AI 卡片和 Gemini。');
    await chrome.storage.local.set({ lastAttempt: Date.now() });
    let session = state.session || crypto.randomUUID().replaceAll('-', '');
    let data;
    try {
      data = { Status: 'Connected', UpdatedUtc: new Date().toISOString(), Session: session, Meters: await readQuota() };
    } catch (error) {
      // Break continuity on every failure: never join data across a possible account change.
      session = crypto.randomUUID().replaceAll('-', '');
      data = { Status: error.message === 'login' ? 'LoginRequired' : 'Unavailable', UpdatedUtc: new Date().toISOString(), Session: session, Meters: [] };
    }
    await chrome.storage.local.set({ session });
    try {
      await native({ op: 'snapshot', data });
      await note(data.Status === 'Connected' ? `已同步 ${new Date().toLocaleTimeString()} · ` + data.Meters.map(m => `${m.Kind === 1 ? '当前' : '每周'}剩余 ${(100 - m.UsedPercent).toFixed(1)}%`).join(' / ') :
        data.Status === 'LoginRequired' ? '请在本浏览器登录 Gemini。若仍失败，可能是网页接口变更。' : '未读到有效额度：网络、账号权限或网页接口可能有变化。');
    } catch { await note('本机桥接拒绝或未收到额度数据，请检查安装。'); }
  })().finally(() => { running = null; });
  return running;
}
async function schedule() { if ((await chrome.storage.local.get('enabled')).enabled) await chrome.alarms.create('quota', { periodInMinutes: 5 }); }
chrome.runtime.onInstalled.addListener(schedule);
chrome.runtime.onStartup.addListener(() => { schedule(); refresh(); });
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'quota') refresh(); });
chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (sender.id !== chrome.runtime.id || sender.url !== chrome.runtime.getURL('popup.html')) return;
  (async () => {
    if (message.op === 'connect') { await chrome.storage.local.set({ enabled: true, lastAttempt: 0, session: crypto.randomUUID().replaceAll('-', '') }); await schedule(); await refresh(); }
    if (message.op === 'refresh') await refresh();
    if (message.op === 'disconnect') {
      await chrome.storage.local.set({ enabled: false }); await chrome.alarms.clear('quota');
      if (running) await running;
      await native({ op: 'snapshot', data: { Status: 'LoginRequired', UpdatedUtc: new Date().toISOString(), Session: crypto.randomUUID().replaceAll('-', ''), Meters: [] } }).catch(() => {});
      await note('已断开，不再查询。');
    }
    reply(await chrome.storage.local.get('message'));
  })().catch(() => reply({ message: '操作失败，请重新打开扩展重试。' }));
  return true;
});
