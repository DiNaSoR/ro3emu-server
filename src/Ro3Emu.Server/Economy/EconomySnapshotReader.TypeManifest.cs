using System.Reflection;
using System.Text.Json.Serialization;
using Google.Protobuf.Reflection;

namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    /// <summary>
    /// Frozen classification surface, independent of SQL schema. Adding a field to an existing persisted DTO or
    /// protobuf cannot silently classify a new balance as progression. Review the decoder and bump this pin when
    /// the persisted format changes; a matching runtime type alone is not a reviewed economic classification.
    /// </summary>
    // C/D 13e: Dungeon.State.PendingJourney names a captured act/cycle, not a second holding of
    // its dungeon loot. Authored Journey boss acts have no direct goods; later node claims stay separate.
    // Emotes: role.emoji_play is DBEmojiPlayInfo (emote ids: favourites, unlocks, idle emote); no balance, no asset.
    // Achievements: role.achievements is DBRoleAchievementInfo (statuses, score counters, points, claimed reward and
    // hint levels) and role.achievement_runtime is AchievementService.RuntimeState (the last counted login day). Neither
    // holds a balance or an asset: a claim's goods are granted by its own cd13e/achievement-award receipt, and the
    // claimed status / reward level there is only the once-only source marker of that receipt.
    // Escort: RoleState.Cycle and the ledger's Cycle name the Territory War repeat the escort season belongs to
    // (TerritoryWarsClock.EpochOf); a key, not a balance. The decoder reads it into the escort epoch.
    // Special Class Items: role.career_special is DBCareerSpecial (two CartRemouldTable ids: pendant and mount slot); a
    // free lease, no balance, no asset.
    // Adventure Group: role.adventure_group is DBAdventureGroupRelationData (claimed group level rewards, the leave
    // cooldown, the weekly social-money counter, group fields mirrored for 5068). No balance and no asset: a level reward's
    // goods are granted by its own cd13e/advguild-award receipt, and takenGroupRewardLevel is only that receipt's
    // once-only marker. The adventure_group / adventure_group_member tables hold names, rosters, the tree and group EXP
    // (a progression counter, not a currency); they are classified as social tables.
    // Pass play: role.activity_runtime PassPlay (NewActivityService.PassPlay) holds the day's and week's challenge
    // progress, daily EXP caps, paid chapters and EXP counters. No balance and no asset: pass EXP is currency 132, granted
    // by the pass/play receipt that also saves these markers.
    // Valhalla Trials: role.heroic_spirit is DBOdinStatue (Blessing statue EXP and level). Not a balance: its EXP is
    // bought with Einherjar Coins by the cd13b/valhalla-pray receipt that also saves it. Cleared layers are
    // dungeons Records "valhalla:<layer>", written with the layer's reward by the dungeon obligation.
    // Monster Hunt: role.area_concur is DBAreaConcurInfo (kill counters per monster and map, claimed stage markers,
    // the area's claimed-stage count). No balance: kill counters ride the monster-kill receipt, and a claim's goods are
    // granted by its own cd13e/monsterhunt-award receipt whose claimed marker is only that receipt's once-only source.
    public const string ExpectedFormatManifestHash = "6F3AD57ECA3574DA6EA6ED8ABC95F87229A7C06938F446D0E6DD9ACEE9A86307";
    public static string FormatManifestHash => State.FormatManifestHash;

    private sealed partial class State
    {
        private static readonly Lazy<string> FormatManifest = new(ComputeFormatManifest);
        internal static string FormatManifestHash => FormatManifest.Value;

        private static string ComputeFormatManifest()
        {
            var entries = new SortedSet<string>(StringComparer.Ordinal);
            var seenTypes = new HashSet<Type>();
            var seenMessages = new HashSet<string>(StringComparer.Ordinal);
            void Message(MessageDescriptor descriptor)
            {
                if (!seenMessages.Add(descriptor.FullName)) return;
                foreach (var field in descriptor.Fields.InFieldNumberOrder())
                {
                    entries.Add($"proto:{descriptor.FullName}:{field.FieldNumber}:{field.Name}:{field.FieldType}:{field.IsRepeated}:{field.IsMap}:{field.IsRequired}");
                    if (field.FieldType == FieldType.Message) Message(field.MessageType);
                    if (field.FieldType == FieldType.Enum)
                        foreach (var value in field.EnumType.Values) entries.Add($"enum:{field.EnumType.FullName}:{value.Number}:{value.Name}");
                }
            }
            void TypeShape(Type type)
            {
                if (!seenTypes.Add(type)) return;
                if (type.IsArray) { TypeShape(type.GetElementType()!); return; }
                if (type.IsGenericType)
                    foreach (var argument in type.GetGenericArguments()) TypeShape(argument);
                if (type.IsEnum)
                {
                    foreach (string name in Enum.GetNames(type)) entries.Add($"enum:{type.FullName}:{name}:{Enum.Format(type, Enum.Parse(type, name), "D")}");
                    return;
                }
                if (type.Assembly != typeof(EconomySnapshotReader).Assembly) return;
                foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (p.GetIndexParameters().Length != 0 || p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
                    entries.Add($"json:{type.FullName}:{p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name}:{p.PropertyType.FullName}:{p.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType?.FullName}");
                    TypeShape(p.PropertyType);
                }
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(x => x.IsDefined(typeof(JsonIncludeAttribute))))
                {
                    entries.Add($"field:{type.FullName}:{f.Name}:{f.FieldType.FullName}");
                    TypeShape(f.FieldType);
                }
            }
            foreach (var pair in ProtoDescriptors.OrderBy(x => x.Key, StringComparer.Ordinal))
            { entries.Add($"role-proto:{pair.Key}:{pair.Value.FullName}"); Message(pair.Value); }
            foreach (var pair in JsonRoleTypes.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                entries.Add($"role-json:{pair.Key}:{pair.Value}");
                var type = typeof(EconomySnapshotReader).Assembly.GetType("Ro3Emu.Server.Game." + pair.Value);
                if (type == null) entries.Add("missing:" + pair.Value); else TypeShape(type);
            }
            entries.Add("role-string-set:task_followup:System.String");
            entries.Add("dungeon_reward_obligation:Ro3Emu.Server.Accounts.DungeonRewardObligation");
            TypeShape(typeof(Ro3Emu.Server.Accounts.DungeonRewardObligation));
            // These JSON wrappers carry embedded protobuf bytes in addition to their explicit DTO graph.
            Message(Romsg.DBMountsInfo.Descriptor);
            Message(Romsg.DBLotteryData.Descriptor);
            Message(Romsg.DBDailyActivityInfo.Descriptor);
            entries.Add("activity_eve_global:romsg.DBSeasonEveInfo");
            Message(Romsg.DBSeasonEveInfo.Descriptor);
            entries.Add("festival_round:Ro3Emu.Server.Game.FestivalService.Round");
            TypeShape(typeof(Ro3Emu.Server.Game.FestivalService.Round));
            entries.Add("server_state/season_history:Ro3Emu.Server.Game.SeasonService.Season");
            TypeShape(typeof(Ro3Emu.Server.Game.SeasonService.Season));
            return Hash(string.Join("\n", entries));
        }
    }
}
