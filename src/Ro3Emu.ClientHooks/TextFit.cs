using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// English text in labels laid out for Chinese: the client sizes many labels for 2–4 CJK glyphs, so an English
    /// word either breaks in the middle ("Challeng" / "e") or the second line spills out of the frame. The patched
    /// TMP_Text setters / Awake report the label here (ClientTools TextSink: object parameter, so Unity.TextMeshPro
    /// needs no reference back to this DLL); one frame later, after TMP has laid the text out, a label that split a
    /// word or overflows its rect gets TMP auto-size between 60 % and its own size. Labels that fit are untouched,
    /// and a label is only ever shrunk, never enlarged.
    /// </summary>
    public static class TextFit
    {
        private const float MinScale = 0.6f;
        private static readonly HashSet<TMP_Text> Pending = new HashSet<TMP_Text>();
        private static readonly List<TMP_Text> Batch = new List<TMP_Text>();
        private static readonly Dictionary<int, float> Original = new Dictionary<int, float>();

        /// <summary>Called by the patched TMP_Text text setters and Awake.</summary>
        public static void Touch(object label)
        {
            if (label is TMP_Text tmp) Pending.Add(tmp);
        }

        /// <summary>Per frame (UiTweaks.Tick): check what changed in the previous frame.</summary>
        public static void Tick()
        {
            if (Pending.Count == 0) return;
            Batch.Clear();
            Batch.AddRange(Pending);
            Pending.Clear();
            foreach (var tmp in Batch)
            {
                try { Check(tmp); }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.TextFit] " + e.Message); }
            }
        }

        private static void Check(TMP_Text tmp)
        {
            if (tmp == null || !tmp.isActiveAndEnabled || string.IsNullOrEmpty(tmp.text)) return;
            if (!HasLatin(tmp.text)) return;
            int id = tmp.GetInstanceID();
            if (!Original.TryGetValue(id, out float size))
            {
                size = tmp.enableAutoSizing ? tmp.fontSizeMax : tmp.fontSize;
                Original[id] = size;
            }
            if (tmp.enableAutoSizing && tmp.fontSizeMin <= size * MinScale) return; // already fitted (by us or the prefab)
            tmp.ForceMeshUpdate();
            if (!SplitsWord(tmp) && !Overflows(tmp)) return;
            tmp.fontSizeMax = size;
            tmp.fontSizeMin = Mathf.Max(1f, size * MinScale);
            tmp.enableAutoSizing = true;
            tmp.ForceMeshUpdate();
        }

        /// <summary>A line starts in the middle of a word: TMP only does that when one word is wider than the rect.</summary>
        private static bool SplitsWord(TMP_Text tmp)
        {
            var info = tmp.textInfo;
            string text = tmp.text;
            for (int line = 1; line < info.lineCount; line++)
            {
                int first = info.lineInfo[line].firstCharacterIndex;
                if (first <= 0 || first >= info.characterCount) continue;
                char previous = info.characterInfo[first - 1].character, current = info.characterInfo[first].character;
                if (char.IsLetterOrDigit(previous) && char.IsLetterOrDigit(current)) return true;
            }
            return false;
        }

        private static bool Overflows(TMP_Text tmp)
        {
            if (tmp.isTextOverflowing) return true;
            var rect = tmp.rectTransform.rect;
            var bounds = tmp.textBounds.size;
            return rect.height > 1f && bounds.y > rect.height * 1.08f || rect.width > 1f && !tmp.enableWordWrapping && bounds.x > rect.width * 1.08f;
        }

        private static bool HasLatin(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z') return true;
            }
            return false;
        }
    }
}
