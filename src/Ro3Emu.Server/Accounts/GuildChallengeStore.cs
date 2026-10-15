using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Accounts;

/// <summary>Guild Challenge aggregate and durable settlement outbox, separate from GuildStore's membership writes.</summary>
public sealed class GuildChallengeStore
{
    private readonly AccountStore accounts;
    public GuildChallengeStore(AccountStore accounts)
    {
        this.accounts = accounts;
    }

    public GuildChallengeService.GuildState Load(long guild) =>
        accounts.ReadSync<GuildChallengeService.GuildState>(new GuildChallengeReadPlan(GuildChallengeReadKind.State, guild));

    public GuildChallengeService.Clear? LoadClear(string id) =>
        accounts.ReadSync<List<GuildChallengeService.Clear>>(new GuildChallengeReadPlan(GuildChallengeReadKind.Clear, id: id))
            .SingleOrDefault();

    public async Task<IReadOnlyList<GuildChallengeService.Clear>> PendingAsync() =>
        await accounts.ReadAsync<List<GuildChallengeService.Clear>>(new GuildChallengeReadPlan(GuildChallengeReadKind.Pending));

    public IReadOnlyList<GuildChallengeService.Clear> Pending() =>
        accounts.ReadSync<List<GuildChallengeService.Clear>>(new GuildChallengeReadPlan(GuildChallengeReadKind.Pending));

    public IReadOnlyList<GuildChallengeService.Clear> Clears(long guild, int season, long cycle, int? difficulty = null) =>
        accounts.ReadSync<List<GuildChallengeService.Clear>>(new GuildChallengeReadPlan(
            GuildChallengeReadKind.Occurrence, guild, season, cycle, difficulty));

    internal IReadOnlyList<GuildChallengeService.Clear> Clears() =>
        accounts.ReadSync<List<GuildChallengeService.Clear>>(new GuildChallengeReadPlan(GuildChallengeReadKind.AllClears));

    internal void EnlistState(long guild, GuildChallengeService.GuildState state, RoleItemPlanBuilder transaction) =>
        transaction.Add(new GuildChallengeStatePlan(guild, state));

    internal void EnlistClear(GuildChallengeService.Clear clear, RoleItemPlanBuilder transaction) =>
        transaction.Add(new GuildChallengeClearPlan(clear));

    public void Save(long guild, GuildChallengeService.GuildState state)
    {
        var plan = new RoleItemPlanBuilder(accounts);
        EnlistState(guild, state, plan);
        plan.Commit(accounts, "guild-challenge/save-state");
    }
}
