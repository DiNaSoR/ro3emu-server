using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// What a Timeline cutscene asks the audio engine for, written to Player.log when it starts.
    /// Story cutscenes of type Timeline (PlotInterpretation _iType 2, e.g. 130011, the Novice job change in
    /// Tritura Academy) run under TL_Manager's "TlManager" object as "Timeline_&lt;id&gt;" with a PlayableDirector.
    /// LC_CutSceneManager posts Ctrl_Mute_InGame when one starts (UI_Common.bnk: -120 dB on SFX_InGame, BGM,
    /// VO and UI_Amb), so only SFX_CutScene, the UI buses and video audio (VideoInput, straight to the master bus)
    /// are left audible while it plays.
    /// The dump lists every track with its binding and the notification receivers on that binding, every clip
    /// and marker with its settings (Wwise event names included), and the Wwise components in the cutscene's
    /// objects, each Wwise event with its bank and whether that bank is loaded.
    /// "!client audio cutscene [id]" plays a story again (default 130011) to get the dump on demand.
    /// TL_SignalWwise markers sit on tracks whose bound object receives them; in this build only TL_Receiver posts
    /// them (TL_ReceiverWwise.OnNotify is empty, and TL_Manager does not bind TL_TrackWwise tracks), so a marker whose
    /// track has no TL_Receiver on its binding is posted from here when the director's time passes it.
    /// </summary>
    internal static class CutsceneAudio
    {
        private const string ManagerName = "TlManager";
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly HashSet<int> Seen = new HashSet<int>();

        // TL_SignalWwise markers nothing would post, per playing director.
        private sealed class Pending
        {
            public PlayableDirector Director;
            public readonly List<KeyValuePair<double, string>> Markers = new List<KeyValuePair<double, string>>();
            public double LastTime;
        }
        private static readonly List<Pending> Watching = new List<Pending>();
        private static int _frame;
        private static bool _failed;

        /// <summary>From UiTweaks.Tick, every frame; looks for a newly started cutscene three times a second.</summary>
        public static void Tick()
        {
            if (_failed) return;
            try
            {
                if (Watching.Count > 0) PostPassedMarkers();
                if (++_frame % 20 != 0) return;
                var root = GameObject.Find(ManagerName);
                if (root == null) return;
                foreach (Transform child in root.transform)
                {
                    var director = child.GetComponent<PlayableDirector>();
                    if (director == null || director.state != PlayState.Playing) continue;
                    if (!Seen.Add(director.GetInstanceID())) continue;
                    if (Seen.Count > 256) { Seen.Clear(); Seen.Add(director.GetInstanceID()); }
                    Dump(director);
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Debug.LogWarning("[Ro3Emu.CutsceneAudio] off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>"!client audio cutscene [id]": plays a story cutscene (LC_InteractActions.PlayStoryWithDynamicModel,
        /// local player only, so the job-change cutscene does not send a job change at its end).</summary>
        public static void Play(string idText)
        {
            long id = long.TryParse(idText, out var v) ? v : 130011;
            var global = LuaPatches.Global();
            if (global == null) { Debug.Log("[Ro3Emu.CutsceneAudio] play: Lua not ready"); return; }
            var actions = global.Obf_LuA<Obf_R.Obf_ff>("package.loaded.lc_interactactions");
            if (actions == null)
            {
                var manager = HappyEngine.Base.SingletonMonoBehaviour<LA_LuaManager>.Instance;
                var loaded = manager != null ? manager.Obf_MD("lc_interactactions") : null;
                actions = loaded != null && loaded.Length > 0 ? loaded[0] as Obf_R.Obf_ff : null;
            }
            var play = actions?.Obf_RuA<Obf_R.Obf_Ef>("PlayStoryWithDynamicModel");
            if (play == null) { Debug.Log("[Ro3Emu.CutsceneAudio] play: PlayStoryWithDynamicModel not found"); return; }
            Debug.Log("[Ro3Emu.CutsceneAudio] play: story " + id);
            play.Obf_HuA(id, 1L);
        }

        private static void Dump(PlayableDirector director)
        {
            var sb = new StringBuilder();
            var timeline = director.playableAsset as TimelineAsset;
            var pending = new Pending { Director = director, LastTime = director.time };
            sb.Append("[Ro3Emu.CutsceneAudio] ").Append(director.name).Append(" started: asset=")
              .Append(director.playableAsset != null ? director.playableAsset.name : "null")
              .Append(" duration=").Append(director.duration.ToString("F1")).Append("s mode=").Append(director.timeUpdateMode);
            Receivers(sb, "director object", director.gameObject);
            if (timeline != null)
            {
                if (timeline.markerTrack != null) Track(sb, director, timeline.markerTrack, 1, null);
                foreach (var track in timeline.GetRootTracks()) Track(sb, director, track, 1, pending);
            }
            else sb.Append("\n  (not a TimelineAsset)");

            sb.Append("\n  wwise components:");
            int n = 0;
            foreach (var c in director.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string type = c.GetType().Name;
                if (!(type.StartsWith("Ak", StringComparison.Ordinal) || type.StartsWith("AD_Wwise", StringComparison.Ordinal)
                      || type.StartsWith("TL_Receiver", StringComparison.Ordinal))) continue;
                if (++n > 40) { sb.Append("\n    ..."); break; }
                sb.Append("\n    ").Append(Path(c.transform, director.transform)).Append(' ').Append(type);
                Describe(sb, c);
            }
            if (n == 0) sb.Append(" none");
            Banks(sb);
            if (pending.Markers.Count > 0)
            {
                pending.Markers.Sort((a, b) => a.Key.CompareTo(b.Key));
                Watching.Add(pending);
                sb.Append("\n  ").Append(pending.Markers.Count).Append(" Wwise marker(s) have no receiver that posts them; posting them here");
            }
            Debug.Log(sb.ToString());
        }

        /// <param name="pending">Collects this track's unreceived Wwise markers; null for the timeline's own marker
        /// track (its markers go to the director's object, which TL_Manager gives a TL_Receiver).</param>
        private static void Track(StringBuilder sb, PlayableDirector director, TrackAsset track, int depth, Pending pending)
        {
            string pad = "\n" + new string(' ', depth * 2);
            var binding = director.GetGenericBinding(track);
            sb.Append(pad).Append("track ").Append(track.GetType().Name).Append(" '").Append(track.name).Append('\'')
              .Append(track.muted ? " MUTED" : "")
              .Append(" binding=").Append(binding == null ? "none" : binding.GetType().Name + " " + binding.name);
            var bound = binding as GameObject ?? (binding as Component)?.gameObject;
            if (bound != null) Receivers(sb, "bound object", bound);
            bool received = pending == null || (bound != null && bound.GetComponent<TL_Receiver>() != null);
            foreach (var clip in track.GetClips())
            {
                sb.Append(pad).Append("  clip ").Append(clip.start.ToString("F2")).Append('+').Append(clip.duration.ToString("F2"))
                  .Append(' ').Append(clip.asset != null ? clip.asset.GetType().Name : "null").Append(" '").Append(clip.displayName).Append('\'');
                if (clip.asset != null) Describe(sb, clip.asset);
            }
            foreach (var marker in track.GetMarkers())
            {
                sb.Append(pad).Append("  marker ").Append(marker.time.ToString("F2")).Append(' ').Append(marker.GetType().Name);
                Describe(sb, marker);
                if (marker is TL_SignalWwise signal && !string.IsNullOrEmpty(signal.m_kWwiseEvent))
                {
                    sb.Append(' ').Append(EventBank(signal.m_kWwiseEvent));
                    if (!received)
                    {
                        pending.Markers.Add(new KeyValuePair<double, string>(marker.time, signal.m_kWwiseEvent));
                        sb.Append(" (no receiver: posted by Ro3Emu)");
                    }
                }
            }
            foreach (var child in track.GetChildTracks()) Track(sb, director, child, depth + 1, pending);
        }

        /// <summary>Posts the unreceived markers the playing directors passed since the last frame, as TL_Receiver does.</summary>
        private static void PostPassedMarkers()
        {
            for (int i = Watching.Count - 1; i >= 0; i--)
            {
                var p = Watching[i];
                if (p.Director == null || p.Director.state != PlayState.Playing) { Watching.RemoveAt(i); continue; }
                double now = p.Director.time;
                if (now < p.LastTime) p.LastTime = -1; // looped or rewound
                foreach (var m in p.Markers)
                {
                    if (m.Key <= p.LastTime || m.Key > now) continue;
                    uint id = HappyEngine.Base.Singleton<AudioManager>.Instance.PostEvent(m.Value);
                    Debug.Log("[Ro3Emu.CutsceneAudio] " + p.Director.name + " " + now.ToString("F2") + "s: posted marker " + m.Value + " -> playingId=" + id);
                }
                p.LastTime = now;
            }
        }

        /// <summary>The loaded (or loading) banks, so a cutscene's events can be checked against them.</summary>
        private static void Banks(StringBuilder sb)
        {
            if (AD_WwiseManager.Instance == null) return;
            var names = new List<string>();
            foreach (var b in AD_WwiseManager.Instance.GetAllBankInfos()) names.Add(b.Name + (b.State == AD_WwiseManager.BankLoadState.Loaded ? "" : ":" + b.State));
            sb.Append("\n  banks: ").Append(string.Join(" ", names));
        }

        private static string EventBank(string name) =>
            AK.WwiseDefine.dataRevEvents.TryGetValue(name, out uint id) ? EventBank(id) : "(event not in wwisedefine)";

        private static string EventBank(uint id)
        {
            if (!AK.WwiseDefine.dataEvents.TryGetValue(id, out var e)) return "(event " + id + " not in wwisedefine)";
            string bank = AK.WwiseDefine.dataBanks.TryGetValue(e.Bank, out var b) ? b.Name : e.Bank.ToString();
            string state = "not loaded";
            if (AD_WwiseManager.Instance != null)
                foreach (var info in AD_WwiseManager.Instance.GetAllBankInfos())
                    if (string.Equals(info.Name, bank, StringComparison.OrdinalIgnoreCase) || info.BankID == e.Bank) { state = info.State.ToString(); break; }
            return "(bank " + bank + " " + state + ")";
        }

        /// <summary>The components on <paramref name="go"/> that receive Timeline notifications (markers).</summary>
        private static void Receivers(StringBuilder sb, string what, GameObject go)
        {
            var names = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                if (c is INotificationReceiver) names.Add(c.GetType().Name);
            sb.Append(" [").Append(what).Append(" receivers: ").Append(names.Count == 0 ? "none" : string.Join(",", names)).Append(']');
        }

        /// <summary>Simple public settings of a clip asset, marker or component (strings, numbers, Wwise references).</summary>
        private static void Describe(StringBuilder sb, object o)
        {
            for (var type = o.GetType(); type != null && type != typeof(UnityEngine.Object) && type != typeof(object); type = type.BaseType)
            {
                if (type.Namespace != null && type.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal)) break;
                foreach (var f in type.GetFields(Fields | BindingFlags.DeclaredOnly))
                {
                    if (!f.IsPublic && f.GetCustomAttribute<SerializeField>() == null) continue;
                    object value;
                    try { value = f.GetValue(o); } catch { continue; }
                    string text = Value(value);
                    if (text != null) sb.Append(' ').Append(f.Name).Append('=').Append(text);
                }
            }
        }

        private static string Value(object value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return '"' + s + '"';
                case bool _: case int _: case uint _: case long _: case float _: case double _: return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                case Enum e: return e.ToString();
                case AK.Wwise.BaseType ak: return "wwise:" + ak.Name + "#" + ak.Id + (ak is AK.Wwise.Event ? " " + EventBank(ak.Id) : "");
                case System.Collections.IList list when list.Count > 0 && list.Count <= 16:
                    var parts = new List<string>();
                    foreach (var item in list) parts.Add(Value(item) ?? item?.GetType().Name ?? "null");
                    return "[" + string.Join(",", parts) + "]";
                default: return null;
            }
        }

        private static string Path(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent) parts.Insert(0, t.name);
            return parts.Count == 0 ? "." : string.Join("/", parts);
        }
    }
}
