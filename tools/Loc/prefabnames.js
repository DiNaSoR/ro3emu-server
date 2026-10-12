// collect candidate prefab names from Lua literals + C# decomp literals, keep those that exist as bundle assets
const fs = require('fs'); const path = require('path');
const B = require('./bundle.js');
const roots = ['re/clientB/lua-src', 're/decomp/Assembly-CSharp'].map(d => require('path').join(require('path').resolve(__dirname, '../..'), d));
const cand = new Set();
function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p);
    else if (/\.(lua|cs)$/.test(e.name) && !/^Localization_/.test(e.name)) {
      const t = fs.readFileSync(p, 'latin1');
      const re = /"([A-Za-z0-9_\-\/\.]{3,120})"/g; let m;
      while ((m = re.exec(t))) {
        let s = m[1];
        if (s.includes('/')) s = s.slice(s.lastIndexOf('/') + 1);
        s = s.replace(/\.prefab$/i, '');
        cand.add(s.toLowerCase());
      }
    }
  }
}
for (const r of roots) walk(r);
// also file basenames of Lua UI views/ctrls -> prefab names
const meta = B.meta();
const found = [];
for (const c of cand) {
  const h = B.V.lowerHash(c + '.prefab');
  if (meta.bundles.has(h)) found.push(c + '.prefab');
}
fs.writeFileSync(__dirname + '/prefabs.txt', found.sort().join('\n'));
console.log('candidates', cand.size, 'prefabs found', found.length);
