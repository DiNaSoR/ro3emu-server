using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The client's GM panel ops beyond the first few (GMConfig rows, sent on 30300 as GmOpEnum + comma-separated
/// parameters in the panel's field order; dropdown fields carry GMConfig._kOptionID values). GmShowBuff sends only the
/// selected unit (or the GM's own uid); GmShowAttr, GmCastSkill and GmTargetCastSkill append it as a last field.
/// A target id of 0 or an empty field means the GM.
/// </summary>
public sealed partial class GmCommands
{
    private const int OpStrengthen = 4, OpRefine = 5, OpResetAttrPnt = 9, OpChangeHp = 23, OpDropPool = 26, OpAddNpc = 27,
        OpKillEntity = 30, OpDumpEntityAttr = 32, OpSetEntityAttr = 33, OpChangeEntityAttr = 34, OpAddBuff = 35,
        OpRemoveBuff = 36, OpNoCd = 52, OpDamageFix = 53, OpDumpServerLevelInfo = 64, OpSendMailPerson = 70,
        OpSendMailSystemByCfgId = 71, OpSendMailSystemByCustom = 72, OpShowBuff = 76, OpShowAttr = 77,
        OpShowLocation = 79, OpUnlimit = 80, OpFlyToTarget = 83, OpDungeonCmd = 93, OpOpGuild = 94, OpSetEntityCamp = 95,
        OpTask = 99, OpEnterDungeon = 108, OpCurAllSkillLevUpMax = 110;

    private const int AttrMaxHp = 101, AttrMaxSp = 102, AttrHp = 2000, AttrSp = 2001;

    /// <summary>GMConfig 1006/1007 part ids (weapon = both hands) to EquipGridType grids.</summary>
    private static readonly Dictionary<int, int[]> PartGrids = new()
    {
        [1] = [1, 2], [2] = [3], [3] = [4], [4] = [5], [5] = [6], [6] = [7],
    };

    /// <summary>GMConfig 1021 options (accept, give up, reset, hand in, complete, delete) to TaskService.GmAsync.</summary>
    private static readonly string[] TaskOps = ["accept", "drop", "drop", "done", "finish", "drop"];

    private World World => services.GetRequiredService<World>();
    private CombatService Combat => services.GetRequiredService<CombatService>();
    private BuffService Buffs => services.GetRequiredService<BuffService>();

