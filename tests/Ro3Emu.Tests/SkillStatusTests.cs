using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Skill statuses that now do what their descriptions say: control buffs hold monsters, chills slow them, a hit
/// breaks a freeze into its chill, immunity refuses control, cleanses remove the statuses they name and skill shields
/// get their absorb amount. Table facts read the exported data; the monster checks boot a server.
/// </summary>
public sealed class SkillStatusTests : IAsyncLifetime
{
    private static readonly Lazy<GameData> Exported = new(() => new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance));

    private static GameData Data => Exported.Value;

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-skillstatus-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication? _app;
    private string _http = "";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app == null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task Server()
    {
        var ports = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = ports.Item1; o.MicroGatePort = ports.Item2; o.GameGatePort = ports.Item3;
        });
        SureHitRolls.Install(_app);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{ports.Item1}";
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private GameSession Session(string name) => _app!.Services.GetRequiredService<SessionRegistry>().All()
        .Single(s => s.Account == name.ToLowerInvariant());
    private GameLoop Loop => _app!.Services.GetRequiredService<GameLoop>();
    private BuffService Buffs => _app!.Services.GetRequiredService<BuffService>();
    private MonsterService Monsters => _app!.Services.GetRequiredService<MonsterService>();
    private Task Run(Func<Task> work) => Loop.InvokeAsync(work);

    private async Task Eventually(Func<bool> check, string what)
    {
        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until)
        {
            bool ok = false;
            await Run(() => { ok = check(); return Task.CompletedTask; });
            if (ok) return;
            await Task.Delay(50);
        }
        Assert.Fail("Timed out waiting for " + what);
    }

    private Task<MonsterUnit> Monster(string name, float dx) => Loop.InvokeAsync(async () =>
    {
        var s = Session(name);
        var template = Monsters.Template(5000001)! with { RoamDistance = 0, Atk = 1, Matk = 1 };
        var unit = await Monsters.SpawnSingleAsync(s.SceneEntry, 5000001,
            new MapPoint(s.Position.X + dx, s.Position.Y, s.Position.Z, 0), s.InstanceId, customTemplate: template);
        Assert.NotNull(unit);
        return unit!;
    });

    private static System.Text.Json.JsonElement Buff(int id) => Data.Row("BuffConfig", id)!.Value;
    private static System.Text.Json.JsonElement Skill(int id) => Data.Row("SkillConfig", id)!.Value;

    [ClientDataFact]
    public void ControlBuffsNameTheirStateByCategory()
    {
        Assert.Equal(18, BuffService.StateFor(Buff(901601))); // Stun
        Assert.Equal(11, BuffService.StateFor(Buff(902001))); // Frozen (Frost Diver)
        Assert.Equal(12, BuffService.StateFor(Buff(902101))); // Petrify (Stone Curse)
        Assert.Equal(19, BuffService.StateFor(Buff(902201))); // Sleep
        Assert.Equal(20, BuffService.StateFor(Buff(902301))); // Silence
        Assert.Equal(21, BuffService.StateFor(Buff(902401))); // Immobilize (Anklesnare)
        Assert.Equal(18, BuffService.StateFor(Buff(905801))); // the monster copy of Stun, kind 63 = 13 + 50
        Assert.Equal(11, BuffService.StateFor(Buff(908101))); // the PvP copy of Frozen, kind 88 = 9 + 79
        Assert.Equal(0, BuffService.StateFor(Buff(902601)));  // the chill after a thaw only slows
        Assert.Equal(0, BuffService.StateFor(Buff(903101)));  // Endure is a beneficial tag, not a control
    }

    [ClientDataFact]
    public void ImmunityRefusesControlAndDebuffImmunityEveryDebuff()
    {
        int[] immunity = [20], debuffImmunity = [21, 20];
        Assert.True(BuffService.Blocks(immunity, Buff(901601)));   // Stun
        Assert.True(BuffService.Blocks(immunity, Buff(902301)));   // Silence
        Assert.False(BuffService.Blocks(immunity, Buff(903001)));  // Poisoned is not a control
        Assert.True(BuffService.Blocks(debuffImmunity, Buff(903001)));
        Assert.False(BuffService.Blocks([19], Buff(901601)));      // Endure alone
    }

    [ClientDataFact]
    public void CleansesRemoveTheStatusesTheyName()
    {
        var healing = CombatService.CleanseTargets(Skill(1401401)); // Healing Skills: Silence, Blind
        Assert.Equal([6, 5], healing);
        Assert.True(CombatService.Cleanses(healing, Buff(902301)));
        Assert.True(CombatService.Cleanses(healing, Buff(902501)));
        Assert.False(CombatService.Cleanses(healing, Buff(901601)));
        var recovery = CombatService.CleanseTargets(Skill(1401411)); // Recovery mode adds Stun, Frozen, Petrify
        Assert.True(CombatService.Cleanses(recovery, Buff(901601)));
        Assert.True(CombatService.Cleanses(recovery, Buff(902001)));
        var detoxify = CombatService.CleanseTargets(Skill(1500701)); // Thief Detoxify: Poisoned
        Assert.True(CombatService.Cleanses(detoxify, Buff(903001)));
        Assert.True(CombatService.Cleanses(detoxify, Buff(907201))); // the monster copy of Poisoned
        Assert.False(CombatService.Cleanses(detoxify, Buff(902301)));
        Assert.Equal([9030], CombatService.CleanseTargets(Skill(1531401))); // Guillotine Cross Antidote
    }

    [ClientDataFact]
    public void SkillShieldsGetTheirDescribedAmount()
    {
        var caster = new StatSheet { [103] = 2000, [104] = 1000, [101] = 5000 };
        var recipient = new StatSheet { [101] = 9000 };
        Assert.Equal(800, CombatService.ShieldAmount(Buff(1411705), caster, recipient));  // Kyrie Lv5: MATK x 80%
        Assert.Equal(1000, CombatService.ShieldAmount(Buff(1430310), caster, recipient)); // Radiant Holy Light: MATK x 100%
        Assert.Equal(500, CombatService.ShieldAmount(Buff(1130505), caster, recipient));  // Rift Impact: 10% Max Life
        Assert.Equal(0, CombatService.ShieldAmount(Buff(901601), caster, recipient));     // not a shield
    }

    [ClientDataFact]
    public void SteelCrowOwnsTheBlitzBeatBonusStoredUnderBeastbane()
    {
        // Steel Crow Lv5 reads Beastbane's level-5 row 1310405: +500 bp on Blitz Beat; Beastbane reads none.
        Assert.Equal(1310405, CombatService.PassiveBuffRow(13105, Skill(1310505)));
        Assert.Equal(0, CombatService.PassiveBuffRow(13104, Skill(1310405)));
        Assert.Equal(1100301, CombatService.PassiveBuffRow(11003, Skill(1100301))); // every other passive keeps its own row
    }

    [ClientDataFact]
    public async Task StunHoldsAMonsterAndWearsOff()
    {
        await Server();
        await using var bot = await InWorld("Stunner");
        var m = await Monster("Stunner", 1); // already in reach: only the swing is held
        long firstSwing = 0;
        await Run(async () =>
        {
            await Buffs.AddSupportAsync(m, 901601, 800, Session("Stunner").Role!.Id);
            Monsters.Engage(m, Session("Stunner"));
            firstSwing = m.NextSwingMs;
        });
        await Run(() =>
        {
            Assert.True(m.Forbids(3) && m.Forbids(5));
            Assert.NotEqual(0, m.BuffStatusBits & (1L << 17)); // state 18 Stun
            return Task.CompletedTask;
        });
        // Engaged and in reach, but stunned: no swing is started while the stun lasts.
        await Task.Delay(500);
        await Run(() => { Assert.Equal(firstSwing, m.NextSwingMs); Assert.False(m.Moving); return Task.CompletedTask; });
        await Eventually(() => !m.Forbids(3) && m.BuffStatusBits == 0, "the stun to wear off");
        await Eventually(() => m.NextSwingMs > firstSwing, "the monster to swing again");
    }

    [ClientDataFact]
    public async Task AHitBreaksAFreezeIntoItsChill()
    {
        await Server();
        await using var bot = await InWorld("Thawer");
        var m = await Monster("Thawer", 3);
        long source = 0;
        await Run(async () =>
        {
            source = Session("Thawer").Role!.Id;
            await Buffs.AddSupportAsync(m, 902001, 10000, source); // Frost Diver's Frozen
        });
        await Run(() => { Assert.True(m.Forbids(3)); return Task.CompletedTask; });
        await Run(() => Buffs.BreakOnHitAsync(m));
        await Run(() =>
        {
            Assert.False(m.Forbids(3));
            Assert.Equal(-5000, m.BuffSpeedBp); // 902601: -50% movement for 3 s
            Assert.Equal(m.Template.SpeedCm / 2, m.SpeedCm(MonsterService.NowMs));
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task ImmuneMonstersShrugOffStun()
    {
        await Server();
        await using var bot = await InWorld("Immune");
        var m = await Monster("Immune", 3);
        await Run(async () =>
        {
            long source = Session("Immune").Role!.Id;
            await Buffs.AddSupportAsync(m, 903201, 10000, m.Uid); // Immunity (category 20) on itself
            await Buffs.AddSupportAsync(m, 901601, 5000, source);
        });
        await Run(() => { Assert.False(m.Forbids(3)); return Task.CompletedTask; });
    }
}
