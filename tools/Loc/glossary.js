// Official RO3 terminology: Localization_zh_CN / Localization_en pairs whose Chinese contains a term.
// usage: node tools/Loc/glossary.js <zh term> [more terms...] [--max 20]
// Reads data/locexp/Localization_{zh_CN,en}.json (or $RO3_LOCEXP). Shortest Chinese first, so exact terms lead.
const path = require('path');
const dir = process.env.RO3_LOCEXP || path.resolve(__dirname, '..', '..', 'data', 'locexp');
const zh = require(path.join(dir, 'Localization_zh_CN.json'));
const en = require(path.join(dir, 'Localization_en.json'));
const args = process.argv.slice(2);
let max = 15;
const mi = args.indexOf('--max');
if (mi >= 0) { max = +args[mi + 1]; args.splice(mi, 2); }
for (const term of args) {
  const hits = [];
  for (const [k, v] of Object.entries(zh)) {
    const z = v && v._kDes;
    if (typeof z !== 'string' || !z.includes(term)) continue;
    const e = en[k] && en[k]._kDes;
    if (!e || e === 'None') continue;
    hits.push([z, e]);
  }
  hits.sort((a, b) => a[0].length - b[0].length);
  console.log(`== ${term} (${hits.length})`);
  for (const [z, e] of hits.slice(0, max)) console.log(`${z.replace(/\n/g, '\\n')}\t${e.replace(/\n/g, '\\n')}`);
}
