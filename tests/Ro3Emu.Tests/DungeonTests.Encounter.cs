using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.ReconstructedDungeons;

namespace Ro3Emu.Tests;

/// <summary>Story boss encounters (Reconstructed/story-encounters.json, DungeonService.Encounter).</summary>
public sealed partial class DungeonTests
{
    private const long Eddga = 1022001;
    private const int EddgaNpc = 5300001, Wule = 5300086, Boya = 5300087;

    [ClientDataFact]
    public void EveryStoryEncounterNamesClientRows()
    {
        var data = Service<GameData>();
        var monsters = Service<MonsterService>();
        var catalog = Service<DungeonCatalog>();
        var encounters = Service<ReconstructedDungeons>().AllEncounters;
        Assert.NotEmpty(encounters);
        foreach (var e in encounters)
        {
            Assert.NotNull(monsters.Template(e.Boss));
            Assert.All(e.BossSkills, s => Assert.NotNull(data.Row("SkillConfig", s.Skill)));
            foreach (var (npc, kit) in e.Kits)
            {
                Assert.NotNull(monsters.Template(npc));
                Assert.All(kit.Skills, id => Assert.True(data.Row("SkillConfig", id) != null, $"{npc}: skill {id}"));
            }
            var present = new HashSet<int> { e.Boss };
            var lines = e.BossSkills.Where(s => s.Line != 0).Select(s => new EncounterLine(s.Line, 0, 0)).Concat(e.WinLines).ToList();
            foreach (var stage in e.Stages)
            {
                Assert.True(stage.Quest == 0 || data.Table("DungeonQuestConfig").Values.Any(q =>
                    GameData.Int(q, "_iDungeonID") == e.Scene && GameData.Int(q, "_iQuestID") == stage.Quest), $"quest {stage.Quest}");
                foreach (var ally in stage.Allies)
                {
                    Assert.True(e.Kits.ContainsKey(ally.NpcId), $"{ally.NpcId} has no kit");
                    Assert.True(catalog.ScenePoint(e.Scene, ally.Point) != null, $"point {ally.Point}");
                    present.Add(ally.NpcId);
                }
                foreach (var line in stage.Lines.Concat(stage.EndLines))
                {
                    var chat = data.Row("ChatConfig", line.Chat);
                    Assert.True(chat != null, $"chat {line.Chat}");
                    // A bubble needs its speaker on the field by then.
                    if (GameData.Int(chat.Value, "_iType") == 2) Assert.True(present.Contains(line.By), $"chat {line.Chat} by {line.By}");
                }
            }
            Assert.All(lines, l => Assert.True(data.Row("ChatConfig", l.Chat) != null, $"chat {l.Chat}"));
        }
    }

    [Fact]
    public void EncounterHitAreasFollowTheBossFacing()
    {
        (float, float, float) facingPlusZ = (0, 0, 0), facingPlusX = (0, 0, 90);
        var circle = new EncounterHit(0, HitShape.Circle, 5, 0, 0, 0, 4, 10000);
        Assert.True(DungeonService.InArea(circle, facingPlusZ, 0, 9));    // 4 m ahead + 5 m
        Assert.False(DungeonService.InArea(circle, facingPlusZ, 0, -2.5f));
        Assert.True(DungeonService.InArea(circle, facingPlusX, 9, 0));
        var cone = new EncounterHit(0, HitShape.Cone, 6, 120, 0, 0, 0, 10000);
        Assert.True(DungeonService.InArea(cone, facingPlusZ, 2, 3));      // ~34 degrees off
        Assert.False(DungeonService.InArea(cone, facingPlusZ, 3, -1));    // behind
        Assert.False(DungeonService.InArea(cone, facingPlusZ, 0, 7));     // too far
        var rect = new EncounterHit(0, HitShape.Rect, 0, 0, 12, 4, 0, 10000);
        Assert.True(DungeonService.InArea(rect, facingPlusX, 11, 1.5f));
        Assert.False(DungeonService.InArea(rect, facingPlusX, 11, 3));
        Assert.False(DungeonService.InArea(rect, facingPlusZ, 11, 0));    // beside, not ahead
    }

