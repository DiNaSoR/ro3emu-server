// node find.js <needle> : search lua_cjk.json, data/config/*.json, data/sceneeditor/*.json, loc tables
const fs = require('fs'); const path = require('path');
const needle = process.argv[2];
const rows = JSON.parse(fs.readFileSync(__dirname + '/lua_cjk.json', 'utf8'));
let n = 0;
for (const r of rows) if (r.text.includes(needle)) { if (n++ < 15) console.log('LUA', r.file + ':' + r.line, JSON.stringify(r.text).slice(0, 100), '|', r.callee || r.sink); }
console.log('lua hits', n);
for (const dir of ['data/config', 'data/sceneeditor'].map(d => require('path').join(require('path').resolve(__dirname, '../..'), d))) {
  for (const f of fs.readdirSync(dir)) {
    if (!f.endsWith('.json')) continue;
    if (/^Localization_/.test(f) && !/zh_CN|en/.test(f)) continue;
    const t = fs.readFileSync(path.join(dir, f), 'utf8');
    let i = t.indexOf(needle), c = 0;
    while (i >= 0 && c < 3) { console.log('CFG', f, t.slice(Math.max(0, i - 120), i + 60).replace(/\n/g, ' ')); c++; i = t.indexOf(needle, i + 1); }
  }
}
