// Scan the #US (user string) heap of every managed DLL in a directory; output CJK literals.
// usage: node us_scan.js <dir-or-file> [more...] > out.tsv
const fs = require('fs');
const path = require('path');

const CJK = /[　-〿㐀-鿿豈-﫿＀-￯]/;

function readUS(file) {
  const b = fs.readFileSync(file);
  if (b.readUInt16LE(0) !== 0x5a4d) return null;
  const pe = b.readUInt32LE(0x3c);
  if (b.readUInt32LE(pe) !== 0x4550) return null;
  const coff = pe + 4;
  const nsec = b.readUInt16LE(coff + 2);
  const optSize = b.readUInt16LE(coff + 16);
  const opt = coff + 20;
  const magic = b.readUInt16LE(opt);
  const ddOff = opt + (magic === 0x20b ? 112 : 96);
  const cliRva = b.readUInt32LE(ddOff + 14 * 8);
  if (!cliRva) return null;
  const secs = [];
  const sh = opt + optSize;
  for (let i = 0; i < nsec; i++) {
    const o = sh + i * 40;
    secs.push({ va: b.readUInt32LE(o + 12), vs: b.readUInt32LE(o + 8), raw: b.readUInt32LE(o + 20), rs: b.readUInt32LE(o + 16) });
  }
  const rva2off = (r) => {
    for (const s of secs) if (r >= s.va && r < s.va + Math.max(s.vs, s.rs)) return r - s.va + s.raw;
    return -1;
  };
  const cli = rva2off(cliRva);
  const mdRva = b.readUInt32LE(cli + 8);
  const md = rva2off(mdRva);
  if (b.readUInt32LE(md) !== 0x424a5342) return null;
  const verLen = b.readUInt32LE(md + 12);
  let p = md + 16 + verLen;
  const nStreams = b.readUInt16LE(p + 2);
  p += 4;
  let us = null;
  for (let i = 0; i < nStreams; i++) {
    const off = b.readUInt32LE(p), size = b.readUInt32LE(p + 4);
    p += 8;
    let name = '';
    while (b[p] !== 0) name += String.fromCharCode(b[p++]);
    p++;
    p = (p + 3) & ~3;
    if (name === '#US') us = { off: md + off, size };
  }
  if (!us) return [];
  const out = [];
  let q = us.off + 1;
  const end = us.off + us.size;
  while (q < end) {
    let len, hdr;
    const c = b[q];
    if ((c & 0x80) === 0) { len = c; hdr = 1; }
    else if ((c & 0xc0) === 0x80) { len = ((c & 0x3f) << 8) | b[q + 1]; hdr = 2; }
    else { len = ((c & 0x1f) << 24) | (b[q + 1] << 16) | (b[q + 2] << 8) | b[q + 3]; hdr = 4; }
    if (len === 0) { q += hdr; continue; }
    const s = b.toString('utf16le', q + hdr, q + hdr + (len - 1));
    out.push({ token: (0x70000000 | (q - us.off)) >>> 0, s });
    q += hdr + len;
  }
  return out;
}

const files = [];
for (const a of process.argv.slice(2)) {
  const st = fs.statSync(a);
  if (st.isDirectory()) for (const f of fs.readdirSync(a)) { if (/\.dll$/i.test(f)) files.push(path.join(a, f)); }
  else files.push(a);
}
const esc = (s) => s.replace(/\\/g, '\\\\').replace(/\t/g, '\\t').replace(/\r/g, '\\r').replace(/\n/g, '\\n');
const summary = [];
for (const f of files) {
  let list;
  try { list = readUS(f); } catch (e) { continue; }
  if (!list) continue;
  let n = 0;
  for (const { token, s } of list) {
    if (CJK.test(s)) { n++; process.stdout.write(path.basename(f) + '\t' + token.toString(16) + '\t' + esc(s) + '\n'); }
  }
  summary.push([path.basename(f), list.length, n]);
}
summary.sort((a, b) => b[2] - a[2]);
for (const r of summary) if (r[2]) process.stderr.write(r.join('\t') + '\n');
process.stderr.write('dlls scanned: ' + summary.length + '\n');
