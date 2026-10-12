// classify CJK TMP texts in prefabs and write inventory TSV/JSON
const fs = require('fs');
const L = require('./locindex.js').load();
const views = JSON.parse(fs.readFileSync(__dirname + '/viewindex.json', 'utf8'));
const rows = JSON.parse(fs.readFileSync(__dirname + '/prefab_text.json', 'utf8'));
const HAN = /[㐀-鿿豈-﫿]/;
const out = []; const stat = {};
const uniq = {};
for (const r of rows) {
  if (r.field !== 'm_text' && !(r.comp === 'TMP_InputField' && r.field === 'm_Text')) continue;
  if (!r.text || !HAN.test(r.text)) continue;
  let cls, viewProp = null;
  const sidOk = r.sid && r.sid !== '' ;
  if (sidOk && !r.ctrlByProgram) cls = L.en[r.sid] ? 'auto-sid' : 'auto-sid-missing';
  else if (r.binder == null) cls = 'unbound';
  else {
    const bname = r.binder.split('/').pop().toLowerCase();
    const v = views[bname] || views[r.prefabs[0].replace(/\.prefab$/, '')];
    const e = v && v.idx[r.bindIdx];
    if (!e) cls = 'bound-noview';
    else { viewProp = v.base + '.' + e.prop; cls = e.set > 0 ? 'bound-set' : e.refs > 0 ? 'bound-ref' : 'bound-unused'; }
  }
  if (sidOk && r.ctrlByProgram) cls += '+sid';
  stat[cls] = (stat[cls] || 0) + 1;
  (uniq[cls] = uniq[cls] || new Set()).add(r.text);
  const tr = sidOk && L.en[r.sid] ? { key: r.sid, en: L.en[r.sid]._kDes, how: 'sid' } : L.lookup(r.text);
  out.push({ key: r.prefabs[0].replace(/\.prefab$/, '') + ':' + r.go, prefab: r.prefabs.join(','), bundle: r.bundle, go: r.go, cls, sid: r.sid, ctrlByProgram: r.ctrlByProgram, view: viewProp, text: r.text, locKey: tr ? tr.key : null, en: tr ? tr.en : null, match: tr ? tr.how : null, autoSize: r.autoSize, fontSize: r.fontSize });
}
console.log(stat);
for (const k in uniq) console.log(k, 'uniq', uniq[k].size);
const work = out.filter(o => !o.cls.startsWith('auto-sid') || o.cls === 'auto-sid-missing');
const wu = new Set(work.map(o => o.text)); const wm = new Set(work.filter(o => o.en).map(o => o.text));
console.log('work rows', work.length, 'unique texts', wu.size, 'with existing en via loc table', wm.size);
const byMatch = {}; for (const o of work) byMatch[o.match] = (byMatch[o.match] || 0) + 1; console.log(byMatch);
fs.writeFileSync(__dirname + '/prefab_inventory.json', JSON.stringify(out));
