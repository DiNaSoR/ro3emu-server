using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Profile cards without stickers. LC_DAL_Personal.ParsePersonalInfo (lua-src Logic/Asset/LC_DAL_Personal.lua:698-753)
    /// walks decalInfos and showcaseVisibleList with pairs() unguarded, while this client's lua-protobuf
    /// (NW_GameServerHandler: no_decode_default_array) decodes an empty repeated field as nil, so every card without a
    /// sticker fails to parse: the own card (5016 PersonalPlayerInfo) never exists, which leaves the profile editor and
    /// the friend window's signature and mood without data, and others' cards (UserDataService "Personal") abort the
    /// whole public-card parse. The original server met the same decoder. ParsePersonalInfo is wrapped: a missing list
    /// becomes an empty table, then the original runs.
    /// </summary>
    public static class PersonalCards
    {
        /// <summary>LC_DAL_Personal.ParsePersonalInfo(info) (called with '.') -> card table or nil.</summary>
        public delegate object Parse(Obf_R.Obf_ff info);

        private static readonly string[] Lists = { "decalInfos", "showcaseVisibleList" };
        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly Parse ParseWrapper = ParsePersonalInfo;
        private static Obf_R.Obf_Ef _original;
        private static Obf_R.Obf_Cf _env;
        private static bool _reported;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_Cf env, Obf_R.Obf_ff personal)
        {
            _env = env;
            _original = null;
            try
            {
                var original = personal?.Obf_RuA<Obf_R.Obf_Ef>("ParsePersonalInfo");
                if (original == null)
                {
                    Debug.LogWarning("[Ro3Emu.PersonalCards] LC_DAL_Personal.ParsePersonalInfo not found; cards without stickers stay unreadable");
                    return;
                }
                _original = original;
                personal.Obf_luA<string, Parse>("ParsePersonalInfo", ParseWrapper);
                Debug.Log("[Ro3Emu.PersonalCards] LC_DAL_Personal.ParsePersonalInfo wrapped: missing sticker and showcase lists read as empty");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.PersonalCards] " + e.GetType().Name + ": " + e.Message); }
        }

        private static object ParsePersonalInfo(Obf_R.Obf_ff info)
        {
            if (info != null)
                try
                {
                    foreach (var list in Lists)
                        if (info.Obf_RuA<object>(list) == null) info.Obf_luA<string, Obf_R.Obf_ff>(list, _env.Obf_wTA());
                }
                catch (Exception e)
                {
                    if (!_reported) Debug.LogWarning("[Ro3Emu.PersonalCards] " + e.GetType().Name + ": " + e.Message);
                    _reported = true;
                }
            var result = _original?.Obf_HuA(info);
            return result != null && result.Length > 0 ? result[0] : null;
        }
    }
}
