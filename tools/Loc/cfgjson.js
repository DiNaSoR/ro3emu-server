// CJK string fields in data/config/*.json and data/sceneeditor/*.json
const fs = require('fs'); const path = require('path');
const HAN = /[\u3400-\u9fff\uf900-\ufaff]/;
const out = [];
for (const dir of ['data/config', 'data/sceneeditor'].map(d => require('path').join(require('path').resolve(__dirname, '../..'), d))) {
  const stats = {};
  for (const f of fs.readdirSync(dir)) {
    if (!f.endsWith('.json') || /^Localization_/.test(f)) continue;
    let j; try { j = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')); } catch { continue; }
    const table = f.replace(/\.json$/, '');
    const walk = (v, p, rowId) => {
      if (typeof v === 'string') { if (HAN.test(v)) { const field = p.replace(/\[\d+\]/g, '[]'); const k = table + '.' + field; (stats[k] = stats[k] || { n: 0, u: new Set(), ex: [] }); stats[k].n++; stats[k].u.add(v); if (stats[k].ex.length < 3) stats[k].ex.push(v.slice(0, 24)); out.push({ dir: path.basename(dir), table, row: rowId, field: p, text: v }); } return; }
      if (Array.isArray(v)) v.forEach((x, i) => walk(x, p + '[' + i + ']', rowId));
      else if (v && typeof v === 'object') for (const k in v) walk(v[k], p ? p + '.' + k : k, rowId);
    };
    if (j && typeof j === 'object') for (const id in j) walk(j[id], '', id);
  }
  console.log('==', dir);
  for (const [k, v] of Object.entries(stats).sort((a, b) => b[1].n - a[1].n).slice(0, +process.argv[2] || 40)) console.log(v.n, v.u.size, k, '|', v.ex.join(' | '));
}
fs.writeFileSync(__dirname + '/cfg_cjk.json', JSON.stringify(out));
