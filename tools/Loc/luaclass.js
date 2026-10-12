// classify Lua CJK literals: log / editor-gm / config / code-candidate
const fs = require('fs');
const rows = JSON.parse(fs.readFileSync(__dirname + '/lua_cjk.json', 'utf8'));
const HAN = /[㐀-鿿豈-﫿]/;
const LOGCALL = /(^|\.)(Log\w*|LogError\w*|LogWarning\w*|DebugLog|print|error|assert|Assert|Lua_LogWarning|write|LogFormat|LogErrorFormat|ReleaseLogError)$/;
const EDITOR = /^(Editors\/|GMGlobalMock|Config\/CFG\/Lua_CFG_GMConfig|Config\/DataConfig\/GMConfig|Config\/DataConfig\/GMCombinationConfig|UI\/GM\/|Logic\/GM\/|LuaDebug|MonitorHookSetup|Profiler\/|ConfigOptimizer\/|Diagnostics\/|Performance\/|Config\/Lua_BattleCurvePoolsKey_Gen|BehaviorTree\/)/;
function classify(r) {
  if (!HAN.test(r.text)) return 'symbols-only';
  if (EDITOR.test(r.file)) return 'editor-gm-debug';
  if (r.callee && LOGCALL.test(r.callee)) return 'log';
  if (r.file.startsWith('Config/SceneEditor/') || r.file.startsWith('Config/Map/')) return 'config-sceneeditor';
  if (r.file.startsWith('Config/')) return 'config';
  // string.format feeding into log? look at use line
  if (r.callee === 'string.format' || r.callee === 'string.Format') return 'code-format';
  return 'code';
}
const out = {}; const lists = {};
for (const r of rows) { const c = classify(r); r.cls = c; out[c] = (out[c] || 0) + 1; (lists[c] = lists[c] || new Set()).add(r.text); }
console.log(out); for (const k in lists) console.log(k, 'uniq', lists[k].size);
fs.writeFileSync(__dirname + '/lua_cjk_cls.json', JSON.stringify(rows));
if (process.argv[2]) for (const r of rows.filter(r => r.cls === process.argv[2])) console.log(r.file + ':' + r.line, '|', (r.callee || r.sink || '').slice(0, 50), '|', JSON.stringify(r.text).slice(0, 70));
