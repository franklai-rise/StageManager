import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
const exe = process.argv[2];
function call(origin, payload) {
  const body = Buffer.from(JSON.stringify(payload)); const header = Buffer.alloc(4); header.writeUInt32LE(body.length);
  return spawnSync(exe, [origin], { input: Buffer.concat([header, body]), timeout: 20000, windowsHide: true });
}
const origin = 'chrome-extension://koplbglpgeojafoahfepmmamfidoeolo/';
const result = call(origin, { op: 'status' });
assert.equal(result.status, 0); assert.ok(result.stdout.length > 4);
assert.equal(result.stdout.readUInt32LE(0), result.stdout.length - 4);
assert.equal(JSON.parse(result.stdout.subarray(4).toString()).ok, true);
assert.equal(call('chrome-extension://other/', { op: 'status' }).status, 2);
assert.equal(call(origin, { op: 'execute', command: 'no' }).status, 1);
console.log('PASS native protocol framing, settings probe, wrong origin, unknown command; no quota data written.');
