using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// A "Quit Game" entry in the PC Function Menu's side column (Trophy / Ranking / Unstuck ... Support / Settings):
    /// the original only quits through Settings → Log Out → Exit. The Settings entry (button, icon, label) is cloned,
    /// placed one step above Support at the Support → Settings spacing, relabelled, given a power icon of our own,
    /// its own click handlers removed, and a click quits.
    /// </summary>
    public static class QuitButton
    {
        private const string Menu = "UI_Sys_MainFunctionMenuWidget_PC(Clone)";
        private const string CloneName = "Ro3Emu_Quit_Btn";
        private const string Icon = "ro3emu_quit_icon";
        private const float IconScale = 0.72f;

        private static TMP_Text _label, _settingsLabel;

        public static void Tick()
        {
            // The menu fades its labels in (vertex alpha), and the clone was taken mid-fade: follow Settings' label.
            if (_label != null && _settingsLabel != null)
            {
                if (_label.color != _settingsLabel.color) _label.color = _settingsLabel.color;
                if (_label.alpha != _settingsLabel.alpha) _label.alpha = _settingsLabel.alpha;
            }
            var menu = GameObject.Find(Menu);
            if (menu == null) return;
            var all = menu.GetComponentsInChildren<Transform>(true);
            if (all.Any(t => t.name == CloneName)) return;
            var settings = Button(all, "Set");
            var support = Button(all, "Questionnaire");
            if (settings == null || support == null) return;

            var quit = UnityEngine.Object.Instantiate(settings.gameObject, settings.parent, false);
            quit.name = CloneName;
            // World space: Support and Settings sit under different parents.
            quit.transform.position = support.position + (support.position - settings.position);
            quit.SetActive(true);

            // Drop every handler the clone carries (Lua binds the original by its view slot, not this copy).
            foreach (var button in quit.GetComponentsInChildren<Button>(true)) button.onClick.RemoveAllListeners();
            foreach (var behaviour in quit.GetComponentsInChildren<MonoBehaviour>(true))
            {
                // LangTransUtils_TMP re-applies the "Settings" language key on enable. Graphics stay: this client's
                // TMP text is itself a click handler (links).
                if (behaviour is IPointerClickHandler && !(behaviour is Button) && !(behaviour is Graphic) || behaviour.GetType().Name == "LangTransUtils_TMP")
                    behaviour.enabled = false;
            }
            quit.AddComponent<QuitClick>();
            _label = quit.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.name == "SetName_TTxt");
            _settingsLabel = settings.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.name == "SetName_TTxt");
            if (_label != null) _label.text = "Quit";
            foreach (var image in quit.GetComponentsInChildren<Image>(true))
            {
                if (image.name != "SetListIcon_Img") continue;
                var icon = TextureSwap.Custom(Icon, image.sprite);
                if (icon == null) continue;
                image.sprite = icon;
                // The symbol fills its canvas edge to edge, the gear does not: at the same rect it drew 43 px wide
                // against the gear's 31 and touched the label. Scaled to the gear's size and gap.
                image.rectTransform.localScale *= IconScale;
            }
            // Function 41 (Feedback) is open on our server since FeedbackButton: its slot is logged to check it against quit.
            var feedback = Button(all, "Feedback");
            Debug.Log("[Ro3Emu.QuitButton] added: support=" + support.position + " settings=" + settings.position + " quit=" + quit.transform.position
                + " feedback=" + (feedback != null ? feedback.position.ToString() : "none")
                + " label=" + (_label != null ? _label.color + "/" + _label.alpha + " enabled=" + _label.enabled : "none")
                + " settingsLabel=" + (_settingsLabel != null ? _settingsLabel.color.ToString() : "none"));
        }

        /// <summary>The visible "&lt;key&gt;_Btn" (the prefab also holds inactive copies of the column for other layouts).</summary>
        private static Transform Button(Transform[] all, string key) =>
            all.FirstOrDefault(t => t.name == key + "_Btn" && t.gameObject.activeInHierarchy && t.GetComponent<RectTransform>() != null);

        private sealed class QuitClick : MonoBehaviour, IPointerClickHandler
        {
            /// <summary>The window close button's own path: GameLogic's "Tip / are you sure / Cancel / Confirm" popup.</summary>
            public void OnPointerClick(PointerEventData eventData)
            {
                try
                {
                    HappyEngine.Base.SingletonMonoBehaviour<LA_LuaManager>.Instance.Obf_LD("GameLogic", "OnWindowCloseButtonClick", true, true);
                }
                catch (Exception e)
                {
                    Debug.LogError("[Ro3Emu.QuitButton] confirm popup failed, quitting: " + e);
                    Application.Quit();
                }
            }
        }
    }
}
