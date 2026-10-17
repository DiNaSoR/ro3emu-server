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

        // The quest update event, alternately run with the Lua GC stopped, to see how much of it is garbage collection.
        private static Obf_R.Obf_Ef _collect;
        private static int _questUpdates;
        private static bool _inQuestUpdate;
        private static readonly List<string> QuestNotes = new List<string>();

        public static void Run(string args)
        {
            switch (args.Trim())
            {
                case "on": Enable(); break;
                case "off": Disable(); break;
                case "logtest": LogTest(); break;
                default: Debug.Log("[Ro3Emu.Hitch] usage: !client hitch on|off|logtest (timing " + (_on ? "on" : "off") + ")"); break;
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
                    foreach (var note in QuestNotes) sb.Append("\n  ").Append(note);
                }
                Debug.Log(sb.ToString());
            }
            Calls.Clear();
            Totals.Clear();
            QuestNotes.Clear();
        }

        /// <summary>What one log line costs the main thread: Unity's Debug.Log and the game's Lua_Debug.LogFormat (Lua
        /// traceback + GS_Debug.LogFormat), each with Unity's stack trace for Log messages as set and with it off.</summary>
        private static void LogTest()
        {
            var global = LuaPatches.Global();
            var luaDebug = global?.Obf_LuA<Obf_R.Obf_ff>("package.loaded.lua_debug");
            var logFormat = luaDebug?.Obf_RuA<Obf_R.Obf_Ef>("LogFormat");
            object logType = global?.Obf_LuA<object>("enum_LogType.Log");
            var saved = UnityEngine.Application.GetStackTraceLogType(UnityEngine.LogType.Log);
            var sb = new StringBuilder("[Ro3Emu.Hitch] log test (Unity stack trace for Log: " + saved + "):");
            foreach (var mode in new[] { saved, UnityEngine.StackTraceLogType.None })
            {
                UnityEngine.Application.SetStackTraceLogType(UnityEngine.LogType.Log, mode);
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 5; i++) Debug.Log("[Ro3Emu.Hitch] log test C# " + i);
                double unity = watch.Elapsed.TotalMilliseconds / 5;
                double lua = -1;
                if (logFormat != null && logType != null)
                {
                    watch.Restart();
                    for (int i = 0; i < 5; i++) logFormat.Obf_HuA(logType, true, "[Ro3Emu.Hitch] log test Lua " + i);
                    lua = watch.Elapsed.TotalMilliseconds / 5;
                }
                sb.Append("\n  stack trace ").Append(mode).Append(": Debug.Log ").Append(unity.ToString("F2"))
                  .Append(" ms, Lua_Debug.LogFormat ").Append(lua.ToString("F2")).Append(" ms per line");
            }
            UnityEngine.Application.SetStackTraceLogType(UnityEngine.LogType.Log, saved);
            Debug.Log(sb.ToString());
        }

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
            _collect = global.Obf_RuA<Obf_R.Obf_Ef>("collectgarbage");
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
            try { WrapMethods(manager, global); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.Hitch] methods: " + e.GetType().Name + ": " + e.Message); }
            _on = true;
            Debug.Log("[Ro3Emu.Hitch] timing on: " + (_dispatch != null ? "events" : "no events (DispatchEvent not found)")
                + ", " + Wrapped.Count + " LC_TaskHelper function(s), " + _eventNames.Count + " event names"
            + ", " + _methods + " method(s)" + (_collect != null ? ", GC test" : ""));
        }

        private static void Disable()
        {
            if (_dispatch != null && _eventClass != null) _eventClass.Obf_luA<string, Obf_R.Obf_Ef>("DispatchEvent", _dispatch);
            foreach (var (table, name, original, _) in Wrapped) table.Obf_luA<string, Obf_R.Obf_Ef>(name, original);
            Wrapped.Clear();
            _dispatch = null;
            _on = false;
            Debug.Log("[Ro3Emu.Hitch] timing off");
        }

        // Every wrapper takes four arguments and passes the first result back: xLua fills missing arguments with nil and
        // drops extra ones, so one shape fits all the wrapped functions (none takes more than four or uses varargs).
        public delegate object Four(object a, object b, object c, object d);

        private static void WrapOne(Obf_R.Obf_ff table, string label, string name) => Wrap(table, label, name);
        private static void WrapTwo(Obf_R.Obf_ff table, string label, string name) => Wrap(table, label, name);
        private static void WrapThree(Obf_R.Obf_ff table, string label, string name) => Wrap(table, label, name);
        private static void WrapRet(Obf_R.Obf_ff table, string label, string name) { if (Wrap(table, label, name)) _methods++; }

        private static bool Wrap(Obf_R.Obf_ff table, string label, string name)
        {
            var original = table?.Obf_RuA<Obf_R.Obf_Ef>(name);
            if (original == null) return false;
            Four wrapper = (a, b, c, d) =>
            {
                object result = null;
                Timed(label, () => { var r = original.Obf_HuA(a, b, c, d); result = r != null && r.Length > 0 ? r[0] : null; });
                return result;
            };
            Wrapped.Add((table, name, original, wrapper));
            table.Obf_luA<string, Four>(name, wrapper);
            return true;
        }

        private static void TimedDispatch(object self, object module, object notify, object param)
        {
            var original = _dispatch;
            if (original == null) return;
            long key = LuaPatches.ToLong(module) << 32 | (LuaPatches.ToLong(notify) & 0xFFFFFFFFL);
            bool quest = !_inQuestUpdate && _eventNames != null && _eventNames.TryGetValue(key, out var name)
                && name == "Common.UpdateTaskInfoEvent";
            if (!quest)
            {
                Timed(_inQuestUpdate ? "(inside quest update) event " + EventName(key) : null,
                    () => original.Obf_HuA(self, module, notify, param), key);
                return;
            }
            bool gcOff = _collect != null && (_questUpdates++ & 1) == 1;
            double before = Kb();
            if (gcOff) _collect.Obf_HuA("stop");
            var watch = Stopwatch.StartNew();
            _inQuestUpdate = true;
            try { Timed(null, () => original.Obf_HuA(self, module, notify, param), key); }
            finally
            {
                _inQuestUpdate = false;
                double ms = watch.Elapsed.TotalMilliseconds;
                double after = Kb();
                if (gcOff) _collect.Obf_HuA("restart");
                QuestNotes.Add("quest update " + ms.ToString("F1") + " ms with the Lua GC " + (gcOff ? "stopped" : "running")
                    + ", Lua heap " + before.ToString("F0") + " -> " + after.ToString("F0") + " KB");
            }
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
                string total = label ?? "event " + EventName(eventKey);
                if (Totals.Count < 200 || Totals.ContainsKey(total))
                    Totals[total] = Totals.TryGetValue(total, out var sum) ? (sum.Count + 1, sum.Ms + ms) : (1, ms);
                if (ms >= RecordMs && Calls.Count < 400)
                {
                    Calls.Insert(Math.Min(index, Calls.Count), (depth, total, ms)); // parents before their children
                }
            }
        }

        private static string EventName(long key) => _eventNames != null && _eventNames.TryGetValue(key, out var name)
            ? name : (key >> 32) + ":" + (key & 0xFFFFFFFFL);

        private static double Kb()
        {
            try { return _collect?.Obf_HuA("count")?[0] is object v ? Convert.ToDouble(v) : -1; }
            catch { return -1; }
        }

        private static int _methods;

        private static void WrapMethods(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            _methods = 0;
            WrapRet(global.Obf_RuA<Obf_R.Obf_ff>("LC_ConditionManager"), "LC_ConditionManager:IsProgressCheckDialogue", "IsProgressCheckDialogue");
            var db = global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
            var dbi = db?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(db);
            var taskConfig = dbi != null && dbi.Length > 0 && dbi[0] is Obf_R.Obf_ff d ? d.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_TaskConfig") : null;
            WrapRet(taskConfig, "TaskConfig:GetTaskProgressTarget", "GetTaskProgressTarget");
            WrapRet(taskConfig, "TaskConfig:GetTaskTrack", "GetTaskTrack");
            var system = LuaPatches.Module(manager, global, "lc_system_task");
            if (system != null)
            {
                WrapTwo(system, "LC_System_Task:SetNpcAtMap", "SetNpcAtMap");
                WrapOne(system, "LC_System_Task:_onLocalPlayerMove", "_onLocalPlayerMove");
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
