using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Another player's profile attributes (global cache "Extra", RoleExpandInfo.selfproperty) and Base.isShowDetail follow
/// the role's own SettingShowDetailInfo: sent only for ShowAll, never for a role that has not chosen it.
/// </summary>
public sealed class ProfileDetailTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-detail-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<(BotClient Bot, long Role)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task<Dictionary<object, object?>> Card(BotClient reader, long role)
    {
        var response = await reader.GetRoleFieldsAsync([role], "Base", "Extra");
        var entry = (Dictionary<object, object?>)((object?[])response["Results"]!).Single()!;
        return (Dictionary<object, object?>)entry["Data"]!;
    }

    private static bool ShowsDetail(Dictionary<object, object?> card)
        => RoleAdditionBaseInfo.Parser.ParseFrom((byte[])card["Base"]!).IsShowDetail;

    /// <summary>As LC_SettingServer sends it: the value alone, so NoShow is an empty submessage.</summary>
    private static async Task ShowDetail(BotClient bot, ShowDetailInfoType value)
    {
        var req = new SaveSettingReq
        {
            DbSettingInfo = new DBSettingInfo { ShowDetailInfo = new SettingShowDetailInfo { ShowDetailInfo = value } },
        };
        req.Type.Add(SettingType.ShowDetailInfo);
        using var reply = bot.Inbox.Expect(e => e.IsGame(MsgId.SaveSetting));
        await bot.SendGameAsync(MsgId.SaveSetting, req);
        await reply.WaitAsync(Wait);
    }

    [Fact]
    public async Task AttributesReachOthersOnlyWhileTheRoleShowsAll()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, _) = await InWorld("Bob");
        await using var _bob = bob;

        // Never chosen: NoShow, so neither the flag nor the attributes.
        var card = await Card(bob, aliceId);
        Assert.False(ShowsDetail(card));
        Assert.False(card.ContainsKey("Extra"));

        await ShowDetail(alice, ShowDetailInfoType.ShowAll);
        card = await Card(bob, aliceId);
        Assert.True(ShowsDetail(card));
        var extra = RoleExpandInfo.Parser.ParseFrom((byte[])card["Extra"]!);
        Assert.Equal(aliceId, extra.Selfproperty.EntityID);
        Assert.NotEmpty(extra.Selfproperty.Propertys);

        await ShowDetail(alice, ShowDetailInfoType.NoShow);
        card = await Card(bob, aliceId);
        Assert.False(ShowsDetail(card));
        Assert.False(card.ContainsKey("Extra"));

        // Offline: the stored choice still sets the flag, but there is no live character to compute attributes from.
        await ShowDetail(alice, ShowDetailInfoType.ShowAll);
        await alice.SendGameAsync(MsgId.Logout, new LogoutReq());
        await alice.Inbox.WaitAsync(e => e.IsGame(MsgId.Logout), Wait);
        await alice.DisposeAsync();
        await WaitUntilOffline(aliceId);
        card = await Card(bob, aliceId);
        Assert.True(ShowsDetail(card));
        Assert.False(card.ContainsKey("Extra"));
    }

    private async Task WaitUntilOffline(long role)
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        for (var until = DateTime.UtcNow + Wait; ; await Task.Delay(20))
        {
            bool gone = await loop.InvokeAsync(() => Task.FromResult(sessions.All().All(s => s.Role?.Id != role)));
            if (gone) return;
            Assert.True(DateTime.UtcNow < until, "the role never left");
        }
    }
}
