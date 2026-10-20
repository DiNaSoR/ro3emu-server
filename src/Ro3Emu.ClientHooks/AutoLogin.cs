using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Logs in for the player when RO3Emu.exe (or a launcher) already did: it signs in with POST /sdk/login and starts
    /// ro3.exe with the token in RO3EMU_LOGIN_TOKEN (and the account in RO3EMU_LOGIN_ACCOUNT). The game then skips its
    /// login screen and its server screen and opens on character select. Without the variables nothing changes.
    /// <list type="bullet">
    /// <item>Login: UI_Sys_LoginWidgetBaseCtrl.OnOpen calls self:OpenAccountPanel(), which opens the account/password
    /// widget (whose Enter calls LC_LoginManager:AccountLogin, the SDK login, then _loginCallBack ->
    /// LC_LoginManager:GetInstance():AccountLoginBySdk(token, uid)). The wrapper makes that last call itself with the
    /// launcher's token instead of opening the widget. A late install (the widget already open) does the same from
    /// <see cref="Tick"/>.</item>
    /// <item>Server: AccountLoginBySdk sets the token and fetches the server list; the LoginSelectServer event shows
    /// UI_Sys_Login_ChooseServertWidgetCtrl, whose _onUpdateCurrentServer picks the recent (or first) area. The wrapper
    /// then presses its Enter (self:_onClickLogin(): maintenance, white list and activation code checks, then
    /// LC_LoginManager:ConnectToServer) on the next tick.</item>
    /// </list>
    /// Both happen once per game start: after a logout (or a refused token) the screens work as usual. The variables are
    /// cleared from the process once read, so nothing the game starts inherits the token.
    /// </summary>
    public static class AutoLogin
    {
        internal const string TokenVariable = "RO3EMU_LOGIN_TOKEN", AccountVariable = "RO3EMU_LOGIN_ACCOUNT";

        /// <summary>Seconds the account panel has been open without our wrapper before Tick logs in itself (late install).</summary>
        private const float LateLoginSeconds = 1f;

        private const string AccountPanelModule = "ui_sys_login_accountpasswordwidgetctrl";

        private static string _token, _account;
        private static bool _read, _loginPending, _enterPending;
        private static Obf_R.Obf_ff _global, _baseClass, _serverClass, _enterWidget;
        private static Obf_R.Obf_Ef _openAccountPanel, _updateCurrentServer;
        private static float _loginSceneSince = -1f;

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly LuaPatches.SelfMethod OpenAccountPanelWrapper = OpenAccountPanel, UpdateCurrentServerWrapper = UpdateCurrentServer;

        /// <summary>From LuaPatches.Tick, once per LuaEnv (before the login UI opens: the modules are required here).</summary>
        internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            try
            {
                ReadVariables();
                if (!_loginPending) return;
                _global = global;
                _baseClass = LuaPatches.Module(manager, global, "ui_sys_loginwidgetbasectrl");
                _serverClass = LuaPatches.Module(manager, global, "ui_sys_login_chooseservertwidgetctrl");
                _openAccountPanel = _baseClass?.Obf_RuA<Obf_R.Obf_Ef>("OpenAccountPanel");
                _updateCurrentServer = _serverClass?.Obf_RuA<Obf_R.Obf_Ef>("_onUpdateCurrentServer");
                if (_openAccountPanel != null) _baseClass.Obf_luA<string, LuaPatches.SelfMethod>("OpenAccountPanel", OpenAccountPanelWrapper);
                if (_updateCurrentServer != null) _serverClass.Obf_luA<string, LuaPatches.SelfMethod>("_onUpdateCurrentServer", UpdateCurrentServerWrapper);
                _enterPending = _updateCurrentServer != null;
                Debug.Log("[Ro3Emu.AutoLogin] launcher login for " + (_account ?? "?") + ": login screen "
                    + (_openAccountPanel != null ? "skipped" : "not found") + ", server screen " + (_enterPending ? "skipped" : "not found"));
            }
            catch (Exception e)
            {
                _loginPending = _enterPending = false;
                Debug.LogWarning("[Ro3Emu.AutoLogin] " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>From UiTweaks.Tick (every 10th frame, outside any Lua call).</summary>
        public static void Tick()
        {
            if (!_loginPending && _enterWidget == null) return;
            try
            {
                if (_enterWidget != null)
                {
                    var widget = _enterWidget;
                    _enterWidget = null;
                    widget.Obf_RuA<Obf_R.Obf_Ef>("_onClickLogin")?.Obf_HuA(widget);
                    Debug.Log("[Ro3Emu.AutoLogin] server chosen, entering");
                    return;
                }
                // Late install: the login widget opened (and showed the account panel) before the wrapper was in.
                if (_global == null || !AccountPanelOpened()) { _loginSceneSince = -1f; return; }
                if (_loginSceneSince < 0f) { _loginSceneSince = Time.realtimeSinceStartup; return; }
                if (Time.realtimeSinceStartup - _loginSceneSince < LateLoginSeconds) return;
                // AccountLoginBySdk fetches the server list; its LoginSelectServer event makes the login widget
                // (_onSelectServer) hide the account panel and show the server screen, as after a typed login.
                Debug.Log("[Ro3Emu.AutoLogin] login screen was open before the hooks; logging in from here");
                Login();
            }
            catch (Exception e)
            {
                _loginPending = _enterPending = false;
                _enterWidget = null;
                Debug.LogWarning("[Ro3Emu.AutoLogin] " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void ReadVariables()
        {
            if (_read) return;
            _read = true;
            _token = Environment.GetEnvironmentVariable(TokenVariable);
            _account = Environment.GetEnvironmentVariable(AccountVariable);
            Environment.SetEnvironmentVariable(TokenVariable, null);
            Environment.SetEnvironmentVariable(AccountVariable, null);
            _loginPending = !string.IsNullOrEmpty(_token);
            if (string.IsNullOrEmpty(_account)) _account = "player";
        }

        private static void OpenAccountPanel(Obf_R.Obf_ff self)
        {
            if (_loginPending)
            {
                try { Login(); return; }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.AutoLogin] login: " + e.GetType().Name + ": " + e.Message); }
            }
            _openAccountPanel?.Obf_HuA(self);
        }

        /// <summary>What _loginCallBack does after a successful SDK login, with the launcher's token.</summary>
        private static void Login()
        {
            _loginPending = false;
            var manager = LoginManager() ?? throw new InvalidOperationException("LC_LoginManager not found");
            var call = manager.Obf_RuA<Obf_R.Obf_Ef>("AccountLoginBySdk") ?? throw new InvalidOperationException("AccountLoginBySdk not found");
            call.Obf_HuA(manager, _token, _account);
            _token = null;
            Debug.Log("[Ro3Emu.AutoLogin] logged in as " + _account);
        }

        private static void UpdateCurrentServer(Obf_R.Obf_ff self)
        {
            _updateCurrentServer?.Obf_HuA(self);
            try
            {
                // Only after our own login (not while the account panel waits for the player), and once.
                if (!_enterPending || _loginPending || self == null || self.Obf_RuA<object>("m_kCurrentServer") == null) return;
                _enterPending = false;
                _enterWidget = self;
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.AutoLogin] server: " + e.GetType().Name + ": " + e.Message); }
        }

        private static Obf_R.Obf_ff LoginManager()
        {
            var cls = _global?.Obf_RuA<Obf_R.Obf_ff>("LC_LoginManager");
            var instance = cls?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(cls);
            return instance != null && instance.Length > 0 && instance[0] is Obf_R.Obf_ff table ? table : cls;
        }

        /// <summary>
        /// The account/password widget exists: OpenAccountPanel creates it (UI_Sys_Login_AccountPasswordWidget), which
        /// requires its module, so package.loaded holds it once the panel was shown without our wrapper. Usually the
        /// login screen opens a moment before LuaPatches installs (it needs the world modules), so this is the common
        /// path. (LC_LoginManager:IsLoginScene is no use here: it is true on the select-role scene only, and raises
        /// before the first scene loads.)
        /// </summary>
        private static bool AccountPanelOpened()
        {
            try { return _global?.Obf_LuA<Obf_R.Obf_ff>("package.loaded." + AccountPanelModule) != null; }
            catch (Exception) { return false; }
        }
    }
}
