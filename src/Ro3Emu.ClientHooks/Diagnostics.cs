using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Server-triggered client inspection (game message 77012, UTF-8 command line; GM "!client ..."), written to
    /// Player.log. Runs on the main thread from the chat adapter's pump.
    /// </summary>
    public static class Diagnostics
    {
        public const uint MsgId = 77012;

        public static void Run(string line)
        {
            var parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            try
            {
                switch (parts[0])
                {
                    case "find":
                        Find(parts.Length > 1 ? parts[1] : "weapon");
                        break;
                    case "panels":
                        Panels();
                        break;
                    case "dumptex":
                        DumpTextures(parts.Length > 1 ? parts[1] : "Txt");
                        break;
                    default:
                        Debug.Log("[Ro3Emu.Diag] unknown command " + line);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Diag] " + line + " failed: " + e);
            }
        }

        /// <summary>Every transform whose name contains <paramref name="filter"/>: path, state, renderers.</summary>
        private static void Find(string filter)
        {
            var all = Resources.FindObjectsOfTypeAll<Transform>();
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var t in all)
            {
                if (t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.gameObject.scene.name == null) continue; // prefab assets, not scene objects
                if (++hits > 60) break;
                sb.Append("\n  ").Append(Path(t))
                  .Append(" active=").Append(t.gameObject.activeInHierarchy)
                  .Append(" layer=").Append(t.gameObject.layer)
                  .Append(" pos=").Append(t.position.ToString("F2"))
                  .Append(" scale=").Append(t.lossyScale.ToString("F3"));
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    sb.Append("\n      renderer ").Append(r.GetType().Name).Append(' ').Append(r.name)
                      .Append(" enabled=").Append(r.enabled)
                      .Append(" visible=").Append(r.isVisible)
                      .Append(" layer=").Append(r.gameObject.layer)
                      .Append(" bounds=").Append(r.bounds.size.ToString("F2"))
                      .Append(" mat=").Append(r.sharedMaterial != null ? r.sharedMaterial.name + "/" + (r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-") : "null");
                }
            }
            Debug.Log("[Ro3Emu.Diag] find '" + filter + "': " + hits + " hit(s)" + sb);
        }

        /// <summary>Open UI views: active objects named UI_Sys_* / UI_Com_* (the view prefabs' roots), outermost only.</summary>
        private static void Panels()
        {
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || !t.gameObject.activeInHierarchy) continue;
                if (!t.name.StartsWith("UI_Sys_", StringComparison.Ordinal) && !t.name.StartsWith("UI_Com_", StringComparison.Ordinal)) continue;
                bool nested = false;
                for (var p = t.parent; p != null && !nested; p = p.parent)
                    nested = p.name.StartsWith("UI_Sys_", StringComparison.Ordinal) || p.name.StartsWith("UI_Com_", StringComparison.Ordinal);
                if (nested) continue;
                if (++hits > 120) break;
                sb.Append("\n  ").Append(Path(t)).Append(" children=").Append(t.childCount);
            }
            Debug.Log("[Ro3Emu.Diag] panels: " + hits + sb);
        }

        /// <summary>
        /// The textures of RawImage / Image components under transforms whose name contains <paramref name="filter"/>
        /// (also inactive ones): name and size in the log, a PNG copy in &lt;client&gt;/ro3emu/dump/.
        /// </summary>
        private static void DumpTextures(string filter)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "ro3emu", "dump");
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Texture tex = null;
                var raw = t.GetComponent<RawImage>(); // Unity objects: no ?. (destroyed objects compare equal to null)
                if (raw != null) tex = raw.texture;
                if (tex == null) { var img = t.GetComponent<Image>(); if (img != null && img.sprite != null) tex = img.sprite.texture; }
                if (tex == null || ++hits > 40) continue;
                string safe = tex.name;
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                string file = System.IO.Path.Combine(dir, (safe.Length > 0 ? safe : "tex" + hits) + ".png");
                try { SavePng(tex, file); }
                catch (Exception e) { file = "save failed: " + e.Message; }
                sb.Append("\n  ").Append(Path(t)).Append(" tex=").Append(tex.name).Append(' ').Append(tex.width).Append('x').Append(tex.height)
                  .Append(" -> ").Append(file);
                var sprite = t.GetComponent<Image>();
                if (sprite != null && sprite.sprite != null)
                    sb.Append(" sprite=").Append(sprite.sprite.name).Append(" rect=").Append(sprite.sprite.textureRect.ToString("F0"))
                      .Append(" size=").Append(((RectTransform)t).rect.size.ToString("F0")).Append(" color=").Append(sprite.color);
            }
            Debug.Log("[Ro3Emu.Diag] dumptex '" + filter + "': " + hits + sb);
        }

        private static readonly string AutoDumpFilter = Environment.GetEnvironmentVariable("RO3EMU_DUMPTEX");
        private static int _autoDumpFrame;

        /// <summary>
        /// With RO3EMU_DUMPTEX=&lt;filter&gt; in the client's environment: dumptex every ~5 s from the frame pump, for screens
        /// before world entry (login, role select) where the GM "!client" command cannot reach.
        /// </summary>
        public static void AutoDump()
        {
            if (string.IsNullOrEmpty(AutoDumpFilter) || ++_autoDumpFrame % 300 != 0) return;
            try { DumpTextures(AutoDumpFilter); } catch (Exception e) { Debug.LogError("[Ro3Emu.Diag] autodump: " + e); }
        }

        /// <summary>Any texture (also non-readable / compressed) through a render texture into a PNG.</summary>
        internal static void SavePng(Texture tex, string file)
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(file, ImageConversion.EncodeToPNG(copy));
            UnityEngine.Object.Destroy(copy);
        }

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
