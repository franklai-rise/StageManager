const status = document.querySelector('#status');
chrome.storage.local.get('message').then(s => { status.textContent = s.message || '尚未连接'; });
for (const op of ['connect', 'refresh', 'disconnect']) document.getElementById(op).onclick = async () => {
  try {
    if (op === 'connect' && !await chrome.permissions.request({ origins: ['https://gemini.google.com/*'] })) { status.textContent = '未授权，不会读取额度。'; return; }
    status.textContent = '正在处理…';
    const result = await chrome.runtime.sendMessage({ op }); status.textContent = result?.message || '已处理';
  } catch { status.textContent = '连接失败，请确认扩展和本机桥接已安装。'; }
};