    /// <summary>The panel ops of this file, or null when the op is not one of them.</summary>
    private async Task<string?> PanelOpAsync(GameSession s, int op, string[] args)
    {
        switch (op)
        {
            case OpStrengthen:
            case OpRefine:
                {
                    if (!PartGrids.TryGetValue(I(Arg(args, 0)), out var grids)) throw new ArgumentException("part 1-6");
                    int level = Has(args, 1) ? I(args[1]) : 1;
                    return await services.GetRequiredService<EquipmentService>().GmSetLevelAsync(s, grids, level, op == OpRefine);
                }
            case OpResetAttrPnt:
                await character.ResetStatsAsync(s);
                return "Stat points reset.";
            case OpChangeHp:
                return await ChangeHpAsync(s, Target(s, Arg(args, 0)), Has(args, 1) ? L(args[1]) : 0, Has(args, 2) ? L(args[2]) : 0);
            case OpDropPool:
                return await DropPoolAsync(s, L(Arg(args, 0)));
            case OpAddNpc:
                {
                    int npc = I(Arg(args, 0));
                    var at = new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, s.Face);
                    var unit = await services.GetRequiredService<MonsterService>().SpawnSingleAsync(s.RuntimeMap, npc, at);
                    return unit == null ? $"NPC {npc} is not a monster the server can spawn here." : $"Spawned {npc} (uid {unit.Uid}).";
                }
            case OpKillEntity:
                {
                    var t = Target(s, Arg(args, 0));
                    return await ChangeHpAsync(s, t, 0, t.Monster?.Hp ?? character.Stats(t.Player!)[AttrHp]);
                }
            case OpDumpEntityAttr:
                return Describe(Target(s, Arg(args, 0)), null);
            case OpShowAttr:
                return Describe(Target(s, Arg(args, 1)), Has(args, 0) ? I(args[0]) : null);
            case OpSetEntityAttr:
            case OpChangeEntityAttr:
                return await SetAttrAsync(s, Target(s, Arg(args, 0)), I(Arg(args, 1)), L(Arg(args, 2)), op == OpChangeEntityAttr);
            case OpAddBuff:
                return await AddBuffAsync(s, I(Arg(args, 0)), Has(args, 1) ? I(args[1]) : 0, Target(s, Arg(args, 2)));
            case OpRemoveBuff:
                return await RemoveBuffAsync(I(Arg(args, 0)), Target(s, Arg(args, 1)));
            case OpShowBuff:
                return ShowBuffs(Target(s, Arg(args, 0)));
            case OpNoCd:
                s.GmNoCooldown = Arg(args, 0) == "1";
                if (s.GmNoCooldown) { s.SkillCds.Clear(); s.SkillModeCds.Clear(); }
                return s.GmNoCooldown ? "No cooldowns: on (this session)." : "No cooldowns: off.";
            case OpDamageFix:
                // GMConfig 2005 "damage fluctuation": 关闭 (off) sends 1, 开启 (on) sends 0.
                Combat.FixedDamageRoll = Arg(args, 0) == "1";
                return Combat.FixedDamageRoll ? "Damage fluctuation off: every hit rolls 100% (whole server, until restart)."
                    : "Damage fluctuation on.";
            case OpDumpServerLevelInfo:
                return $"Server level {character.ServerLevel()}.";
            case OpSendMailPerson:
                return await MailAsync(s, [s], args, 2);
            case OpSendMailSystemByCustom:
                return await MailAsync(s, OnlineRoles(), args, -1);
            case OpSendMailSystemByCfgId:
                return await TemplateMailAsync(I(Arg(args, 0)), Has(args, 1) ? I(args[1]) : 0);
            case OpFullHpMp:
                {
                    var stats = character.Stats(s);
                    await character.SetHpSpAsync(s, stats[AttrMaxHp], stats[AttrMaxSp]);
                    return "HP and SP full.";
                }
            case OpShowLocation:
                return string.Create(CultureInfo.InvariantCulture,
                    $"Map {s.SceneEntry} {data.MapName(s.SceneEntry)} at {s.Position.X:F1} {s.Position.Y:F1} {s.Position.Z:F1}, facing {s.Face:F0}");
            case OpUnlimit:
                s.GmInvincible = Has(args, 0) ? args[0] != "0" : !s.GmInvincible;
                return s.GmInvincible ? "Invincible: on (this session)." : "Invincible: off.";
            case OpFlyToTarget:
                return await FlyToAsync(s, Target(s, Arg(args, 0)));
            case OpDungeonCmd:
                if (Arg(args, 0) != "leave") return "Use Enter Dungeon to enter a dungeon.";
                await services.GetRequiredService<DungeonService>().LeaveAsync(s);
                return "Leaving the dungeon.";
            case OpEnterDungeon:
                {
                    long scene = L(Arg(args, 0));
                    await services.GetRequiredService<DungeonService>().EnterAsync(s, new EnterDungeonReq { SceneEntry = scene });
                    return $"Dungeon {scene}: entry requested.";
                }
            case OpOpGuild:
                return await GuildAsync(s, args);
            case OpSetEntityCamp:
                {
                    var t = Target(s, Arg(args, 0));
                    int camp = I(Arg(args, 1));
                    if (t.Monster == null) return "Only a monster's camp can be changed.";
                    return await services.GetRequiredService<WorldEffectsService>().SetCampAsync(t.Monster, camp)
                        ? $"Monster {t.Monster.Uid} camp {camp}." : $"Camp {camp} cannot be set (neutral is 0).";
                }
            case OpTask:
                {
                    int option = I(Arg(args, 0));
                    if (option < 0 || option >= TaskOps.Length) throw new ArgumentException("task state");
                    return await tasks.GmAsync(s, TaskOps[option], L(Arg(args, 1)));
                }
            case OpCurAllSkillLevUpMax:
                {
                    var skills = services.GetRequiredService<SkillService>();
                    if (Arg(args, 0) != "1")
                    {
                        await skills.ResetAsync(s, new SkillResetReq { Plan = skills.Build(s).CurrentPlanIndex });
                        return "Skills reset.";
                    }
                    int levels = await skills.GmLearnAllAsync(s);
                    return levels == 0 ? "No skill could be raised (no skill points left, or all at maximum)." : $"Learned {levels} skill levels.";
                }
            default:
                return null;
        }
    }

    /// <summary>The unit a panel op is aimed at: the GM (0 or empty), an online player by role id, or a world unit.</summary>
    private (GameSession? Player, MonsterUnit? Monster, WorldUnit? Unit) Target(GameSession s, string id)
    {
        long uid = string.IsNullOrWhiteSpace(id) ? 0 : L(id);
        if (uid == 0 || uid == s.Role!.Id) return (s, null, null);
        if (sessions.All().FirstOrDefault(o => o.Role?.Id == uid) is { } player) return (player, null, null);
        var unit = World.AllUnits().FirstOrDefault(u => u.Uid == uid) ?? throw new ArgumentException($"no unit {uid}");
        return (null, unit as MonsterUnit, unit);
    }

    private async Task<string> ChangeHpAsync(GameSession s, (GameSession? Player, MonsterUnit? Monster, WorldUnit? Unit) t, long heal, long damage)
    {
        if (t.Monster is { } m)
        {
            if (heal > 0) await Combat.GmHealMonsterAsync(m, heal);
            if (damage > 0 && await Combat.GmDamageMonsterAsync(s, m, damage) == null) return $"Monster {m.Uid} cannot be hit from here.";
            return m.Dead || m.Hp == 0 ? $"Monster {m.Uid} killed." : $"Monster {m.Uid} HP {m.Hp}/{m.Template.MaxHp}.";
        }
        if (t.Player is not { } p) return "That unit has no HP.";
        if (heal > 0) await character.SetHpSpAsync(p, character.Stats(p)[AttrHp] + heal, null);
        if (damage > 0) await Combat.GmDamagePlayerAsync(p, damage);
        var stats = character.Stats(p);
        return p.Dead ? $"{p.Role?.Name} died." : $"{p.Role?.Name} HP {stats[AttrHp]}/{stats[AttrMaxHp]}.";
    }

    private string Describe((GameSession? Player, MonsterUnit? Monster, WorldUnit? Unit) t, int? attr)
    {
        if (t.Player is { } p)
        {
            var stats = character.Stats(p);
            if (attr is { } id) return $"{p.Role?.Name}: attribute {id} = {stats[id]}";
            return $"{p.Role?.Name} (uid {p.Role?.Id}) Lv {p.Progress?.Level}: " + string.Join(", ",
                stats.Data.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        }
        if (t.Monster is { } m)
        {
            var tp = m.Template;
            var values = new Dictionary<int, long>
            {
                [AttrMaxHp] = tp.MaxHp, [AttrMaxSp] = tp.MaxSp, [103] = tp.Atk, [104] = tp.Matk, [105] = tp.Def, [106] = tp.Mdef,
                [AttrHp] = m.Hp, [2002] = tp.Level,
            };
            if (attr is { } id) return values.TryGetValue(id, out long v) ? $"Monster {m.Uid}: attribute {id} = {v}" : $"Monster {m.Uid}: attribute {id} unknown";
            return $"Monster {m.Uid} (NPC {tp.NpcId}) Lv {tp.Level}: HP {m.Hp}/{tp.MaxHp}, SP {tp.MaxSp}, ATK {tp.Atk}, MATK {tp.Matk}, "
                + $"DEF {tp.Def}, MDEF {tp.Mdef}, HIT {tp.Hit}, FLEE {tp.Flee}, CRI {tp.Cri}, element {tp.Element}, race {tp.Race}, "
                + $"camp {m.CampOverride ?? tp.Camp}{(m.Dead ? ", dead" : "")}";
        }
        return $"Unit {t.Unit?.Uid} (no attributes)";
    }

    private async Task<string> SetAttrAsync(GameSession s, (GameSession? Player, MonsterUnit? Monster, WorldUnit? Unit) t, int attr, long value, bool relative)
    {
        if (attr is not (AttrHp or AttrSp)) return $"Only current HP ({AttrHp}) and SP ({AttrSp}) can be changed; other attributes come from the character's build.";
        if (t.Monster is { } m)
        {
            if (attr != AttrHp) return "Monsters have no SP.";
            long target = relative ? m.Hp + value : value;
            return target < m.Hp
                ? await ChangeHpAsync(s, t, 0, m.Hp - Math.Max(0, target))
                : await ChangeHpAsync(s, t, target - m.Hp, 0);
        }
        if (t.Player is not { } p) return "That unit has no attributes.";
        long current = character.Stats(p)[attr];
        long next = relative ? current + value : value;
        if (attr == AttrHp && next < current) return await ChangeHpAsync(s, t, 0, current - Math.Max(0, next));
        await character.SetHpSpAsync(p, attr == AttrHp ? next : null, attr == AttrSp ? next : null);
        return $"{p.Role?.Name}: attribute {attr} = {character.Stats(p)[attr]}";
    }

    private async Task<string> AddBuffAsync(GameSession s, int buff, int durationMs, (GameSession? Player, MonsterUnit? Monster, WorldUnit? Unit) t)
    {
        if (data.Row("BuffConfig", buff) is not { } row) return $"No buff {buff}.";
        string name = data.TextOf(row, "_iName");
        if (t.Player is { } p) await Buffs.AddAsync(p, buff, durationMs, s.Role!.Id);
        else if (t.Unit is { } u) await Buffs.AddSupportAsync(u, buff, durationMs, s.Role!.Id);
        long ms = durationMs > 0 ? durationMs : GameData.Int(row, "_iTime") is var time and > 0 ? time : 60000;
        return string.Create(CultureInfo.InvariantCulture, $"Buff {buff} {name} for {ms / 1000d:0.#} s.");
    }

    private async Task<string> RemoveBuffAsync(int buff, (GameSession? Player, MonsterUnit? Monster, WorldUnit? Unit) t)
    {
        int removed = 0;
        if (t.Player is { } p)
            foreach (var active in p.Buffs.Where(b => b.ConfigId == buff && !b.IsPassive && !b.IsTotemOwned).ToArray()) { await Buffs.RemoveAsync(p, active); removed++; }
        else if (t.Unit is { } u)
        {
            removed = Buffs.UnitBuffs(u).Count(b => b.ConfigId == buff);
            await Buffs.RemoveUnitConfigAsync(u, buff);
        }
        return removed == 0 ? $"No buff {buff} to remove." : $"Removed buff {buff}.";
    }

    private string ShowBuffs((GameSession? Player, MonsterUnit? Monster, WorldUnit? Unit) t)
    {
        long now = MonsterService.NowMs;
        var list = t.Player is { } p ? p.Buffs.ToList() : t.Unit is { } u ? Buffs.UnitBuffs(u).ToList() : [];
        if (list.Count == 0) return "No buffs.";
        return "Buffs: " + string.Join(", ", list.Select(b =>
        {
            string name = data.Row("BuffConfig", b.ConfigId) is { } row ? data.TextOf(row, "_iName") : "";
            string left = b.IsPassive ? "passive" : $"{Math.Max(0, b.ExpireMs - now) / 1000}s";
            return $"{b.ConfigId} {name} x{b.PileCount} ({left})";
        }));
    }

    private async Task<string> DropPoolAsync(GameSession s, long pool)
    {
        var drops = services.GetRequiredService<RewardService>().RollDropPool(pool, s);
        if (drops.Count == 0) return data.Row("DropPoolConfig", pool) == null ? $"No drop pool {pool}." : $"Drop pool {pool} dropped nothing this time.";
        var results = new List<string>();
        foreach (var (entry, count) in drops) results.Add(await character.AddItemAsync(s, entry, count));
        return string.Join("\n", results);
    }

    private async Task<string> FlyToAsync(GameSession s, (GameSession? Player, MonsterUnit? Monster, WorldUnit? Unit) t)
    {
        if (t.Player is { } p)
        {
            if (ReferenceEquals(p, s)) return "That is you.";
            if (p.SceneEntry != s.SceneEntry || p.InstanceId != s.InstanceId)
            {
                await WarpAsync(s, p.SceneEntry, p.Position.X, p.Position.Z);
                return $"Warped to {p.Role?.Name}.";
            }
            await scene.TeleportInMapAsync(s, new MapPoint(p.Position.X, p.Position.Y, p.Position.Z, s.Face));
            return $"Teleported to {p.Role?.Name}.";
        }
        var u = t.Unit!;
        if (u.WorldScene != s.WorldScene) return $"Unit {u.Uid} is on another map ({u.SceneEntry}).";
        await scene.TeleportInMapAsync(s, new MapPoint(u.X + 2, u.Y, u.Z, s.Face));
        return $"Teleported to unit {u.Uid}.";
    }

    private async Task<string> GuildAsync(GameSession s, string[] args)
    {
        // GMConfig 3002 fills its "create,{1}" template: the first field is the command, the second the name.
        if (Arg(args, 0) != "create" || !Has(args, 1)) return "Usage: create,<guild name>";
        GuildHeadIcon icon = new();
        foreach (var row in data.Table("GuildIconConfig").Values.OrderBy(r => GameData.Int(r, "_iID")))
        {
            if (GameData.Int(row, "_iCreateShow") == 0 || GameData.Int(row, "_iDefaultUnlock") == 0) continue;
            long type = GameData.Int(row, "_iType"), id = GameData.Int(row, "_iID");
            if (type == 1 && icon.BackgroundType == 0) icon.BackgroundType = id;
            if (type == 2 && icon.IconType == 0) icon.IconType = id;
        }
        await services.GetRequiredService<GuildService>().CreateAsync(s, new GuildCreateReq { Name = args[1], Icon = icon });
        return $"Guild '{args[1]}': creation requested (the guild window shows the result).";
    }

    /// <summary>
    /// GMConfig 1017 / 1019 custom mail: title, content, mail type or area, NPC, expiry, reward value and reward kind
    /// (0 drop pool, 1 item, 2 award). An item value is "id" or "id:count", several joined by ';'. The expiry is in days.
    /// </summary>
    private async Task<string> MailAsync(GameSession s, IReadOnlyList<GameSession> recipients, string[] args, int categoryField)
    {
        string title = Arg(args, 0), content = Arg(args, 1);
        if (title.Length == 0) throw new ArgumentException("title");
        int category = categoryField >= 0 && Has(args, categoryField) ? I(args[categoryField]) : 1;
        DateTimeOffset? expires = Has(args, 4) && I(args[4]) > 0 ? DateTimeOffset.UtcNow.AddDays(I(args[4])) : null;
        string value = Arg(args, 5);
        int kind = Has(args, 6) ? I(args[6]) : 1;
        var mail = services.GetRequiredService<MailService>();
        var rewards = services.GetRequiredService<RewardService>();
        int sent = 0;
        foreach (var to in recipients)
        {
            var goods = new List<MailService.Attachment>();
            if (value.Length > 0)
            {
                if (kind == 1)
                    foreach (var part in value.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var pair = part.Split([':', '*'], StringSplitOptions.TrimEntries);
                        goods.Add(new MailService.Attachment(I(pair[0]), pair.Length > 1 ? I(pair[1]) : 1));
                    }
                else if (kind == 0)
                    goods.AddRange(rewards.RollDropPool(L(value), to).Select(d => new MailService.Attachment(d.Entry, d.Count)));
                else
                    goods.AddRange(rewards.RollAwardBound(L(value), to).Select(d => new MailService.Attachment(d.Entry, d.Count, d.Bind)));
            }
            await mail.SendRawAsync(to.Role!.Id, title, content.Length > 0 ? content : title, goods, expiresAt: expires,
                category: category is 1 or 3 ? category : 1);
            sent++;
        }
        return recipients.Count == 1 && ReferenceEquals(recipients[0], s) ? $"Mail '{title}' delivered." : $"Mail '{title}' delivered to {sent} online characters.";
    }

    /// <summary>GMConfig 1018: a MailConfig template to every online character (offline characters are not reached).</summary>
    private async Task<string> TemplateMailAsync(int templateId, int days)
    {
        if (data.Row("MailConfig", templateId) == null) return $"No MailConfig {templateId}.";
        var mail = services.GetRequiredService<MailService>();
        DateTimeOffset? expires = days > 0 ? DateTimeOffset.UtcNow.AddDays(days) : null;
        int sent = 0;
        foreach (var to in OnlineRoles())
        {
            try { await mail.SendTemplateAsync(to.Role!.Id, templateId, expires); sent++; }
            catch (ArgumentException e) { return $"Mail {templateId}: {e.Message}"; }
        }
        return $"Mail {templateId} delivered to {sent} online characters.";
    }

    private List<GameSession> OnlineRoles() => sessions.All().Where(o => o.Role != null).ToList();

    private static string Arg(string[] args, int index) => index < args.Length ? args[index].Trim() : "";
    private static bool Has(string[] args, int index) => Arg(args, index).Length > 0;
}
