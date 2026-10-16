// Developers only: RO3Emu.exe export reads the same table from the Lua bytecode (src/Ro3Emu.DataExport/MsgTableExporter.cs).
// Extracts the romsg message table (id, name, request type, response type) from the decompiled
// NW_Helper_PBDefine.lua / NW_Helper_EZPBDefine.lua / NW_AutoSyncClient_Define.lua into a TSV.
// usage: node msgtable.js <lua-src dir> <out.tsv>
const fs = require('fs');
const path = require('path');
const [, , luaDir, outFile] = process.argv;
const files = ['NW_Helper_PBDefine.lua', 'NW_Helper_EZPBDefine.lua', 'NW_AutoSyncClient_Define.lua'];
const rows = new Map();
for (const f of files) {
  const p = path.join(luaDir, 'Network', f);
  if (!fs.existsSync(p)) continue;
  // unluac emits either `T.field = <literal>` or `Lx = <literal>` followed by `T.field = Lx`.
  let cur = {};
  const regs = {};
  const value = v => (v in regs ? regs[v] : v.replace(/^"|"$/g, ''));
  for (const line of fs.readFileSync(p, 'utf8').split(/\r?\n/)) {
    let m;
    if ((m = line.match(/^\s*(L\d+_\d+) = ("[^"]*"|\d+)\s*$/))) { regs[m[1]] = m[2].replace(/^"|"$/g, ''); continue; }
    if (!(m = line.match(/\.(msg_name|msg_id|req_name|res_name) = (\S+)\s*$/))) continue;
    const v = value(m[2]);
    if (m[1] === 'msg_name') cur = { name: v };
    else if (m[1] === 'msg_id') cur.id = +v;
    else if (m[1] === 'req_name') cur.req = v;
    else cur.res = v;
    if (cur.id !== undefined && cur.name && cur.res !== undefined) rows.set(cur.id, cur);
  }
}
const sorted = [...rows.values()].sort((a, b) => a.id - b.id);
fs.writeFileSync(outFile, sorted.map(r => [r.id, r.name, r.req || '', r.res || ''].join('\t')).join('\n') + '\n');
console.log(`${sorted.length} messages -> ${outFile}`);
