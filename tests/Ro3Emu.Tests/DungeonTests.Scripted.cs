using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Bot;

namespace Ro3Emu.Tests;

/// <summary>
/// Scripted story scenes without a fight (Reconstructed/scripted-scenes.json, DungeonService.Scripted): each scene is
/// entered, played through the events its client sends (32700 on the scene actors, walks into server regions, 84011
/// at a story's end), settles as a clear, and the clear counts for its story step so the next one is offered.
/// </summary>
public sealed partial class DungeonTests
{
    public static TheoryData<long> ScriptedScenes() => new(new ReconstructedDungeons(
        Options.Create(new ServerOptions())).AllScripted.Select(s => s.Scene).Order());

    [ClientDataTheory]
    [MemberData(nameof(ScriptedScenes))]
    public void AScriptedSceneResolvesToOneStepPerConfiguredStageAgainstTheClientData(long scene)
    {
        var scripted = Service<ReconstructedDungeons>().Scripted(scene)!;
        var plan = Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = scene });
        Assert.NotNull(plan); // every actor, point, region, buff, plot, interaction and message resolved
        Assert.Equal(scripted.Steps.Count, plan.Waves.Count);
        Assert.All(plan.Waves, w => Assert.Empty(w.Spawns));
        Assert.Equal(scripted.Steps, plan.Waves.Select(w => w.Step!));
        // The tracker walks the scene's configured DungeonQuestConfig quests in order, each of them.
        var quests = Service<GameData>().Table("DungeonQuestConfig").Values
            .Where(q => GameData.Int(q, "_iDungeonID") == scene).Select(q => (int)GameData.Int(q, "_iQuestID")).Order();
        Assert.Equal(quests, plan.Waves.Select(w => w.Quest).Distinct());
        Assert.Equal(plan.Waves.Select(w => w.Quest).Order(), plan.Waves.Select(w => w.Quest));
    }

    [ClientDataFact]
    public async Task TheVortexScenePlaysFromTheCorridorThroughTheJumpToTheEndOfThePassage()
    {
        const long scene = 1000201;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 10104290"); // Enter the Ant Queen's Nest: [101018, 1000201, 1]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var bobo = Assert.Single(await ActorsIn(alice, 1));
        Assert.Equal((5300027, 1), (bobo.NpcId, bobo.EntityType)); // Bobo, a monster-type unit the client creates

        // Explore ahead: the corridor region 25 (where guide point 24 ends) is the server's to notice.
        var map = Service<WorldData>().Map(scene)!;
        await WalkInto(alice, map.Regions[25]);
        await Stage(alice, 2, quest: 2);
        var vortex = Assert.Single(await ActorsIn(alice, 1, a => a.NpcId == 5300030));
        Assert.DoesNotContain(await ActorsIn(alice, 1), a => a.NpcId == 5300027); // Bobo was sucked in

        // Jump into the vortex: the client's auto interaction 5300030 in range; then the passage below.
        Assert.Equal(ErrorCodes.NpcTooFar, await Interact(alice, vortex, 5300030)); // not from the corridor's far end
        await Beside(alice, vortex.X, vortex.Y, vortex.Z);
        Assert.Equal(0, await Interact(alice, vortex, 5300030));
        await Stage(alice, 3, quest: 3);
        var top = map.Points[21];
        Assert.True(MonsterService.Distance(Session(alice).Position.X, Session(alice).Position.Z, top.X, top.Z) < 1,
            "the jump lands at point 21, the top of the passage");
        await ActorsIn(alice, 0); // nothing is shown now

        await WalkInto(alice, map.Regions[22]); // the bottom of the passage
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        await StoryMovesOn(alice, 10104290, 10104300);
        Assert.DoesNotContain(Service<World>().AllUnits(), u => u.InstanceId == instance);
    }

    [ClientDataFact]
    public async Task TheDeniroSceneOpensOneInteractionPerStepUntilTheRideToTheDepths()
    {
        const long scene = 1000301;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 10104300"); // Explore the Ant Queen's Nest: [101018, 1000301, 1]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var actors = await ActorsWhen(alice, shown => shown.Count == 2 && shown.Any(a => a.Buffs.Count > 0));
        var bobo = actors.Single(a => a.NpcId == 5300041);
        var deniro = actors.Single(a => a.NpcId == 5300005);
        // Bobo carries buff group 53014, the NPC condition of 'Rescue Bobo'; the Deniro carries nothing yet.
        Assert.Equal([5301401], bobo.Buffs.Select(b => b.ConfigId));
        Assert.Empty(deniro.Buffs);
        var seen = alice.Inbox.Snapshot().Where(e => e.IsGame(GameAoiSink.SyncAoiView))
            .SelectMany(e => AoIViewChangeData.Parser.ParseFrom(e.Body).Bases).Last(b => b.Uuid == (ulong)bobo.Uid);
        Assert.Equal((5300041UL, 1, 5301401), (seen.EntityTemplateID, seen.EntityType, seen.BuffData.AddBuffs.Single().ConfigId));

        await Beside(alice, deniro.X, deniro.Y, deniro.Z);
        Assert.Equal(0, await Interact(alice, deniro, 5300005)); // the Deniro's own ride, not this step's event
        Assert.Equal(1, Session(alice).DungeonAttempt!.Wave + 1);
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(alice, deniro, 5300028)); // not one of the Deniro's

        (int Npc, int Interaction, int Buff)[] chain =
        [
            (5300041, 5300028, 5301401), // help Bobo up
            (5300005, 5300005, 5365401), // try to ride the Deniro
            (5300055, 5300010, 5365201), // collect the fluorescent substance
            (5300005, 5300009, 5365301), // feed the Deniro
            (5300005, 5300008, 5365101), // ride to the depths
        ];
        for (int step = 0; step < chain.Length; step++)
        {
            if (step > 0) await Stage(alice, step + 1, quest: step + 1); // the first one was awaited above
            var (npc, buff) = (chain[step].Npc, chain[step].Buff);
            // exactly one interaction is open: the actor of this step carries its buff, no other actor carries one
            var shown = await ActorsWhen(alice, all => all.Count == (step == 2 ? 3 : 2) && all.Count(a => a.Buffs.Count > 0) == 1
                && all.Any(a => a.NpcId == npc && a.Buffs.Select(b => b.ConfigId).SequenceEqual([buff])));
            var actor = shown.Single(a => a.NpcId == npc);
            Assert.All(shown.Where(a => a != actor), a => Assert.Empty(a.Buffs));
            if (step == 1)
                // The kept Deniro got its buff in place (31104), without leaving and re-entering the view.
                await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                    n => n.UnitId == actor.Uid && n.Buffs.Any(b => b.ConfigId == 5365401));
            await Beside(alice, actor.X, actor.Y, actor.Z);
            Assert.Equal(0, await Interact(alice, actor, chain[step].Interaction));
        }
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        var end = Service<WorldData>().Map(scene)!.Points[31]; // the ride ends in the end region
        Assert.True(MonsterService.Distance(Session(alice).Position.X, Session(alice).Position.Z, end.X, end.Z) < 1);
        await StoryMovesOn(alice, 10104300, 10104310);
    }

    [ClientDataFact]
    public async Task LouiesGamePlaysItsStoriesAndOnlyKatiesBellWins()
    {
        const long scene = 3120402;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 10106270"); // The Mysterious Thief Gang: [101018, 3120402, 1]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        // The intro story: dungeon var 100020016 [140010] in a var-change event, never in the 30401 snapshot.
        await Story(alice, 140010);
        await ActorsIn(alice, 0); // nothing is shown now
        await PlotEnd(alice, 140010);

        await Stage(alice, 2, quest: 1);
        var actors = await ActorsIn(alice, 6);
        Assert.Equal(3, actors.Count(a => a.NpcId == 400137)); // the bells
        var katie = actors.Single(a => a.NpcId == 400134);
        var wrong = actors.Single(a => a.NpcId == 400135);
        Assert.Equal(2, katie.EntityType); // TriggerNpcInteract looks the ring up as an NPC by its config id
        // Walking up opens the ring's dialogue (auto interaction 20003001, answered so the client runs action 53).
        await Beside(alice, wrong.X, wrong.Y, wrong.Z);
        Assert.Equal(0, await Interact(alice, wrong, 20004001));
        Assert.Equal(0, await Interact(alice, wrong, 5300041)); // 'Choose this one!': message 12, a wrong guess
        await Task.Delay(200);
        Assert.Equal(2, Session(alice).DungeonAttempt!.Wave + 1); // the game goes on
        await Beside(alice, katie.X, katie.Y, katie.Z);
        Assert.Equal(0, await Interact(alice, katie, 20003001));
        Assert.Equal(2, Session(alice).DungeonAttempt!.Wave + 1); // opening the dialogue is not choosing
        Assert.Equal(0, await Interact(alice, katie, 5300040)); // message 11: Katie found

        var reveal = await Stage(alice, 3, quest: 1);
        Assert.Equal([1L, 1L], reveal.Vars.Single(v => v.VarId == 10003).Vi64); // the game's goal shows (1/1)
        await Story(alice, 140020);
        await ActorsIn(alice, 0); // nothing is shown now
        await PlotEnd(alice, 140020);

        await Stage(alice, 4, quest: 2);
        var instrument = Service<WorldData>().Map(scene)!.Points[37];
        await WalkTo(alice, instrument.X + 6, instrument.Y, instrument.Z); // not yet within its 4 m
        await Task.Delay(200);
        Assert.False(Session(alice).DungeonAttempt!.Settled);
        await WalkTo(alice, instrument.X + 1, instrument.Y, instrument.Z);
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        await StoryMovesOn(alice, 10106270, 10106280);
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(DungeonService.BaseInfo))
            .SelectMany(e => DungeonBaseInfoNotify.Parser.ParseFrom(e.Body).Vars), v => v.VarId == DungeonService.StoryVar);
    }

    [Fact]
    public void TheSwitchOffLeavesTheScriptedScenesUnreconstructed()
    {
        var off = new ReconstructedDungeons(Options.Create(new ServerOptions { ReconstructedDungeons = false }));
        Assert.Null(off.Scripted(1000201));
        Assert.NotEmpty(off.AllScripted);
    }

    [ClientDataFact]
    public void ServerRegionsUseTheClientsCircleAndRectangleFootprints()
    {
        var map = Service<WorldData>().Map(1000201)!;
        var vortex = map.Regions[20]; // circle, radius 5 (its length)
        Assert.Equal(1, vortex.Shape);
        Assert.True(vortex.Contains(vortex.X + 4.9f, vortex.Z));
        Assert.False(vortex.Contains(vortex.X + 3.6f, vortex.Z + 3.6f));
        var end = map.Regions[22]; // rectangle 30 x 5: length along x, width along z
        Assert.Equal(2, end.Shape);
        Assert.True(end.Contains(end.X + 14.5f, end.Z + 2.4f));
        Assert.False(end.Contains(end.X, end.Z + 2.6f));
        Assert.False(end.Contains(end.X + 15.1f, end.Z));
        var turned = end with { Direction = 90 };
        Assert.True(turned.Contains(turned.X, turned.Z + 14.5f));
        Assert.False(turned.Contains(turned.X + 14.5f, turned.Z));
    }

    // ---- helpers ----

    /// <summary>The step's 30401: its stage number and the quest the tracker shows last in var 10001.</summary>
    private static Task<DungeonBaseInfoNotify> Stage(BotClient bot, int stage, int quest) =>
        Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            info => info.Stage == stage && info.Vars.Any(v => v.VarId == 10001 && v.Vi64.LastOrDefault() == quest));

    private static async Task Story(BotClient bot, long plot)
    {
        var e = await Next(bot, DungeonService.Event, DungeonEventNotify.Parser, n => (int)n.EventType == 4);
        Assert.Equal([plot], e.Vars.Single(v => v.VarId == DungeonService.StoryVar).Vi64);
    }

    /// <summary>What the client sends when the story ends: 32709 and the plot's end action 601 → 84011.</summary>
    private static async Task PlotEnd(BotClient bot, long plot)
    {
        await bot.SendGameAsync(TaskService.ExitCutSceneState, new ExitCutSceneStateReq());
        await bot.SendGameAsync(TaskService.PlotActingEnd, new PlotActingEndReq { PlotId = plot });
    }

    /// <summary>The scene actors shown, once exactly <paramref name="count"/> of them match.</summary>
    private Task<IReadOnlyList<DungeonService.SceneActorUnit>> ActorsIn(BotClient bot, int count,
        Func<DungeonService.SceneActorUnit, bool>? match = null) =>
        ActorsWhen(bot, shown => shown.Count(a => match == null || match(a)) == count, match);

    /// <summary>The scene actors shown (those matching), once the room's whole set satisfies <paramref name="ready"/>.</summary>
    private async Task<IReadOnlyList<DungeonService.SceneActorUnit>> ActorsWhen(BotClient bot,
        Func<IReadOnlyList<DungeonService.SceneActorUnit>, bool> ready, Func<DungeonService.SceneActorUnit, bool>? match = null)
    {
        DungeonService.SceneActorUnit[] shown = [];
        for (int i = 0; i < 150; i++)
        {
            shown = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
                Session(bot).DungeonAttempt?.SceneActors.ToArray() ?? []));
            if (ready(shown)) return shown.Where(a => match == null || match(a)).ToArray();
            await Task.Delay(20);
        }
        throw new TimeoutException($"The scene actors did not reach the expected state; shown: {string.Join(", ", shown.Select(a => $"{a.NpcId}@{a.Point}[{string.Join('/', a.Buffs.Select(b => b.ConfigId))}]"))}");
    }

    private static async Task<int> Interact(BotClient bot, DungeonService.SceneActorUnit actor, int interaction)
    {
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        {
            NpcUid = actor.Uid, InteractionId = interaction, SceneConfigId = actor.SceneEntry,
        });
        var reply = await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser,
            r => r.Req.NpcUid == actor.Uid && r.Req.InteractionId == interaction);
        return reply.Result;
    }

    private Task Beside(BotClient bot, float x, float y, float z) =>
        OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(x + 1, y, z, 0)));

    /// <summary>The client's walk report ends at (x, z): a short real walk from beside it, like any step.</summary>
    private async Task WalkTo(BotClient bot, float x, float y, float z)
    {
        await Beside(bot, x - 1.5f, y, z);
        var to = new Vec3 { X = x, Y = y, Z = z };
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = to, BStop = true, SpeedRate = 1 });
        for (int i = 0; i < 100; i++)
        {
            var at = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(bot).Position));
            if (Math.Abs(at.X - x) < 0.01f && Math.Abs(at.Z - z) < 0.01f) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The walk to ({x}, {z}) was not accepted");
    }

    private Task WalkInto(BotClient bot, MapRegion region) => WalkTo(bot, region.X, region.Y, region.Z);

    /// <summary>The clear counts for the story step (101018) and the chain offers the next one.</summary>
    private async Task StoryMovesOn(BotClient bot, long task, long next)
    {
        TaskState State(long id) => Session(bot).Tasks!.Infos.TryGetValue(id, out var info) ? info.Status : TaskState.None;
        for (int i = 0; i < 200 && !(TaskService.IsDelivered(Session(bot), task) && State(next) is TaskState.Running or TaskState.Finish); i++)
            await Task.Delay(20);
        Assert.True(TaskService.IsDelivered(Session(bot), task), $"task {task} is {State(task)}");
        Assert.True(State(next) is TaskState.Running or TaskState.Finish, $"next task {next} is {State(next)}");
    }
}
