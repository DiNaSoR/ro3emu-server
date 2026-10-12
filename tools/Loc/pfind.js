const fs = require('fs');
const rows = JSON.parse(fs.readFileSync(__dirname + '/prefab_text.json', 'utf8'));
const needle = process.argv[2];
let n = 0;
for (const r of rows) if (r.text && r.text.includes(needle) && (r.field === 'm_text' || process.argv[3])) { if (n++ < 20) console.log(r.prefabs.join(','), '|', r.go, '|', r.comp, JSON.stringify(r.text).slice(0, 60), 'sid', r.sid, 'ctrl', r.ctrlByProgram); }
console.log('hits', n);
