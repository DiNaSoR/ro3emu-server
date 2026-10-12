// Build re/loc/*.tsv inventories from the scan outputs. Pipeline (node, run in this dir):
//   node luascan.js ../../re/clientB/lua-src lua_cjk.json && node luaclass.js
//   node prefabnames.js && node --max-old-space-size=8192 prefabscan.js && node viewindex.js
//   node --max-old-space-size=8192 prefabclass.js
//   node us_scan.js $RO3_CLIENT_DIR/ro3_Data/Managed > us_managed.tsv
//   node --max-old-space-size=8192 build_inventory.js && node launcher.js
// Intermediates (lua_cjk*.json, prefabs.txt, prefab_text.json, viewindex.json, prefab_inventory.json) land next to the scripts.
const fs = require('fs'); const path = require('path');
const OUT = require('path').join(require('path').resolve(__dirname, '../..'), 're/loc');
fs.mkdirSync(OUT, { recursive: true });
const L = require('./locindex.js').load();
const HAN = /[\u3400-\u9fff\uf900-\ufaff]/;
const esc = (s) => s == null ? '' : String(s).replace(/\\/g, '\\\\').replace(/\t/g, '\\t').replace(/\r/g, '\\r').replace(/\n/g, '\\n');
function writeTsv(name, header, rows) {
  const lines = [header.join('\t')].concat(rows.map(r => header.map(h => esc(r[h])).join('\t')));
  fs.writeFileSync(path.join(OUT, name), '\ufeff' + lines.join('\n') + '\n');
  console.log(name, rows.length);
}
const tr = (text) => L.lookup(text) || {};
const counts = {};
const queue = new Map(); // text -> {cats:Set, n, en, key, where}
function enqueue(text, cat, where, en, key, vis) {
  if (!HAN.test(text)) return;
  if (!queue.has(text)) queue.set(text, { text, cats: new Set(), n: 0, en: null, key: null, where, vis: new Set() });
  const q = queue.get(text); q.cats.add(cat); q.n++; q.vis.add(vis);
  if (!q.en && en) { q.en = en; q.key = key; }
}

// ---------- 1. prefab TMP text ----------
const pinv = JSON.parse(fs.readFileSync(__dirname + '/prefab_inventory.json', 'utf8'));
const VIS = { 'unbound': 'yes', 'unbound+sid': 'yes', 'bound-unused': 'likely', 'bound-unused+sid': 'likely', 'bound-ref': 'likely', 'bound-ref+sid': 'likely', 'bound-set': 'placeholder', 'bound-set+sid': 'placeholder', 'bound-noview': 'placeholder', 'bound-noview+sid': 'placeholder', 'auto-sid': 'no (auto-translated by sid)', 'auto-sid-missing': 'blank (sid not in any table)' };
const prow = pinv.map(o => ({
  key: o.key, category: 'prefab', visible: VIS[o.cls] || o.cls, class: o.cls, prefab: o.prefab, bundle_file_hash: o.bundle, go_path: o.go,
  text: o.text, sid: o.sid, ctrlByProgram: o.ctrlByProgram, lua_view_prop: o.view, existing_key: o.locKey, existing_en: o.en, match: o.match,
  zh_len: [...o.text].length, autosize: o.autoSize, font_size: o.fontSize,
}));
writeTsv('prefab_text.tsv', ['key', 'visible', 'class', 'prefab', 'go_path', 'text', 'sid', 'ctrlByProgram', 'lua_view_prop', 'existing_key', 'existing_en', 'match', 'zh_len', 'autosize', 'font_size', 'bundle_file_hash'], prow);
for (const r of prow) if (!r.class.startsWith('auto-sid') || r.class === 'auto-sid-missing') enqueue(r.text, 'prefab', r.key, r.existing_en, r.existing_key, r.visible);
counts.prefab = prow.length;

