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

        private static readonly Dispatch DispatchWrapper = TimedDispatch;
        private static Obf_R.Obf_Ef _dispatch;
        private static Obf_R.Obf_ff _eventClass;
        private static readonly List<(Obf_R.Obf_ff Table, string Name, Obf_R.Obf_Ef Original, object Wrapper)> Wrapped =
            new List<(Obf_R.Obf_ff, string, Obf_R.Obf_Ef, object)>();
        private static Dictionary<long, string> _eventNames;
        private static readonly List<(int Depth, string Name, double Ms)> Calls = new List<(int, string, double)>();
        private static int _depth;
        private static bool _on;

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
                }
                Debug.Log(sb.ToString());
            }
            Calls.Clear();
        }

        private static void Enable()
        {
            var global = LuaPatches.Global();
            var manager = HappyEngine.Base.SingletonMonoBehaviour<LA_LuaManager>.Instance;
            if (global == null || manager == null) { Debug.Log("[Ro3Emu.Hitch] Lua is not up yet"); return; }
            if (_on) Disable();
            _eventNames = EventNames(global);
            _eventClass = LuaPatches.Module(manager, global, "lc_event");
            _dispatch = _eventClass?.Obf_RuA<Obf_R.Obf_Ef>("DispatchEvent");
            if (_dispatch != null) _eventClass.Obf_luA<string, Dispatch>("DispatchEvent", DispatchWrapper);
            var helper = global.Obf_RuA<Obf_R.Obf_ff>("LC_TaskHelper");
            if (helper != null)
            {
                foreach (var name in new[] { "SetClientNPC", "CheckClientNPCShow", "DeleteClientNpcId", "DeleteNpcAtMap",
                    "DeleteMatchMapClientNPC", "SetClientObjectNPC", "SetMatchMapClientNPC", "ClearUpdateTaskTemplateData" })
                    WrapOne(helper, "LC_TaskHelper." + name, name);
                WrapTwo(helper, "LC_TaskHelper.SetNpcAtMap", "SetNpcAtMap");
            }
            _on = true;
            Debug.Log("[Ro3Emu.Hitch] timing on: " + (_dispatch != null ? "events" : "no events (DispatchEvent not found)")
                + ", " + Wrapped.Count + " LC_TaskHelper function(s), " + _eventNames.Count + " event names");
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

        private static void TimedDispatch(object self, object module, object notify, object param)
        {
            var original = _dispatch;
            if (original == null) return;
            long key = LuaPatches.ToLong(module) << 32 | (LuaPatches.ToLong(notify) & 0xFFFFFFFFL);
            Timed(null, () => original.Obf_HuA(self, module, notify, param), key);
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
                if (ms >= RecordMs && Calls.Count < 400)
                {
                    string name = label ?? "event " + (_eventNames != null && _eventNames.TryGetValue(eventKey, out var n)
                        ? n : (eventKey >> 32) + ":" + (eventKey & 0xFFFFFFFFL));
                    Calls.Insert(Math.Min(index, Calls.Count), (depth, name, ms)); // parents before their children
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
