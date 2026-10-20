using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Chat memory and flood bounds (issue #144): history store, empty groups, joins and send rate.</summary>
public sealed class ChatLimitTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-chatlimit-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
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

    private ChatService Chat => _app.Services.GetRequiredService<ChatService>();

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<Tencent.IM.TextChatMessage> LineAsync(BotClient bot, string content)
    {
        while (true)
        {
            var line = await bot.NextChatLineAsync();
            if (line.Content == content) return line;
        }
    }

    private static async Task<bool> RefusedSendAsync(BotClient bot)
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(ChatService.TunnelDown) && BitConverter.ToUInt32(e.Body) == 3, Wait);
        return !Tencent.IM.SendTextChatMessageResponse.Parser.ParseFrom(e.Body.AsSpan(8)).ChatResult;
    }

    [Fact]
    public async Task MadeUpChannelIdsKeepABoundedHistory()
    {
        long now = 0;
        Chat.Clock = () => now += 1000; // no rate limit in the way
        await using var alice = await InWorld("Alice");
        for (int i = 0; i < ChatService.MaxRecentGroups + 50; i++)
            await alice.SendChatAsync($"9_{i}", 2, "spam");
        await alice.SendChatAsync("9_last", 2, "done");
        await LineAsync(alice, "done");

        Assert.True(Chat.RecentGroupCount <= ChatService.MaxRecentGroups, $"{Chat.RecentGroupCount} channels kept");

        // The newest channel keeps its history: joining it replays the line.
        await alice.JoinChatAsync("9_last");
        Assert.Equal("9_last", (await LineAsync(alice, "done")).ReceiveId);
    }

    [Fact]
    public async Task LoggingOutRemovesEmptyChatGroups()
    {
        await using var alice = await InWorld("Alice");
        int before = Chat.GroupCount;
        var bob = await InWorld("Bob");
        await bob.JoinChatAsync("9_made_up");
        Assert.Equal(before + 1, Chat.GroupCount);

        await bob.DisposeAsync();
        var deadline = DateTime.UtcNow + Wait;
        while (Chat.GroupCount != before && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(before, Chat.GroupCount);
    }

    [Fact]
    public async Task JoinsAreCappedPerSession()
    {
        await using var alice = await InWorld("Alice");
        for (int i = 0; i < ChatService.MaxJoinedGroups; i++)
        {
            try { await alice.JoinChatAsync($"9_{i}"); }
            catch (Exception) { break; } // the client's own channels may already count toward the cap
        }
        await Assert.ThrowsAnyAsync<Exception>(() => alice.JoinChatAsync("9_one_too_many"));
        await Assert.ThrowsAnyAsync<Exception>(() => alice.JoinChatAsync("9_" + new string('x', ChatService.MaxReceiveIdChars)));
    }

    [Fact]
    public async Task ChatFloodIsRefusedThenRefills()
    {
        long now = 1_000_000;
        Chat.Clock = () => now;
        await using var alice = await InWorld("Alice");
        await alice.JoinChatAsync("4_1");
        for (int i = 0; i < ChatService.SendBurst; i++)
        {
            await alice.SendChatAsync("4_1", 2, $"line {i}", channelIndex: 4);
            Assert.False(await RefusedSendAsync(alice));
        }
        await alice.SendChatAsync("4_1", 2, "one too many", channelIndex: 4);
        Assert.True(await RefusedSendAsync(alice));

        now += 1000 / ChatService.SendLinesPerSecond;
        await alice.SendChatAsync("4_1", 2, "after a pause", channelIndex: 4);
        Assert.False(await RefusedSendAsync(alice));
        await LineAsync(alice, "after a pause");
    }
}
