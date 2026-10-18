using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The pay manager can be created without the store SDK. Lua_SDK_Factory.GetPayMgr builds Lua_SDK_PayMgr on first
    /// use and caches it only after its Init returns. Init asks the store for its prices (MetaSDKUtil.ReqSkuInfo); with
    /// no SDK proxy (the emulator's launcher-less client) ReqSkuInfo answers at once through
    /// GameLogic.OnSkuInfoResult, which calls GetPayMgr again: the manager is not cached yet, so it builds another one,
    /// and so on until Lua's C stack overflows. Each level logs "[MetaSDKUtil] sdk proxy is not init" (the freeze), and
    /// the error unwinds the caller, so the Top Up packs and the Quick Buy of a month card never send their order
    /// (2001). The price list only ever reaches a log line (Lua_SDK_PayMgr.OnSkuInfoResult), so Init is replaced by a
    /// no-op on the class table; every other method stays the client's own.
    /// </summary>
    internal static class PayManager
    {
        public delegate void SelfMethod(Obf_R.Obf_ff self);

        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly SelfMethod InitWrapper = Init;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff payMgr)
        {
            try
            {
                if (payMgr?.Obf_RuA<object>("Init") == null)
                {
                    Debug.LogWarning("[Ro3Emu.Pay] Lua_SDK_PayMgr.Init not found; buying may freeze the client");
                    return;
                }
                payMgr.Obf_luA<string, SelfMethod>("Init", InitWrapper);
                Debug.Log("[Ro3Emu.Pay] Lua_SDK_PayMgr.Init skips the store SDK's price request");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.Pay] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Init(Obf_R.Obf_ff self) { }
    }
}
