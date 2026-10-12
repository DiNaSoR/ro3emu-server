// Build index: view base name (lower) -> [tmpIndex] = {prop, refs, set}
const fs = require('fs'); const path = require('path');
const LUA = require('path').join(require('path').resolve(__dirname, '../..'), 're/clientB/lua-src');
const root = LUA + '/UI';
const views = {};
const ctrlFiles = [];
const strip = (s) => s.split('\r').join('');
function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p); else if (e.name.endsWith('.lua')) {
      if (/View\.lua$/.test(e.name) && /[\\/]Views[\\/]/.test(p)) {
        const t = strip(fs.readFileSync(p, 'latin1')).split('\n');
        const base = e.name.replace(/View\.lua$/, '');
        const idx = {};
        for (let i = 0; i < t.length; i++) {
          if (/A0_2\.GetTmpText$/.test(t[i].trim())) {
            const n = /L3_2 = (\d+)/.exec(t[i + 1] || ''); const pm = /A0_2\.(_\w+) = L1_2/.exec(t[i + 3] || '');
            if (n && pm) idx[+n[1]] = { prop: 'm_k' + pm[1].slice(1) };
          }
        }
        views[base.toLowerCase()] = { file: path.relative(LUA, p).replace(/\\/g, '/'), base, idx };
      } else ctrlFiles.push(p);
    }
  }
}
walk(root);
const byDir = {};
for (const f of ctrlFiles) { const d = path.normalize(path.dirname(path.dirname(f))); (byDir[d] = byDir[d] || []).push(f); }
const cache = new Map();
const read = (f) => { if (!cache.has(f)) cache.set(f, strip(fs.readFileSync(f, 'latin1'))); return cache.get(f); };
for (const v of Object.values(views)) {
  const dir = path.normalize(path.dirname(path.dirname(path.join(LUA, v.file))));
  const cands = byDir[dir] || [];
  for (const e of Object.values(v.idx)) {
    let refs = 0, set = 0; const needle = '.' + e.prop + '\n';
    for (const f of cands) {
      const t = read(f);
      let i = t.indexOf(needle);
      while (i >= 0) {
        refs++;
        const after = t.slice(i, i + 500);
        if (/\.SetText\b|\.text = |SetTextWithLanguage|SetLanguageText|Trans\b|SetTMPText|SetTxt/.test(after)) set++;
        i = t.indexOf(needle, i + 1);
      }
    }
    e.refs = refs; e.set = set;
  }
}
fs.writeFileSync(__dirname + '/viewindex.json', JSON.stringify(views));
console.log('views', Object.keys(views).length);
