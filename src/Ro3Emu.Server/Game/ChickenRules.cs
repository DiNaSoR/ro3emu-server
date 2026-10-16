namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle ("Chicken", match entry 2, scene 2240101) server-side parameters. The client ships the original
/// server's dungeon script config as Config/Dungeon/DungeonLua_2240101.lua (no client Lua reads it); the server does not
/// parse Config/Dungeon/*.lua, so its values are copied here with their line numbers. Everything not in that file or in
/// a client table is marked (inferred) and kept in this one class so it can be tuned.
/// </summary>
public static class ChickenRules
{
    // ---- DungeonLua_2240101.lua: DungeonBase.DungeonStageModule (:197-219), enum_ChickenDungeonStage
    // (Lua_GameDefine.lua:13941-13952: None=-1, ChooseCareer=0, ShowCareer=1, ChoosePos=2, StartGame=3) ----
    public const int StageNone = -1, StageChooseCareer = 0, StageShowCareer = 1, StageChoosePos = 2, StageStartGame = 3;
    /// <summary>maxTime per stage in ms (DL:200, :206, :211, :215). Also PublicParam SelectOccupationChicken 30,
    /// SelectOccupationChickenShow 10, SelectPositionChicken 30.</summary>
    public static readonly long[] StageMs = [30_000, 10_000, 30_000, 1_200_000];
    /// <summary>DL:238 LoginWaitingTime 13000: (inferred) the loading grace folded into the first stage, so a player who
    /// loads last still gets the full 30 s career pick.</summary>
    public const long LoginWaitingMs = 13_000;

    // ---- DungeonLua_2240101.lua: revive rules ----
    /// <summary>DL:230 FirstResurrectionTime 180000: free revive inside the first 3 minutes of the battle (UITips 33014;
    /// the top widget compares DungeonVar 707 with the clock, UI_Sys_Chicken_TopWidgetCtrl.lua:455-480).</summary>
    public const long FirstResurrectionMs = 180_000;
    /// <summary>DL:252 ResurrectionLimit 1: (inferred) ID-tag resurrections per player.</summary>
    public const int ResurrectionLimit = 1;
    /// <summary>DL:234 IdentityDropLimit 3: (inferred) ID tags a player can drop in one match.</summary>
    public const int IdentityDropLimit = 3;
    /// <summary>DL:8 ChickenReliveChantTime 20000: a teammate's revive channel.</summary>
    public const long ReliveChantMs = 20_000;
    /// <summary>DL:226 "EatChickenObserve " 10000 (also PublicParam EatChickenObserve 10): (inferred) the time a revived
    /// player has to pick a point on the 82663 revive map before the server picks one.</summary>
    public const long RelifeChooseMs = 10_000;
    /// <summary>(inferred) A revive channel breaks when the reviver walks this far (m) from where it started.</summary>
    public const float ReliveChannelMoveLimit = 2f;
    /// <summary>DL:4 AssemblySkillCd 5000: a skill put on the bar cannot be used for 5 s.</summary>
    public const long AssemblySkillCdMs = 5_000;
    /// <summary>Free revives per player per match (5036 freeRelifeCountLeft): one is (inferred) from UITips 33014
    /// ("one free revive within 3 minutes").</summary>
    public const int FreeRelifeCount = 1;

