using System.Linq;
using TMPro;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The novice pass 9201 (30 levels from base level 10, EXP from main story quests), which the client data leaves
    /// half done: its rows copy Pass II's name (NewActivityConfig _iName 11930000020 "Pass II", BattlePassConfig
    /// _iActivityName "Movement of the God of Nature · II"), and its level rewards (BattlePassRewardConfig 920100-920159)
    /// are AwardConfig ids with no MultiverseGroupConfig row, through which the pass view reads every reward
    /// (UI_Sys_Season_PassListWidgetBaseCtrl._refreshRewardList, GetSeasonAwardPreviewForComItem), so it showed empty
    /// slots. The server pays them from AwardConfig directly. Injected with the pass challenge rows (PassChallenges).
    /// </summary>
    internal static class NovicePass
    {
        private const long ActivityId = 9201;
        private const long FirstReward = 920100, LastReward = 920159;
        private const long TextBase = 99_130_000_900;
        private const string Subtitle = "The First Steps";

        internal static void Inject(Obf_R.Obf_ff dbm, Obf_R.Obf_ff texts)
        {
            var groups = PassChallenges.Values(dbm, "m_kCFG_MultiverseGroupConfig");
            var activities = PassChallenges.Values(dbm, "m_kCFG_NewActivityConfig");
            var passes = PassChallenges.Values(dbm, "m_kCFG_BattlePassConfig");
            if (groups == null || activities == null || passes == null)
            {
                Debug.LogWarning("[Ro3Emu.Pass] novice pass configs missing");
                return;
            }
            // Type 0 (AwardConfig) with the same id, as the season rewards' own rows are.
            for (long id = FirstReward; id <= LastReward; id++)
                PassChallenges.RawSet(groups, id, PassChallenges.Row("_iID", id, "_itype", 0L, "_iValue_default", id));

            PassChallenges.Text(texts, TextBase + 1, "Novice Pass");
            PassChallenges.Text(texts, TextBase + 2, Subtitle);
            var activity = activities.Obf_ruA<long, Obf_R.Obf_ff>(ActivityId);
            if (activity != null) PassChallenges.RawSet(activity, "_iName", PassChallenges.List(TextBase + 1));
            var pass = passes.Obf_ruA<long, Obf_R.Obf_ff>(ActivityId);
            if (pass != null) PassChallenges.RawSet(pass, "_iActivityName", PassChallenges.List(TextBase + 2));
        }

        /// <summary>The pass view shows the novice pass: its subtitle is ours.</summary>
        internal static bool Shown(GameObject widget) =>
            widget.GetComponentsInChildren<TMP_Text>(false).Any(t => t.text == Subtitle);

        /// <summary>
        /// The block under the art announcing the wardrobe reward ("Dinner Date", "Premium Pass Lv. 50 Rewards"): the
        /// novice pass copies Pass I's, but ends at level 30 and has no such reward. The art itself stays.
        /// </summary>
        internal static void ShowSpecialReward(Transform[] all, bool show)
        {
            var detail = all.FirstOrDefault(t => t.name == "Pic_WardrobeDetail");
            if (detail != null && detail.gameObject.activeSelf != show) detail.gameObject.SetActive(show);
        }
    }
}
