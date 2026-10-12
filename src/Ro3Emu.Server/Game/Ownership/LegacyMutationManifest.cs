namespace Ro3Emu.Server.Game.Ownership;

/// <summary>
/// Step 06 bridge. Audited single-role families use WholeRole. Unconverted cross-role services use
/// the explicit realm reservation until their participant staging is converted in steps 08–13.
/// This is a migration backlog, not permission for a helper to expand an already held scope.
/// Only messages the dispatcher does not admit otherwise reach it: economic/owner requests, the owned continuations
/// (scene loaded, role select, pre-switch, logout, heartbeat) and ids unknown to the client are decided before.
/// </summary>
internal static class LegacyMutationManifest
{
    internal static ReservationKey[] Message(GameSession session, uint message)
    {
        // C/D 15: role creation writes only its new role (the unique name index decides between accounts).
        if (message == MsgId.LoginRoleCreate) return [ReservationKey.Account(session.Account ?? "unbound")];
        if (session.Role is { } role && SingleRole(message)) return [ReservationKey.Role(role.Id)];
        return [ReservationKey.Realm];
    }

    private static bool SingleRole(uint id) => id is
        MsgId.Walk or MsgId.ChangeFace or MsgId.SaveSetting or
        MsgId.ClientEnterMainUi or MsgId.UploadClientSdkInfo or
        CharacterService.PlayerTransformJob or CharacterService.ChangeStatPlan or CharacterService.RenameStatPlan or
        CardService.LibraryCardUnlock or // C/D 13a: these commands reserve only their role, like their sibling routes
        CombatService.UseSkill or CombatService.SkillInterrupt or
        // C/D 16 (S3): target lock is a session field; chat and in-scene teleport act on the sender. As Realm they
        // queued every fighter's next cast behind them (31102 p99 1.4 s).
        CombatService.LockEnemy or ChatService.TunnelUp or ChatService.JoinChannel or SceneService.SceneTeleport or
        SceneService.NpcInteraction or // C/D 16 (S4): portals and NPC talk; as Realm a map hop waited behind the market up to 3.4 s
        >= 31151 and <= 31160 or // C/D 13c skill plans
        >= 33501 and <= 33515 or // C/D 13c life jobs
        >= 82901 and <= 82903 or // C/D 13c season page
        >= 83220 and <= 83222 or >= 83550 and <= 83553 or // C/D 13c refinement and talent
        >= 31183 and <= 31189 or // wardrobe
        >= 33302 and <= 33325 or // pets and incubation
        >= 32743 and <= 32750 or // mounts
        CareerSpecialService.Lease or CareerSpecialService.CancelLease or // own 5002 and 5012 battle slot only
        >= 82362 and <= 82370 or // totems
        >= 82422 and <= 82426 or // emblems
        TowerService.Info or TowerService.SetPets or TowerService.FirstPassRange or TowerService.ClaimFirstPass or
        TowerService.Sweep or TowerService.End or
        TowerService.Start or // stage G: a solo private floor (its return position, room and handoff); as Realm p99 1.2 s at 500 players
        DungeonService.SetJobPosition or DungeonService.SetGuide or DungeonService.ClaimFirstPass or
        DungeonService.Statistics or DungeonService.StatisticsHistory or DungeonService.StatisticsCurrent or
        DungeonService.StatisticsModule or DungeonService.QueryResult or
        WorldInteractionService.Action or WorldInteractionService.SeatChange or WorldInteractionService.VehicleSync or
        WorldCoreService.ReliveItem or // E04 owns the role payment and the exclusive revive claim
        >= 31301 and <= 31501 or // character, equipment, card and stat plans
        >= 82332 and <= 82340 or // ordinary shops
        EmojiService.SetIdle or EmojiService.AddCollection or EmojiService.DelCollection or // own 5006 only
        AchievementService.GetReward or AchievementService.GetPointReward or // own 5019, items and currency
        AchievementService.UpdateHintLevel or AchievementService.GetAllReward or
        FeedbackService.SubmitMsg; // reads the sender, appends to its own report file

    internal static ReservationKey[] Session(GameSession session) => session.Role is { } role
        ? [ReservationKey.Role(role.Id)] : [ReservationKey.Account(session.Account ?? "unbound")];
}
