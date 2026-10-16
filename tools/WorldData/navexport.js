// Developers only: RO3Emu.exe export does the same for any client folder (src/Ro3Emu.DataExport/NavExporter.cs).
// Extract the nav grid (*_dynamicmapdata.bytes) of every MapResource row from the RO3 VFS.
// usage: node navexport.js [outDir (default ./maps)]
// MapResource._kNavDataName "X data.bytes" is loaded by the client as "X_dynamicmapdata.bytes" (RoSpace.LoadSpaceDirect).
const fs = require('fs');
const path = require('path');
const { loadMeta, extract } = require('./vfsget.js');
const cfg = JSON.parse(fs.readFileSync(path.join(__dirname, '../../data/config/MapResource.json'), 'utf8'));
const rows = Object.values(cfg);
const outDir = path.resolve(process.argv[2] || path.join(__dirname, 'maps'));
fs.mkdirSync(outDir, { recursive: true });
const meta = loadMeta();
const names = new Set(rows.map(r => r._kNavDataName).filter(Boolean));
let ok = 0; const miss = [];
for (const n of names) {
  const dyn = n.includes('_dynamicmapdata.bytes') ? n : n.replace('data.bytes', '_dynamicmapdata.bytes');
  const r = extract(meta, dyn);
  if (r.data) { ok++; fs.writeFileSync(path.join(outDir, dyn), r.data); } else miss.push(n);
}
console.log('unique nav names', names.size, 'extracted', ok, 'missing', miss.length, miss.join(' '));
