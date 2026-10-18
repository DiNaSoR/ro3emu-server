using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Removes the "Don't just stand there—get back to your adventure!" prompt. It is a client-only tutorial guide:
    /// GuideConfig 199 (_iCondition 15 = idle, _kValue "8" seconds, _iType 2 = repeated; PC step 1001990, text
    /// 10540000242) points at the quest tracker whenever the player has given no input for 8 s, until base level
    /// publicparamconfig.GuideIdleDurationStopLevel (30). LC_GuideManager._onUpdateIdleGuide counts the idle time each
    /// frame and calls self:_tryTriggerIdleGuide() once it passes the guide's value. A repeated guide ignores the
    /// finished list the server sends (5055), so the server cannot turn it off. The method is replaced through the class
    /// table (shared by the live instance, looked up on every call) by one that only restarts the idle timer, so it runs
    /// once per idle stretch instead of every frame. Guide 199 is the only idle guide, so no other guide is affected.
    /// </summary>
    public static class IdlePrompt
    {
        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly LuaPatches.SelfMethod TriggerWrapper = SkipIdleGuide;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff guideManager)
        {
            try
            {
                if (guideManager?.Obf_RuA<Obf_R.Obf_Ef>("_tryTriggerIdleGuide") == null)
                {
                    Debug.LogWarning("[Ro3Emu.IdlePrompt] LC_GuideManager._tryTriggerIdleGuide not found; the idle prompt stays");
                    return;
                }
                guideManager.Obf_luA<string, LuaPatches.SelfMethod>("_tryTriggerIdleGuide", TriggerWrapper);
                Debug.Log("[Ro3Emu.IdlePrompt] idle guide (\"Don't just stand there\") disabled");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.IdlePrompt] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void SkipIdleGuide(Obf_R.Obf_ff self)
        {
            try { self?.Obf_luA<string, object>("m_fIdleGuideTimer", 0d); }
            catch (Exception) { /* the guide stays off either way */ }
        }
    }
}
