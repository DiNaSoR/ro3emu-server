using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class KillEconomyTests
{
    /// <summary>A kill that levels a hurt Novice: the client's last HP values are the new maximum.</summary>
    [ClientDataFact]
    public async Task KillLevelUpShowsFullHpOnTheClient()
    {
        await using var bot = await Enter();
        using (var client = new HttpClient())
            (await client.PostAsync(http + "/ro3emu/gm?account=killtest", new StringContent("!job 1100"))).EnsureSuccessStatusCode();
        var player = Session;
        var character = Service<CharacterService>();
        var monster = await Service<GameLoop>().InvokeAsync(async () =>
        {
            player.Progress!.Skills[11001] = 1;
            Service<RewardService>().ResolveKillForTest = (_, _) => ([], 20, 0); // lv1 -> lv3
            await character.SetHpSpAsync(player, 10, 5);
            var monsters = Service<MonsterService>();
            var target = await monsters.SpawnSingleAsync(player.SceneEntry, 5000001,
                new MapPoint(player.Position.X, player.Position.Y, player.Position.Z, 0), player.InstanceId,
                customTemplate: monsters.Template(5000001)! with { RoamDistance = 0 });
            target!.Hp = 1; return target;
        });
        int seen = bot.Inbox.Snapshot().Count;
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = monster.Uid });
        var cast = CastSkillRsp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), TimeSpan.FromSeconds(10))).Body);
        Assert.Equal(0, cast.Result);
        await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify), TimeSpan.FromSeconds(10));
        // The level up's own full-stats update (it floats the gains), then its separate refill.
        static bool Change(BotEvent e, Func<OnePropertyChange, bool> match) =>
            e.IsGame(CharacterService.OnePropertyChange) && match(OnePropertyChange.Parser.ParseFrom(e.Body));
        for (var until = DateTime.UtcNow.AddSeconds(10); ; await Task.Delay(50))
        {
            var got = bot.Inbox.Snapshot().Skip(seen).ToList();
            int levelUp = got.FindIndex(e => Change(e, c => c.AttrModTypes.Count > 0));
            if (levelUp >= 0 && got.Skip(levelUp + 1).Any(e => Change(e, c => c.Property.Propertys.All(p => p.PropertyId is 2000 or 2001)))) break;
            Assert.True(DateTime.UtcNow < until, "no refill after the level up's stats");
        }
        var stats = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(character.Stats(player)));

        var hud = new Dictionary<int, long>();
        var head = new Dictionary<int, long>();
        var log = new List<string>();
        foreach (var e in bot.Inbox.Snapshot().Skip(seen))
        {
            if (e.IsGame(CharacterService.OnePropertyChange))
            {
                var ps = OnePropertyChange.Parser.ParseFrom(e.Body).Property.Propertys;
                foreach (var p in ps) hud[p.PropertyId] = p.Data;
                log.Add("31008 " + string.Join(",", ps.Where(p => p.PropertyId is 101 or 2000).Select(p => $"{p.PropertyId}={p.Data}")));
            }
            else if (e.IsGame(CharacterService.AoiAttrChange))
            {
                var ps = AOIAttrChange.Parser.ParseFrom(e.Body).Property;
                foreach (var p in ps) head[(int)p.Idx] = p.Data;
                log.Add("31048 " + string.Join(",", ps.Where(p => p.Idx is 101 or 2000).Select(p => $"{p.Idx}={p.Data}")));
            }
            else log.Add(e.ToString() ?? "");
        }
        string trace = $"lv {player.Progress!.Level} hp {stats[2000]}/{stats[101]} dead {monster.Dead}\n" + string.Join("\n", log);
        Assert.True(player.Progress.Level >= 3 && stats[101] == stats[2000], trace);
        Assert.True(hud[101] == stats[101] && hud[2000] == stats[101], trace);
        Assert.True(head[101] == stats[101] && head[2000] == stats[101], trace);
    }
}
