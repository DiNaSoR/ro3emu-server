using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Xunit;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [Fact]
    public void SkillCostModifierEditsTheNamedCostNumber()
    {
        using var doc = JsonDocument.Parse("""{"_iID":1140011,"_iSkillID":11400,"_kCost":[[1,2000,0,30],[1,2001,0,12]]}""");
        // BuffConfig 1153401: [0, 11400, 35, 5, -10000, 0, 3] makes the first cost tuple's flat amount free.
        using var tuple = JsonDocument.Parse("""["0","11400","35","5","-10000","0","3"]""");
        var free = SkillParameterRules.Parse(tuple.RootElement)!.Value;
        Assert.Equal((0, 3), (free.Index, free.Element));
        var row = SkillParameterRules.Effective(doc.RootElement, [free], []);
        Assert.Equal("[[1,2000,0,0],[1,2001,0,12]]", row.GetProperty("_kCost").GetRawText());
        // Index -1 reaches every tuple.
        var all = free with { Operation = 1, Value = -5, Index = -1 };
        Assert.Equal("[[1,2000,0,25],[1,2001,0,7]]", SkillParameterRules.Effective(doc.RootElement, [all], []).GetProperty("_kCost").GetRawText());
        // Mana Recharge's scope-0 "-1" tuple is left to per_sp_dedution 1232, which its buff also grants.
        using var mana = JsonDocument.Parse("""["0","-1","35","5","-1000","0","3"]""");
        Assert.Null(SkillParameterRules.Parse(mana.RootElement));
    }

    [ClientDataFact]
    public async Task SpConsumptionPercentLowersSpCostOnServerAndClient()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4100"); await Teach("alice", 14001); // Heal 1400101: 10 SP
        var player = Session("alice");
        long Sp() => Character.Stats(player)[2001];
        // Mana Recharge 10 (buff 1420410): per_sp_dedution 1232 -1000 = SP consumption -10%.
        await Loop.InvokeAsync(async () =>
        {
            player.StatusBits |= 1L << 15; player.LastCombatMs = long.MaxValue / 2; // no regeneration under test
            await _app.Services.GetRequiredService<BuffService>().AddAsync(player, 1420410, 600000, player.Role!.Id);
        });
        var notice = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            n => n.AffectParamDict.ContainsKey(14001) && n.AffectParamDict[14001].AffectParam.ContainsKey(35));
        var cost = Assert.Single(notice.AffectParamDict[14001].AffectParam[35].AffectValueListList);
        Assert.Equal([1L, 2001, 0, 9], cost.AffectValueList);

        long full = await Loop.InvokeAsync(() => Task.FromResult(Sp()));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400101, TargetId = player.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Equal(full - 9, await Loop.InvokeAsync(() => Task.FromResult(Sp())));

        // 9 SP is now enough although the configured cost is 10.
        await Loop.InvokeAsync(async () => { player.SkillCds.Clear(); player.SkillModeCds.Clear(); await Character.SetHpSpAsync(player, null, 9); });
        await Task.Delay(600);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400101, TargetId = player.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Equal(0, await Loop.InvokeAsync(() => Task.FromResult(Sp())));
    }

    [ClientDataFact]
    public async Task SacrificeBasicAttackSwingsCostHp()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1400"); // Paladin: basic attack 1001101
        var player = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var skills = _app.Services.GetRequiredService<SkillService>();
        // Sacrifice 10 (buff 1150110) switches basic attacks 10011/10012/11400 to mode 10, whose rows cost 30 HP.
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(player, 1150110, 600000, player.Role!.Id));
        int normal = await Loop.InvokeAsync(() => Task.FromResult(skills.NormalAttack(player)));
        var row = await Loop.InvokeAsync(() => Task.FromResult(combat.EffectiveSkillRow(player, normal)));
        Assert.NotNull(row);
        Assert.Contains("[1,2000,0,30]", row!.Value.GetProperty("_kCost").GetRawText().Replace(" ", ""));
        int rowId = row.Value.GetProperty("_iID").GetInt32();

        var target = await Target("alice", stationary: true);
        long full = await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.FromResult(Character.Stats(player)[2000]); });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = rowId, TargetId = target.Uid });
        var reply = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, reply.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == reply.SessionId);
        // The swing the attack loop repeats on its own pays too.
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId != reply.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
        long hp = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(player)[2000]));
        Assert.True(hp <= full - 60, $"two swings should cost 60 HP: {full} -> {hp}");
    }
}
