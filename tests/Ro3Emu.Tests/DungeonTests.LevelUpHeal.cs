using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>A base level up refills HP and SP (classic RO): the first quest's Porings level a Novice mid-fight.</summary>
public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task BaseLevelUpRefillsHpAndSp()
    {
        var bot = await InWorld("Healer");
        var character = Service<CharacterService>();
        var s = Session(bot);
        await OnLoop(async () =>
        {
            await character.SetHpSpAsync(s, 10, 5);
            await character.AddExpAsync(s, 1, 0); // lv1 needs 5: no level up, no heal
        });
        Assert.Equal(1, s.Progress!.Level);
        Assert.True(s.CurrentHp < 30, $"HP {s.CurrentHp}"); // still hurt (natural regen may add a little)

        int seen = bot.Inbox.Snapshot().Count;
        await OnLoop(() => character.AddExpAsync(s, 20, 0)); // past lv2 (5) and lv3 (8)
        var stats = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(character.Stats(s)));
        Assert.True(s.Progress.Level >= 3);
        Assert.Equal(stats[101], stats[2000]); // HP full at the new maximum
        Assert.Equal(stats[102], stats[2001]);

        // What the client shows: its HUD bar reads the last 31008 values, its head bar the last 31048 values.
        await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.AoiAttrChange)
            && AOIAttrChange.Parser.ParseFrom(e.Body).Property.Any(p => p.Idx == 2000 && p.Data == stats[101]), TimeSpan.FromSeconds(5));
        var hud = new Dictionary<int, long>();
        var head = new Dictionary<int, long>();
        foreach (var e in bot.Inbox.Snapshot().Skip(seen))
        {
            if (e.IsGame(CharacterService.OnePropertyChange))
                foreach (var p in OnePropertyChange.Parser.ParseFrom(e.Body).Property.Propertys) hud[p.PropertyId] = p.Data;
            else if (e.IsGame(CharacterService.AoiAttrChange))
                foreach (var p in AOIAttrChange.Parser.ParseFrom(e.Body).Property) head[(int)p.Idx] = p.Data;
        }
        Assert.Equal((stats[101], stats[101]), (hud[101], hud[2000]));
        Assert.Equal((stats[102], stats[102]), (hud[102], hud[2001]));
        Assert.Equal((stats[101], stats[101]), (head[101], head[2000]));
    }
}
