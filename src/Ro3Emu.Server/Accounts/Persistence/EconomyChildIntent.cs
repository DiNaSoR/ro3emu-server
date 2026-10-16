using System.Reflection;
using System.Text.Json;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// E05d's audited typed-plan inventory. The capture is made while the caller still owns the builder,
/// before revision checks or replay; callbacks and later writer-generated IDs are never part of intent.
/// Unknown executor kinds fail closed until their detached fields have been reviewed for inclusion.
/// </summary>
internal static class EconomyChildIntent
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "role/guard-blob", "role/write-blob", "role/write-progress", "role/award-state", "item/write-delta",
        "item/write-snapshot", "friend/gift-enlisted", "friend/SaveMetadata", "friend/SetIntimacy",
        "guild/save", "guild/role-view", "guild/recover-delete", "guild/delete", "guild/market-save",
        "guild/escort-materials", "guild/caravan-reward", "guild_campfire/save", "guild-campfire/save",
        "guild-caravan/member", "guild-caravan/version", "guild-challenge/state", "guild/event-append",
        "guild_challenge/clear", "mail/financial-append", "auction/source-claim", "auction/item"
    };

    internal static byte[] Capture(IReadOnlyList<DbPlan> guards, IReadOnlyList<DbPlan> parts)
    {
        if (parts.Count == 0) throw new InvalidOperationException("E05d envelope has no typed child.");
        var payload = new
        {
            format = "e05d-typed-children-v1",
            guards = guards.Select(Project).ToArray(),
            parts = parts.Select(Project).ToArray()
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > 512 * 1024) throw new InvalidOperationException("E05d typed child intent exceeds receipt limit.");
        return bytes;
    }

    private static object Project(DbPlan plan)
    {
        if (!Supported.Contains(plan.Label))
            throw new InvalidOperationException("E05d typed child has no audited intent capture: " + plan.Label);
        var type = plan.GetType();
        var fields = new List<(string Name, object? Value)>();
        for (Type? current = type; current is not null && current != typeof(DbPlan); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (field.IsStatic || field.FieldType == typeof(RoleRevisionVector) ||
                    field.FieldType == typeof(OwnerRevisionValue) ||
                    plan is GuildSavePlan && field.Name == "_changedMembers" ||
                    plan is FriendWritePlan && field.Name == "_roles" ||
                    plan is GuildDeletePlan && field.Name == "_members") continue;
                object? value = field.GetValue(plan);
                if (value is Delegate) throw new InvalidOperationException("E05d typed child retained a callback.");
                fields.Add((current.FullName + "/" + field.Name, value is DbPlan nested
                    ? Project(nested) : value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType())));
            }
        }
        return new
        {
            type = type.FullName,
            label = plan.Label,
            auctionSource = plan is AuctionSourceClaimPlan claim ? claim.SourceKey :
                plan is AuctionItemWritePlan lot ? lot.SourceKey : null,
            auctionCreatesNew = plan is AuctionItemWritePlan item ? item.CreatesNew : (bool?)null,
            roles = plan.RoleWrites.OrderBy(x => x.RoleId).ThenBy(x => x.Part, StringComparer.Ordinal).ToArray(),
            pins = plan.RolePins.Order().ToArray(),
            entities = plan.EntityWrites.OrderBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Part, StringComparer.Ordinal).ToArray(),
            deltas = plan.EntityDeltaWrites.OrderBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Part, StringComparer.Ordinal).ToArray(),
            fields = fields.OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => new { x.Name, x.Value }).ToArray()
        };
    }
}
