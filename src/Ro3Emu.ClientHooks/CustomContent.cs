using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Custom content from the server (content/*.json there, docs/guide/custom-content.md): config rows the client does not
    /// ship (TaskConfig, NPCConfig, DialogueConfig, ...) and their English texts, added to the client's own Lua tables. The
    /// Lua payload cannot change (the recovery manifest pins every chunk's hash), but the client reads config rows live:
    /// Lua_DBManager.m_kCFG_&lt;Table&gt; lazily builds the global m_kMeta&lt;Table&gt; (Lua_CFG_Base), whose GetEntryPtr reads
    /// m_kConfigData.m_kValues[id] on every lookup, and Language.Trans reads the global LanguageKV (the loaded
    /// Localization_&lt;lang&gt; m_kValues). So a row set into those tables is a row of the game.
    /// <list type="bullet">
    /// <item>The server sends the rows as game message <see cref="MsgId"/> (UTF-8 JSON {"texts": {id: text}, "rows":
    /// {Table: {id: row}}}) at role login, before the quest and NPC data; it is applied at once when it arrives on the
    /// main thread, else on the next tick, and kept in &lt;client&gt;/ro3emu/custom-content.json so the next start has it
    /// before login.</item>
    /// <item>Custom NPCs come as scene-editor kNpcList entries, added to the map's scene-editor module (required like
    /// LC_InteractActions does): the quest tracker's walk-to-NPC actions (ActionConfig 142/143) find their target as
    /// kNpcList[id] there (LC_InteractActions.GetPosByTargetID), and the minimap lists it. IsServerNpc is set, so the
    /// client does not spawn its own copy; the server spawns the NPC.</item>
    /// <item>Every LuaPatches tick checks one row per table and sets them again when it is gone (Lua_DBManager.ReLoadAllCfg
    /// and Language.Load replace the tables). A table's cached id list (m_kAllIDList) is cleared so lists include the new
    /// ids.</item>
    /// </list>
    /// No Lua source runs: tables are made with the client's table.pack and filled with rawset.
    /// </summary>
    public static class CustomContent
    {
        /// <summary>Must equal the server's CustomContentService.ContentMsg.</summary>
        public const uint MsgId = 77020;

        private sealed class Content
        {
            public readonly Dictionary<long, string> Texts = new Dictionary<long, string>();
            public readonly Dictionary<string, Dictionary<long, object>> Rows = new Dictionary<string, Dictionary<long, object>>();
            /// <summary>Scene-editor module name -> kNpcList key -> entry.</summary>
            public readonly Dictionary<string, Dictionary<long, object>> Npcs = new Dictionary<string, Dictionary<long, object>>();
            public byte[] Raw;
        }

        private static volatile Content _content;
        private static volatile bool _dirty;            // _content changed since it was last applied
        /// <summary>Tables whose Lua_CFG module indexes its rows in OnInitDatas, rerun after rows are added.</summary>
        private static readonly Dictionary<string, string> Reindexed = new Dictionary<string, string>
        {
            ["ShopConfig"] = "OnInitDatas", ["ShopCommodityConfig"] = "OnInitDatas",
            // Lua_CFG_WardRobeConfig.ctor builds the wardrobe's type lists and suit pieces, leaving out _iDisable looks.
            ["WardRobeConfig"] = "ctor",
        };
        /// <summary>Per table, the first row last set: a row that replaces one of the client's own is never missing,
        /// so a reload that brings the client's row back shows as a different row there.</summary>
        private static readonly Dictionary<string, Obf_R.Obf_ff> Applied = new Dictionary<string, Obf_R.Obf_ff>();
        private static volatile byte[] _toCache;        // received, not written to the cache file yet
        private static string _cachePath;
        private static bool _cacheRead;
        private static int _mainThread = -1;
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>From ServerImAdapter.OnMessage (possibly off the main thread).</summary>
        internal static void Receive(byte[] payload)
        {
            try
            {
                var content = Parse(payload);
                _content = content;
                _dirty = true;
                _toCache = payload;
                Debug.Log("[Ro3Emu.CustomContent] received " + payload.Length + " bytes: " + Describe(content));
                if (Thread.CurrentThread.ManagedThreadId == _mainThread) Apply();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.CustomContent] bad payload: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>From LuaPatches.Tick (main thread, every 30th frame) once the LuaEnv is up.</summary>
        internal static void Tick()
        {
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            if (_cachePath == null)
                _cachePath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "ro3emu", "custom-content.json");
            if (!_cacheRead)
            {
                _cacheRead = true;
                try
                {
                    if (_content == null && File.Exists(_cachePath))
                    {
                        var raw = File.ReadAllBytes(_cachePath);
                        _content = Parse(raw);
                        _dirty = true;
                        Debug.Log("[Ro3Emu.CustomContent] cached content: " + Describe(_content));
                    }
                }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.CustomContent] cache unreadable: " + e.Message); }
            }
            var pending = _toCache;
            if (pending != null)
            {
                _toCache = null;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
                    File.WriteAllBytes(_cachePath, pending);
                }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.CustomContent] cache not written: " + e.Message); }
            }
            Apply();
        }

        private static void Apply()
        {
            var content = _content;
            if (content == null) return;
            var global = LuaPatches.Global();
            // As LuaPatches: GlobalRequire sets LC_ItemShowHelper once the game's modules are up; requiring before is unsafe.
            if (global == null || global.Obf_RuA<object>("LC_ItemShowHelper") == null) return;
            try
            {
                var rawget = global.Obf_LuA<Obf_R.Obf_Ef>("rawget");
                var rawset = global.Obf_LuA<Obf_R.Obf_Ef>("rawset");
                var pack = global.Obf_LuA<Obf_R.Obf_Ef>("table.pack");
                if (rawget == null || rawset == null || pack == null) return;
                bool force = _dirty;
                _dirty = false;
                if (content.Texts.Count > 0 && global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV") is Obf_R.Obf_ff kv)
                {
                    if (force || Missing(rawget, kv, First(content.Texts.Keys)))
                    {
                        foreach (var text in content.Texts)
                        {
                            var row = NewTable(pack, rawset);
                            rawset.Obf_HuA(row, "_iID", text.Key);
                            rawset.Obf_HuA(row, "_kDes", text.Value);
                            rawset.Obf_HuA(kv, text.Key, row);
                        }
                        Note("texts: " + content.Texts.Count + " set in LanguageKV");
                    }
                }
                foreach (var table in content.Rows)
                {
                    var values = Values(global, table.Key, out var instance, out var data);
                    if (values == null) { Once("table " + table.Key + " not found"); continue; }
                    long firstKey = First(table.Value.Keys);
                    if (!force && !Missing(rawget, values, firstKey) && !Replaced(global, rawget, values, table.Key, firstKey)) continue;
                    int added = 0;
                    var meta = RowMeta(global, rawget, values, table.Value);
                    if (meta == null) Once(table.Key + ": no row metatable found, rows keep no defaults");
                    foreach (var row in table.Value)
                    {
                        if (Missing(rawget, values, row.Key)) added++;
                        var value = ToLua(row.Value, pack, rawset);
                        if (meta != null) Adopt(global, rawget, value, row.Value, meta);
                        rawset.Obf_HuA(values, row.Key, value);
                    }
                    if (added > 0 && data != null && LuaPatches.ToLong(data.Obf_RuA<object>("m_kCount")) is long count && count != long.MinValue)
                        rawset.Obf_HuA(data, "m_kCount", count + added);
                    // Lua_CFG_Base.GetAllIDList caches the sorted ids on first use.
                    if (instance != null) rawset.Obf_HuA(instance, "m_kAllIDList", null);
                    // ...and on the proxy, which keeps its own after Lua_DBManager.ReLoadAllCfg (see Reindex).
                    if (global.Obf_LuA<Obf_R.Obf_ff>("Lua_DBManager.m_kCFG_" + table.Key) is Obf_R.Obf_ff proxy)
                        rawset.Obf_HuA(proxy, "m_kAllIDList", null);
                    // Some Lua_CFG modules index their rows once, at load (OnInitDatas). Lua_CFG_ShopCommodityConfig builds
                    // ShopItemsMap (the shop tabs' lists by _iClassID) and AppearanceIdMap there, and Lua_CFG_ShopConfig
                    // ShopTypeMap/SubShopMap/npcShopMap (the shops by type and id, for UI_Sys_ShopNpcPanel), so added rows
                    // stay out of the shop until it runs again; it starts from new tables, so a rerun is safe.
                    if (First(rawget.Obf_HuA(values, firstKey)) is Obf_R.Obf_ff firstRow) Applied[table.Key] = firstRow;
                    if (Reindexed.TryGetValue(table.Key, out var method)) Reindex(global, rawget, table.Key, method, instance);
                    Note(table.Key + ": " + table.Value.Count + " row(s) set (" + added + " new)");
                }
                if (content.Npcs.Count > 0)
                {
                    var manager = HappyEngine.Base.SingletonMonoBehaviour<LA_LuaManager>.Instance;
                    foreach (var module in content.Npcs)
                    {
                        var list = manager == null ? null : LuaPatches.Module(manager, global, module.Key)?.Obf_RuA<Obf_R.Obf_ff>("kNpcList");
                        if (list == null) { Once("scene editor " + module.Key + " has no kNpcList"); continue; }
                        if (!force && !Missing(rawget, list, First(module.Value.Keys))) continue;
                        foreach (var npc in module.Value) rawset.Obf_HuA(list, npc.Key, ToLua(npc.Value, pack, rawset));
                        Note(module.Key + ": " + module.Value.Count + " NPC(s) set in kNpcList");
                    }
                }
            }
            catch (Exception e)
            {
                Once("apply failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Reruns OnInitDatas on the table's instance and, when it holds its own data, on the Lua_DBManager proxy.
        /// Lua_DBManager.ReLoadAllCfg (on a language load) calls proxy:ctor(name), which runs Lua_CFG_Base's ctor
        /// with self = the proxy: the proxy then keeps its own m_kConfigData and index maps (ShopItemsMap), which
        /// shadow the instance's for every proxy:OnGet... call the UI makes. With no instance found, the proxy only.
        /// </summary>
        private static void Reindex(Obf_R.Obf_ff global, Obf_R.Obf_Ef rawget, string table, string method, Obf_R.Obf_ff instance)
        {
            var proxy = global.Obf_LuA<Obf_R.Obf_ff>("Lua_DBManager.m_kCFG_" + table);
            bool proxyOwnsData = proxy != null && Raw(rawget, proxy, "m_kConfigData") != null;
            var done = new List<string>();
            if (instance != null) Init(global, rawget, method, instance, "instance", done);
            if (proxy != null && (proxyOwnsData || instance == null)) Init(global, rawget, method, proxy, "proxy", done);
            if (done.Count == 0) Once(table + ": " + method + " not found, rows not reindexed");
            else Note(table + ": reindexed on " + string.Join(", ", done));
        }

        private static void Init(Obf_R.Obf_ff global, Obf_R.Obf_Ef rawget, string method, Obf_R.Obf_ff self, string name, List<string> done)
        {
            var init = self.Obf_RuA<Obf_R.Obf_Ef>(method);
            if (init == null) return;
            init.Obf_HuA(self);
            // The Fashion tab's size (sets, 50201) shows in a Player.log whether the added commodities made it in.
            var tab = Raw(rawget, self.Obf_RuA<Obf_R.Obf_ff>("ShopItemsMap"), 50201L) as Obf_R.Obf_ff;
            var len = tab == null ? null : global.Obf_LuA<Obf_R.Obf_Ef>("rawlen")?.Obf_HuA(tab);
            done.Add("the " + name + (len != null && len.Length > 0 ? " (Fashion tab 50201: " + LuaPatches.ToLong(len[0]) + " item(s))" : ""));
        }

        private static object Raw(Obf_R.Obf_Ef rawget, Obf_R.Obf_ff table, object key)
        {
            if (table == null) return null;
            var r = rawget.Obf_HuA(table, key);
            return r != null && r.Length > 0 ? r[0] : null;
        }

        /// <summary>
        /// The live m_kValues of a config table: through Lua_DBManager (its proxy's __index builds the global
        /// m_kMeta&lt;Table&gt; on first use), else the module itself (package.loaded or require of the lower-case name).
        /// </summary>
        private static Obf_R.Obf_ff Values(Obf_R.Obf_ff global, string table, out Obf_R.Obf_ff instance, out Obf_R.Obf_ff data)
        {
            instance = null;
            data = null;
            var proxy = global.Obf_LuA<Obf_R.Obf_ff>("Lua_DBManager.m_kCFG_" + table);
            if (proxy != null)
            {
                data = proxy.Obf_RuA<Obf_R.Obf_ff>("m_kConfigData"); // builds m_kMeta<Table> when not built yet
                // The proxy's __index keeps the instance on the Lua_DBManager class (main chunk local, not a global).
                instance = global.Obf_LuA<Obf_R.Obf_ff>("Lua_DBManager.m_kMeta" + table)
                    ?? global.Obf_RuA<Obf_R.Obf_ff>("m_kMeta" + table);
                if (instance != null) data = instance.Obf_RuA<Obf_R.Obf_ff>("m_kConfigData") ?? data;
            }
            if (data == null)
            {
                var manager = HappyEngine.Base.SingletonMonoBehaviour<LA_LuaManager>.Instance;
                if (manager != null) data = LuaPatches.Module(manager, global, table.ToLowerInvariant());
            }
            return data?.Obf_RuA<Obf_R.Obf_ff>("m_kValues");
        }

        /// <summary>
        /// The metatable the client's config module gives its rows (DialogueConfig.lua's tail: every row, or every entry
        /// of a row array, gets { __index = defaults, __newindex = read-only guard, __pairs }). The exported data leaves
        /// out fields equal to the default, so rows without it read nil there (DialogueConfig _iSortID: "attempt to
        /// compare number with nil" in GetNextDialogueConfig). Taken from the first rows of the table that are not ours;
        /// for an array row, from its first entry. Null when none is found.
        /// </summary>
        private static Obf_R.Obf_ff RowMeta(Obf_R.Obf_ff global, Obf_R.Obf_Ef rawget, Obf_R.Obf_ff values, Dictionary<long, object> ours)
        {
            var next = global.Obf_LuA<Obf_R.Obf_Ef>("next");
            var getmetatable = global.Obf_LuA<Obf_R.Obf_Ef>("getmetatable");
            if (next == null || getmetatable == null) return null;
            object key = null;
            for (int i = 0; i < 64; i++)
            {
                var r = next.Obf_HuA(values, key);
                if (r == null || r.Length < 2 || r[0] == null) return null;
                key = r[0];
                if (LuaPatches.ToLong(key) is long k && ours.ContainsKey(k)) continue;
                if (!(r[1] is Obf_R.Obf_ff sample)) continue;
                if (MetaOf(getmetatable, sample) is Obf_R.Obf_ff meta) return meta;
                var first = rawget.Obf_HuA(sample, 1L);
                if (first != null && first.Length > 0 && first[0] is Obf_R.Obf_ff entry && MetaOf(getmetatable, entry) is Obf_R.Obf_ff inner)
                    return inner;
            }
            return null;
        }

        private static Obf_R.Obf_ff MetaOf(Obf_R.Obf_Ef getmetatable, Obf_R.Obf_ff table)
        {
            var r = getmetatable.Obf_HuA(table);
            return r != null && r.Length > 0 ? r[0] as Obf_R.Obf_ff : null;
        }

        /// <summary>Gives a new row (or each entry of a new row array) the client's row metatable.</summary>
        private static void Adopt(Obf_R.Obf_ff global, Obf_R.Obf_Ef rawget, object value, object json, Obf_R.Obf_ff meta)
        {
            var setmetatable = global.Obf_LuA<Obf_R.Obf_Ef>("setmetatable");
            if (setmetatable == null || !(value is Obf_R.Obf_ff table)) return;
            if (json is List<object> list)
            {
                for (int i = 1; i <= list.Count; i++)
                {
                    var r = rawget.Obf_HuA(table, (long)i);
                    if (r != null && r.Length > 0 && r[0] is Obf_R.Obf_ff entry) setmetatable.Obf_HuA(entry, meta);
                }
            }
            else setmetatable.Obf_HuA(table, meta);
        }

        private static bool Replaced(Obf_R.Obf_ff global, Obf_R.Obf_Ef rawget, Obf_R.Obf_ff values, string table, long key)
        {
            if (!Applied.TryGetValue(table, out var mine)) return true;
            try
            {
                var rawequal = global.Obf_LuA<Obf_R.Obf_Ef>("rawequal");
                return rawequal == null || !(First(rawequal.Obf_HuA(First(rawget.Obf_HuA(values, key)), mine)) is bool same && same);
            }
            catch (Exception) { return true; } // a row from an earlier LuaEnv
        }

        private static object First(object[] r) => r != null && r.Length > 0 ? r[0] : null;

        private static bool Missing(Obf_R.Obf_Ef rawget, Obf_R.Obf_ff table, long key)
        {
            var result = rawget.Obf_HuA(table, key);
            return result == null || result.Length == 0 || result[0] == null;
        }

        private static long First(IEnumerable<long> keys)
        {
            foreach (var k in keys) return k;
            return 0;
        }

        /// <summary>JSON value -> Lua: objects and arrays become tables (numeric object keys become integer keys, array
        /// items keys 1..n), numbers stay integer or float as written, null leaves the field unset.</summary>
        private static object ToLua(object value, Obf_R.Obf_Ef pack, Obf_R.Obf_Ef rawset)
        {
            if (value is Dictionary<string, object> obj)
            {
                var table = NewTable(pack, rawset);
                foreach (var field in obj)
                {
                    if (field.Value == null) continue;
                    object key = long.TryParse(field.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? (object)n : field.Key;
                    rawset.Obf_HuA(table, key, ToLua(field.Value, pack, rawset));
                }
                return table;
            }
            if (value is List<object> list)
            {
                var table = NewTable(pack, rawset);
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null) rawset.Obf_HuA(table, (long)(i + 1), ToLua(list[i], pack, rawset));
                return table;
            }
            return value;
        }

        /// <summary>A new Lua table: table.pack() without its count fields (as MapModules.NewTable).</summary>
        private static Obf_R.Obf_ff NewTable(Obf_R.Obf_Ef pack, Obf_R.Obf_Ef rawset)
        {
            var table = (Obf_R.Obf_ff)pack.Obf_HuA()[0];
            rawset.Obf_HuA(table, "n", null);
            rawset.Obf_HuA(table, "__paramslen", null);
            return table;
        }

        private static Content Parse(byte[] payload)
        {
            var root = new Json(Encoding.UTF8.GetString(payload)).Value() as Dictionary<string, object>
                ?? throw new FormatException("not a JSON object");
            var content = new Content { Raw = payload };
            if (root.TryGetValue("texts", out var t) && t is Dictionary<string, object> texts)
                foreach (var e in texts)
                    if (long.TryParse(e.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && e.Value is string s)
                        content.Texts[id] = s;
            if (root.TryGetValue("rows", out var r) && r is Dictionary<string, object> tables)
                foreach (var table in tables)
                {
                    if (!(table.Value is Dictionary<string, object> rows)) continue;
                    var byId = new Dictionary<long, object>();
                    foreach (var row in rows)
                        if (long.TryParse(row.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && row.Value != null)
                            byId[id] = row.Value;
                    if (byId.Count > 0) content.Rows[table.Key] = byId;
                }
            if (root.TryGetValue("npcs", out var n) && n is List<object> npcs)
                foreach (var item in npcs)
                {
                    if (!(item is Dictionary<string, object> npc) || !(npc.TryGetValue("module", out var m) && m is string module && module.Length > 0)
                        || !(npc.TryGetValue("entry", out var e) && e is Dictionary<string, object> entry)
                        || !(entry.TryGetValue("Id", out var id) && id is long key)) continue;
                    if (!content.Npcs.TryGetValue(module, out var byKey)) content.Npcs[module] = byKey = new Dictionary<long, object>();
                    byKey[key] = entry;
                }
            return content;
        }

        private static string Describe(Content content)
        {
            var parts = new List<string> { content.Texts.Count + " text(s)" };
            foreach (var table in content.Rows) parts.Add(table.Key + " " + table.Value.Count);
            foreach (var module in content.Npcs) parts.Add(module.Key + " NPCs " + module.Value.Count);
            return string.Join(", ", parts.ToArray());
        }

        private static void Once(string message) { if (Reported.Add(message)) Debug.LogWarning("[Ro3Emu.CustomContent] " + message); }
        private static void Note(string message) { Debug.Log("[Ro3Emu.CustomContent] " + message); }

        /// <summary>A small JSON reader: objects -> Dictionary, arrays -> List, integers -> long, other numbers -> double.</summary>
        private sealed class Json
        {
            private readonly string _s;
            private int _p;

            public Json(string s) { _s = s; }

            public object Value()
            {
                Skip();
                if (_p >= _s.Length) throw new FormatException("unexpected end");
                char c = _s[_p];
                if (c == '{') return Object();
                if (c == '[') return Array();
                if (c == '"') return String();
                if (Word("true")) return true;
                if (Word("false")) return false;
                if (Word("null")) return null;
                return Number();
            }

            private Dictionary<string, object> Object()
            {
                var result = new Dictionary<string, object>();
                _p++;
                Skip();
                if (Peek() == '}') { _p++; return result; }
                while (true)
                {
                    Skip();
                    var key = String();
                    Skip();
                    Expect(':');
                    result[key] = Value();
                    Skip();
                    if (Peek() == ',') { _p++; continue; }
                    Expect('}');
                    return result;
                }
            }

            private List<object> Array()
            {
                var result = new List<object>();
                _p++;
                Skip();
                if (Peek() == ']') { _p++; return result; }
                while (true)
                {
                    result.Add(Value());
                    Skip();
                    if (Peek() == ',') { _p++; continue; }
                    Expect(']');
                    return result;
                }
            }

            private string String()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (_p >= _s.Length) throw new FormatException("unterminated string");
                    char c = _s[_p++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = _s[_p++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(_s.Substring(_p, 4), 16)); _p += 4; break;
                        default: sb.Append(e); break;
                    }
                }
            }

            private object Number()
            {
                int start = _p;
                while (_p < _s.Length && "+-0123456789.eE".IndexOf(_s[_p]) >= 0) _p++;
                var text = _s.Substring(start, _p - start);
                if (text.Length == 0) throw new FormatException("unexpected '" + _s[start] + "' at " + start);
                if (text.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private bool Word(string word)
            {
                if (string.CompareOrdinal(_s, _p, word, 0, word.Length) != 0) return false;
                _p += word.Length;
                return true;
            }

            private char Peek() => _p < _s.Length ? _s[_p] : '\0';

            private void Expect(char c)
            {
                if (Peek() != c) throw new FormatException("expected '" + c + "' at " + _p);
                _p++;
            }

            private void Skip()
            {
                while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++;
            }
        }
    }
}
