using System.Globalization;
using Ro3Emu.Bot;

// A bot player for live tests next to the real client.
// usage: Ro3Emu.BotCli <name> [server http base] [data dir]
// Then type commands on stdin (or pipe a file):
//   walk <x> <z>          walk there in steps (WALK every 0.1 s, like the client)
//   circle <r> <secs>     walk around the current spot
//   say <channel> <text>  chat in a group, e.g. say 4_1 hello  (map chat is "4_<sceneId>")
//   whisper <roleId> <text>
//   face <deg>
//   wait <secs>
//   party                 create a party;  invite <roleId>  invite into it;  friend <roleId>  send a friend request
//   guildapply <guildId>  apply to a guild;  guildhelp <guildId>  respond to a guild being founded;  guildleave
//   quit
string name = args.Length > 0 ? args[0] : "Botty";
string http = args.Length > 1 ? args[1] : "http://127.0.0.1:5380";
string dataDir = args.Length > 2 ? args[2] : FindDataDir();

await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
await bot.LoginAsync(name.ToLowerInvariant(), "bot");
await bot.EnterGateAsync();
long roleId = bot.Roles.FirstOrDefault(r => r.Name == name)?.Id ?? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
var self = await bot.EnterWorldAsync(roleId);
float x = self.MovementInfo.Pos.X, y = self.MovementInfo.Pos.Y, z = self.MovementInfo.Pos.Z;
Console.WriteLine($"{name} ({roleId}) in world at {x:F1},{z:F1}");

// Heartbeat like the real client, so the session looks alive.
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(5000);
        try { await bot.SendGameAsync(1, new Romsg.Ping()); } catch { return; }
    }
});

string? line;
while ((line = Console.ReadLine()) != null)
{
    var p = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    if (p.Length == 0) continue;
    try
    {
        switch (p[0])
        {
            case "walk":
                await WalkTo(F(p[1]), F(p[2]));
                break;
            case "circle":
                float r = F(p[1]), secs = F(p[2]), cx = x, cz = z;
                for (float t = 0; t < secs; t += 0.1f)
                {
                    float a = t / secs * MathF.Tau;
                    await Step(cx + r * MathF.Cos(a), cz + r * MathF.Sin(a), stop: false);
                }
                await Step(cx, cz, stop: true);
                break;
            case "join":
                await bot.JoinChatAsync(p[1]);
                break;
            case "say":
                // Channel index from the group id prefix: "4_x" map, "@TGS#_1_x" world, ...
                int channel = int.TryParse(p[1].Replace("@TGS#_", "").Split('_')[0], out var c) ? c : 4;
                await bot.SendChatAsync(p[1], 2, p.Length > 2 ? p[2] : "", channel);
                break;
            case "whisper":
                await bot.SendChatAsync(p[1], 1, p.Length > 2 ? p[2] : "", channelIndex: 5);
                break;
            case "face":
                await bot.SendGameAsync(31020, new Romsg.ChangeFaceReq { Angle = F(p[1]) });
                break;
            case "wait":
                await Task.Delay(TimeSpan.FromSeconds(F(p[1])));
                break;
            case "party":
                await bot.SendGameAsync(42000, new Romsg.TeamCreateReq { TeamType = Romsg.TeamType.Five, TargetId = 1001 });
                break;
            case "invite":
                await bot.SendGameAsync(42004, new Romsg.InviteIntoTeamReq { Guid = long.Parse(p[1], CultureInfo.InvariantCulture) });
                break;
            case "friend":
                await bot.MicroAsync("FriendService", "apply", new() { ["Uid"] = roleId.ToString(), ["Target"] = p[1] });
                break;
            case "guildapply":
                await bot.SendGameAsync(32223, new Romsg.GuildEnterApplyReq { GuildIds = { long.Parse(p[1], CultureInfo.InvariantCulture) } });
                break;
            case "guildhelp":
                await bot.SendGameAsync(32220, new Romsg.GuildCreateHelperOpeReq { GuildIds = { long.Parse(p[1], CultureInfo.InvariantCulture) } });
                break;
            case "guildleave":
                await bot.SendGameAsync(32234, new Romsg.GuildMemberLeaveReq());
                break;
            case "quit":
                return;
        }
        Console.WriteLine($"ok: {line}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"error: {line}: {e.Message}");
    }
}

async Task WalkTo(float tx, float tz)
{
    const float speed = 5.5f, tick = 0.1f;
    while (true)
    {
        float dx = tx - x, dz = tz - z, d = MathF.Sqrt(dx * dx + dz * dz);
        if (d <= speed * tick)
        {
            await Step(tx, tz, stop: true);
            return;
        }
        await Step(x + dx / d * speed * tick, z + dz / d * speed * tick, stop: false);
    }
}

async Task Step(float nx, float nz, bool stop)
{
    float dir = (MathF.Atan2(nx - x, nz - z) * 180f / MathF.PI + 360f) % 360f;
    x = nx;
    z = nz;
    await bot.SendGameAsync(31082, new Romsg.WalkReq { Pos = new Romsg.Vec3 { X = x, Y = y, Z = z }, Dir = dir, Face = dir, BStop = stop, SpeedRate = 1 });
    if (!stop) await Task.Delay(100);
}

static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

static string FindDataDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return Path.Combine(dir.FullName, "data");
    }
    return "data";
}
