using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Footstep, swing and chant sounds for player characters. The game plays them as animation events:
    /// RD_AnimationState posts the m_kAudioEventList of the state's RD_AnimationStateAdditionalData, loaded per model
    /// from &lt;model&gt;_stateadditionaldata.bytes. In this client build every character body's file is a 5-byte stub
    /// (an empty map) while monster files hold their cues, so a character never makes a sound of its own.
    /// This puts cues back into those empty maps, using events the banks still carry (Common.bnk):
    /// <list type="bullet">
    /// <item>walking and running: Sfx_Player_Common_Walking_Loop (a random footstep, despite the name), twice a cycle;</item>
    /// <item>Chant (the cast bar): Sfx_SKill_Common_ChantStart;</item>
    /// <item>normal attacks: Sfx_Player_Common_Skill_NormalAttack_&lt;weapon&gt;, the weapon guessed from the job.</item>
    /// </list>
    /// The cue timings and the job-to-weapon table are our choice, not the original data.
    /// </summary>
    public static class AnimationSounds
    {
        private const string Footstep = "Sfx_Player_Common_Walking_Loop";
        private const string ChantStart = "Sfx_SKill_Common_ChantStart";
        private const string SwingPrefix = "Sfx_Player_Common_Skill_NormalAttack";

        private static readonly string[] MoveStates = { "Walking", "Running0", "Running1", "Fmove0", "Fmove1", "FMove0", "FMove1" };
        private static readonly string[] AttackStates = { "Attacking0", "Attacking1", "Attacking1_1", "Attacking1_2", "Attacking1_3", "HitAttack0" };

        // Model_Body_<Job>_<F|M>: the job's usual weapon.
        private static readonly Dictionary<string, string> JobWeapon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Novice", "_Dagger" }, { "Thief", "_Dagger" }, { "Assassin", "_Katar" }, { "Rogue", "_Dagger" },
            { "Swordman", "_Sword" }, { "Swordsman", "_Sword" }, { "Knight", "_Sword" }, { "Crusader", "_Sword" }, { "RuneKnight", "_Spear_A" },
            { "Magician", "_Rod" }, { "Mage", "_Rod" }, { "Wizard", "_Rod" }, { "Sage", "_Book" }, { "Warlock", "_Rod" },
            { "Archer", "_Bow" }, { "Hunter", "_Bow" }, { "Ranger", "_Bow" }, { "Bard", "_Bow" }, { "Dancer", "_Bow" },
            { "Acolyte", "_Blunt" }, { "Priest", "_Blunt" }, { "Monk", "_Fist" },
            { "Merchant", "_Axe" }, { "Blacksmith", "_Axe" }, { "Alchemist", "_Axe" },
        };

        private const System.Reflection.BindingFlags Any =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        private static readonly System.Reflection.FieldInfo MapField = typeof(RD_Visible_Animator).GetField("m_kAnimationStateAdditionalData", Any);
        private static readonly System.Reflection.MethodInfo LengthMethod = typeof(RD_Visible_Animator).GetMethod("GetAnimationLength", Any, null, new[] { typeof(string) }, null);

        // Maps already filled. They are shared by every model of one kind (the pool hands out one per file) and
        // replaced when the pool reloads the file, so a new empty map is filled again.
        private static readonly HashSet<object> Filled = new HashSet<object>();
        private static int _frame;
        private static bool _failed;

        /// <summary>From UiTweaks.Tick, every frame; scans the loaded models twice a second.</summary>
        public static void Tick()
        {
            if (_failed || ++_frame % 30 != 0) return;
            try
            {
                foreach (var v in UnityEngine.Object.FindObjectsOfType<RD_Visible_UniversalAnimator>())
                {
                    string name = v.gameObject.name;
                    if (!name.StartsWith("Model_Body_", StringComparison.Ordinal)) continue;
                    var map = MapField.GetValue(v) as Dictionary<string, RD_AnimationStateAdditionalData>;
                    if (map == null)
                    {
                        map = new Dictionary<string, RD_AnimationStateAdditionalData>();
                        MapField.SetValue(v, map);
                    }
                    if (map.Count > 0 || !Filled.Add(map)) continue; // real cues, or already ours
                    if (Filled.Count > 512) { Filled.Clear(); Filled.Add(map); }
                    Fill(v, name, map);
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Debug.LogWarning("[Ro3Emu.AnimationSounds] off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Fill(RD_Visible_Animator v, string name, Dictionary<string, RD_AnimationStateAdditionalData> map)
        {
            foreach (var state in MoveStates)
            {
                float length = Length(v, state);
                if (length <= 0) continue;
                map[state] = Cues(Cue(Footstep, 0f), Cue(Footstep, length * 0.5f));
            }
            if (Length(v, "Chant") > 0) map["Chant"] = Cues(Cue(ChantStart, 0f));
            string swing = SwingPrefix + Weapon(name);
            foreach (var state in AttackStates)
                if (Length(v, state) > 0) map[state] = Cues(Cue(swing, 0.05f));
            Debug.Log("[Ro3Emu.AnimationSounds] " + name + ": " + map.Count + " animation(s) given sounds (swing " + swing + ")");
        }

        private static string Weapon(string modelName)
        {
            // Model_Body_Magician_F_dots(Clone) -> Magician
            var parts = modelName.Split('_');
            return parts.Length > 2 && JobWeapon.TryGetValue(parts[2], out var weapon) ? weapon : "";
        }

        private static float Length(RD_Visible_Animator v, string state)
        {
            try { return LengthMethod != null ? (float)LengthMethod.Invoke(v, new object[] { state }) : -1f; }
            catch { return -1f; }
        }

        private static RD_AnimationStateAdditionalData Cues(params RD_Animation_Event_AttachAudio[] cues) =>
            new RD_AnimationStateAdditionalData { m_kAudioEventList = new List<RD_Animation_Event_AttachAudio>(cues) };

        // Posted once per animation cycle at `start` seconds in. A long trigger window, so the sound is never stopped
        // mid-way; loop-triggered, so it fires again each cycle and is not cut when the animation ends.
        private static RD_Animation_Event_AttachAudio Cue(string evt, float start)
        {
            var cue = new RD_Animation_Event_AttachAudio { m_bLoopTrigger = true, m_fTriggerStartTime = start, m_fTriggerLength = 30f };
            cue.m_kAudioResNameList.Add(evt);
            return cue;
        }
    }
}