    [ClientDataFact]
    public async Task EddgaFightBringsItsCompanionsLinesAndStages()
    {
        var parameters = (Dictionary<string, System.Text.Json.JsonElement>)Service<GameData>().Table("PublicParamConfig");
        parameters["FiveGamePlay_CloseTime"] = System.Text.Json.JsonSerializer.SerializeToElement(3);
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 60"); // Eddga's skills should not end the run before the companions do their part
        await Gm(alice, "!quest accept 10102200");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = Eddga }));
        long instance = await Load(alice, Eddga);
        var boss = await BossIn(instance, EddgaNpc);
        var boya = await BossIn(instance, Boya);
        var wule = await BossIn(instance, Wule);
        Assert.True(boya.ScriptedTargeting && wule.ScriptedTargeting && boss.ScriptedTargeting);
        // Eddga cannot fall in stage 1: hits stop at 60% until the adventurers arrive.
        Assert.Equal(boss.Template.MaxHp * 60 / 100, boss.HpFloor);
        Assert.Equal(boss.HpFloor, boss.HpAfter(boss.HpFloor + 10, 1_000_000));

        // Stage 1: the tracker shows "Cover Boya and Wule"; Boya's first line is her own bubble.
        await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            info => info.Vars.Any(v => v.VarId == 10001 && v.Vi64.LastOrDefault() == 1));
        var bubble = await Next(alice, DungeonService.Event, DungeonEventNotify.Parser, e => e.CliFuncname == "ShowShoutingChatBubble");
        Assert.Equal(5300018, bubble.Params[0].IntParams.Single());
        Assert.Equal(boya.Uid, bubble.Params[1].IntParams.Single());
        await Next(alice, DungeonService.Event, DungeonEventNotify.Parser,
            e => e.CliFuncname == "Shouting" && e.Params[0].IntParams.Single() == 5300019);
        // Wule and Boya fight Eddga with their own swings.
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => (hit.SrcID == boya.Uid || hit.SrcID == wule.Uid) && hit.Dsts.Any(d => d.DstID == boss.Uid && d.Damage > 0));

        // Stage 2: the flare brings the two parties of six; the tracker moves on.
        Assert.True(await Service<GameLoop>().InvokeAsync(() =>
            Service<DungeonService>().SkipEncounterStageAsync(Session(alice).DungeonAttempt!)));
        await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            info => info.Vars.Any(v => v.VarId == 10001 && v.Vi64.LastOrDefault() == 2));
        var party = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits().OfType<MonsterUnit>()
            .Where(m => m.InstanceId == instance && m.Template.NpcId is >= 5300007 and <= 5300009 or >= 5300070 and <= 5300078)
            .ToArray()));
        Assert.Equal(12, party.Length);
        Assert.Equal(0, boss.HpFloor); // the last stage can be won
        var uids = party.Select(m => m.Uid).ToHashSet();
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => uids.Contains(hit.SrcID) && hit.Dsts.Any(d => d.DstID == boss.Uid && d.Damage > 0));

        // A companion's last hit settles the run as the player's win.
        await OnLoop(() => { boss.Hp = 1; return Task.CompletedTask; });
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        Assert.True(boss.Dead);
        await Next(alice, DungeonService.Event, DungeonEventNotify.Parser,
            e => e.CliFuncname == "Shouting" && e.Params[0].IntParams.Single() == 5300012);

        // The won story run sends the player back out after its countdown (shortened here from the client's 15 s).
        var countdown = await Next(alice, DungeonService.Event, DungeonEventNotify.Parser, e => e.CliFuncname == "SetCountDown");
        Assert.True(countdown.Params[0].IntParams.Single() > _clock.GetUtcNow().ToUnixTimeSeconds());
        await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser, n => n.Content.StartsWith("Leave the dungeon in"));
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry != Eddga); // back where the run began
    }
}
