using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Every Top Up entry (the shop's Charge button, UI_Sys_ShopPanelCtrl.OnClickCharge; the "+" of a crystal counter,
    /// UI_Com_CoinItemWidgetCtrl; the Fashion cart's "not enough crystals" prompt, UI_Sys_AppearanceShopCart_PanelCtrl)
    /// first reads the global RechargeExternalUrl, PublicParamConfig's "https://console.genesis3.is/" in this build,
    /// and opens that site in the browser when it is set. Empty, they open the in-game UI_Sys_TopUpPanel, whose
    /// orders (2001-2004) this server answers (ShopService.TopUp). Set both on publicparamconfig and on the global
    /// table, and set again whenever something restores it.
    /// </summary>
    internal static class TopUpButton
    {
        private const string Key = "RechargeExternalUrl";
        private static Obf_R.Obf_ff _param;
        private static bool _logged;

        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff param)
        {
            _param = param;
            Probe(global);
        }

        /// <summary>Empties the URL where it is set (cheap: two table reads).</summary>
        internal static void Probe(Obf_R.Obf_ff global)
        {
            bool cleared = false;
            if (_param != null && _param.Obf_RuA<object>(Key) is string p && p.Length > 0) { _param.Obf_luA<string, object>(Key, ""); cleared = true; }
            if (global != null && global.Obf_RuA<object>(Key) is string g && g.Length > 0) { global.Obf_luA<string, object>(Key, ""); cleared = true; }
            if (cleared && !_logged)
            {
                _logged = true;
                Debug.Log("[Ro3Emu.TopUp] " + Key + " emptied: Top Up opens the in-game screen");
            }
        }
    }
}