    // ---- items, chests ----
    /// <summary>PublicParam ChickenBagLimit 200 (pack weight, EatChickenItemConfig._iWeight).</summary>
    public const int DefaultBagLimit = 200;
    /// <summary>PublicParam EatChickenPoisonBuffId 15999999 ("HP drain" icon while outside the zone).</summary>
    public const int DefaultPoisonBuff = 15999999;
    public const int CurrencyItem = 10006, IdentityCardItem = 50001;
    /// <summary>NPCConfig: supply crates 15000501..509 (_iNPCSubType 10, _iSubTypeValue 0).</summary>
    public const int DeathCrate = 15000052, DiscardCrate = 15000053, IdentityTag = 15000056;
    /// <summary>DL:11-64 Chicken_Box_Drop: crate NPC -> EatChickenDropPoolConfig row.</summary>
    public static readonly IReadOnlyDictionary<int, int> BoxDrop = new Dictionary<int, int>
    {
        [15000501] = 3001, [15000502] = 3002, [15000503] = 3003, [15000504] = 3004, [15000505] = 3005,
        [15000506] = 3006, [15000507] = 3007, [15000508] = 3008, [15000509] = 3009,
        [15000601] = 3010, [15000602] = 3011, [15000701] = 3012, [15000801] = 3013,
    };
    /// <summary>DL:119-168 Chicken_CareerSkill: the extra skill each match career gets.</summary>
    public static readonly IReadOnlyDictionary<int, int> CareerSkill = new Dictionary<int, int>
    {
        [101] = 15991000, [102] = 15991100, [103] = 15992000, [104] = 15992100, [105] = 15993000, [106] = 15993100,
        [107] = 15994000, [108] = 15994100, [109] = 15995000, [110] = 15995100, [111] = 15996000, [112] = 15996100,
    };
    /// <summary>(inferred) Supply crates spawned at the battle start: per entrant, with a floor for small test lobbies
    /// and a ceiling for the 100-player shape. The tiers 501..509 are weighted toward the low ones.</summary>
    public const int CratesPerPlayer = 3, MinCrates = 12, MaxCrates = 150;
    public static readonly (int Npc, int Weight)[] CrateWeights =
        [(15000501, 30), (15000502, 22), (15000503, 16), (15000504, 11), (15000505, 8), (15000506, 6), (15000507, 4),
         (15000508, 2), (15000509, 1)];
    /// <summary>(inferred) Pickup reach: NPCConfig _iInteractDistance 2 m plus the walk-report slack SceneService uses.</summary>
    public const float PickupReach = 10f;
    /// <summary>Scene-editor points (sc_pvp_eatchicken_001_sceneeditor.json): region 708, the circle (r 400) that covers
    /// the island, is (inferred) the first safe zone; PublicParam EatChickenPosition [392,56] is "no landing chosen".</summary>
    public const float IslandX = 430f, IslandZ = 402f, IslandRadius = 400f;
    public const int UnchosenX = 392, UnchosenZ = 56;
    /// <summary>Map extent of the scene editor's points (x 27..826, z 28..825).</summary>
    public const int MapMin = 20, MapMax = 830;

    // ---- poison zone ----
    /// <summary>DL:255-272 chicken_poison: the poison strength from a circle index on (0..3 500, 4..6 700, 7..8 1000,
    /// 9 1300). (inferred) Basis points of the victim's max HP per tick: the HP-drain buff EatChickenPoisonBuffId
    /// 15999999 carries the same shape, T_AXadd0_etc [[2,1000,101,3]] = 10% of the recipient's attribute 101 (max HP;
    /// tuple meaning in PetCombatRules.AttributeAmount). chicken_poison_threhold [3,100,500] (DL:273-280) has no known
    /// meaning and is not used.</summary>
    public static readonly (int Circle, long BasisPoints)[] PoisonStrength = [(0, 500), (4, 700), (7, 1000), (9, 1300)];
    /// <summary>(inferred) Poison damage cadence.</summary>
    public const long PoisonTickMs = 1_000;

    /// <summary>One designed shrink: a countdown (DungeonVar 100010002), then the shrink to the new radius (100010003).</summary>
    public sealed record ZoneStep(int WaitSeconds, int ShrinkSeconds, int EndRadius);

    /// <summary>
    /// (inferred) The client data has no circle schedule, only its transport
    /// (LC_DunGeon_Chicken.lua:554-607) and the damage steps for circles 0..9. This designed schedule shrinks the
    /// island circle in 9 steps to radius 0 at 18 min 35 s, inside the 20 min battle stage; centres are rolled per match
    /// (each new circle inside the previous one). Tune here.
    /// </summary>
    public static readonly ZoneStep[] ZoneSchedule =
    [
        new(120, 75, 280), new(90, 70, 200), new(90, 60, 140), new(75, 55, 100), new(70, 50, 70),
        new(60, 45, 45), new(55, 40, 28), new(50, 35, 15), new(45, 30, 0),
    ];

    public static long PoisonBasisPoints(int circle)
    {
        long strength = PoisonStrength[0].BasisPoints;
        foreach (var (from, value) in PoisonStrength) if (circle >= from) strength = value;
        return strength;
    }

    // ---- 5036 / HUD ----
    /// <summary>LC_DunGeon_VarDefine.lua:448-482 HUD counters.</summary>
    public const int VarSurvivalPlayers = 701, VarSurvivalTeams = 702, VarTotalPlayers = 704, VarTotalTeams = 705,
        VarFreeRevivalTime = 707;
    /// <summary>Config/InjectConfig/DungeonVariableFunctionConfig.lua:57-77 poison arrays.</summary>
    public const int VarPoisonCountDown = 100010002, VarPoisonShrinkMain = 100010003;
    /// <summary>enum_ChickenAreaIndex Main (Lua_GameDefine.lua:13996-14002).</summary>
    public const int AreaMain = 0;
}
