using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Debug = UnityEngine.Debug;
using Time = UnityEngine.Time;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Finds what makes a frame slow ("!client hitch on|off"), into Player.log as [Ro3Emu.Hitch].
    /// <list type="bullet">
    /// <item>Always: a frame that takes longer than <see cref="SlowFrameMs"/> is logged with its length.</item>
    /// <item>While on: every Lua event (LC_Event:DispatchEvent, which runs its listeners synchronously) and a few
    /// LC_TaskHelper functions are timed; a slow frame lists the calls of that frame that took 2 ms or more, nested by depth.
    /// The wrappers are C# delegates put into the class tables (Lua calls them like the originals), calling the originals.</item>
    /// </list>
    /// </summary>
    public static class HitchProfiler
    {
        private const float SlowFrameMs = 60f;
        private const double RecordMs = 2.0;

        public delegate void Dispatch(object self, object module, object notify, object param);
        public delegate void OneArg(object a);
        public delegate void TwoArgs(object a, object b);
        public delegate void ThreeArgs(object a, object b, object c);

        private static readonly Dispatch DispatchWrapper = TimedDispatch;
        private static Obf_R.Obf_Ef _dispatch;
        private static Obf_R.Obf_ff _eventClass;
        private static readonly List<(Obf_R.Obf_ff Table, string Name, Obf_R.Obf_Ef Original, object Wrapper)> Wrapped =
            new List<(Obf_R.Obf_ff, string, Obf_R.Obf_Ef, object)>();
        private static Dictionary<long, string> _eventNames;
        private static readonly List<(int Depth, string Name, double Ms)> Calls = new List<(int, string, double)>();
        // Every timed call of the frame, also the short ones: name -> (count, total ms).
        private static readonly Dictionary<string, (int Count, double Ms)> Totals = new Dictionary<string, (int, double)>();
        private static int _depth;
        private static bool _on;

        // Sampling inside the slow event: a Lua count hook (C# delegate) takes a traceback every SampleMs and charges the
        // time since the previous sample to that stack. Count hooks only fire on Lua instructions, so time spent in a C#
        // call is charged to the Lua code right after it returns (its caller), which still names the culprit.
        private const double SampleMs = 0.5;
        private const int HookCount = 1000;
        private static readonly TwoArgs HookWrapper = OnHook;
        private static Obf_R.Obf_Ef _setHook, _traceback;
        private static readonly Stopwatch SampleWatch = new Stopwatch();
        private static double _lastSample;
        private static bool _sampling;
        private static readonly Dictionary<string, double> Stacks = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> Leaves = new Dictionary<string, double>();

        public static void Run(string args)
        {
            switch (args.Trim())
            {
                case "on": Enable(); break;
                case "off": Disable(); break;
                default: Debug.Log("[Ro3Emu.Hitch] usage: !client hitch on|off (timing " + (_on ? "on" : "off") + ")"); break;
            }
        }

        /// <summary>Every frame (UiTweaks.Tick).</summary>
        public static void Tick()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            if (ms >= SlowFrameMs && Time.frameCount > 300)
            {
                var sb = new StringBuilder("[Ro3Emu.Hitch] frame ").Append(Time.frameCount).Append(" took ")
                    .Append(ms.ToString("F0")).Append(" ms");
                if (_on)
                {
                    sb.Append("; Lua calls of the previous frame over ").Append(RecordMs).Append(" ms: ").Append(Calls.Count);
                    foreach (var (depth, name, callMs) in Calls)
                        sb.Append("\n  ").Append(' ', depth * 2).Append(name).Append(' ').Append(callMs.ToString("F1")).Append(" ms");
                    sb.Append("\n  all timed calls (count, total):");
                    foreach (var pair in Totals)
                        if (pair.Value.Ms >= 1.0)
                            sb.Append("\n    ").Append(pair.Key).Append(" x").Append(pair.Value.Count).Append(' ')
                              .Append(pair.Value.Ms.ToString("F1")).Append(" ms");
                    sb.Append("\n  ").Append(TaskCounts());
                    AppendTop(sb, "time by innermost named Lua function (sampled)", Leaves);
                    AppendTop(sb, "time by stack, innermost first (sampled)", Stacks);
                }
                Debug.Log(sb.ToString());
            }
            Calls.Clear();
            Totals.Clear();
            Stacks.Clear();
            Leaves.Clear();
        }

        private static void AppendTop(StringBuilder sb, string title, Dictionary<string, double> table)
        {
            if (table.Count == 0) return;
            var rows = new List<KeyValuePair<string, double>>(table);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            sb.Append("\n  ").Append(title).Append(':');
            for (int i = 0; i < rows.Count && i < 15; i++)
                sb.Append("\n    ").Append(rows[i].Value.ToString("F1")).Append(" ms  ").Append(rows[i].Key);
        }

        private static void StartSampling()
        {
            if (_setHook == null || _sampling) return;
            _sampling = true;
            SampleWatch.Restart();
            _lastSample = 0;
            try { _setHook.Obf_HuA(HookWrapper, "", HookCount); }
            catch (Exception e) { _sampling = false; Debug.LogWarning("[Ro3Emu.Hitch] sethook: " + e.Message); _setHook = null; }
        }

        private static void StopSampling()
        {
            if (!_sampling) return;
            _sampling = false;
            try { _setHook.Obf_HuA(); } catch (Exception e) { Debug.LogWarning("[Ro3Emu.Hitch] sethook off: " + e.Message); }
            Sample(); // the tail since the last sample
        }

        private static void OnHook(object ev, object line)
        {
            if (SampleWatch.Elapsed.TotalMilliseconds - _lastSample >= SampleMs) Sample();
        }

        private static void Sample()
        {
            double now = SampleWatch.Elapsed.TotalMilliseconds;
            double ms = now - _lastSample;
            _lastSample = now;
            string trace;
            try { trace = _traceback?.Obf_HuA()?[0] as string; } catch { return; }
            if (trace == null) return;
            var names = new List<string>();
            foreach (var raw in trace.Split('\n'))
            {
                string row = raw.Trim();
                int at = row.IndexOf(" in ", StringComparison.Ordinal);
                if (at < 0 || row.StartsWith("[C]", StringComparison.Ordinal)) continue;
                string name = row.Substring(at + 4);
                if (name.StartsWith("function <", StringComparison.Ordinal) || name.EndsWith("'?'", StringComparison.Ordinal)) continue;
                name = name.Replace("function ", "").Replace("method ", ":").Replace("field ", ".")
                    .Replace("upvalue ", "up ").Replace("'", "");
                if (names.Count == 0 || names[names.Count - 1] != name) names.Add(name);
                if (names.Count >= 5) break;
            }
            if (names.Count == 0) names.Add("(unnamed)");
            Add(Leaves, names[0], ms);
            if (Stacks.Count < 300) Add(Stacks, string.Join(" < ", names), ms);
        }

        private static void Add(Dictionary<string, double> table, string key, double ms) =>
            table[key] = table.TryGetValue(key, out double sum) ? sum + ms : ms;

        /// <summary>How many quests the client holds in AllTaskInfo.TaskInfos, by status (UpdateTaskInfoEvent walks them all).</summary>
        private static string TaskCounts()
        {
            try
            {
                var global = LuaPatches.Global();
                var location = global?.Obf_RuA<Obf_R.Obf_ff>("ServerLocation");
                var assets = location?.Obf_RuA<Obf_R.Obf_Ef>("GetServer")?.Obf_HuA(location, "LC_AssetManager");
                if (assets == null || assets.Length == 0 || !(assets[0] is Obf_R.Obf_ff manager)) return "tasks: asset manager not found";
                var infos = manager.Obf_RuA<Obf_R.Obf_Ef>("GetAssetData")?.Obf_HuA(manager, "AllTaskInfo", "TaskInfos");
                if (infos == null || infos.Length == 0 || !(infos[0] is Obf_R.Obf_ff table)) return "tasks: none";
                var byStatus = new SortedDictionary<long, int>();
                table.Obf_OuA<object, object>((_, info) =>
                {
                    long status = info is Obf_R.Obf_ff t ? LuaPatches.ToLong(t.Obf_RuA<object>("status")) : -1;
                    byStatus[status] = byStatus.TryGetValue(status, out int n) ? n + 1 : 1;
                });
                var sb = new StringBuilder("tasks by status:");
                foreach (var pair in byStatus) sb.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
                return sb.ToString();
            }
            catch (Exception e) { return "tasks: " + e.GetType().Name + ": " + e.Message; }
        }

        private static void Enable()
        {
            var global = LuaPatches.Global();
            var manager = HappyEngine.Base.SingletonMonoBehaviour<LA_LuaManager>.Instance;
            if (global == null || manager == null) { Debug.Log("[Ro3Emu.Hitch] Lua is not up yet"); return; }
            if (_on) Disable();
            _eventNames = EventNames(global);
            var debugLib = global.Obf_RuA<Obf_R.Obf_ff>("debug");
            _setHook = debugLib?.Obf_RuA<Obf_R.Obf_Ef>("sethook");
            _traceback = debugLib?.Obf_RuA<Obf_R.Obf_Ef>("traceback");
            _eventClass = LuaPatches.Module(manager, global, "lc_event");
            _dispatch = _eventClass?.Obf_RuA<Obf_R.Obf_Ef>("DispatchEvent");
            if (_dispatch != null) _eventClass.Obf_luA<string, Dispatch>("DispatchEvent", DispatchWrapper);
            var helper = global.Obf_RuA<Obf_R.Obf_ff>("LC_TaskHelper");
            if (helper != null)
            {
                foreach (var name in new[] { "SetClientNPC", "CheckClientNPCShow", "DeleteClientNpcId", "DeleteNpcAtMap",
                    "DeleteMatchMapClientNPC", "SetClientObjectNPC", "SetMatchMapClientNPC", "ClearUpdateTaskTemplateData" })
                    WrapOne(helper, "LC_TaskHelper." + name, name);
                foreach (var name in new[] { "AddClientNpcId" })
                    WrapOne(helper, "LC_TaskHelper." + name, name);
                WrapTwo(helper, "LC_TaskHelper.SetNpcAtMap", "SetNpcAtMap");
                WrapThree(helper, "LC_TaskHelper.SetClientSingleNPC", "SetClientSingleNPC");
            }
            _on = true;
            Debug.Log("[Ro3Emu.Hitch] timing on: " + (_dispatch != null ? "events" : "no events (DispatchEvent not found)")
                + ", " + Wrapped.Count + " LC_TaskHelper function(s), " + _eventNames.Count + " event names"
            + (_setHook != null && _traceback != null ? ", sampling" : ", no sampling (debug.sethook not found)"));
        }

        private static void Disable()
        {
            StopSampling();
            if (_dispatch != null && _eventClass != null) _eventClass.Obf_luA<string, Obf_R.Obf_Ef>("DispatchEvent", _dispatch);
            foreach (var (table, name, original, _) in Wrapped) table.Obf_luA<string, Obf_R.Obf_Ef>(name, original);
            Wrapped.Clear();
            _dispatch = null;
            _on = false;
            Debug.Log("[Ro3Emu.Hitch] timing off");
        }

        private static void WrapOne(Obf_R.Obf_ff table, string label, string name)
        {
            var original = table.Obf_RuA<Obf_R.Obf_Ef>(name);
            if (original == null) return;
            OneArg wrapper = a => Timed(label, () => original.Obf_HuA(a));
            Wrapped.Add((table, name, original, wrapper));
            table.Obf_luA<string, OneArg>(name, wrapper);
        }

        private static void WrapTwo(Obf_R.Obf_ff table, string label, string name)
        {
            var original = table.Obf_RuA<Obf_R.Obf_Ef>(name);
            if (original == null) return;
            TwoArgs wrapper = (a, b) => Timed(label, () => original.Obf_HuA(a, b));
            Wrapped.Add((table, name, original, wrapper));
            table.Obf_luA<string, TwoArgs>(name, wrapper);
        }

        private static void WrapThree(Obf_R.Obf_ff table, string label, string name)
        {
            var original = table.Obf_RuA<Obf_R.Obf_Ef>(name);
            if (original == null) return;
            ThreeArgs wrapper = (a, b, c) => Timed(label, () => original.Obf_HuA(a, b, c));
            Wrapped.Add((table, name, original, wrapper));
            table.Obf_luA<string, ThreeArgs>(name, wrapper);
        }

        private static void TimedDispatch(object self, object module, object notify, object param)
        {
            var original = _dispatch;
            if (original == null) return;
            long key = LuaPatches.ToLong(module) << 32 | (LuaPatches.ToLong(notify) & 0xFFFFFFFFL);
            bool sample = _depth == 0 && _eventNames != null && _eventNames.TryGetValue(key, out var name)
                && name == "Common.UpdateTaskInfoEvent";
            if (sample) StartSampling();
            try { Timed(null, () => original.Obf_HuA(self, module, notify, param), key); }
            finally { if (sample) StopSampling(); }
        }

        private static void Timed(string label, Action call, long eventKey = 0)
        {
            int index = Calls.Count;
            int depth = _depth++;
            var watch = Stopwatch.StartNew();
            try { call(); }
            finally
            {
                _depth--;
                double ms = watch.Elapsed.TotalMilliseconds;
                string total = label ?? "event " + (_eventNames != null && _eventNames.TryGetValue(eventKey, out var en)
                    ? en : (eventKey >> 32) + ":" + (eventKey & 0xFFFFFFFFL));
                if (Totals.Count < 200 || Totals.ContainsKey(total))
                    Totals[total] = Totals.TryGetValue(total, out var sum) ? (sum.Count + 1, sum.Ms + ms) : (1, ms);
                if (ms >= RecordMs && Calls.Count < 400)
                {
                    Calls.Insert(Math.Min(index, Calls.Count), (depth, total, ms)); // parents before their children
                }
            }
        }

        /// <summary>(LC_ModuleId.X &lt;&lt; 32) + LC_NotifyId.X.Y -> "X.Y", as LC_Event:DispatchEvent builds its key.</summary>
        private static Dictionary<long, string> EventNames(Obf_R.Obf_ff global)
        {
            var names = new Dictionary<long, string>();
            try
            {
                var modules = global.Obf_RuA<Obf_R.Obf_ff>("LC_ModuleId");
                var notifies = global.Obf_RuA<Obf_R.Obf_ff>("LC_NotifyId");
                if (modules == null || notifies == null) return names;
                modules.Obf_OuA<string, object>((moduleName, moduleValue) =>
                {
                    long moduleId = LuaPatches.ToLong(moduleValue);
                    if (moduleId == long.MinValue || !(notifies.Obf_RuA<object>(moduleName) is Obf_R.Obf_ff ids)) return;
                    ids.Obf_OuA<string, object>((notifyName, notifyValue) =>
                    {
                        long notifyId = LuaPatches.ToLong(notifyValue);
                        if (notifyId != long.MinValue) names[moduleId << 32 | (notifyId & 0xFFFFFFFFL)] = moduleName + "." + notifyName;
                    });
                });
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.Hitch] event names: " + e.GetType().Name + ": " + e.Message); }
            return names;
        }
    }
}
