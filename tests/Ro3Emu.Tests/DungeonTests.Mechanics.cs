using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task TheStoryPharaohCannotCastCleansingLightOutsideTheShelterMechanic()
    {
        const long scene = 3120702;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        var pharaoh = await BossIn(await Load(alice, scene), 5300010);
        await Gm(alice, FormattableString.Invariant($"!actors walk {pharaoh.X} {pharaoh.Z}"));
        await OnLoop(() =>
        {
            var book = Assert.IsType<BossSkillBook>(pharaoh.Template.Skills);
            int index = Array.FindIndex(book.Skills, skill => skill.Id == 5340301);
            Assert.True(index >= 0); // the generic AI also knows this skill from the client's kit
            pharaoh.BossSkills = new BossSkillRuntime(book) { Engaged = true };
            Array.Fill(pharaoh.BossSkills.ReadyMs, long.MaxValue);
            pharaoh.BossSkills.ReadyMs[index] = 0;
            pharaoh.NextSpecialMs = pharaoh.BusyUntilMs = 0;
            pharaoh.Target = Session(alice);
            Assert.False(Service<MonsterService>().TryBossSkill(pharaoh, MonsterService.NowMs));
            int other = Array.FindIndex(book.Skills, skill => skill.Id != 5340301);
            Assert.True(other >= 0);
            pharaoh.BossSkills.ReadyMs[other] = 0;
            Assert.True(Service<MonsterService>().TryBossSkill(pharaoh, MonsterService.NowMs)); // other specials still work
            return Task.CompletedTask;
        });
    }

    // Moonlight Flower's hide-and-seek (inferred): standing in the open is found and the game starts over; a darkness
    // point passes a round and is searched, so hiding there again is found; another side of the room wins the second round.
    [ClientDataFact]
    public async Task MoonlightFlowersHideAndSeekIsWonByHidingInTwoPlaces()
    {
        const long scene = 3120802;
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        await using var alice = await InWorld("Alice");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var seeker = Assert.Single(await ActorsIn(alice, 1));
        Assert.Equal(400097, seeker.NpcId);
        var map = Service<WorldData>().Map(scene)!;
        Task Hide(long point) => Gm(alice, FormattableString.Invariant($"!actors walk {map.Points[point].X} {map.Points[point].Z}"));
        Task<SystemBoardCastMsgNtf> Says(int chat) => Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == chat);

        // Her opening bubbles over her own head, then the first round in the open: found.
        Assert.Equal(seeker.Uid, (await Says(5310011)).PlayerId);
        await Says(5310005); // "I'm about to catch you!"
        await Says(5310010); // the countdown tip
        await Says(5310014); // "You're no good at hide-and-seek!"
        // In the south-west darkness: the round passes; staying there is found, the place having been searched.
        await Hide(67);
        await Says(5310006); // "Play with me a little longer!"
        await Says(5310014);
        // The game started over: the same place passes round one again, the south-east corner wins round two.
        await Says(5310006);
        await Hide(78);
        await Says(5310007); // "I had a great time~"
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
    }

    // The story Pharaoh's Cleansing Light (inferred): the raid's tip, then his strongest skill on whoever stands outside the
    // Pyramid's shadow.
    [ClientDataFact]
    public async Task ThePharaohsCleansingLightHitsWhoeverIsNotBehindThePyramid()
    {
        const long scene = 3120702;
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var pyramid = Assert.Single(await ActorsIn(alice, 1, a => a.NpcId == 5300011));
        var point = Service<WorldData>().Map(scene)!.Points[20];
        Assert.Equal((point.X + 6, point.Z + 7), (pyramid.X, pyramid.Z)); // beside Pharaoh's point, no point of its own
        var pharaoh = await BossIn(instance, 5300010);
        long me = Session(alice).Role!.Id;
        await Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == 4370002); // "hide behind the Pyramid!"
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 5340301 && h.Dsts.Any(d => d.DstID == me));
        Assert.Equal(pharaoh.Uid, hit.SrcID);
    }

    [ClientDataFact]
    public async Task ThePyramidProtectsThePlayerAndCleansingLightStopsAfterVictory()
    {
        const long scene = 3120702;
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 20;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        var pyramid = Assert.Single(await ActorsIn(alice, 1, a => a.NpcId == 5300011));
        var pharaoh = await BossIn(instance, 5300010);
        long me = Session(alice).Role!.Id;
        Task<SystemBoardCastMsgNtf> Warning() => Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == 4370002);
        await Warning();
        float dx = pyramid.X - pharaoh.X, dz = pyramid.Z - pharaoh.Z;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        await Gm(alice, FormattableString.Invariant($"!actors walk {pyramid.X + dx / length * 2} {pyramid.Z + dz / length * 2}"));
        int start = alice.Inbox.Snapshot().Count;
        await Warning(); // one entire strike and the next warning have passed while behind cover
        Assert.DoesNotContain(alice.Inbox.Snapshot().Skip(start).Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SkillId == 5340301 && h.Dsts.Any(d => d.DstID == me));
        using var exposed = alice.Inbox.Expect(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body) is { SkillId: 5340301 } h && h.Dsts.Any(d => d.DstID == me));
        await Gm(alice, FormattableString.Invariant($"!actors walk {pharaoh.X} {pharaoh.Z}"));
        await exposed.WaitAsync(Wait);
        await Kill(alice, pharaoh);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        int settled = alice.Inbox.Snapshot().Count;
        await Task.Delay(4500); // beyond the next scheduled warning
        Assert.DoesNotContain(alice.Inbox.Snapshot().Skip(settled), e => e.IsGame(1022)
            && SystemBoardCastMsgNtf.Parser.ParseFrom(e.Body).ChatId == 4370002);
    }

}
