using System;
using System.Linq;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// GM "!client audio ...": what Wwise is doing for the local player, written to Player.log.
    ///   audio                   output device, listener and player emitter state
    ///   audio play [event]      posts the event on the player (as the game does for its own sounds)
    ///   audio near [event] [m] [P1|P2|P3]  posts it on a temporary emitter m metres (default 2) from the player
    ///   audio rtpc              the volume sliders and side-chain game parameters
    ///   audio log [off]         turns the game's own debug log on (every sound it posts)
    ///   audio stopall           stops every playing sound
    ///   audio listen [m]        moves the listener m metres (default 1.5) above the player; "listen 0" undoes it
    ///   audio anim              the animation sound events (footsteps, swings) of the player's and two other models
    ///   audio rawdata [file...] copies the named animation data files (as the game loads them) to ro3emu/dump
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
                case "log":
                    // The game's own debug log (off in release builds): "Post Event : <id>" for every sound it posts,
                    // and its own reasons for skipping one ("current 3d num", "csGameObject is nil").
                    HappyEngine.GS_Debug.ShowLog = parts.Length < 2 || parts[1] != "off";
                    Step("game debug log " + (HappyEngine.GS_Debug.ShowLog ? "on" : "off"));
                    TraceSound3D(HappyEngine.GS_Debug.ShowLog);
                    break;
                case "stopall":
                    AD_WwiseManager.Instance.StopAll();
                    Step("stopped every playing sound");
                    break;
                case "listen":
                    Listen(parts.Length > 1 ? Float(parts[1], 1.5f) : 1.5f);
                    break;
                case "anim":
                    Anim();
                    break;
                case "rawdata":
                    RawData(parts);
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

        private const System.Reflection.BindingFlags Any =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        private static object Field(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Any);
                if (f != null) return f.GetValue(o);
            }
            return "(no field)";
        }

        // Footstep and swing sounds are animation events: RD_AnimationState posts the m_kAudioEventList of the
        // state's additional data (loaded from m_kAnimationStateAdditionalDataPath). Shows that data per model.
        private static void Anim()
        {
            var player = AD_WwisePlayer.globalPlayer;
            Transform playerRoot = player != null ? player.transform.root : null;
            if (playerRoot != null)
                foreach (var v in playerRoot.GetComponentsInChildren<RD_Visible_Animator>(true))
                    DescribeAnimator("player", v);
            else Step("anim: no local player");
            int others = 0;
            foreach (var v in UnityEngine.Object.FindObjectsOfType<RD_Visible_Animator>())
            {
                if (playerRoot != null && v.transform.root == playerRoot) continue;
                if (++others > 2) break;
                DescribeAnimator("other", v);
            }
        }

        private static void DescribeAnimator(string who, RD_Visible_Animator v)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("anim ").Append(who).Append(' ').Append(Diagnostics.PathOf(v.transform))
              .Append(" type=").Append(v.GetType().Name)
              .Append(" active=").Append(v.gameObject.activeInHierarchy)
              .Append(" clipPlayable=").Append(Field(v, "m_bUseClipPlayable"))
              .Append(" dots=").Append(v.m_bUseDOTS)
              .Append(" controller=").Append(Field(v, "m_kControllerDataPath"))
              .Append(" additional=").Append(Field(v, "m_kAnimationStateAdditionalDataPath"))
              .Append(" playing=").Append(Field(v, "m_sCurrentPlayingState"));
            var map = Field(v, "m_kAnimationStateAdditionalData") as System.Collections.Generic.Dictionary<string, RD_AnimationStateAdditionalData>;
            if (map == null) sb.Append(" additionalData=NULL");
            else
            {
                sb.Append(" additionalData states=").Append(map.Count);
                int withAudio = 0;
                foreach (var kv in map)
                {
                    var list = kv.Value?.m_kAudioEventList;
                    if (list == null || list.Count == 0) continue;
                    if (++withAudio > 25) continue;
                    sb.Append("\n    ").Append(kv.Key).Append(':');
                    foreach (var e in list)
                        sb.Append(' ').Append(e.m_kAudioResNameList.Count > 0 ? e.m_kAudioResNameList[0] : "-")
                          .Append('@').Append(e.m_fTriggerStartTime.ToString("F2"));
                }
                sb.Append("\n    states with sounds: ").Append(withAudio);
            }
            Step(sb.ToString());
        }

        private static readonly string[] DefaultRawData =
        {
            "model_body_magician_f_stateadditionaldata.bytes", "model_body_novice_m_stateadditionaldata.bytes",
            "model_monsterjunior_pupalow_stateadditionaldata.bytes",
        };

        private static void RawData(string[] parts)
        {
            var names = parts.Length > 1 ? parts.Skip(1).ToArray() : DefaultRawData;
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "ro3emu", "dump");
            System.IO.Directory.CreateDirectory(dir);
            foreach (var name in names)
            {
                byte[] bytes = RD_RenderSetting.m_kResourceLoder?.LoadUserData(name);
                if (bytes == null) { Step("rawdata " + name + ": not found"); continue; }
                string file = System.IO.Path.Combine(dir, name);
                System.IO.File.WriteAllBytes(file, bytes);
                Step("rawdata " + name + ": " + bytes.Length + " bytes -> " + file);
            }
        }

        // The game's 3D sounds (skills, hits) go through Lua AD_WwiseManager:PlaySound3D(id, go, ...). While the debug
        // log is on, that method is wrapped to log which object each sound is posted on, and where.
        public delegate object Sound3DMethod(object self, object id, object go, object a3, object a4, object a5, object a6);
        private static readonly Sound3DMethod Sound3DWrapper = Sound3D;
        private static Obf_R.Obf_ff _wwiseTable;
        private static Obf_R.Obf_Ef _sound3DOriginal;

        private static void TraceSound3D(bool on)
        {
            var global = LuaPatches.Global();
            var table = global?.Obf_RuA<Obf_R.Obf_ff>("AD_WwiseManager");
            if (table == null) { Step("sound trace: AD_WwiseManager not loaded"); return; }
            if (on && _sound3DOriginal == null)
            {
                _sound3DOriginal = table.Obf_RuA<Obf_R.Obf_Ef>("PlaySound3D");
                if (_sound3DOriginal == null) { Step("sound trace: PlaySound3D not found"); return; }
                _wwiseTable = table;
                table.Obf_luA<string, Sound3DMethod>("PlaySound3D", Sound3DWrapper);
                Step("sound trace on");
            }
            else if (!on && _sound3DOriginal != null)
            {
                _wwiseTable.Obf_luA<string, Obf_R.Obf_Ef>("PlaySound3D", _sound3DOriginal);
                _sound3DOriginal = null;
                Step("sound trace off");
            }
        }

        private static object Sound3D(object self, object id, object go, object a3, object a4, object a5, object a6)
        {
            try
            {
                var sb = new System.Text.StringBuilder("sound3d ").Append(id);
                Transform t = go as Transform ?? (go as GameObject)?.transform ?? (go as Component)?.transform;
                if (t == null) sb.Append(" on ").Append(go == null ? "nil" : go.GetType().Name);
                else
                {
                    sb.Append(" on ").Append(Diagnostics.PathOf(t)).Append(" active=").Append(t.gameObject.activeInHierarchy)
                      .Append(" pos=").Append(t.position.ToString("F2"));
                    var player = AD_WwisePlayer.globalPlayer;
                    if (player != null) sb.Append(" fromPlayer=").Append(Vector3.Distance(player.transform.position, t.position).ToString("F2"));
                    var ak = t.GetComponent<AkGameObj>();
                    sb.Append(" akGameObj=").Append(ak != null);
                }
                sb.Append(" args=").Append(a3).Append(',').Append(a4).Append(',').Append(a5).Append(',').Append(a6);
                var r = _sound3DOriginal?.Obf_HuA(self, id, go, a3, a4, a5, a6);
                sb.Append(" -> ").Append(r != null && r.Length > 0 ? r[0] : "-");
                Step(sb.ToString());
                return r != null && r.Length > 0 ? r[0] : null;
            }
            catch (Exception e) { Step("sound3d trace failed: " + e.Message); return null; }
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
