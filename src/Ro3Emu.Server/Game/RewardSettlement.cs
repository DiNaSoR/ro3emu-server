using System.Security.Cryptography;
using System.Text;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game;

/// <summary>E04 staged death identity. Restart creates a new run and every new actor a new generation
/// (inferred spawn policy); neither a recycled UID nor a reattached player can reopen the old source.</summary>
public sealed record RewardSourceId(string Realm, string Run, string Generation)
{
    internal static readonly string ProcessRun = Guid.NewGuid().ToString("N");
    public string Key => $"kill/{Realm}/{Run}/{Generation}";
}

/// <summary>One frozen natural reward source. Completion belongs to the economic receipt, not its packets.</summary>
public sealed class RewardSettlement(string sourceKey, string commandKind, bool autoDecompose = false)
{
    public string SourceKey { get; } = sourceKey;
    public string CommandKind { get; } = commandKind;
    public string OperationId { get; } = "reward-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)));
    public long EffectiveUtcMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool AutoDecompose { get; } = autoDecompose;
    public bool Committed { get; internal set; }
    internal bool OutcomeUnknown { get; set; }
    internal bool DeferPresentation { get; set; }
    internal Func<Task>? Presentation { get; set; }
    internal List<(int Entry, int Count)> AdditionalGoods { get; } = [];
    internal Action? BeforeAdditionalGoods { get; set; }
    internal Action<RoleItemPlanBuilder>? Enlist { get; set; }
    internal Action<RoleItemPlanBuilder>? EnlistAfterCards { get; set; }
    internal bool TaskObligationsStaged { get; set; }
    internal long[]? TaskParticipantIds { get; set; }
    internal Action? ResetTaskStage { get; set; }
    internal bool AfkRecordStaged { get; set; }
    internal Action? ResetAfkStage { get; set; }
    internal bool HuntStaged { get; set; }
    internal Action? ResetHuntStage { get; set; }
    internal Action? Applied { get; set; }
    internal async Task PresentAsync()
    {
        var present = Presentation;
        Presentation = null; // a failed packet/hook never reopens a settled economic source
        if (present != null) await present();
    }
}
