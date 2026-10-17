using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// GM "!client audio ...": what Wwise is doing for the local player, written to Player.log.
    ///   audio                   output device, listener and player emitter state
    ///   audio play [event]      posts the event on the player (as the game does for its own sounds)
    ///   audio near [event] [m] [P1|P2|P3]  posts it on a temporary emitter m metres (default 2) from the player
    ///   audio rtpc              the volume sliders and side-chain game parameters
    ///   audio stopall           stops every playing sound
    ///   audio listen [m]        moves the listener m metres (default 1.5) above the player; "listen 0" undoes it
    /// The game puts the Wwise listener exactly on the local player, so the player's own sounds play at distance 0;
    /// "near" and "listen" tell whether that is what silences them.
    /// </summary>
    internal static class AudioDiagnostics
    {
        // A sound the local player makes (Common.bnk, routed to PLAYER_SKILL_P1).
        private const string DefaultEvent = "Sfx_Player_Common_Recover_MP_P1";

        private static GameObject _listenTarget;

        public static void Run(string args)
        {
            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0] : "";
            switch (sub)
            {
                case "":
                    Info();
                    break;
                case "play":
                    Play(parts.Length > 1 ? parts[1] : DefaultEvent, null, "P1");
                    break;
                case "near":
                    Play(parts.Length > 1 ? parts[1] : DefaultEvent, parts.Length > 2 ? Float(parts[2], 2f) : 2f, parts.Length > 3 ? parts[3] : "P1");
                    break;
                case "rtpc":
                    Rtpc();
                    break;
                case "stopall":
                    AD_WwiseManager.Instance.StopAll();
                    Step("stopped every playing sound");
                    break;
                case "listen":
                    Listen(parts.Length > 1 ? Float(parts[1], 1.5f) : 1.5f);
                    break;
                default:
                    Debug.Log("[Ro3Emu.Diag] audio: unknown subcommand " + sub + " (audio | audio play [event] | audio near [event] [m] | audio listen [m])");
                    break;
            }
        }

        private static float Float(string s, float fallback) =>
            float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

        // One line per step, written before the next native call, so a crash inside Wwise still shows how far it got.
        private static void Step(string text) => Debug.Log("[Ro3Emu.Diag] audio: " + text);

        private static void Info()
        {
            Step("wwise initialized=" + AkUnitySoundEngine.IsInitialized());
            var global = AD_WwiseManager.Instance != null ? AD_WwiseManager.Instance.GetGlobalObj() : null;
            if (global != null)
            {
                var listener = global.GetComponent<AD_WwiseListener>();
                Step("listener object " + global.name + " transform=" + global.transform.position.ToString("F2")
                     + (listener != null
                         ? " reported=" + listener.GetPosition().ToString("F2") + " fwd=" + listener.GetForward().ToString("F2")
                           + " up=" + listener.GetUpward().ToString("F2") + " follows=" + (listener.GetPlayer() != null ? listener.GetPlayer().name : "-")
                         : " (no AD_WwiseListener yet)")
                     + " akListener=" + (global.GetComponent<AkAudioListener>() != null));
            }
            else Step("no Wwise global object");

            var player = AD_WwisePlayer.globalPlayer;
            if (player != null)
            {
                var ak = player.GetComponent<AkGameObj>();
                Step("player " + player.name + " active=" + player.activeInHierarchy + " pos=" + player.transform.position.ToString("F2")
                     + " fwd=" + player.transform.forward.ToString("F2") + " scale=" + player.transform.lossyScale.ToString("F3")
                     + (ak != null
                         ? " akGameObj enabled=" + ak.enabled + " registered=" + ak.GameObjIsRegistered()
                           + " reported=" + ak.GetPosition().ToString("F2") + " defaultListeners=" + ak.IsUsingDefaultListeners
                         : " (no AkGameObj yet)"));
            }
            else Step("no local player registered with the audio manager");

            Step("querying the output device");
            var speakers = AkUnitySoundEngine.GetSpeakerConfiguration();
            Step("main output: channels=" + speakers.uNumChannels + " configType=" + speakers.eConfigType + " mask=0x" + speakers.uChannelMask.ToString("X"));
            AkUnitySoundEngine.GetPanningRule(out int panning);
            Step("panning rule=" + (panning == 0 ? "speakers" : panning == 1 ? "headphones" : panning.ToString()));
        }

        private static void Play(string evt, float? metres, string playerSwitch)
        {
            var player = AD_WwisePlayer.globalPlayer;
            if (player == null || AD_WwiseManager.Instance == null) { Debug.Log("[Ro3Emu.Diag] audio play: no local player"); return; }
            GameObject target = player;
            if (metres.HasValue)
            {
                var cam = Camera.main;
                var dir = cam != null ? -cam.transform.forward : player.transform.forward;
                dir.y = 0;
                if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
                target = new GameObject("Ro3Emu.AudioProbe");
                target.transform.position = player.transform.position + dir.normalized * metres.Value;
                target.transform.rotation = player.transform.rotation;
                // Same switch the game sets on the local player, so the P1 variant plays.
                AD_WwiseManager.Instance.SetSwitch("Player", playerSwitch, target);
                UnityEngine.Object.Destroy(target, 10f);
            }
            uint id = AD_WwiseManager.Instance.PostEvent(evt, target);
            if (metres.HasValue) evt += " switch=" + playerSwitch;
            Debug.Log("[Ro3Emu.Diag] audio " + (metres.HasValue ? "near " + metres.Value.ToString("F1") + "m" : "play") + " " + evt
                      + " on " + target.name + " at " + target.transform.position.ToString("F2") + " -> playingId=" + id
                      + (id == 0 ? " (not posted: unknown event or bank not loaded)" : ""));
        }

        // The game parameters that scale the player buses (side-chain meters) and the volume sliders.
        private static readonly string[] RtpcNames =
        {
            "RPTC_Volume_Master", "RPTC_Volume_SFX", "RPTC_Volume_BGM", "RPTC_Volume_VO",
            "SC_BusVolume_Boss_skill", "SC_BusVolume_Boss_hit", "SC_BusVolume_MVP_hit", "SC_BusVolume_MVP_skill",
            "SC_BusVolume_Player_skill_P1", "SC_BusVolume_Player_skill_P3", "SC_BusVolume_Player_hit_P2", "SC_BusVolume_Player_hit_P3",
            "SC_BusVolume_Monster_skill", "SC_BusVolume_UI_Combat", "SC_BusVolume_VO_2D",
        };

        private static void Rtpc()
        {
            foreach (var name in RtpcNames)
            {
                bool ok = AD_WwiseManager.Instance.GetRTPCValue(name, out var value, null);
                Step("rtpc " + name + " = " + (ok ? value.ToString("F2") : "(query failed)"));
            }
        }

        private static void Listen(float metres)
        {
            var mgr = AD_WwiseManager.Instance;
            var player = AD_WwisePlayer.globalPlayer;
            if (mgr == null) return;
            if (_listenTarget != null)
            {
                mgr.RemoveListenerTarget();
                UnityEngine.Object.Destroy(_listenTarget);
                _listenTarget = null;
            }
            if (metres > 0 && player != null)
            {
                _listenTarget = new GameObject("Ro3Emu.ListenerOffset");
                _listenTarget.transform.SetParent(player.transform, false);
                _listenTarget.transform.localPosition = new Vector3(0, metres, 0);
                mgr.SetListenerTarget(_listenTarget);
            }
            Debug.Log("[Ro3Emu.Diag] audio listen: listener " + (_listenTarget != null ? metres.ToString("F1") + "m above the player" : "back on the player"));
        }
    }
}
