using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// A "Challenges" button in the battle pass view (UI_Sys_Season_PassWidget, bottom row: "Gift to a Friend",
    /// "Premium Benefits"): the pass challenges panel (PassChallenges) has no entry in the original client. The
    /// "Premium Benefits" button (Equities_Btn) is cloned one step to its right at the Gift → Benefits spacing,
    /// relabelled, given the season daily-task icon, its own click handlers removed, and a click asks for the
    /// challenges and opens their panel.
    /// </summary>
    public static class PassButton
    {
        private const string Widget = "UI_Sys_Season_PassWidget(Clone)";
        private const string CloneName = "Ro3Emu_PassChallenges_Btn";
        private const string IconSprite = "season_icon_daily"; // Season_Icon atlas: a checklist with a tick

        public static void Tick()
        {
            var widget = GameObject.Find(Widget);
            if (widget == null) return;
            var all = widget.GetComponentsInChildren<Transform>(true);
            // One widget shows every pass in turn; the novice pass earns from story quests, not from the challenges.
            bool novice = NovicePass.Shown(widget);
            NovicePass.ShowSpecialReward(all, !novice);
            var existing = all.FirstOrDefault(t => t.name == CloneName);
            if (existing != null)
            {
                if (existing.gameObject.activeSelf == novice) existing.gameObject.SetActive(!novice);
                return;
            }
            if (novice) return;
            var benefits = Button(all, "Equities");
            var gift = Button(all, "GiveFriend");
            if (benefits == null || gift == null) return;

            var button = UnityEngine.Object.Instantiate(benefits.gameObject, benefits.parent, false);
            button.name = CloneName;
            button.transform.position = benefits.position + (benefits.position - gift.position);
            button.SetActive(true);
            // Drop every handler the clone carries (Lua binds the original by its view slot, not this copy).
            foreach (var b in button.GetComponentsInChildren<Button>(true)) b.onClick.RemoveAllListeners();
            foreach (var behaviour in button.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IPointerClickHandler && !(behaviour is Button) && !(behaviour is Graphic) || behaviour.GetType().Name == "LangTransUtils_TMP")
                    behaviour.enabled = false;
            }
            button.AddComponent<Click>();
            var label = button.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.name == "Equities_TTxt");
            var giftLabel = gift.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
            if (label != null)
            {
                label.text = "Challenges";
                // The labels auto-size: one short word would come out larger than its neighbours.
                if (giftLabel != null)
                {
                    label.enableAutoSizing = false;
                    label.fontSize = giftLabel.fontSize;
                }
            }
            var icon = button.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.name == "Pic_Icon");
            var sprite = Diagnostics.FindSprite(IconSprite);
            if (icon != null && sprite != null)
            {
                icon.sprite = sprite;
                icon.preserveAspect = true;
            }
            Debug.Log("[Ro3Emu.PassButton] added" + (sprite == null ? " (no " + IconSprite + " loaded, kept the benefits icon)" : ""));
        }

        /// <summary>The visible "&lt;key&gt;_Btn".</summary>
        private static Transform Button(Transform[] all, string key) =>
            all.FirstOrDefault(t => t.name == key + "_Btn" && t.gameObject.activeInHierarchy && t.GetComponent<RectTransform>() != null);

        private sealed class Click : MonoBehaviour, IPointerClickHandler
        {
            public void OnPointerClick(PointerEventData eventData) => PassChallenges.Request();
        }
    }
}
