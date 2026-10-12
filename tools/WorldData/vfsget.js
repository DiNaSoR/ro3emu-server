// Extract a "bytes" asset from the RO3 VFS (<RO3_CLIENT_DIR>/ro3_Data/StreamingAssets/VFS) by file name.
// usage: node vfsget.js <name>[,<name>...] [outDir]   e.g. sc_prt_fild08_001_dynamicmapdata.bytes
const fs = require('fs');
const path = require('path');
const VFS = require('path').join(process.env.RO3_CLIENT_DIR || 'C:/RO3/ROO_PC', 'ro3_Data/StreamingAssets/VFS');
const M64 = (1n << 64n) - 1n;
const C1 = 9782798678568883157n, C2 = 5545529020109919103n;
const rotl = (x, r) => ((x << BigInt(r)) | (x >> BigInt(64 - r))) & M64;
const fmix = k => { k ^= k >> 33n; k = (k * 18397679294719823053n) & M64; k ^= k >> 33n; k = (k * 14181476777654086739n) & M64; k ^= k >> 33n; return k; };
function murmur64(buf, seed = 0n) {
  let h1 = seed, h2 = seed; const len = buf.length; const nb = Math.floor(len / 16);
  for (let i = 0; i < nb; i++) {
    let k1 = buf.readBigUInt64LE(i * 16), k2 = buf.readBigUInt64LE(i * 16 + 8);
    k1 = (k1 * C1) & M64; k1 = rotl(k1, 31); k1 = (k1 * C2) & M64; h1 ^= k1;
    h1 = rotl(h1, 27); h1 = (h1 + h2) & M64; h1 = (h1 * 5n + 1390208809n) & M64;
    k2 = (k2 * C2) & M64; k2 = rotl(k2, 33); k2 = (k2 * C1) & M64; h2 ^= k2;
    h2 = rotl(h2, 31); h2 = (h2 + h1) & M64; h2 = (h2 * 5n + 944331445n) & M64;
  }
  const t = buf.subarray(nb * 16); const r = len & 15;
  let k1 = 0n, k2 = 0n;
  for (let i = Math.min(r, 15) - 1; i >= 8; i--) k2 ^= BigInt(t[i]) << BigInt((i - 8) * 8);
  if (r > 8) { k2 = (k2 * C2) & M64; k2 = rotl(k2, 33); k2 = (k2 * C1) & M64; h2 ^= k2; }
  for (let i = Math.min(r, 8) - 1; i >= 0; i--) k1 ^= BigInt(t[i]) << BigInt(i * 8);
  if (r > 0) { k1 = (k1 * C1) & M64; k1 = rotl(k1, 31); k1 = (k1 * C2) & M64; h1 ^= k1; }
  h1 ^= BigInt(len); h2 ^= BigInt(len); h1 = (h1 + h2) & M64; h2 = (h2 + h1) & M64;
  h1 = fmix(h1); h2 = fmix(h2); return (h1 + h2) & M64;
}
const lowerHash = s => murmur64(Buffer.from(s.replace(/[A-Z]/g, c => c.toLowerCase()), 'utf8'));

function loadMeta() {
  const b = fs.readFileSync(path.join(VFS, 'assetmeta.data'));
  let o = 0; const bundles = new Map(), bytes = new Map(), raws = new Map();
  const rd = (map) => {
    const n = b.readUInt32LE(o); o += 4;
    for (let i = 0; i < n; i++) {
      const e = { asset: b.readBigUInt64LE(o), loadType: b[o + 8], dyn: b.readUInt16LE(o + 9), fileHash: b.readBigUInt64LE(o + 11), vfsHash: b.readBigUInt64LE(o + 19) };
      o += 27; if (!map.has(e.asset)) map.set(e.asset, []); map.get(e.asset).push(e);
    }
    return n;
  };
  const nb = rd(bundles), ny = rd(bytes);
  const nr = b.readUInt32LE(o); o += 4;
  for (let i = 0; i < nr; i++) { const l = b.readUInt16LE(o); o += 2; const s = b.toString('ascii', o, o + l); o += l; const dyn = b.readUInt16LE(o); o += 2; raws.set(lowerHash(s), { s, dyn }); }
  return { bundles, bytes, raws, nb, ny, nr };
}
function hexPrefix(h) { return h.toString(16).padStart(16, '0').slice(0, 2); }
const vfsCache = new Map();
function loadVfs(file) {
  if (vfsCache.has(file)) return vfsCache.get(file);
  const fd = fs.openSync(file, 'r'); const hdr = Buffer.alloc(24); fs.readSync(fd, hdr, 0, 24, 0);
  if (hdr.toString('ascii', 0, 4) !== 'RO3V') throw new Error('bad magic ' + file);
  const off = Number(hdr.readBigUInt64LE(8)), size = Number(hdr.readBigUInt64LE(16));
  const m = Buffer.alloc(size); fs.readSync(fd, m, 0, size, off);
  const n = m.readUInt32LE(0); const ent = [];
  for (let i = 0; i < n; i++) ent.push([m.readBigUInt64LE(4 + i * 16), m.readBigUInt64LE(12 + i * 16)]);
  const total = m.readBigUInt64LE(4 + n * 16);
  ent.sort((a, b) => (a[1] < b[1] ? -1 : a[1] > b[1] ? 1 : 0));
  const idx = new Map();
  ent.forEach((e, i) => idx.set(e[0], [e[1], (i + 1 < ent.length ? ent[i + 1][1] : total) - e[1]]));
  const v = { fd, idx, ver: hdr.readUInt32LE(4) }; vfsCache.set(file, v); return v;
}
function extract(meta, name) {
  const h = lowerHash(name);
  const list = meta.bytes.get(h) || meta.bundles.get(h);
  if (!list) return { name, hash: h.toString(16), found: false, raw: meta.raws.has(h) };
  const e = list[0];
  if (e.fileHash === e.vfsHash) {
    const f = path.join(VFS, hexPrefix(e.fileHash), e.fileHash.toString(16) + '.bundle');
    return { name, e, file: f, data: fs.readFileSync(f), variants: list.length };
  }
  const vf = path.join(VFS, (e.vfsHash ^ BigInt(e.dyn)).toString(16) + '.bytes');
  const v = loadVfs(vf); const p = v.idx.get(e.fileHash);
  if (!p) return { name, e, vf, found: false };
  const buf = Buffer.alloc(Number(p[1])); fs.readSync(v.fd, buf, 0, buf.length, Number(p[0]));
  return { name, e, file: vf, offset: p[0], data: buf, variants: list.length };
}
module.exports = { lowerHash, loadMeta, extract };
if (require.main === module) {
  const meta = loadMeta();
  console.log('meta counts bundle/bytes/raw', meta.nb, meta.ny, meta.nr);
  const out = process.argv[3] || __dirname;
  for (const name of process.argv[2].split(',')) {
    const r = extract(meta, name);
    if (!r.data) { console.log('MISS', name, JSON.stringify(r, (k, v) => typeof v === 'bigint' ? v.toString(16) : v)); continue; }
    fs.writeFileSync(path.join(out, name), r.data);
    console.log('OK', name, 'size', r.data.length, 'from', r.file, 'off', r.offset, 'loadType', r.e.loadType, 'dyn', r.e.dyn, 'variants', r.variants, 'head', r.data.subarray(0, 64).toString('hex'));
  }
}
