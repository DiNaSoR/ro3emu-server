using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // Talent tree 10102 (a node's effect depends on the job). Knight: gate 10102003, Pierce Enhance 10102101 > Rapid Pierce
    // 10102102, Swift Sword 10102105 > Double Counter 10102107. Wizard: 10102103 Elemental Pursuit, 10102126 Flame Burst.
    // The tree opens at a total talent level of 40.
    private async Task ChooseProfessionTalent(BotClient bot, string name, int option)
    {
        await Gm(name, "!quest done 10101170");
        await Gm(name, "!quest done 10103160");
        var data = _app.Services.GetRequiredService<GameData>();
        await Loop.InvokeAsync(async () =>
        {
            var s = Session(name);
            var plan = s.SeasonTalent!.Plan[s.SeasonTalent.CurPlanId];
            foreach (var row in data.Table("SeasonTalentConfig").Values)
            {
                long tree = GameData.Int(row, "_iTalentTreeID");
                if ((tree == 10101 || tree == 10102 && GameData.Int(row, "_iType") == 1)
                    && plan.TalentNodes.TryGetValue(GameData.Int(row, "_iId"), out var node))
                    node.CurrentLevel = (int)GameData.Int(row, "_iMaxLevel");
            }
            await _app.Services.GetRequiredService<SeasonTalentService>().SyncAsync(s);
        });
        await bot.SendGameAsync(SeasonTalentService.Upgrade, new SeasonTalentUpgradeReq { TalentId = option });
        Assert.Equal(0, (await Next(bot, SeasonTalentService.Upgrade, SeasonTalentUpgradeResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task RapidPierceAddsItsExtraHitsOnlyWhenItsChanceRolls()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1200"); await Teach("alice", 11120);
        await ChooseProfessionTalent(alice, "alice", 10102102);
        _app.Services.GetRequiredService<TalentProcs>().Roll = _ => 0;
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1112001, TargetId = monster.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        var proc = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == 1112101);
        Assert.Equal(monster.Uid, proc.Dsts.Single().DstID);
        Assert.True(proc.Dsts.Single().Damage > 0);

        // "a ${1}% Chance" (30): a roll at or above it brings no proc. Pierce's 1 s action outlasts the 0.8 s cooldown.
        _app.Services.GetRequiredService<TalentProcs>().Roll = bound => bound - 1;
        await Task.Delay(1200);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1112001, TargetId = monster.Uid });
        var again = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, x => x.SessionId != accepted.SessionId);
        Assert.Equal(0, again.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == again.SessionId);
        await Task.Delay(300);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body) is { SkillId: 1112101 } x && x.SessionId != proc.SessionId);
    }

    [ClientDataFact]
    public async Task DoubleCounterCastsBowlingBashOneExtraTime()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1200"); await Teach("alice", 11104);
        await ChooseProfessionTalent(alice, "alice", 10102107);
        _app.Services.GetRequiredService<TalentProcs>().Roll = _ => 0;
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1110401, TargetId = monster.Uid, TargetPos = monster.Pos.Clone() });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        var extra = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == 1112701);
        Assert.Contains(extra.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
    }

    // Elemental Pursuit takes its values from its _kOtherDesc buff: "After [Fire Bolt] ... hits, it triggers
    // [Elemental Pursuit] ... to the target."
    [ClientDataFact]
    public async Task ElementalPursuitFollowsFireBolt()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2200"); await Teach("alice", 12004);
        await ChooseProfessionTalent(alice, "alice", 10102103);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = monster.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var proc = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == 1240301);
        Assert.Contains(proc.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
    }

    // Opening has no proc row: "After casting [Basic Attack], there is a ${1}% Chance to Deals DMG to the enemy an Extra
    // ${2} times, each dealing PATK * ${3}% + ${4}". The extra hit is the attack's own row with that damage.
    [ClientDataFact]
    public async Task OpeningAddsADescribedHitToTheBasicAttack()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5200"); await Teach("alice", 15000);
        await ChooseProfessionTalent(alice, "alice", 10102103);
        _app.Services.GetRequiredService<TalentProcs>().Roll = _ => 0;
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1500001, TargetId = monster.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        var extra = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            x => x.SkillId == 1500001 && x.SessionId != accepted.SessionId && x.SrcID == Session("alice").Role!.Id);
        Assert.Contains(extra.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
    }

    // Meteor Storm leaves a field, so Flame Burst centres on its target point: "After using [Meteor Storm], immediately
    // triggers [Fire Explosion] ... to up to ${2} enemies within a ${1}-meter radius."
    [ClientDataFact]
    public async Task FlameBurstFollowsAMeteorStormField()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2200"); await Teach("alice", 12106);
        await ChooseProfessionTalent(alice, "alice", 10102126);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210601, TargetPos = monster.Pos.Clone() });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var proc = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == 1244201);
        Assert.Contains(proc.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
    }
}
