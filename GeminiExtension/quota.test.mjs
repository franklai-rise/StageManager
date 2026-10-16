import assert from 'node:assert/strict';
import { parseQuota } from './quota.mjs';
const now = Date.now(), reset = Math.floor(now / 1000) + 3600;
const frame = meters => ")]}'\n42\n" + JSON.stringify([['wrb.fr', 'jSf9Qc', JSON.stringify([null, meters])]]);
assert.deepEqual(parseQuota(frame([[0, 0, 1, [[reset, 0]]], [0, 1, 2, [[reset, 0]]]]), now).map(m => m.UsedPercent), [0, 100]);
for (const meters of [[], [[0, -1, 1]], [[0, 1.1, 1]], [[0, '0.1', 1]], [[0, .1, 1], [0, .2, 1]], [[0, .1, 1, [[0, 0]]]]]) assert.throws(() => parseQuota(frame(meters), now));
assert.throws(() => parseQuota('<html>login</html>', now));
console.log('PASS Gemini parser: quota extremes, malformed response, duplicates, missing quota, invalid reset.');
