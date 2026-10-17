using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The element strength/weakness word ("+25%" in the element's colour) shows once per burst of hits on a target
    /// instead of with every hit.
    /// <para>
    /// LC_DamageHelper.ProcessDamageWord splits a hit into its shown hits (SkillHitInfo.split: Fire Bolt level 10 shows
    /// 10) and gives each one its own element word (L32_1, from EleConfig:GetElementAffect), drawn by _ShowDamageValue
    /// with an enum_HudWordType.Element_* word type next to the damage number. That is the client's own behaviour, but a
    /// 10-bolt cast stacks ten identical words. _ShowDamageValue is wrapped: an element word for the same attacker,
    /// target and word type within <see cref="QuietSeconds"/> of the previous one is skipped (the damage numbers still all
    /// show). Each skipped word moves the window on, so one cast or one attack chain shows it once.
    /// </para>
    /// </summary>
    public static class ElementWords
    {
        private const float QuietSeconds = 1.0f;

        public delegate void ShowValue(object self, object a1, object source, object target, object value, object wordType);

        private static readonly ShowValue Wrapper = Show;
        private static Obf_R.Obf_Ef _original;
        private static readonly HashSet<long> ElementTypes = new HashSet<long>();
        private static readonly Dictionary<(long, long, long), float> LastShown = new Dictionary<(long, long, long), float>();

        private static object _doneEnv;

        /// <summary>From LuaPatches.Tick (every 30th frame) once its patches are in: installs once per LuaEnv, as soon as
        /// the game has loaded LC_DamageHelper itself (it is not required early here: it reads its configs when loaded).</summary>
        internal static void Probe(object env, Obf_R.Obf_ff global)
        {
            if (ReferenceEquals(env, _doneEnv) || global == null) return;
            var helper = global.Obf_LuA<Obf_R.Obf_ff>("package.loaded.lc_damagehelper");
            if (helper == null) return;
            _doneEnv = env;
            try { Install(global, helper); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.ElementWords] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff helper)
        {
            ElementTypes.Clear();
            LastShown.Clear();
            var types = global.Obf_RuA<Obf_R.Obf_ff>("enum_HudWordType");
            types?.Obf_OuA<string, object>((name, value) =>
            {
                long id = LuaPatches.ToLong(value);
                if (name.StartsWith("Element_", StringComparison.Ordinal) && id != long.MinValue) ElementTypes.Add(id);
            });
            _original = helper?.Obf_RuA<Obf_R.Obf_Ef>("_ShowDamageValue");
            if (_original == null || ElementTypes.Count == 0)
            {
                Debug.LogWarning("[Ro3Emu.ElementWords] not installed (" + (_original == null ? "_ShowDamageValue" : "enum_HudWordType") + " not found)");
                return;
            }
            helper.Obf_luA<string, ShowValue>("_ShowDamageValue", Wrapper);
            Debug.Log("[Ro3Emu.ElementWords] element words show once per burst (" + ElementTypes.Count + " word types)");
        }

        private static void Show(object self, object a1, object source, object target, object value, object wordType)
        {
            try
            {
                long type = LuaPatches.ToLong(wordType);
                if (ElementTypes.Contains(type))
                {
                    var key = (LuaPatches.ToLong(source), LuaPatches.ToLong(target), type);
                    float now = Time.time;
                    bool recent = LastShown.TryGetValue(key, out float last) && now - last < QuietSeconds;
                    LastShown[key] = now;
                    if (LastShown.Count > 256) LastShown.Clear();
                    if (recent) return;
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.ElementWords] " + e.GetType().Name + ": " + e.Message); }
            _original.Obf_HuA(self, a1, source, target, value, wordType);
        }
    }
}
