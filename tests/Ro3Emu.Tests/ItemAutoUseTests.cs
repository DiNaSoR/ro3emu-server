using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// ItemConfig._QuickUse [3, 0] items (enum_FastUseType.AutoUse, no delay) are used by the server whichever way they
/// reach the bag. Each grant path below puts a look voucher in the bag and must end with the look lit and the voucher
/// gone; a path that added items without the 5009 bag sync that triggers the use would fail here.
/// </summary>
public sealed class ItemAutoUseTests : IAsyncLifetime
{
    // ItemConfig [[200, 0, look]], _iLevelNeed 1, _iCD 5000; the looks are enabled in WardRobeConfig.
    private const int GmVoucher = 3003101, GmLook = 10030417;
    private const int MailVoucher = 3003102, MailLook = 10030419;
    private const int LootVoucher = 3003103, LootLook = 10030425;
    private const int StackVoucher = 3003104, StackLook = 10030426;
    private const int ActivatedAppearance = 200, ActivatedMounts = 201, AddPetIncubateRoomExp = 603; // ActionConfig

    private readonly string directory = Path.Combine(RepoRoot(), ".loadtest-temp", "auto-use-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private GameSession Alice => Service<SessionRegistry>().All().Single(s => s.Account == "autousealice" && s.Role != null);

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter()
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("autousealice", "pw"); await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("AutoUseAlice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        await Next(bot, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
        return bot;
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(http + "/ro3emu/gm?account=autousealice", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    private int Bag(int entry) => Alice.Items.Where(i => i.Entry == entry).Sum(i => i.Stack);
    private DBWardRobeInfo SavedWardrobe => DBWardRobeInfo.Parser.ParseFrom(Service<RoleStore>().LoadWardrobe(Alice.Role!.Id));

    private async Task UsedAsync(BotClient bot, int voucher, int look)
    {
        Assert.Equal(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser, m => m.ItemId == voucher)).Result);
        Assert.Equal(0, Bag(voucher));
        Assert.True(SavedWardrobe.AllLightUpAppearances.ContainsKey(look));
    }

    [ClientDataFact]
    public async Task EveryGrantPathUsesTheItemOnReceipt()
    {
        await using var bot = await Enter();

        await Gm($"!item {GmVoucher} 1");
        await UsedAsync(bot, GmVoucher, GmLook);

        string mail = await Service<GameLoop>().InvokeAsync(() => Service<MailService>().SendRawAsync(Alice.Role!.Id, "voucher", "voucher",
            [new MailService.Attachment(MailVoucher, 1)]));
        await bot.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mail } });
        Assert.Equal(0, (await Next(bot, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        await UsedAsync(bot, MailVoucher, MailLook);

        await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().GrantAsync(Alice, [(LootVoucher, 1)]));
        await UsedAsync(bot, LootVoucher, LootLook);
    }

    // Two of one voucher: the first lights the look, the second waits out the item's _iCD, is refused as owned (2316)
    // and stays in the bag for the player to sell or keep.
    [ClientDataFact]
    public async Task AVoucherForAnOwnedLookStaysInTheBag()
    {
        await using var bot = await Enter();
        await Gm($"!item {StackVoucher} 2");
        Assert.Equal(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser, m => m.ItemId == StackVoucher)).Result);
        Assert.True(SavedWardrobe.AllLightUpAppearances.ContainsKey(StackLook));
        await Task.Delay(TimeSpan.FromSeconds(6)); // _iCD 5000
        await Service<GameLoop>().InvokeAsync(() => Task.CompletedTask);
        Assert.Equal(1, Bag(StackVoucher));
    }

    // Every _QuickUse [3, 0] item with a use effect is one the server auto-uses, through an action it hosts.
    [ClientDataFact]
    public async Task EveryAutoUseItemInTheClientDataQualifies()
    {
        await using var bot = await Enter();
        var data = Service<GameData>();
        var autoUse = Service<ItemAutoUseService>();
        var listed = data.Table("ItemConfig").Values.Where(r => r.TryGetProperty("_QuickUse", out var q)
            && q.ValueKind == JsonValueKind.Array && q.GetArrayLength() > 0 && GameData.Number(q[0]) == 3).ToList();
        Assert.NotEmpty(listed);
        foreach (var row in listed)
        {
            int id = (int)GameData.Int(row, "_iID");
            var actions = row.GetProperty("_iUseEffect").EnumerateArray()
                .Select(e => e.GetArrayLength() > 0 ? (int)(GameData.Number(e[0]) ?? 0) : 0).Where(a => a > 0).ToList();
            if (actions.Count == 0) { Assert.False(autoUse.IsAutoUse(id)); continue; } // the empty card chest 1300060
            Assert.True(autoUse.IsAutoUse(id), $"item {id}");
            Assert.All(actions, a => Assert.Contains(a, new[] { ActivatedAppearance, ActivatedMounts, AddPetIncubateRoomExp }));
            Assert.All(actions, a => Assert.Equal(1, GameData.Int(data.Row("ActionConfig", a)!.Value, "_iEXECHost")));
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
