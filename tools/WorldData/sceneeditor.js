// Dumps world data from the client's scene-editor tables (Config/SceneEditor/<MapConfig._kResSceneEditorName>.lua).
// Export them to JSON first:
//   tools/ConfigExport/bin/Release/net10.0/ConfigExport.exe re/clientB/luac <seDir> Config\SceneEditor
// (add `lua.Encoding = Encoding.UTF8;` before `lua.OpenLibs()` in ConfigExport if you want the Chinese names).
// usage: node sceneeditor.js <seDir> maps                 born / resurrection point and counts per MapConfig row
//        node sceneeditor.js <seDir> npcs <mapId>         kNpcList of one map (npc / portal / kafra) as a markdown table
//        node sceneeditor.js <seDir> portals [mapId,...]  portal (NpcTag 26) table with computed arrival points
//
const fs = require('fs');
const path = require('path');
const C = path.join(__dirname, '../../data/config/');
const [seDir, cmd, arg] = process.argv.slice(2);
const M = require(C + 'MapConfig.json');
const N = require(C + 'NPCConfig.json');
const R = require(C + 'MapResource.json');
const L = require(C + 'Localization_en.json');
const loc = {}; for (const r of (Array.isArray(L) ? L : Object.values(L))) loc[r._iID] = r._kDes;
const f2 = x => Math.round(x * 100) / 100;
const se = id => { const p = path.join(seDir, M[id]._kResSceneEditorName + '.json'); return fs.existsSync(p) ? JSON.parse(fs.readFileSync(p)) : null; };
const npcName = id => { const c = N[id] || {}; const n = (c._kName && loc[c._kName[0]]) || '?'; const t = c._kTitle && c._kTitle[0] && loc[c._kTitle[0]]; return t ? `${n} / ${t}` : n; };

if (cmd === 'maps') {
  for (const id of Object.keys(M).map(Number).sort((a, b) => a - b)) {
    const m = M[id]; const j = m._kResSceneEditorName ? se(id) : null;
    let born = '-', res = '-', np = 0, srv = 0, tp = 0, kp = 0;
    if (j) {
      const b = (j.m_kBornPoints || []).find(x => x.isDefault) || (j.m_kBornPoints || [])[0];
      if (b) born = `(${f2(b.position.x)}, ${f2(b.position.y)}, ${f2(b.position.z)}) face ${b.direction}`;
      const r = (j.m_kResurrectionPointList || [])[0];
      if (r) res = `(${f2(r.position.x)}, ${f2(r.position.y)}, ${f2(r.position.z)})`;
      for (const n of Object.values(j.kNpcList || {})) { if (n.NpcTag === 2) { np++; if (n.IsServerNpc) srv++; } if (n.NpcTag === 26) tp++; if (n.NpcTag === 28) kp++; }
    }
    console.log(`| ${id} | ${loc[m._iName[0]] || ''} | ${(R[m._iMapResourceID] || {})._kResource} | ${m._iSceneType} | ${born} | ${res} | ${np}/${srv} | ${tp} | ${kp} |`);
  }
} else if (cmd === 'npcs') {
  const j = se(+arg);
  for (const [k, n] of Object.entries(j.kNpcList || {})) {
    const c = N[n.NpcId] || {}; const tag = { 2: 'npc', 26: 'portal', 28: 'kafra' }[n.NpcTag] || n.NpcTag;
    console.log(`| ${k} | ${tag}${n.NpcTag === 2 && !n.IsServerNpc ? ' (client)' : ''} | ${n.NpcId} | ${npcName(n.NpcId)} | ${f2(n.x)}, ${f2(n.y)}, ${f2(n.z)} | ${n.FaceDirection ?? '-'} | ${JSON.stringify(c._iInteractID || [])} |`);
  }
} else if (cmd === 'portals') {
  const want = arg ? arg.split(',').map(Number) : null; const tp = {}; const byMap = {};
  for (const id of Object.keys(M)) {
    const j = M[id]._kResSceneEditorName && se(id); if (!j) continue;
    for (const n of Object.values(j.kNpcList || {})) if (n.NpcTag === 26) { tp[`${id}:${n.TravelPointID}`] = n; (byMap[id] ||= []).push(n); }
  }
  for (const id of Object.keys(byMap).map(Number).sort((a, b) => a - b)) {
    if (want && !want.includes(id)) continue;
    for (const n of byMap[id]) {
      const t = (n.TravelTargetInfoArray || [])[0]; const d = t && tp[`${t.MapID}:${t.TravelPointID}`];
      const arr = d ? `${f2(d.x + d.FootholdPos_x)}, ${f2(d.y + d.FootholdPos_y)}, ${f2(d.z + d.FootholdPos_z)} face ${d.FootholdPosRot}` : 'MISSING';
      const trig = n.TriggerType === 1 ? `r=${n.TravelPointRadius}` : `box ${n.TravelPointLengthX}x${n.TravelPointLengthZ}x${n.TravelPointHeight}`;
      console.log(`| ${id} | ${n.TravelPointID} | ${n.NpcId} ${npcName(n.NpcId)} | ${f2(n.x)}, ${f2(n.y)}, ${f2(n.z)} | ${trig} | ${t ? `${t.MapID} / TP ${t.TravelPointID}` : '-'} | ${arr} |`);
    }
  }
} else {
  console.error('usage: node sceneeditor.js <seDir> maps | npcs <mapId> | portals [mapId,...]');
  process.exit(1);
}
