// bundle access helpers on top of vfsget.js + unityfs.js
const fs = require('fs'); const path = require('path');
const V = require('../WorldData/vfsget.js');
const U = require('./unityfs.js');
const VFS = require('path').join(process.env.RO3_CLIENT_DIR || 'C:/RO3/ROO_PC', 'ro3_Data/StreamingAssets/VFS');
let META = null;
function meta() { return META || (META = V.loadMeta()); }
const ro3v = new Map();
function ro3vIndex(file) {
  if (ro3v.has(file)) return ro3v.get(file);
  const fd = fs.openSync(file, 'r'); const hdr = Buffer.alloc(24); fs.readSync(fd, hdr, 0, 24, 0);
  const off = Number(hdr.readBigUInt64LE(8)), size = Number(hdr.readBigUInt64LE(16));
  const m = Buffer.alloc(size); fs.readSync(fd, m, 0, size, off);
  const n = m.readUInt32LE(0); const ent = [];
  for (let i = 0; i < n; i++) ent.push([m.readBigUInt64LE(4 + i * 16), m.readBigUInt64LE(12 + i * 16)]);
  const total = m.readBigUInt64LE(4 + n * 16);
  ent.sort((a, b) => (a[1] < b[1] ? -1 : a[1] > b[1] ? 1 : 0));
  const idx = new Map(); ent.forEach((e, i) => idx.set(e[0], [Number(e[1]), Number((i + 1 < ent.length ? ent[i + 1][1] : total) - e[1])]));
  const v = { fd, idx }; ro3v.set(file, v); return v;
}
// entry -> raw UnityFS bytes
function bundleBytes(e) {
  const fh = e.fileHash.toString(16);
  const own = path.join(VFS, e.fileHash.toString(16).padStart(16, '0').slice(0, 2), fh + '.bundle');
  if (e.fileHash === e.vfsHash) return { file: own, data: fs.readFileSync(own) };
  const comb = path.join(VFS, (e.vfsHash ^ BigInt(e.dyn)).toString(16) + '.bundle');
  const v = ro3vIndex(comb); const p = v.idx.get(e.fileHash);
  if (!p) throw new Error('not in container ' + fh);
  const buf = Buffer.alloc(p[1]); fs.readSync(v.fd, buf, 0, p[1], p[0]);
  return { file: comb + '#' + fh, data: buf };
}
function entriesFor(name) { return meta().bundles.get(V.lowerHash(name)) || null; }
function openBundle(e) {
  const { file, data } = bundleBytes(e);
  const nodes = U.readUnityFS(data);
  const files = [];
  for (const n of nodes) {
    if (/\.res[Ss]$|\.resource$/.test(n.path)) continue;
    try { files.push({ path: n.path, sf: U.parseSerialized(n.data) }); } catch (err) { files.push({ path: n.path, err: String(err) }); }
  }
  return { file, files };
}
module.exports = { meta, entriesFor, bundleBytes, openBundle, V, U };
