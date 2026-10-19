using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Bound crystals count as crystals. The server spends bound crystals (item 16, the Top Up bonuses) first on every
    /// crystal (item 3) price (ShopService.Payment), but the client's checks read currency 3 alone: the buy dialog
    /// (UI_Com_Shop_BuyTipsPanelCtrl, "not enough" tip 22001), the Fashion cart (UI_Sys_AppearanceShopCart_PanelCtrl)
    /// and the currency exchange (UI_Sys_CoinExchangeWidgetCtrl) all ask LC_ItemHelper.GetItemCount(3) before sending
    /// anything. GetItemCount is wrapped: for item 3 it returns crystals plus bound crystals. Every caller looks the
    /// function up on the table at call time, so the wrapper reaches them all.
    /// <para>On a realm with Ro3Emu:CashShop:Free the server pays every crystal price and says so at login (game message
    /// <see cref="MsgId"/>, UTF-8 JSON {"free": bool}); item 3 then counts at least <see cref="FreeBalance"/>, so those
    /// checks let every buy, Fashion cart, gift and exchange through and the balance shows as plenty.</para>
    /// </summary>
    public static class BoundCrystals
    {
        private const long Crystal = 3, BoundCrystal = 16;

        /// <summary>Must equal the server's ShopService.CashShopMsg.</summary>
        public const uint MsgId = 77021;

        /// <summary>The crystals the client sees on a free cash shop realm: above any single price or exchange (999999).</summary>
        public const long FreeBalance = 9999999;

        private static volatile bool _free;

        /// <summary>From ServerImAdapter.OnMessage (possibly off the main thread).</summary>
        internal static void Receive(byte[] payload)
        {
            string text = System.Text.Encoding.UTF8.GetString(payload ?? new byte[0]);
            bool free = text.Replace(" ", "").Contains("\"free\":true");
            if (free != _free) Debug.Log("[Ro3Emu.BoundCrystals] free cash shop " + (free ? "on: crystals count as " + FreeBalance : "off"));
            _free = free;
        }

        /// <summary>LC_ItemHelper.GetItemCount(itemId) (called with '.') -> count.</summary>
        public delegate object Count(object itemId);

        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly Count CountWrapper = GetItemCount;
        private static Obf_R.Obf_Ef _original;
        private static bool _reported;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff helper)
        {
            _original = null;
            try
            {
                var original = helper?.Obf_RuA<Obf_R.Obf_Ef>("GetItemCount");
                if (original == null)
                {
                    Debug.LogWarning("[Ro3Emu.BoundCrystals] LC_ItemHelper.GetItemCount not found; bound crystals stay uncounted");
                    return;
                }
                _original = original;
                helper.Obf_luA<string, Count>("GetItemCount", CountWrapper);
                Debug.Log("[Ro3Emu.BoundCrystals] LC_ItemHelper.GetItemCount wrapped: crystals include bound crystals");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.BoundCrystals] " + e.GetType().Name + ": " + e.Message); }
        }

        private static object GetItemCount(object itemId)
        {
            object count = First(_original?.Obf_HuA(itemId));
            if (LuaPatches.ToLong(itemId) != Crystal) return count;
            try
            {
                long bound = LuaPatches.ToLong(First(_original.Obf_HuA(BoundCrystal)));
                long own = LuaPatches.ToLong(count);
                long total = own == long.MinValue ? long.MinValue : own + Math.Max(0, bound);
                if (_free && total < FreeBalance) return FreeBalance;
                if (bound > 0 && own != long.MinValue) return total;
            }
            catch (Exception e)
            {
                if (!_reported) Debug.LogWarning("[Ro3Emu.BoundCrystals] " + e.GetType().Name + ": " + e.Message);
                _reported = true;
            }
            return count;
        }

        private static object First(object[] result) => result != null && result.Length > 0 ? result[0] : null;
    }
}
