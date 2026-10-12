// Splits the strings that still need English into translation batches.
// usage: node tools/Loc/make_batches.js <out dir> [--size 220] [--tier core|placeholder|misses] [--misses <loc_misses.tsv>]
//   core:        visible yes / likely / blank-sid / fallback rows (prefab, Lua code, confirmed config)
//   placeholder: prefab placeholders only
//   misses:      strings from a runtime miss log that the map does not cover yet
// Each batch is a TSV: text, visible, zh_len, example_location. Strings already in data/loc/loc_en.tsv are skipped.
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..', '..');
const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : def; };
const outDir = args[0];
const size = +opt('--size', 220);
const tier = opt('--tier', 'core');
const unescape = s => s.replace(/\\(.)/g, (_, c) => (c === 't' ? '\t' : c === 'n' ? '\n' : c === 'r' ? '\r' : c));
const escape = s => s.replace(/\\/g, '\\\\').replace(/\t/g, '\\t').replace(/\r/g, '\\r').replace(/\n/g, '\\n');
const cjk = /[぀-鿿⺀-⿟豈-﫿]/;

const known = new Set();
for (const l of fs.readFileSync(path.join(root, 'data', 'loc', 'loc_en.tsv'), 'utf8').split('\n')) {
  if (!l || l.startsWith('#')) continue;
  known.add(unescape(l.slice(0, l.indexOf('\t'))));
}

let rows = [];
if (tier === 'misses') {
  for (const l of fs.readFileSync(opt('--misses'), 'utf8').replace(/^﻿/, '').split('\n')) {
    const text = unescape(l.split('\t')[0] || '');
    if (text && cjk.test(text) && !known.has(text)) rows.push({ text, visible: 'runtime', zh_len: String(text.length), example_location: l.split('\t')[1] || '' });
  }
} else {
  const lines = fs.readFileSync(path.join(root, 're', 'loc', 'translation_queue.tsv'), 'utf8').replace(/^﻿/, '').split('\n');
  const header = lines.shift().split('\t');
  for (const l of lines) {
    if (!l.trim()) continue;
    const c = l.replace(/\r$/, '').split('\t');
    const r = Object.fromEntries(header.map((h, i) => [h, unescape(c[i] ?? '')]));
    if (r.existing_en || known.has(r.text)) continue;
    const v = r.visible.split(',');
    if (v.every(x => x.startsWith('yes (one event)') || x === 'unverified')) continue; // random names / unverified wait for the miss log
    const core = v.some(x => x === 'yes' || x === 'likely' || x === 'fallback' || x.startsWith('blank'));
    if ((tier === 'core') !== core) continue;
    rows.push(r);
  }
}
fs.mkdirSync(outDir, { recursive: true });
let n = 0;
for (let i = 0; i < rows.length; i += size) {
  const part = rows.slice(i, i + size);
  const file = path.join(outDir, `${tier}-${String(++n).padStart(2, '0')}.tsv`);
  fs.writeFileSync(file, 'text\tvisible\tzh_len\texample_location\n' +
    part.map(r => [r.text, r.visible, r.zh_len, r.example_location].map(x => escape(x || '')).join('\t')).join('\n') + '\n', 'utf8');
}
console.log(`${rows.length} strings -> ${n} batches in ${outDir}`);
