using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The game's Lua log calls (Lua_Debug.LogFormat / Log / LogForce / LogWarning) no longer append a Lua stack
    /// traceback, which cost 25-30 ms per line and made the frame hitch when an enemy died.
    /// <para>
    /// Each of those functions runs debug.traceback over the message before handing it to GS_Debug. The client's Lua is
    /// stripped, so for every frame of the stack Lua 5.4 looks the function up by value in all of package.loaded to find
    /// a name, and the game has thousands of modules. Called from deep in the quest update (each kill syncs the quest list,
    /// and LC_TaskHelper.CheckClientNPCShow / SetMatchMapClientNPC log a line per quest NPC), that was 26-32 ms per line,
    /// 5-11 lines per kill (the hitch profiler's numbers). From a shallow stack the same call takes 0.15 ms.
    /// </para>
    /// <para>
    /// The four functions are replaced by C# delegates that do what the originals do minus the traceback: same gating
    /// (Lua_Debug.ShowLog, the force flag, errors always), same prefixes, same GS_Debug calls. Lua_Debug.LogError keeps
    /// its traceback, as errors are rare and the stack is what makes them useful.
    /// </para>
    /// </summary>
    public static class LuaLogs
    {
        // More than any log call passes; string.Format ignores extra (null) arguments.
        public delegate void Args(object a, object b, object c, object d, object e, object f, object g, object h,
            object i, object j, object k, object l);

        private static readonly Args FormatWrapper = (a, b, c, d, e, f, g, h, i, j, k, l) =>
            Format(a, b, c, new[] { d, e, f, g, h, i, j, k, l });
        private static readonly Args LogWrapper = (a, b, c, d, e, f, g, h, i, j, k, l) =>
            Plain("[Lua_Debug:Log]", true, LogType.Log, a, b, c, d, e, f, g, h, i, j, k, l);
        private static readonly Args ForceWrapper = (a, b, c, d, e, f, g, h, i, j, k, l) =>
            Plain("[Lua_Debug:Log]", false, LogType.Log, a, b, c, d, e, f, g, h, i, j, k, l);
        private static readonly Args WarningWrapper = (a, b, c, d, e, f, g, h, i, j, k, l) =>
            Plain("[Lua_Debug:LogWarning]", true, LogType.Warning, a, b, c, d, e, f, g, h, i, j, k, l);

        private static Obf_R.Obf_ff _table;
        private static Obf_R.Obf_Ef _argToString;
        private static long _errorType = (long)LogType.Error;
        private static object _doneEnv;

        /// <summary>From LuaPatches.Tick once its patches are in: installs once per LuaEnv, when lua_debug is loaded.</summary>
        internal static void Probe(object env, Obf_R.Obf_ff global)
        {
            if (ReferenceEquals(env, _doneEnv) || global == null) return;
            var table = global.Obf_LuA<Obf_R.Obf_ff>("package.loaded.lua_debug");
            if (table == null) return;
            _doneEnv = env;
            try
            {
                _table = table;
                _argToString = table.Obf_RuA<Obf_R.Obf_Ef>("ArgToString");
                long error = LuaPatches.ToLong(global.Obf_LuA<object>("enum_LogType.Error"));
                if (error != long.MinValue) _errorType = error;
                if (_argToString == null || table.Obf_RuA<Obf_R.Obf_Ef>("LogFormat") == null)
                {
                    Debug.LogWarning("[Ro3Emu.LuaLogs] not installed (Lua_Debug.ArgToString/LogFormat not found)");
                    return;
                }
                table.Obf_luA<string, Args>("LogFormat", FormatWrapper);
                table.Obf_luA<string, Args>("Log", LogWrapper);
                table.Obf_luA<string, Args>("LogForce", ForceWrapper);
                table.Obf_luA<string, Args>("LogWarning", WarningWrapper);
                Debug.Log("[Ro3Emu.LuaLogs] Lua log lines without the stack traceback");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.LuaLogs] " + e.GetType().Name + ": " + e.Message); }
        }

        private static bool ShowLog() => !(_table.Obf_RuA<object>("ShowLog") is bool show) || show;

        private static string ArgString(params object[] args)
        {
            var result = _argToString.Obf_HuA(args);
            return result != null && result.Length > 0 ? result[0] as string ?? "" : "";
        }

        /// <summary>Lua_Debug.LogFormat(type, force, format, ...).</summary>
        private static void Format(object type, object force, object format, object[] args)
        {
            try
            {
                long kind = LuaPatches.ToLong(type);
                if (kind == long.MinValue) kind = (long)LogType.Log;
                // As the original: skipped only when ShowLog and force are both explicitly false, errors excepted.
                if (!ShowLog() && force is bool f && !f && kind != _errorType) return;
                string text = "[Lua_Debug:LogFormat]" + ArgString(format);
                HappyEngine.GS_Debug.LogFormat((LogType)(int)kind, force is bool b && b, text, args);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.LuaLogs] LogFormat: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>Lua_Debug.Log / LogForce / LogWarning(...): the arguments joined by ArgToString.</summary>
        private static void Plain(string prefix, bool gated, LogType type, params object[] args)
        {
            try
            {
                if (gated && !ShowLog()) return;
                int n = args.Length;
                while (n > 0 && args[n - 1] == null) n--; // trailing nils are padding, not arguments
                Array.Resize(ref args, n);
                string text = prefix + ArgString(args);
                if (type == LogType.Warning) HappyEngine.GS_Debug.LogWarning((object)text);
                else HappyEngine.GS_Debug.Log((object)text);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.LuaLogs] " + prefix + ": " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
