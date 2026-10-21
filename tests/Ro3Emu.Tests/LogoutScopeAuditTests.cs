using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>
/// C/D 15 logout guardrail ("logout"): the handover cleanup runs under its production
/// reservation (role, lifecycle, account and the role's own guild keys - no Realm) with the owner audit on, so every
/// nested expansion and every write outside it is listed instead of thrown. Logout must need nothing more: any entry
/// is a new realm dependency (in production it would fail the cleanup and leave the role for recovery).
/// </summary>
public sealed class LogoutScopeAuditTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-logoutaudit-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{hp}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task PlainRoleLogoutStaysInsideItsReservation()
    {
        var audit = Audit();
        var (bot, id) = await InWorld("OutPlain");
        await bot.DisposeAsync();
        await WaitDetached(id);
        AssertEmpty(audit);
    }

    [Fact]
    public async Task LogoutLeavesNoContinuationBehindTheHandover()
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var (bot, id) = await InWorld("OutOrphan");
        long before = loop.OrphanContinuations;
        await bot.DisposeAsync();
        await WaitDetached(id);
        // A save the cleanup dropped (the tavern blob) resumed after the handover's permit closed, as an orphan.
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(25);
            await loop.InvokeAsync(() => Task.CompletedTask);
        }
        Assert.Equal(before, loop.OrphanContinuations);
    }

    [Fact]
    public async Task RegenWaitingForTheCharacterGateEndsQuietlyWhenTheRoleLeaves()
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var (bot, id) = await InWorld("OutRegen");
        var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role?.Id == id);
        // Another operation holds the gate while the regen queues behind it; the role's attachment changes meanwhile.
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = loop.InvokeAsync(async () =>
        {
            using (await CharacterService.Lock(s)) { locked.SetResult(); await release.Task; }
        });
        await locked.Task.WaitAsync(Wait);
        Task regen = null!;
        // As CombatService.Tick starts it; the detached root logs a failure, the captured task shows it here.
        await loop.InvokeAsync(() =>
        {
            loop.TrackRuntimeDetached("combat regeneration", () => regen = combat.RegenAsync(s));
            return Task.CompletedTask;
        });
        var attachment = await loop.InvokeAsync(() => { var a = s.Attachment; s.Attachment = null; return Task.FromResult(a); });
        release.SetResult();
        await holder.WaitAsync(Wait);
        try { await regen.WaitAsync(Wait); } // used to throw "Role changed before the character gate was acquired"
        finally { await loop.InvokeAsync(() => { s.Attachment = attachment; return Task.CompletedTask; }); }
        await bot.DisposeAsync();
        await WaitDetached(id);
    }

    [Fact]
    public async Task GuildMemberLogoutStaysInsideItsReservation()
    {
        var audit = Audit();
        var (founder, _) = await InWorld("OutLead");
        var (member, memberId) = await InWorld("OutMate");
        await Gm("OutLead", "!lvl 34");
        await founder.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Out", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guild = (await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        await Gm("OutMate", "!lvl 34");
        await founder.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(founder, GuildService.Edit, GuildEditResp.Parser)).Result);
        await member.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } });
        Assert.Equal(0, Assert.Single((await Next(member, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guild);
        await member.DisposeAsync(); // logs out with an online guild mate
        await WaitDetached(memberId);
        await founder.DisposeAsync();
        AssertEmpty(audit);
    }

    private ConcurrentQueue<string> Audit()
    {
        var audit = new ConcurrentQueue<string>();
        _app.Services.GetRequiredService<OwnerRegistry>().UncoveredForTest = audit.Enqueue;
        return audit;
    }

    private void AssertEmpty(ConcurrentQueue<string> audit)
    {
        _app.Services.GetRequiredService<OwnerRegistry>().UncoveredForTest = null;
        var distinct = audit.Select(Normalize).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        output.WriteLine($"logout realm dependencies ({distinct.Count} distinct, {audit.Count} total):");
        foreach (var line in distinct) output.WriteLine("  " + line);
        Assert.True(distinct.Count == 0, "Logout needs keys outside its reservation: " + string.Join(" | ", distinct));
    }

    private async Task WaitDetached(long role)
    {
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        for (int i = 0; i < 250; i++)
        {
            bool detached = await loop.InvokeAsync(() => Task.FromResult(owners.Cached(role) is not { } owner
                || owner.State is RoleOwnerState.Detached or RoleOwnerState.RecoveryRequired));
            if (detached) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Role {role} did not detach.");
    }

    private BotClient Bot() => new(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = Bot();
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private async Task Gm(string name, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={name.ToLowerInvariant()}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    // Role ids differ per run; keep the shape of each entry.
    private static string Normalize(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"\d{3,}", "#");
}