// ---------- 2/3. Lua literals ----------
const lua = JSON.parse(fs.readFileSync(__dirname + '/lua_cjk_cls.json', 'utf8'));
const CODE_HIDE = /(_Test\.lua|Mock|LatencyTest|LuaDebuger|^Camera\/|LC_BuffDisplay|RD_CameraState|LC_AdditiveSceneManager|LC_MapPathHelper|LC_Scene_Sync|LC_Movement_Smooth_Local|^HotFix\/|LC_DAL_GuildLeague|LC_DAL_AdventureGroup|LC_ModelLoader|LC_BattlePassHelper|UI_Sys_MainWidgetBaseCtrl|UI_Sys_AppearanceShopWidgetCtrl|UI_Sys_BuffIcon_BaseWidgetCtrl|Lua_UIObjectPool|Lua_UIWindowBase|Lua_TMPTextNew|LC_SettingHelper|LC_SeasonHelper|LC_Event\.lua|LC_UIModelManager|LC_SceneBase|LC_TransitionBase|LC_StoryManager|LC_LinkUtil|LC_Territory_SandBoxManager|LC_Display_Agent_ChickenBox|LC_MiniGameHelper|LC_QualityCtrl\.lua:1264|LC_BaseDAL|LC_System_TipNew|LC_SeasonEveHelper|LC_ActivityHelper\.lua|Lua_PbProcessor|debugEx|GameLogic\.lua|UI_Com_Tips_Normal_BoxPreviewWidgetCtrl|Dungeon_GroupWidgetCtrl|DungeonTeamPanelModel|Dungeon_GroupPanelModel|Season_ActivePanelModel|Season_PassLevelBuyWidgetCtrl\.lua:592|WardrobePanelCtrl|TerritoryWars_BasicsMap|JoyStickSkillItemWidgetCtrl)/;
const CODE_UNSURE = /(LC_ConditionSystem|LC_InteractCondition|LC_BuffConfigHelper|LC_QualityCtrl)/;
const codeRows = [];
const cfgRows = [];
const excluded = {};
const CFG_VIS = [
  [/Config\/ComplexConfig\/GVGConfig/, 'yes', 'siege (GVG) panel texts, read by UI/GVG controllers'],
  [/Config\/Dungeon\//, 'likely', 'dungeon phase names / scoreboard columns'],
  [/Config\/Define\/ClientConfig/, 'likely', 'camera-mode descriptions in settings'],
  [/Config\/ComplexConfig\/PublicParamConfig/, 'unverified', 'enum names (weapon type, element, quality); no direct Lua reader found'],
  [/MultiverseConfig\.lua/, 'yes', 'season names on server select (UI_Sys_Login_ChoiceServer_SeasonListNewWidgetCtrl)'],
  [/DungeonPhaseConfig/, 'yes', 'GVE stage title/description (UI_Sys_GVE_StageWidgetCtrl)'],
  [/BattlePassPrivilegeConfig/, 'yes', 'season pass privilege names (UI_Sys_Season_PassBuyPanelCtrl)'],
  [/UnitConfig\.lua/, 'unverified', 'raw _iName instead of a text id; many tagged (无需翻译) = no translation needed'],
  [/SpecialStatusConfig|GameplayStatusConfig|MapModeConfig|LifeCategory/, 'unverified', 'status / mode names; reader not confirmed'],
  [/Config\/Task\/Task\.lua/, 'unverified', 'sample task data'],
  [/RandomNameConfig/, 'yes (one event)', 'fake player names, only UI_Sys_Season_EvePlayPanelCtrl'],
  [/TradeConfig|RedDotConfig|DirtyDataConfig|EntityManagerConfig|EnergyConfig|MultiversePublicParamConfig|SkillConfig\.lua|Lua_Config_EffectData|Config\/Skill\/|Lua_ModelTransformDefine|Lua_AniCurveDefine|Lua_GameDefine|Config\/CFG\//, 'no', 'internal names/comments/curve names or log text'],
];
for (const r of lua) {
  const loc = r.file + ':' + r.line;
  if (r.cls === 'code' || r.cls === 'code-format') {
    let vis = CODE_HIDE.test(loc) ? 'no' : CODE_UNSURE.test(loc) ? 'unverified' : (r.cls === 'code-format' ? (/TrainInfoLogList|PermissionSetupListWidget|Lua_VoiceCall_Util/.test(loc) ? 'likely' : 'no') : (/^(UI\/|Logic\/Level\/|Utility\/Lua_VoiceCall_Util)/.test(r.file) ? 'likely' : 'unverified'));
    const t = tr(r.text);
    codeRows.push({ key: loc, visible: vis, file: r.file, line: r.line, text: r.text, sink: r.callee ? 'call ' + r.callee : (r.sink || ''), existing_key: t.key, existing_en: t.en, match: t.how, zh_len: [...r.text].length });
    if (vis !== 'no') enqueue(r.text, 'lua-code', loc, t.en, t.key, vis);
  } else if (r.cls === 'config') {
    let vis = 'unverified', note = '';
    for (const [re, v, n] of CFG_VIS) if (re.test(r.file)) { vis = v; note = n; break; }
    const fieldM = /\.(\w+) = L\d+_\d+$/.exec(r.sink || '') || /^([\w.]+) = <lit>$/.exec(r.sink || '');
    const t = tr(r.text);
    cfgRows.push({ key: loc, visible: vis, file: r.file, line: r.line, field: fieldM ? fieldM[1] : (r.sink || '').slice(0, 40), text: r.text, existing_key: t.key, existing_en: t.en, match: t.how, note, zh_len: [...r.text].length });
    if (vis !== 'no') enqueue(r.text, /RandomNameConfig/.test(r.file) ? 'random-name' : 'lua-config', loc, t.en, t.key, vis);
  } else {
    excluded[r.cls] = excluded[r.cls] || new Map();
    const m = excluded[r.cls]; const k = r.text;
    if (!m.has(k)) m.set(k, { text: k, n: 0, first: loc }); m.get(k).n++;
  }
}
const hdr = ['key', 'visible', 'file', 'line', 'text', 'sink', 'existing_key', 'existing_en', 'match', 'zh_len'];
writeTsv('lua_code.tsv', hdr, codeRows);
const rn = cfgRows.filter(r => /RandomNameConfig/.test(r.file)); const oc = cfgRows.filter(r => !/RandomNameConfig/.test(r.file));
writeTsv('lua_config.tsv', ['key', 'visible', 'file', 'line', 'field', 'text', 'existing_key', 'existing_en', 'match', 'note', 'zh_len'], oc);
writeTsv('lua_random_names.tsv', ['key', 'visible', 'file', 'line', 'field', 'text', 'existing_key', 'existing_en', 'match', 'zh_len'], rn);
for (const [cls, m] of Object.entries(excluded)) {
  const rows = [...m.values()].map(v => ({ text: v.text, occurrences: v.n, first_location: v.first, class: cls }));
  writeTsv('excluded_lua_' + cls + '.tsv', ['class', 'text', 'occurrences', 'first_location'], rows);
}

// ---------- 4. C# literals ----------
// us_managed.tsv = output of: node us_scan.js $RO3_CLIENT_DIR/ro3_Data/Managed > us_managed.tsv  (dll, #US token, text)
const us = fs.readFileSync(__dirname + '/us_managed.tsv', 'utf8').split('\n').filter(Boolean).map(l => l.split('\t'));
const CS_VIS = [
  [/^(确定|取消|重试|退出|忽略|重启|本地游戏版本号|最新游戏版本号|客户端更新完成|修复客户端|是否清理客户端|正在下载文件|下载速度|当前处于非wifi|您的客户端版本过低|静音|下载资源包出错|很抱歉，无法下载更新|检查到有客户端|正在分析文件差异|文件差异分析完成|正在下载:|请用启动器启动游戏)/, 'fallback', 'LauncherLanguage._compatibleDict fallback (GameUpdateContent_en.json in launcher.asset is used first)'],
  [/^(小时 |分钟 |秒)$/, 'unverified', 'time-unit suffixes (Assembly-CSharp)'],
  [/^(直播中| \/ 直播)$/, 'unverified', 'live-stream label (AVPro media player)'],
  [/^(英语|韩语|泰语|台湾繁体)$/, 'unverified', 'language names'],
];
const csRows = [];
for (const [dll, tok, text] of us) {
  if (!text || !HAN.test(text)) continue;
  let vis = 'no', note = 'log / debug / tool UI';
  for (const [re, v, n] of CS_VIS) if (re.test(text)) { vis = v; note = n; break; }
  const t = tr(text);
  csRows.push({ key: dll + ':' + tok, visible: vis, dll, us_token: tok, text, existing_key: t.key, existing_en: t.en, match: t.how, note });
  if (vis !== 'no') enqueue(text, 'csharp', dll + ':' + tok, t.en, t.key, vis);
}
writeTsv('csharp_literals.tsv', ['key', 'visible', 'dll', 'us_token', 'text', 'existing_key', 'existing_en', 'match', 'note'], csRows);

// ---------- 5. other prefab strings (not text components) summary ----------
const prefabAll = JSON.parse(fs.readFileSync(__dirname + '/prefab_text.json', 'utf8'));
const other = new Map();
for (const r of prefabAll) {
  if (r.field === 'm_text' || r.field === 'm_Text') continue;
  if (!HAN.test(r.text)) continue;
  const k = r.comp + '\u0000' + r.field.replace(/\[\d+\]/g, '[]') + '\u0000' + r.text;
  if (!other.has(k)) other.set(k, { component: r.comp, field: r.field.replace(/\[\d+\]/g, '[]'), text: r.text, occurrences: 0, first_prefab: r.prefabs[0] + ':' + r.go });
  other.get(k).occurrences++;
}
const orows = [...other.values()].map(o => ({ ...o, visible: /HUDPrefabScriptText/.test(o.component) && /m_kStrText/.test(o.field) ? 'placeholder' : (o.component === 'TMP_InputField' ? 'likely' : 'no') }));
writeTsv('prefab_other_strings.tsv', ['visible', 'component', 'field', 'text', 'occurrences', 'first_prefab'], orows);
for (const o of orows) if (o.visible !== 'no') { const t = tr(o.text); enqueue(o.text, 'prefab-other', o.first_prefab, t.en, t.key, o.visible); }

// ---------- 6. localization-table issues ----------
const issues = [];
for (const k in L.en) {
  const d = L.en[k]._kDes;
  if (typeof d !== 'string') continue;
  if (HAN.test(d) || /\?\?\?/.test(d) || d === 'None') issues.push({ key: k, issue: HAN.test(d) ? 'cjk-in-en' : d === 'None' ? 'None (falls back to zh_CN)' : 'contains ???', en: d, zh_CN: L.zh[k] && L.zh[k]._kDes });
}
const seen = new Set();
for (const o of pinv) if (o.cls === 'auto-sid-missing' && !seen.has(o.sid)) { seen.add(o.sid); issues.push({ key: o.sid, issue: 'prefab sid not in any Localization table -> label renders empty', en: '', zh_CN: o.text, where: o.key }); }
writeTsv('loc_table_issues.tsv', ['key', 'issue', 'en', 'zh_CN', 'where'], issues);

// ---------- 7. master queue ----------
const qrows = [...queue.values()].map(q => ({ text: q.text, categories: [...q.cats].join(','), visible: [...q.vis].join(','), occurrences: q.n, existing_key: q.key, existing_en: q.en, example_location: q.where, zh_len: [...q.text].length }))
  .sort((a, b) => (a.existing_en ? 1 : 0) - (b.existing_en ? 1 : 0) || b.occurrences - a.occurrences);
writeTsv('translation_queue.tsv', ['text', 'categories', 'visible', 'occurrences', 'existing_key', 'existing_en', 'example_location', 'zh_len'], qrows);
const stat = {};
for (const q of qrows) for (const c of q.categories.split(',')) { stat[c] = stat[c] || { uniq: 0, withEn: 0, chars: 0 }; stat[c].uniq++; if (q.existing_en) stat[c].withEn++; stat[c].chars += q.zh_len; }
console.log(JSON.stringify(stat, null, 1));
console.log('queue total', qrows.length, 'with existing en', qrows.filter(q => q.existing_en).length);
const vstat = {};
for (const r of codeRows) vstat['code:' + r.visible] = (vstat['code:' + r.visible] || 0) + 1;
for (const r of cfgRows) vstat['cfg:' + r.visible] = (vstat['cfg:' + r.visible] || 0) + 1;
for (const r of csRows) vstat['cs:' + r.visible] = (vstat['cs:' + r.visible] || 0) + 1;
console.log(vstat);
