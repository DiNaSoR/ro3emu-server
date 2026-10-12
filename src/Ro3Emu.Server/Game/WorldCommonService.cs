using System.Globalization;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Narrow authenticated developer routes. 1031/1034/30310 have no normal clientB Lua sender;
/// these policies are emulator choices, separate from the proven 30300 GM panel.
/// </summary>
public sealed class WorldCommonService(GmCommands gm, World world, SessionRegistry sessions, IOptions<ServerOptions> options)
{
    public const uint OnlineStatusHeart = 1027, GmTransmit = 1031, Inspect = 1034, DebugHeight = 30302, LegacyGmProc = 30310;

    /// <summary>1027 requests have no recovered request schema; reply with actual live online state.</summary>
    public Task OnlineHeartAsync(GameSession s)
    {
        var state = new OnlineStatuChangeNtf { ChangeType = OnlineStatuChangeNtf.Types.eChangeType.All, TarEntityId = s.Role?.Id ?? 0 };
        foreach (var peer in sessions.All().Where(x => x.Role != null).OrderBy(x => x.Role!.Id))
        {
            state.AllOnlineId.Add(peer.Role!.Id);
            state.OnlineBaseData.Add(new onlineBaseInfo
            {
                PlayerId = peer.Role.Id, AreaId = options.Value.ClusterId,
                Level = peer.Progress?.Level ?? peer.Role.Level, Career = peer.Progress?.Career ?? peer.Role.Career,
            });
        }
        return s.SendGameAsync(OnlineStatusHeart, new OnlineClientHeartResp { AllInfo = state });
    }

    /// <summary>1031 has only UnUsedRsp. The marquee reports denial; the empty reply is a transport acknowledgement.</summary>
    public async Task TransmitAsync(GameSession s, GMCommandTransmitReq req)
    {
        string command = req.Data.Trim();
        if (!gm.Allowed(s) || (req.LPlayerId != 0 && req.LPlayerId != s.Role?.Id)
            || req.LAreaId != 0 || command.Length is 0 or > 512 || !gm.IsCommand(command))
        {
            await GmCommands.ReplyAsync(s, "GM transmit denied: local !command required.");
        }
        else await gm.RunAsync(s, command);
        await s.SendGameAsync(GmTransmit, new UnUsedRsp());
    }

    /// <summary>1034 text inspection is restricted to live AOI subjects; no guessed remote entity lookup.</summary>
    public async Task InspectAsync(GameSession s, InspectCommandReq req)
    {
        var response = new InspectCommandResp { ServerEntityType = req.ServerEntityType, ServerEntityId = req.ServerEntityId };
        if (!gm.Allowed(s) || s.Role == null)
            response.Res = "GM inspect denied.";
        else if (req.Cmd is not ("" or "summary" or "state" or "height"))
            response.Res = "Unsupported inspect command.";
        else if ((req.ServerEntityType == 0 || req.ServerEntityType == 3) && (req.ServerEntityId == 0 || req.ServerEntityId == s.Role.Id))
        {
            response.ServerEntityId = s.Role.Id;
            response.Res = PlayerSummary(s);
            if (req.Cmd == "height") await HeightAsync(s, s.Position.X, s.Position.Y, s.Position.Z);
        }
        else if ((req.ServerEntityType == 0 || req.ServerEntityType == 3)
            && world.Viewers(s).FirstOrDefault(p => p.Role?.Id == req.ServerEntityId) is { } player)
        {
            response.Res = PlayerSummary(player);
            if (req.Cmd == "height") await HeightAsync(s, player.Position.X, player.Position.Y, player.Position.Z);
        }
        else if (req.ServerEntityType == 1 && s.VisibleUnits.FirstOrDefault(u => u.Uid == req.ServerEntityId) is MonsterUnit monster)
        {
            response.Res = string.Create(CultureInfo.InvariantCulture,
                $"monster uid={monster.Uid} npc={monster.Template.NpcId} hp={monster.Hp}/{monster.Template.MaxHp} dead={monster.Dead} scene={monster.SceneEntry} pos={monster.X:F2},{monster.Y:F2},{monster.Z:F2}");
            if (req.Cmd == "height") await HeightAsync(s, monster.X, monster.Y, monster.Z);
        }
        else response.Res = "Entity is not visible or its type is unsupported.";
        await s.SendGameAsync(Inspect, response);
    }

    /// <summary>30310 array operations have no verified mapping to GmOpEnum: explicit refusal prevents false success.</summary>
    public Task LegacyProcAsync(GameSession s, GMProcReq req) =>
        s.SendGameAsync(LegacyGmProc, new GMProcRes { Result = gm.Allowed(s) ? ErrorCodes.FeatureUnavailable : ErrorCodes.NoPermission });

    private static string PlayerSummary(GameSession p) => string.Create(CultureInfo.InvariantCulture,
        $"player uid={p.Role!.Id} name={p.Role.Name} level={p.Progress?.Level ?? p.Role.Level} job={p.Progress?.Career ?? p.Role.Career} scene={p.SceneEntry} pos={p.Position.X:F2},{p.Position.Y:F2},{p.Position.Z:F2} dead={p.Dead}");

    /// <summary>30302 displays the authoritative current altitude at one actual entity position.</summary>
    private static Task HeightAsync(GameSession recipient, float x, float height, float z) =>
        recipient.SendGameAsync(DebugHeight, new DebugShowHeightNtf
        {
            Clear = true,
            Data = { new DebugShowHeightNtf.Types.PointHeight { X = x, Y = z, Height = height } },
        });
}
