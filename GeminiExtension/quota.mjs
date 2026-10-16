// Protocol reference: TokenYou's Gemini adapter (MIT), Diego Parras and contributors.
// Only known quota rows are accepted. No conversations, tokens or account IDs leave the browser.
export function parseQuota(raw, now = Date.now()) {
  const results = [];
  for (const line of raw.split('\n')) {
    if (!line.includes('jSf9Qc') || !line.includes('wrb.fr')) continue;
    let envelope;
    try { envelope = JSON.parse(line); } catch { continue; }
    if (!Array.isArray(envelope)) continue;
    for (const row of envelope) {
      if (!Array.isArray(row) || row[0] !== 'wrb.fr' || row[1] !== 'jSf9Qc' || typeof row[2] !== 'string') continue;
      const payload = JSON.parse(row[2]);
      if (!Array.isArray(payload?.[1])) throw new Error('schema');
      for (const meter of payload[1]) {
        const fraction = meter?.[1], kind = meter?.[2], seconds = meter?.[3]?.[0]?.[0];
        if (kind !== 1 && kind !== 2) continue;
        if (!Number.isFinite(fraction) || fraction < 0 || fraction > 1 || results.some(m => m.Kind === kind)) throw new Error('schema');
        let reset = null;
        if (seconds != null) {
          if (!Number.isFinite(seconds) || seconds * 1000 < now - 86400000 || seconds * 1000 > now + 40 * 86400000) throw new Error('schema');
          reset = new Date(seconds * 1000).toISOString();
        }
        results.push({ Kind: kind, UsedPercent: Math.round(fraction * 10000) / 100, ResetUtc: reset });
      }
    }
  }
  if (!results.length) throw new Error('no quota');
  return results;
}

export async function readQuota() {
  const signal = AbortSignal.timeout(15000);
  async function text(url, options = {}) {
    const response = await fetch(url, { ...options, credentials: 'include', signal });
    if (!response.ok) throw new Error(response.status === 401 || response.status === 403 ? 'login' : 'service');
    if (new URL(response.url).hostname !== 'gemini.google.com') throw new Error('login');
    const reader = response.body.getReader(); let length = 0, chunks = [];
    try {
      while (true) { const { value, done } = await reader.read(); if (done) break; length += value.length; if (length > 8000000) throw new Error('size'); chunks.push(value); }
    } finally { await reader.cancel().catch(() => {}); }
    const bytes = new Uint8Array(length); let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    return new TextDecoder().decode(bytes);
  }
  const html = await text('https://gemini.google.com/app');
  const at = html.match(/"SNlM0e":"([^"\r\n]+)"/)?.[1];
  const bl = html.match(/"cfb2h":"([^"\r\n]+)"/)?.[1];
  if (!at || !bl) throw new Error('login');
  const raw = await text('https://gemini.google.com/_/BardChatUi/data/batchexecute?' + new URLSearchParams({ rpcids: 'jSf9Qc', 'source-path': '/usage', bl, hl: 'en', rt: 'c' }), {
    method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8' },
    body: new URLSearchParams({ 'f.req': JSON.stringify([[['jSf9Qc', '[]', null, 'generic']]]), at })
  });
  return parseQuota(raw);
}
