// Parse an RO3 *_dynamicmapdata.bytes nav grid (mirror of RoSpace.LoadSpaceDirect); prints stats and writes <file>.ascii.txt. usage: node navparse.js <file> [step]
const fs = require('fs');
const path = require('path');
const file = process.argv[2];
const b = fs.readFileSync(file);
const H = ['headSize', 'zDA', 'centerX', 'centerY', 'baseHeight', 'width', 'height', 'blockSize', 'maxOtherHeightCount', 'blockMaxSize', 'eeA', 'blockCount'];
const h = {}; H.forEach((k, i) => h[k] = b.readInt32LE(i * 4));
const bx = Math.ceil(h.width / h.blockSize), by = Math.ceil(h.height / h.blockSize);
console.log(JSON.stringify(h), 'blocksX', bx, 'blocksY', by, 'fileSize', b.length);
const blocks = [];
for (let i = 0; i < h.blockCount; i++) { const o = 48 + i * 16; blocks.push({ normalPos: b.readInt32LE(o), normalCount: b.readInt32LE(o + 4), otherPos: b.readInt32LE(o + 8), otherCount: b.readInt32LE(o + 12) }); }
console.log('block0', JSON.stringify(blocks[0]), 'blockLast', JSON.stringify(blocks[blocks.length - 1]));
const W = h.width, Ht = h.height;
const layers = Array.from({ length: W * Ht }, () => []);
const flagsHist = {}; let p = h.headSize; let mismatch = 0; let oob = 0; let otherTotal = 0;
for (let i = 0; i < h.blockCount; i++) {
  const blk = blocks[i];
  if (blk.normalCount && blk.normalPos !== p) mismatch++;
  const ox = (i % bx) * h.blockSize, oy = Math.floor(i / bx) * h.blockSize;
  for (let j = 0; j < blk.normalCount; j++) {
    const o = p + j * 4; const f0 = b[o], f1 = b[o + 1], y = b.readInt16LE(o + 2);
    const walk = (f0 & 0x80) !== 0, isNormal = (f1 & 0x80) !== 0, cnt = isNormal ? (f1 & 0x7f) : 0;
    const key = (walk ? 'W' : '-') + (isNormal ? 'N' : 'o') + cnt + ' lvl' + ((f0 & 0x78) >> 3) + ' corner' + (f0 & 7);
    flagsHist[key] = (flagsHist[key] || 0) + 1;
    if (cnt > 0) {
      const x = ox + (j % h.blockSize), yy = oy + Math.floor(j / h.blockSize);
      if (x >= W || yy >= Ht) { oob++; continue; }
      layers[yy * W + x].push({ h: y * 0.01, walk });
    }
  }
  p += blk.normalCount * 4;
  for (let k = 0; k < blk.otherCount; k++) {
    const o = p + k * 8; const f0 = b[o]; const y = b.readInt16LE(o + 2); const x = b.readUInt16LE(o + 4), yy = b.readUInt16LE(o + 6);
    otherTotal++;
    if (x < W && yy < Ht) layers[yy * W + x].push({ h: y * 0.01, walk: (f0 & 0x80) !== 0, other: true });
  }
  p += blk.otherCount * 8;
}
console.log('endPtr', p, 'fileSize', b.length, 'normalPosMismatch', mismatch, 'oob', oob, 'otherEntries', otherTotal);
console.log('flag histogram', JSON.stringify(flagsHist));
let walk = 0, minH = 1e9, maxH = -1e9, multi = 0;
for (const L of layers) { if (L.some(l => l.walk)) walk++; if (L.length > 1) multi++; for (const l of L) { if (l.walk) { minH = Math.min(minH, l.h); maxH = Math.max(maxH, l.h); } } }
console.log('walkable cells', walk, 'of', W * Ht, 'multiLayer', multi, 'walk height range', minH.toFixed(2), maxH.toFixed(2));
// ASCII map, downsampled 4x (row 0 = top = max y)
const step = +(process.argv[3] || 4); const rows = [];
for (let y = Ht - 1; y >= 0; y -= step) { let s = ''; for (let x = 0; x < W; x += step) { const L = layers[y * W + x]; s += L.some(l => l.walk) ? '.' : (L.length ? 'x' : '#'); } rows.push(s); }
fs.writeFileSync(file + '.ascii.txt', rows.join('\n'));
console.log('ascii written', file + '.ascii.txt');
