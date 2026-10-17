using System;
using System.Text;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// GM "!client audio ...": what Wwise is doing for the local player, written to Player.log.
    ///   audio                   output device, listener and player emitter state
    ///   audio play [event]      posts the event on the player (as the game does for its own sounds)
    ///   audio near [event] [m]  posts it on a temporary emitter m metres (default 2) in front of the player
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
                    Play(parts.Length > 1 ? parts[1] : DefaultEvent, null);
                    break;
                case "near":
                    Play(parts.Length > 1 ? parts[1] : DefaultEvent, parts.Length > 2 ? Float(parts[2], 2f) : 2f);
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

        private static void Info()
        {
            var sb = new StringBuilder("[Ro3Emu.Diag] audio:");
            sb.Append("\n  wwise initialized=").Append(AkUnitySoundEngine.IsInitialized());
            try
            {
                var speakers = AkUnitySoundEngine.GetSpeakerConfiguration();
                sb.Append("\n  main output: channels=").Append(speakers.uNumChannels)
                  .Append(" configType=").Append(speakers.eConfigType)
                  .Append(" mask=0x").Append(speakers.uChannelMask.ToString("X"));
                var cfg = new AkChannelConfig();
                var caps = new Ak3DAudioSinkCapabilities();
                var r = AkUnitySoundEngine.GetOutputDeviceConfiguration(0, cfg, caps);
                sb.Append("\n  device config (").Append(r).Append("): channels=").Append(cfg.uNumChannels)
                  .Append(" configType=").Append(cfg.eConfigType).Append(" mask=0x").Append(cfg.uChannelMask.ToString("X"))
                  .Append(" 3D-sink: maxObjects=").Append(caps.uMaxSystemAudioObjects)
                  .Append(" availableObjects=").Append(caps.uAvailableSystemAudioObjects)
                  .Append(" passthrough=").Append(caps.bPassthrough)
                  .Append(" multiChannelObjects=").Append(caps.bMultiChannelObjects)
                  .Append(" sinkChannels=").Append(caps.channelConfig.uNumChannels)
                  .Append(" sinkConfigType=").Append(caps.channelConfig.eConfigType);
                AkUnitySoundEngine.GetPanningRule(out int panning);
                sb.Append("\n  panning rule=").Append(panning == 0 ? "speakers" : panning == 1 ? "headphones" : panning.ToString());
            }
            catch (Exception e) { sb.Append("\n  output query failed: ").Append(e.Message); }

            var global = AD_WwiseManager.Instance != null ? AD_WwiseManager.Instance.GetGlobalObj() : null;
            if (global != null)
            {
                var listener = global.GetComponent<AD_WwiseListener>();
                sb.Append("\n  listener object ").Append(global.name).Append(" transform=").Append(global.transform.position.ToString("F2"));
                if (listener != null)
                    sb.Append(" reported=").Append(listener.GetPosition().ToString("F2"))
                      .Append(" fwd=").Append(listener.GetForward().ToString("F2"))
                      .Append(" up=").Append(listener.GetUpward().ToString("F2"))
                      .Append(" follows=").Append(listener.GetPlayer() != null ? listener.GetPlayer().name : "-");
                else sb.Append(" (no AD_WwiseListener yet)");
                sb.Append(" akListener=").Append(global.GetComponent<AkAudioListener>() != null);
                AppendEngineTransform(sb, global);
            }
            else sb.Append("\n  no Wwise global object");

            var player = AD_WwisePlayer.globalPlayer;
            if (player != null)
            {
                var ak = player.GetComponent<AkGameObj>();
                sb.Append("\n  player ").Append(player.name).Append(" active=").Append(player.activeInHierarchy)
                  .Append(" pos=").Append(player.transform.position.ToString("F2"))
                  .Append(" fwd=").Append(player.transform.forward.ToString("F2"))
                  .Append(" scale=").Append(player.transform.lossyScale.ToString("F3"));
                if (ak != null)
                    sb.Append(" akGameObj enabled=").Append(ak.enabled).Append(" registered=").Append(ak.GameObjIsRegistered())
                      .Append(" reported=").Append(ak.GetPosition().ToString("F2"))
                      .Append(" defaultListeners=").Append(ak.IsUsingDefaultListeners);
                else sb.Append(" (no AkGameObj: the game has not posted a sound on the player yet)");
                AppendEngineTransform(sb, player);
                AppendPlaying(sb, player);
            }
            else sb.Append("\n  no local player registered with the audio manager");
            Debug.Log(sb.ToString());
        }

        private static void AppendEngineTransform(StringBuilder sb, GameObject go)
        {
            try
            {
                var t = new AkTransform();
                var r = AkUnitySoundEngine.GetPosition(go, t);
                sb.Append("\n    wwise position (").Append(r).Append("): ").Append(t.Position().ToString("F2"))
                  .Append(" fwd=").Append(t.OrientationFront().ToString("F2")).Append(" up=").Append(t.OrientationTop().ToString("F2"));
            }
            catch (Exception e) { sb.Append("\n    wwise position query failed: ").Append(e.Message); }
        }

        private static void AppendPlaying(StringBuilder sb, GameObject go)
        {
            try
            {
                uint n = 32;
                var ids = new uint[n];
                var r = AkUnitySoundEngine.GetPlayingIDsFromGameObject(go, ref n, ids);
                sb.Append("\n    playing on it (").Append(r).Append("): ").Append(n);
            }
            catch (Exception e) { sb.Append("\n    playing query failed: ").Append(e.Message); }
        }

        private static void Play(string evt, float? metres)
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
                AD_WwiseManager.Instance.SetSwitch("Player", "P1", target);
                UnityEngine.Object.Destroy(target, 10f);
            }
            uint id = AD_WwiseManager.Instance.PostEvent(evt, target);
            Debug.Log("[Ro3Emu.Diag] audio " + (metres.HasValue ? "near " + metres.Value.ToString("F1") + "m" : "play") + " " + evt
                      + " on " + target.name + " at " + target.transform.position.ToString("F2") + " -> playingId=" + id
                      + (id == 0 ? " (not posted: unknown event or bank not loaded)" : ""));
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
