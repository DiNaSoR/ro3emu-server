// Scan all prefabs (prefabs.txt) for serialized text: TMP m_text, UGUI m_Text, LangTransUtils_TMP sid, any CJK string field.
// node prefabscan.js [limit]
const fs = require('fs');
const B = require('./bundle.js');
const CJK = /[㐀-鿿豈-﫿　-〿＀-￯]/;
const names = fs.readFileSync(__dirname + '/prefabs.txt', 'utf8').split('\n').filter(Boolean);
const limit = +process.argv[2] || names.length;
const meta = B.meta();
const byBundle = new Map();
for (const n of names.slice(0, limit)) {
  const es = B.entriesFor(n); if (!es) continue;
  const e = es[0]; const k = e.fileHash.toString(16);
  if (!byBundle.has(k)) byBundle.set(k, { e, prefabs: [] });
  byBundle.get(k).prefabs.push(n);
}
console.error('bundles', byBundle.size);
const rows = []; const stats = { bundles: 0, errors: 0, tmp: 0, tmpCjk: 0, ltu: 0, ugui: 0, other: 0 };
const scriptNames = {};
function collectStrings(v, pfx, out) {
  if (typeof v === 'string') { if (CJK.test(v)) out.push([pfx, v]); return; }
  if (Array.isArray(v)) { v.forEach((x, i) => collectStrings(x, pfx + '[' + i + ']', out)); return; }
  if (v && typeof v === 'object' && !Buffer.isBuffer(v)) for (const k in v) collectStrings(v[k], pfx ? pfx + '.' + k : k, out);
}
let bi = 0;
for (const [k, { e, prefabs }] of byBundle) {
  bi++; if (bi % 200 === 0) console.error(bi, '/', byBundle.size, 'rows', rows.length);
  let bun;
  try { bun = B.openBundle(e); } catch (err) { stats.errors++; continue; }
  stats.bundles++;
  for (const f of bun.files) {
    if (!f.sf || !f.sf.typeTree) continue;
    const sf = f.sf;
    const goName = new Map(); const parent = new Map(); const tfOfGo = new Map(); const scr = new Map(); const ltu = new Map();
    const mbs = [];
    for (const o of sf.objects) {
      const c = o.type.classID;
      try {
        if (c === 1) { const v = B.U.readTree(sf, o); goName.set(o.pathID, v.m_Name); }
        else if (c === 4 || c === 224) { const v = B.U.readTree(sf, o); tfOfGo.set(v.m_GameObject.m_PathID, o.pathID); parent.set(o.pathID, v.m_Father.m_PathID); parent.set('go' + o.pathID, v.m_GameObject.m_PathID); }
        else if (c === 115) { const v = B.U.readTree(sf, o); scr.set(o.pathID, v.m_ClassName); }
        else if (c === 114) mbs.push(o);
      } catch (err) { stats.errors++; }
    }
    const goPath = (go) => {
      const parts = []; let t = tfOfGo.get(go); let guard = 0;
      while (t !== undefined && t !== 0n && guard++ < 64) { parts.unshift(goName.get(parent.get('go' + t)) ?? '?'); t = parent.get(t); }
      return parts.join('/');
    };
    const pending = []; const bound = new Map();
    for (const o of mbs) {
      const top = (o.type.nodes || []).filter(n => n.level === 1).map(n => n.name);
      const isTmp = top.includes('m_text') && top.includes('m_fontAsset');
      const isUgui = top.includes('m_Text') && top.includes('m_FontData');
      const isLtu = top.includes('sid') && top.includes('ctrlByProgram');
      let v; try { v = B.U.readTree(sf, o); } catch (err) { stats.errors++; continue; }
      const go = v.m_GameObject && v.m_GameObject.m_PathID;
      const sname = v.m_Script && v.m_Script.m_FileID === 0 ? scr.get(v.m_Script.m_PathID) : null;
      if (sname) scriptNames[sname] = (scriptNames[sname] || 0) + 1;
      if (top.includes('ChildTmpTextNodes')) { (v.ChildTmpTextNodes||[]).forEach((p, i) => { if (p.m_FileID === 0) bound.set(p.m_PathID, { binderGo: goPath(go), idx: i }); }); (v.ChildTmpInputNodes||[]).forEach((p,i)=>{ if (p.m_FileID===0) bound.set('in'+p.m_PathID,{binderGo:goPath(go),idx:i}); }); }
      if (isLtu) { stats.ltu++; ltu.set(go, { sid: v.sid, ctrl: v.ctrlByProgram, useSid: v.isUseSid }); continue; }
      if (isTmp || isUgui) {
        const text = isTmp ? v.m_text : v.m_Text;
        stats[isTmp ? 'tmp' : 'ugui']++;
        if (text && CJK.test(text)) stats.tmpCjk++;
        pending.push({ go, pid: o.pathID, kind: isTmp ? (sname || 'TMP') : (sname || 'Text'), text, field: isTmp ? 'm_text' : 'm_Text', autoSize: v.m_enableAutoSizing, fontSize: v.m_fontSize, rich: v.m_isRichText });
        continue;
      }
      const out = []; collectStrings(v, '', out);
      for (const [p, s] of out) { stats.other++; pending.push({ go, kind: sname || ('MB:' + top.slice(4, 8).join('|')), text: s, field: p }); }
    }
    for (const p of pending) {
      const l = ltu.get(p.go); const bd = p.pid !== undefined ? bound.get(p.pid) : null;
      rows.push({ prefabs: prefabs.length > 3 ? prefabs.slice(0, 3).concat(['+' + (prefabs.length - 3)]) : prefabs, bundle: k, cab: f.path, go: goPath(p.go), comp: p.kind, field: p.field, text: p.text, sid: l ? l.sid : null, ctrlByProgram: l ? l.ctrl : null, isUseSid: l ? l.useSid : null, binder: bd ? bd.binderGo : null, bindIdx: bd ? bd.idx : null, autoSize: p.autoSize, fontSize: p.fontSize });
    }
  }
}
fs.writeFileSync(__dirname + '/prefab_text.json', JSON.stringify(rows));
console.log(JSON.stringify(stats));
console.log(Object.entries(scriptNames).filter(([n]) => /Text|Lang|TMP|Label|Word/i.test(n)).map(x => x.join(':')).join(' '));
