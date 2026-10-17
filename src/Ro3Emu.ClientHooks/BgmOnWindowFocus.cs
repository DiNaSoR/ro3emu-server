using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Clicking inside a PC window no longer restarts the background music.
    /// <para>
    /// Every press inside a window (UIWindow.OnPointerProgress) brings it to the front through
    /// UIWindowManager.SetTopLayer, which fires OnWindowLayerChanged. The game's Lua sets that callback to
    /// Lua_UIPanelManager:TryPopBGMStack -> _refreshBGM -> AD_WwiseManager:CheckPlayBGM(top BGM name), and CheckPlayBGM
    /// posts Play_BGM when more than publicparamconfig.BGMContinuationTime (60 s) passed since SoundPlayTime.LastBGMTime.
    /// Play_BGM (Player_States.bnk) is Stop + Play on the music object, so the track starts over. Seen as: spend a
    /// minute in the skills window, click a skill's +/- (or anywhere in the window), the music jumps to its start.
    /// </para>
    /// <para>
    /// The callback is wrapped: before the game's own one runs, LastBGMTime is set to the current client time, so
    /// CheckPlayBGM sees no gap and only sets the BGM state (BGM_* events are SetState actions, harmless to repeat).
    /// A window with a BGM of its own still switches the state. Opening and closing panels and scene changes keep the
    /// game's behaviour. While LastBGMTime is nil (no music started yet) nothing is changed, so the first Play_BGM still goes out.
    /// </para>
    /// </summary>
    public static class BgmOnWindowFocus
    {
        private static readonly Action Wrapper = OnLayerChanged;
        private static Action _original;
        private static bool _reported;

        /// <summary>From UiTweaks.Tick (every 10th frame): (re)wraps the callback whenever the Lua side sets a new one.</summary>
        public static void Tick()
        {
            var manager = UIWindowManager.Instance;
            var current = manager.OnWindowLayerChanged;
            if (current == null || ReferenceEquals(current, Wrapper)) return;
            _original = current;
            manager.OnWindowLayerChanged = Wrapper;
            Debug.Log("[Ro3Emu.BgmOnWindowFocus] window focus no longer restarts the music");
        }

        private static void OnLayerChanged()
        {
            try { KeepBgmTime(); }
            catch (Exception e)
            {
                if (!_reported) { _reported = true; Debug.LogWarning("[Ro3Emu.BgmOnWindowFocus] " + e.GetType().Name + ": " + e.Message); }
            }
            _original?.Invoke();
        }

        /// <summary>AD_WwiseManager.SoundPlayTime.LastBGMTime = TimerManagerIns:GetSystemTime(), when music already started.</summary>
        private static void KeepBgmTime()
        {
            var global = LuaPatches.Global();
            var times = global?.Obf_LuA<Obf_R.Obf_ff>("AD_WwiseManager.SoundPlayTime");
            if (times == null || times.Obf_RuA<object>("LastBGMTime") == null) return;
            var timers = global.Obf_RuA<object>("TimerManagerIns") as Obf_R.Obf_ff;
            var now = timers?.Obf_RuA<Obf_R.Obf_Ef>("GetSystemTime")?.Obf_HuA(timers);
            if (now == null || now.Length < 1 || now[0] == null) return;
            times.Obf_luA<string, object>("LastBGMTime", now[0]);
        }
    }
}
