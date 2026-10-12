// reverse index: Chinese text -> [{key, en}] from Localization_zh_CN / zh_TW + en
const fs = require('fs');
// Directory with all 8 Localization_<lang>.json exported with UTF-8 (ConfigExport + lua.Encoding = UTF8).
const D = process.env.RO3_LOCEXP || require('path').join(require('path').resolve(__dirname, '../..'), 'data/locexp');
let cached = null;
function load() {
  if (cached) return cached;
  const en = JSON.parse(fs.readFileSync(D + '/Localization_en.json', 'utf8'));
  const zh = JSON.parse(fs.readFileSync(D + '/Localization_zh_CN.json', 'utf8'));
  const tw = JSON.parse(fs.readFileSync(D + '/Localization_zh_TW.json', 'utf8'));
  const rev = new Map(); const norm = (s) => s.replace(/\s+/g, '').replace(/<[^>]+>/g, '');
  const revN = new Map();
  const add = (m, k, key) => { if (!m.has(k)) m.set(k, []); const a = m.get(k); if (a.length < 5 && !a.includes(key)) a.push(key); };
  for (const t of [zh, tw]) for (const k in t) { const d = t[k]._kDes; if (typeof d !== 'string' || !d) continue; add(rev, d, k); add(revN, norm(d), k); }
  const lookup = (text) => {
    let keys = rev.get(text); let how = 'exact';
    if (!keys) { keys = revN.get(norm(text)); how = 'normalized'; }
    if (!keys) return null;
    const k = keys[0];
    return { key: k, en: en[k] ? en[k]._kDes : null, how, alt: keys.slice(1) };
  };
  cached = { en, zh, tw, lookup };
  return cached;
}
module.exports = { load };
