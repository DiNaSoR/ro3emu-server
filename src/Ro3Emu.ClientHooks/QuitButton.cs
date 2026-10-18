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
    /// The clone is taken as soon as the menu exists, while Lua may still be loading the gear (FunctionConfig icon path,
    /// loaded asynchronously): the copied Image can then be disabled or transparent, or get a sprite assigned after the
    /// copy. So the icon is kept up every frame: our sprite, and the gear's enabled state and colour.
    /// </summary>
    public static class QuitButton
    {
        private const string Menu = "UI_Sys_MainFunctionMenuWidget_PC(Clone)";
        private const string CloneName = "Ro3Emu_Quit_Btn";
        private const string Icon = "ro3emu_quit_icon";
        private const float IconScale = 0.72f;

        private static TMP_Text _label, _settingsLabel;
        private static Image _icon, _settingsIcon;
        private static Sprite _iconSprite;
        private static bool _iconReported;

        public static void Tick()
        {
            // The menu fades its labels in (vertex alpha), and the clone was taken mid-fade: follow Settings' label.
            if (_label != null && _settingsLabel != null)
            {
                if (_label.color != _settingsLabel.color) _label.color = _settingsLabel.color;
                if (_label.alpha != _settingsLabel.alpha) _label.alpha = _settingsLabel.alpha;
            }
            KeepIcon();
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
            _icon = quit.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.name == "SetListIcon_Img");
            _settingsIcon = settings.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.name == "SetListIcon_Img");
            _iconSprite = null;
            _iconReported = false;
            string gear = _settingsIcon == null ? "none"
                : (_settingsIcon.sprite != null ? _settingsIcon.sprite.name : "no sprite") + " enabled=" + _settingsIcon.enabled + " color=" + _settingsIcon.color;
            if (_icon != null)
            {
                _iconSprite = TextureSwap.Custom(Icon, _icon.sprite);
                if (_iconSprite == null) Debug.LogWarning("[Ro3Emu.QuitButton] " + Icon + " not embedded or not decodable: the button keeps the gear");
                else
                {
                    // The symbol fills its canvas edge to edge, the gear does not: at the same rect it drew 43 px wide
                    // against the gear's 31 and touched the label. Scaled to the gear's size and gap.
                    _icon.rectTransform.localScale *= IconScale;
                }
            }
            KeepIcon();
            // Function 41 (Feedback) is open on our server since FeedbackButton: its slot is logged to check it against quit.
            var feedback = Button(all, "Feedback");
            Debug.Log("[Ro3Emu.QuitButton] added: support=" + support.position + " settings=" + settings.position + " quit=" + quit.transform.position
                + " feedback=" + (feedback != null ? feedback.position.ToString() : "none")
                + " label=" + (_label != null ? _label.color + "/" + _label.alpha + " enabled=" + _label.enabled : "none")
                + " settingsLabel=" + (_settingsLabel != null ? _settingsLabel.color.ToString() : "none")
                + " gear=" + gear + " icon=" + (_icon == null ? "none" : _iconSprite != null ? "ours" : "gear"));
        }

        /// <summary>The quit icon shows our sprite, on whenever the gear is on, in the gear's colour (the menu fades it in).</summary>
        private static void KeepIcon()
        {
            if (_icon == null) return;
            if (_iconSprite != null && _icon.sprite != _iconSprite)
            {
                if (!_iconReported && _icon.sprite != null)
                    Debug.Log("[Ro3Emu.QuitButton] icon sprite replaced by " + _icon.sprite.name + "; set back");
                _iconReported = true;
                _icon.sprite = _iconSprite;
            }
            bool on = _settingsIcon == null || _settingsIcon.enabled;
            if (_icon.enabled != on) _icon.enabled = on;
            if (_settingsIcon != null && _icon.color != _settingsIcon.color) _icon.color = _settingsIcon.color;
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
