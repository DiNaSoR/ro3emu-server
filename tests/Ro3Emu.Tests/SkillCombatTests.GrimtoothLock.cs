using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Grimtooth hits at once (template m_kAtkPoints [0]). Its walk lock went on and off within one tick around the
    /// skill's dash and left the player unable to walk until the next Grimtooth (in game 10.18), so a skill that hits
    /// inside the first loop tick takes no lock at all. The cast still lands.
    /// </summary>
    [ClientDataFact]
    public async Task ASkillThatHitsAtOnceTakesNoMovementLock()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15109);
        var target = await Target("alice", stationary: true);
        long hp = await Loop.InvokeAsync(() => { target.Hp = 1_000_000_000; return Task.FromResult(target.Hp); });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510901, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(accepted.SessionId, Session("alice").SkillCast?.Id);
            Assert.Equal(0, Session("alice").CastMoveLockId);
            Assert.Equal(0UL, Session("alice").ForbiddenBits & WalkForbidden);
            return Task.CompletedTask;
        });
        var until = DateTime.UtcNow.AddSeconds(3);
        while (await Loop.InvokeAsync(() => Task.FromResult(target.Hp)) == hp)
        {
            Assert.True(DateTime.UtcNow < until, "Grimtooth never hit");
            await Task.Delay(20);
        }
    }
}
