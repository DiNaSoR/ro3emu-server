// usage: node navquery.js <*_dynamicmapdata.bytes> x z  -> walkable flag and height(s) of grid cell floor(x), floor(z)
const fs = require('fs');
const [file, xs, zs] = process.argv.slice(2);
const b = fs.readFileSync(file);
const W = b.readInt32LE(20), H = b.readInt32LE(24), BS = b.readInt32LE(28), BC = b.readInt32LE(44);
const bx = Math.ceil(W / BS);
const x = Math.floor(+xs), z = Math.floor(+zs);
const bi = Math.floor(z / BS) * bx + Math.floor(x / BS);
const o = 48 + bi * 16; const nPos = b.readInt32LE(o), nCnt = b.readInt32LE(o + 4), oPos = b.readInt32LE(o + 8), oCnt = b.readInt32LE(o + 12);
const j = (z % BS) * BS + (x % BS); const c = nPos + j * 4;
const f0 = b[c], f1 = b[c + 1], hgt = b.readInt16LE(c + 2) * 0.01;
const res = { W, H, cell: [x, z], block: bi, walkable: !!(f0 & 0x80), gridLevel: (f0 & 0x78) >> 3, corner: f0 & 7, isNormal: !!(f1 & 0x80), heightCount: (f1 & 0x80) ? f1 & 0x7f : 0, height: hgt, extra: [] };
for (let k = 0; k < oCnt; k++) { const p = oPos + k * 8; if (b.readUInt16LE(p + 4) === x && b.readUInt16LE(p + 6) === z) res.extra.push({ walk: !!(b[p] & 0x80), h: b.readInt16LE(p + 2) * 0.01 }); }
console.log(JSON.stringify(res));
