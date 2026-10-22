using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The SP cost line of skill tooltips. UI_Com_Skill_PopupItemWidgetCtrl._refreshNowSkillInfo writes
    /// UIWords 33075 "Cost: ${1}" with _generateCostString (UIWords 2073 "SP" .. "*" .. the flat amount of the
    /// [1, 2001, ratio, flat] tuple from Lua_CFG_SkillConfig.GetCostSP) into View.m_kTTXT_CostItemsName and shows it
    /// with m_bShowConsume (the hotbar tip, LC_TipHelper.PopSkillTipPCJoyStick, leaves it on). In game the cast time
    /// and cooldown lines of the same block show but the cost line never does, so the prefab hides that label
    /// (it is not in the Lua). The amount differs from what the player pays too: GetCostSP reads the configured _kCost
    /// only, while the cast check (Lua_CFG_SkillConfig.GetCosts, LC_Skill_Function.CheckSkillCost) uses the cost list
    /// the server sends in 31132 (GetDynamicSkillCost), which carries SP consumption (attribute 1232) and SkillCost buffs.
    /// So _refreshView is wrapped: after the original, the same text is built with the live SP amount, and one frame
    /// later (the view's setters are deferred until the widget is ready) it goes into a copy of the cooldown label
    /// placed after it; the original cost label is hidden so the line never shows twice.
    /// </summary>
    public static class SkillTipCost
    {
        private const string Module = "ui_com_skill_popupitemwidgetctrl";
        private const string CloneName = "Ro3Emu_SkillCost_TTxt";
        private const long CostWord = 33075; // UIWords "Cost: ${1}"
        private const long SpWord = 2073; // UIWords "SP"
        private const long AttrCost = 1; // Lua_GameDefine enum_SkillCostType.Attr
        private const long SpAttribute = 2001; // enum_unitAttrMap.SP

        private static Obf_R.Obf_ff _global, _class;
        private static Obf_R.Obf_Ef _refreshView;
        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly LuaPatches.SelfMethod RefreshWrapper = RefreshView;
        private static readonly Dictionary<Obf_R.Obf_ff, string> Pending = new Dictionary<Obf_R.Obf_ff, string>();
        // Each copy follows its cooldown label's colour (the tooltip fades in by vertex colour).
        private static readonly Dictionary<TMP_Text, TMP_Text> Copies = new Dictionary<TMP_Text, TMP_Text>();
        // The copy made for each cooldown label (it can move out of the cooldown's parent, so Find cannot see it).
        private static readonly Dictionary<Transform, Transform> Clones = new Dictionary<Transform, Transform>();
        private static bool _diagnosed;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            try
            {
                _global = global;
                Pending.Clear();
                _class = LuaPatches.Module(manager, global, Module);
                _refreshView = _class?.Obf_RuA<Obf_R.Obf_Ef>("_refreshView");
                if (_refreshView == null)
                {
                    Debug.LogWarning("[Ro3Emu.SkillTipCost] " + Module + "._refreshView not found; tooltips keep no SP cost");
                    return;
                }
                _class.Obf_luA<string, LuaPatches.SelfMethod>("_refreshView", RefreshWrapper);
                Debug.Log("[Ro3Emu.SkillTipCost] skill tooltip SP cost line wrapped");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SkillTipCost] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void RefreshView(Obf_R.Obf_ff self)
        {
            _refreshView?.Obf_HuA(self);
            try { if (self != null) Pending[self] = CostLine(self); }
            catch (Exception e) { Once("cost line: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>The "Cost: SP*n" line the original would show, with the live SP amount; null when it shows none.</summary>
        private static string CostLine(Obf_R.Obf_ff self)
        {
            // _refreshView: m_bIgnoreSkill hides the info block; _refreshNowSkillInfo: passive skills have none either.
            if (self.Obf_RuA<object>("m_bIgnoreSkill") is bool ignore && ignore) return null;
            if (self.Obf_RuA<object>("m_bShowConsume") is bool show && !show) return null;
            long id = LuaPatches.ToLong(self.Obf_RuA<object>("m_iRealSkillId"));
            var skills = _global.Obf_LuA<Obf_R.Obf_ff>("Lua_DBManager.m_kCFG_SkillConfig");
            var words = _global.Obf_LuA<Obf_R.Obf_ff>("Lua_DBManager.m_kCFG_UIWords");
            if (id == long.MinValue || skills == null || words == null) return null;
            if (First(Method(skills, "IsPassiveSkill", id)) is bool passive && passive) return null;
            if (!(First(_class.Obf_RuA<Obf_R.Obf_Ef>("_generateCostString")?.Obf_HuA(self, id)) is string costs)
                || costs.Length == 0) return null;
            // The 31132 list is per skill group, for the learned level: a next-level or change-target item keeps its
            // configured amount (_refreshView: m_kData.bNextLevel shows TTXT_Next, iTargetSkillID the change view).
            var data = self.Obf_RuA<Obf_R.Obf_ff>("m_kData");
            bool otherLevel = data?.Obf_RuA<object>("bNextLevel") is bool next && next || data?.Obf_RuA<object>("iTargetSkillID") != null;
            if (!otherLevel) costs = WithLiveSp(costs, skills, words, id);
            return First(Method(words, "GetWord", CostWord)) is string format ? format.Replace("${1}", costs) : costs;
        }

        /// <summary>
        /// _generateCostString prints the configured SP ratio as "SP*r%" and the flat amount as "SP*n"; both are swapped
        /// for the amounts of the cost list the client checks the cast against, when that list differs.
        /// </summary>
        private static string WithLiveSp(string costs, Obf_R.Obf_ff skills, Obf_R.Obf_ff words, long id)
        {
            var configured = First(Method(skills, "GetCostSP", id)) as Obf_R.Obf_ff;
            var guid = First(_global.Obf_RuA<Obf_R.Obf_Ef>("GetSelfGuid")?.Obf_HuA());
            var live = guid == null ? null : First(Method(skills, "GetCosts", id, guid)) as Obf_R.Obf_ff;
            if (configured == null || live == null || !(First(Method(words, "GetWord", SpWord)) is string sp)) return costs;
            long ratio = LuaPatches.ToLong(configured.Obf_ruA<long, object>(1));
            long flat = LuaPatches.ToLong(configured.Obf_ruA<long, object>(2));
            for (long i = 1; live.Obf_ruA<long, Obf_R.Obf_ff>(i) is { } tuple; i++)
            {
                if (LuaPatches.ToLong(tuple.Obf_ruA<long, object>(1)) != AttrCost
                    || LuaPatches.ToLong(tuple.Obf_ruA<long, object>(2)) != SpAttribute) continue;
                long liveRatio = LuaPatches.ToLong(tuple.Obf_ruA<long, object>(3));
                long liveFlat = LuaPatches.ToLong(tuple.Obf_ruA<long, object>(4));
                if (liveFlat == long.MinValue) continue;
                string prefix = Regex.Escape(sp + "*");
                if (liveRatio != ratio && ratio > 0 && liveRatio != long.MinValue)
                    costs = Regex.Replace(costs, prefix + ratio + "%", sp + "*" + liveRatio + "%");
                if (liveFlat != flat && flat > 0)
                    costs = Regex.Replace(costs, prefix + flat + "(?![0-9%])", sp + "*" + liveFlat);
                break;
            }
            return costs;
        }

        /// <summary>Per frame (UiTweaks.Tick): puts the lines built last frame into their tooltips.</summary>
        public static void Tick()
        {
            if (Copies.Count > 0) FollowColours();
            if (Pending.Count == 0) return;
            var batch = Pending.ToArray();
            Pending.Clear();
            foreach (var item in batch)
            {
                try { Show(item.Key, item.Value); }
                catch (Exception e) { Once("show: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private static void FollowColours()
        {
            TMP_Text dead = null;
            foreach (var pair in Copies)
            {
                // Unity's == null: a copy or label destroyed with its tooltip drops out (one per frame).
                if (pair.Key == null || pair.Value == null) { dead = pair.Key; continue; }
                if (pair.Key.color != pair.Value.color) pair.Key.color = pair.Value.color;
            }
            if (!ReferenceEquals(dead, null)) Copies.Remove(dead);
        }

        private static void Show(Obf_R.Obf_ff self, string line)
        {
            var view = self.Obf_RuA<Obf_R.Obf_ff>("View");
            var cooldown = TransformOf(view?.Obf_RuA<Obf_R.Obf_ff>("m_kTTXT_CDName"));
            var original = TransformOf(view?.Obf_RuA<Obf_R.Obf_ff>("m_kTTXT_CostItemsName"));
            var cell = TransformOf(view?.Obf_RuA<Obf_R.Obf_ff>("m_kRTRANSFORM_Layout_Cell"));
            if (cooldown == null || cooldown.parent == null) { Once("cooldown label not found"); return; }
            if (!_diagnosed) { _diagnosed = true; Diagnose(original, cooldown, cell, line); }
            if (original != null && original.gameObject.activeSelf) original.gameObject.SetActive(false);
            Clones.TryGetValue(cooldown, out var clone);
            if (line == null || !cooldown.gameObject.activeInHierarchy)
            {
                if (clone != null && clone.gameObject.activeSelf) clone.gameObject.SetActive(false);
                return;
            }
            if (clone == null) { Prune(); Clones[cooldown] = clone = Copy(cooldown); }
            var label = clone.GetComponent<TMP_Text>();
            if (label == null) return;
            if (!clone.gameObject.activeSelf) clone.gameObject.SetActive(true);
            label.text = line;
            Place(clone, cooldown, cell);
            if (cooldown.GetComponent<TMP_Text>() is { } source) Copies[label] = source;
        }

        /// <summary>Drops the copies of tooltips that were destroyed (Unity's == null).</summary>
        private static void Prune()
        {
            foreach (var dead in Clones.Where(pair => pair.Key == null || pair.Value == null).Select(pair => pair.Key).ToList())
                Clones.Remove(dead);
        }

        /// <summary>A copy of the cooldown label without the component that re-applies its language key.</summary>
        private static Transform Copy(Transform cooldown)
        {
            var copy = UnityEngine.Object.Instantiate(cooldown.gameObject, cooldown.parent, false);
            copy.name = CloneName;
            foreach (var behaviour in copy.GetComponents<MonoBehaviour>())
                if (behaviour.GetType().Name == "LangTransUtils_TMP") UnityEngine.Object.Destroy(behaviour);
            return copy.transform;
        }

        /// <summary>
        /// A line of its own under the cooldown's row. In game, cast time and cooldown share one row, so a copy that
        /// stays beside the cooldown label lands on the same line. The row is the cooldown's ancestor, up to the
        /// info block (Layout_Cell), that sits in a vertical layout group inside the block: the copy goes into that group
        /// right after the row. Without such a group, the copy goes one row height below the row.
        /// </summary>
        private static void Place(Transform clone, Transform cooldown, Transform cell)
        {
            // Only groups inside the info block count: without the block, or above it, the copy would leave the tooltip.
            if (cell != null && !cooldown.IsChildOf(cell)) cell = null;
            var row = cooldown;
            while (cell != null && row != cell && row.parent != null && row.parent.GetComponent<VerticalLayoutGroup>() == null)
                row = row.parent;
            if (cell != null && row != cell && row.parent != null && row.parent.GetComponent<VerticalLayoutGroup>() != null)
            {
                if (clone.parent != row.parent) clone.SetParent(row.parent, false);
                int index = row.GetSiblingIndex() + 1;
                if (clone.GetSiblingIndex() != index) clone.SetSiblingIndex(index);
                if (row.parent is RectTransform group) LayoutRebuilder.MarkLayoutForRebuild(group);
                return;
            }
            // No vertical group: below the cooldown's own row (its parent when that lays labels out side by side).
            var line = cooldown.parent.GetComponent<HorizontalLayoutGroup>() != null ? cooldown.parent : cooldown;
            if (clone.parent != line.parent) clone.SetParent(line.parent, false);
            if (line.parent != null && line.parent.GetComponent<LayoutGroup>() != null)
            {
                // A layout group there would override the position: take the slot after the row instead.
                int after = line.GetSiblingIndex() + 1;
                if (clone.GetSiblingIndex() != after) clone.SetSiblingIndex(after);
                if (line.parent is RectTransform group) LayoutRebuilder.MarkLayoutForRebuild(group);
                return;
            }
            if (line is RectTransform from)
                clone.position = cooldown.position - line.up * (from.rect.height * line.lossyScale.y);
        }

        /// <summary>Once: why the original label does not show, for a report from the player's log.</summary>
        private static void Diagnose(Transform original, Transform cooldown, Transform cell, string line)
        {
            var sb = new StringBuilder("[Ro3Emu.SkillTipCost] line=\"").Append(line).Append("\"; cooldown ")
                .Append(Diagnostics.PathOf(cooldown)).Append(" layouts up to the info block:");
            for (var t = cooldown.parent; t != null; t = t.parent)
            {
                sb.Append(' ').Append(t.name).Append('=').Append(t.GetComponent<LayoutGroup>()?.GetType().Name ?? "none");
                if (t == cell) break;
            }
            if (original == null) sb.Append("; cost label not found");
            for (var t = original; t != null && t != cooldown.parent.parent; t = t.parent)
            {
                var rect = t as RectTransform;
                sb.Append("; ").Append(t.name).Append(" active=").Append(t.gameObject.activeSelf);
                if (rect != null) sb.Append(" size=").Append(rect.rect.size).Append(" pos=").Append(rect.anchoredPosition);
                var group = t.GetComponent<CanvasGroup>();
                if (group != null) sb.Append(" alpha=").Append(group.alpha);
                var text = t.GetComponent<TMP_Text>();
                if (text != null) sb.Append(" text=\"").Append(text.text).Append("\" color=").Append(text.color).Append(" enabled=").Append(text.enabled);
            }
            Debug.Log(sb.ToString());
        }

        private static Transform TransformOf(Obf_R.Obf_ff wrapper)
        {
            var result = wrapper?.Obf_RuA<Obf_R.Obf_Ef>("GetRectTransform")?.Obf_HuA(wrapper);
            return First(result) as Transform;
        }

        private static object[] Method(Obf_R.Obf_ff target, string name, params object[] args)
        {
            var function = target.Obf_RuA<Obf_R.Obf_Ef>(name);
            if (function == null) return null;
            var all = new object[args.Length + 1];
            all[0] = target;
            Array.Copy(args, 0, all, 1, args.Length);
            return function.Obf_HuA(all);
        }

        private static object First(object[] results) => results != null && results.Length > 0 ? results[0] : null;

        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static void Once(string message) { if (Reported.Add(message)) Debug.LogWarning("[Ro3Emu.SkillTipCost] " + message); }
    }
}
