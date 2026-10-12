using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Economy;

/// <summary>E03 writer observation uses the independent persisted-format decoder on typed touched keys only.</summary>
public static partial class EconomySnapshotReader
{
    internal sealed record EscrowScope(IReadOnlySet<long> Roles, IReadOnlySet<(string Kind, string Id)> Markets,
        IReadOnlySet<string> AuctionSources, IReadOnlySet<long> Guilds, bool GuildMarket,
        IReadOnlySet<long> CaravanRoles, IReadOnlySet<long> AuctionPrebuyRoles,
        IReadOnlySet<long> AuctionBonusRoles);
    internal sealed record EscrowObservedHoldings(
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> Holdings,
        IReadOnlyDictionary<EconomyLocation, long> InventoryBinds);

    internal static EscrowObservedHoldings ReadEscrowScope(
        SqliteConnection db, SqliteTransaction tx, EscrowScope scope, EconomyCatalog catalog)
    {
        var state = new State(db, tx, catalog, []);
        state.ReadEscrowScope(scope);
        if (state.Issues.Count != 0)
            throw new InvalidDataException("E03 writer scope contains undecodable holdings: " + state.Issues[0]);
        return new(state.Holdings.ToDictionary(h => (h.Asset, h.Location)), state.InventoryBinds);
    }

    private sealed partial class State
    {
        internal Dictionary<EconomyLocation, long> InventoryBinds { get; } = [];
        private void ScopedHold(EconomyAsset asset, EconomyOwner owner, EconomyCompartment compartment,
            string key, long quantity, string fingerprint = "", string instance = "")
        {
            if (quantity <= 0 || asset.Id <= 0) throw new InvalidDataException("Invalid E03 held quantity or asset.");
            Hold(asset, owner, compartment, key, quantity, fingerprint, asset.Epoch, instance);
        }

        internal void ReadEscrowScope(EscrowScope scope)
        {
            foreach (long role in scope.Roles.Order())
            {
                using (var cmd = Command("SELECT currency,mailbox FROM role WHERE id=$id"))
                {
                    cmd.Parameters.AddWithValue("$id", role);
                    using var row = cmd.ExecuteReader();
                    if (row.Read())
                    {
                        string ownerId = Id(role), key = "role/" + ownerId;
                        var owner = new EconomyOwner(EconomyOwnerKind.Role, ownerId);
                        if (row.IsDBNull(0)) throw new InvalidDataException("E03 role currency is NULL.");
                        string currency = row.GetString(0);
                        var seen = new HashSet<long>();
                        if (currency.Length != 0) foreach (string pair in currency.Split(','))
                        {
                            string[] fields = pair.Split(':');
                            if (fields.Length != 2 || !long.TryParse(fields[0], NumberStyles.None,
                                CultureInfo.InvariantCulture, out long id) || !long.TryParse(fields[1],
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount) ||
                                id <= 0 || amount < 0 || !seen.Add(id))
                                throw new InvalidDataException("Invalid E03 currency purse.");
                            if (amount > 0) ScopedHold(new(EconomyAssetKind.Currency, id), owner,
                                EconomyCompartment.Currency, key + "/currency/" + id, amount);
                        }
                        if (!row.IsDBNull(1))
                        {
                            byte[] mailbox = (byte[])row.GetValue(1);
                            if (mailbox.Length > 0)
                            {
                                using var doc = Json(mailbox);
                                var box = doc.RootElement;
                                Fields(box, "NextId Letters");
                                if (!Try(box, "Letters", out var letters)) throw new JsonException("Mailbox letters absent.");
                                int letterIndex = 0;
                                foreach (var letter in Elements(letters))
                                {
                                    Fields(letter, "Id Category CreatedTime RemoveTime Title Content Abstract Sender SenderId TemplateId ImportantType DeliveryKey SourceKey ContentParams Read Claimed Attachments Items Currency");
                                    string path = key + "/mailbox/" + String(letter, "Id", "index-" + letterIndex++);
                                    if (Bool(letter, "Claimed")) continue;
                                    if (Try(letter, "Attachments", out var attachments))
                                    {
                                        int n = 0;
                                        foreach (var a in Elements(attachments))
                                        {
                                            Fields(a, "Entry Count Bind");
                                            long entry = Long(a, "Entry"), amount = Long(a, "Count");
                                            string place = path + "/attachment-" + n++;
                                            if (entry is 100 or 101) continue;
                                            if (!catalog.TryAsset(entry, out var attachmentAsset))
                                                throw new InvalidDataException("Unknown E03 attachment denomination.");
                                            ScopedHold(attachmentAsset,
                                                owner, EconomyCompartment.Mail, place, amount,
                                                attachmentAsset.Kind == EconomyAssetKind.Currency ? "" : GrantFingerprint(Long(a, "Bind")));
                                        }
                                    }
                                    if (Try(letter, "Items", out var letterItems))
                                    {
                                        int n = 0;
                                        foreach (var item in Elements(letterItems)) ScopedItem(item, owner, EconomyCompartment.Mail,
                                            path + "/item-" + n++);
                                    }
                                    if (Try(letter, "Currency", out var money))
                                        foreach (var p in Properties(money))
                                            if (p.Value.GetInt64() > 0) ScopedHold(new(EconomyAssetKind.Currency,
                                                long.Parse(p.Name, CultureInfo.InvariantCulture)), owner,
                                                EconomyCompartment.Mail, path + "/currency-" + p.Name, p.Value.GetInt64());
                                }
                            }
                        }
                    }
                }
                using var items = Command("SELECT guid,entry,stack,pack,sort,grid,attrs,enchantments,str_attrs,bind_type,bind_expire,is_locked FROM item WHERE role_id=$id ORDER BY guid");
                items.Parameters.AddWithValue("$id", role);
                using var r = items.ExecuteReader();
                while (r.Read())
                {
                    long guid = r.GetInt64(0), entry = r.GetInt64(1), stack = r.GetInt64(2);
                    long pack = r.GetInt64(3), sort = r.GetInt64(4), grid = r.GetInt64(5);
                    long bind = r.GetInt64(9), expire = r.GetInt64(10), locked = r.GetInt64(11);
                    string Extra(int column)
                    {
                        if (r.IsDBNull(column)) return "{}";
                        using var doc = Json(r.GetString(column));
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid item attrs.");
                        return Canon(doc.RootElement);
                    }
                    string fingerprint = Hash($"Attrs={Extra(6)}|StringAttrs={Extra(8)}|Enchantments={Extra(7)}" +
                        $"|BindType={bind}|BindExpire={expire}|IsLocked={locked}");
                    var itemOwner = new EconomyOwner(EconomyOwnerKind.Role, Id(role));
                    string itemKey = $"item/{guid}/pack-{pack}/grid-{grid}/sort-{sort}";
                    ScopedHold(new(EconomyAssetKind.Item, entry), itemOwner,
                        EconomyCompartment.Inventory, itemKey, stack, fingerprint, Id(guid));
                    InventoryBinds.Add(new(itemOwner, EconomyCompartment.Inventory, itemKey, Id(guid)), bind);
                }
                if (!scope.CaravanRoles.Contains(role)) continue;
                using var caravan = Command("SELECT state FROM guild_caravan_member WHERE role_id=$role");
                caravan.Parameters.AddWithValue("$role", role);
                if (caravan.ExecuteScalar() is byte[] caravanBytes)
                {
                    using var doc = Json(caravanBytes);
                    if (Try(doc.RootElement, "PendingAwards", out var awards))
                    {
                        int awardIndex = 0;
                        foreach (var award in Elements(awards))
                        {
                            string path = "caravan/" + role + "/" + awardIndex++;
                            bool booked = Bool(award, "GuildBooked");
                            long guild = Long(award, "GuildId");
                            int grantIndex = 0;
                            foreach (var grant in Elements(Prop(award, "Grants")))
                            {
                                string grantKey = path + "/" + grantIndex++;
                                long entry = Long(grant, "Entry"), amount = Long(grant, "Count");
                                if (entry == 104)
                                {
                                    if (!booked) ScopedHold(new(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMoney),
                                        new(EconomyOwnerKind.Guild, Id(guild)), EconomyCompartment.CaravanPending,
                                        grantKey, amount);
                                    continue;
                                }
                                if (!catalog.TryAsset(entry, out var asset))
                                    throw new InvalidDataException("Unknown E03 caravan grant denomination.");
                                ScopedHold(asset, new(EconomyOwnerKind.Role, Id(role)),
                                    EconomyCompartment.CaravanPending, grantKey, amount,
                                    asset.Kind == EconomyAssetKind.Item
                                        ? GrantFingerprint(Long(grant, "BindType"), Long(grant, "ExpireTimeStamp")) : "");
                            }
                        }
                    }
                }
            }
            foreach (var (kind, id) in scope.Markets.OrderBy(x => x.Kind).ThenBy(x => x.Id))
            {
                using var cmd = Command("SELECT data FROM market_record WHERE kind=$kind AND id=$id");
                cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteScalar() is not string data) continue;
                using var doc = Json(data);
                var row = doc.RootElement;
                string key = "market/" + kind + "/" + id;
                switch (kind)
                {
                    case "trade_listing":
                        if (Long(row, "Quantity") != Long(Prop(row, "Item"), "Stack"))
                            throw new InvalidDataException("E03 listing quantity mismatch.");
                        ScopedItem(Prop(row, "Item"), new(EconomyOwnerKind.Role, Id(Long(row, "SellerId"))),
                            EconomyCompartment.TradeListing, key);
                        break;
                    case "trade_preorder":
                        if (Long(row, "Status") == 0) ScopedHold(new(EconomyAssetKind.Currency, Long(row, "Currency")),
                            new(EconomyOwnerKind.Role, Id(Long(row, "BuyerId"))), EconomyCompartment.TradePreorder,
                            key, checked(Long(row, "Price") * Long(row, "Quantity")));
                        break;
                    case "stall":
                        long owner = Long(row, "Owner"), system = Long(row, "SystemId");
                        var stallOwner = owner > 0 ? new EconomyOwner(EconomyOwnerKind.Role, Id(owner))
                            : new EconomyOwner(EconomyOwnerKind.System, Id(system));
                        foreach (var slot in Elements(Prop(row, "Stock")))
                        {
                            var stockItem = Prop(slot, "Item");
                            long count = Long(stockItem, "Stack");
                            if (count < 0 || count == 0 && owner > 0)
                                throw new InvalidDataException("Invalid E03 stall stock.");
                            if (count > 0) ScopedItem(stockItem, stallOwner, EconomyCompartment.Stall,
                                key + "/" + Long(slot, "Id"));
                        }
                        break;
                    case "mail":
                        if (Bool(row, "Claimed")) break;
                        var receiver = new EconomyOwner(EconomyOwnerKind.Role, Id(Long(row, "RoleId")));
                        int index = 0;
                        if (Try(row, "Items", out var legacy)) foreach (var item in Elements(legacy))
                            ScopedItem(item, receiver, EconomyCompartment.LegacyMail, key + "/item-" + index++);
                        if (Try(row, "Currency", out var legacyMoney)) foreach (var p in Properties(legacyMoney))
                            if (p.Value.GetInt64() > 0) ScopedHold(new(EconomyAssetKind.Currency,
                                long.Parse(p.Name, CultureInfo.InvariantCulture)), receiver,
                                EconomyCompartment.LegacyMail, key + "/currency-" + p.Name, p.Value.GetInt64());
                        break;
                    case "trade_history": case "trade_sequence": break;
                    default: throw new InvalidDataException("Unclassified E03 market kind " + kind);
                }
            }
            foreach (string source in scope.AuctionSources.Order())
            {
                using var cmd = Command("SELECT id,state FROM auction_item WHERE source_key=$source ORDER BY id");
                cmd.Parameters.AddWithValue("$source", source);
                using var row = cmd.ExecuteReader();
                while (row.Read())
                {
                    long id = row.GetInt64(0);
                    using var doc = Json((byte[])row.GetValue(1));
                    var lot = doc.RootElement;
                    string key = "auction/lot/" + id;
                    long winner = Long(lot, "Winner"), reserve = Long(lot, "Reserve");
                    bool settled = Long(lot, "State") == (int)Romsg.AuctionState.Settled;
                    if (!settled || winner == 0)
                    {
                        var owner = settled ? new EconomyOwner(EconomyOwnerKind.Recovery, "auction:" + id)
                            : new EconomyOwner(EconomyOwnerKind.Escrow, "auction:" + id);
                        ScopedHold(new(EconomyAssetKind.Item, Long(lot, "Entry")), owner,
                            EconomyCompartment.AuctionLot, key, 1, GrantFingerprint(Long(lot, "Bind")));
                        if (reserve > 0)
                        {
                            var reserveOwner = settled ? new EconomyOwner(EconomyOwnerKind.Recovery, "auction-reserve:" + id)
                                : new EconomyOwner(EconomyOwnerKind.Role, Id(Long(lot, "Owner")));
                            ScopedHold(new(EconomyAssetKind.Currency, Long(lot, "Currency")), reserveOwner,
                                EconomyCompartment.AuctionReserve, key + "/reserve", reserve);
                        }
                    }
                }
            }
            foreach (long role in scope.AuctionPrebuyRoles.Order())
            {
                using var prebuy = Command("SELECT id,quantity,unit_price,currency,processed FROM auction_prebuy WHERE role_id=$role");
                prebuy.Parameters.AddWithValue("$role", role);
                using var row = prebuy.ExecuteReader();
                while (row.Read()) if (row.GetInt64(4) == 0)
                {
                    if (row.GetInt64(1) != 1) throw new InvalidDataException("Unknown E03 prebuy quantity.");
                    long id = row.GetInt64(0);
                    ScopedHold(new(EconomyAssetKind.Currency, row.GetInt64(3)),
                        new(EconomyOwnerKind.Role, Id(role)), EconomyCompartment.AuctionPrebuy,
                        "auction/prebuy/" + id, row.GetInt64(2));
                }
            }
            foreach (long role in scope.AuctionBonusRoles.Order())
            {
                using var bonus = Command("SELECT group_key,amount,claimed FROM auction_bonus WHERE role_id=$role");
                bonus.Parameters.AddWithValue("$role", role);
                using var bonusRow = bonus.ExecuteReader();
                while (bonusRow.Read()) if (bonusRow.GetInt64(2) == 0)
                {
                    string group = bonusRow.GetString(0);
                    ScopedHold(new(EconomyAssetKind.Currency, catalog.AuctionBonusCurrency),
                        new(EconomyOwnerKind.Role, Id(role)), EconomyCompartment.AuctionBonus,
                        "auction/bonus/" + role + "/" + group, bonusRow.GetInt64(1));
                }
            }
            foreach (long id in scope.Guilds.Order())
            {
                using var cmd = Command("SELECT state FROM guild WHERE id=$id");
                cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteScalar() is not byte[] bytes) continue;
                using var doc = Json(bytes);
                var state = doc.RootElement;
                var owner = new EconomyOwner(EconomyOwnerKind.Guild, Id(id));
                string key = "guild/" + id;
                foreach (var field in new[] { ("Money", 1L), ("Contribute", 2L), ("Materials", 3L),
                    ("Reputation", 4L), ("AuctionMaterials", 5L) })
                    if (Long(state, field.Item1) > 0) ScopedHold(new(EconomyAssetKind.GuildResource, field.Item2),
                        owner, EconomyCompartment.GuildTreasury, key + "/" + field.Item1, Long(state, field.Item1));
            }
            if (scope.GuildMarket)
            {
                using var cmd = Command("SELECT state FROM guild_market WHERE id=1");
                if (cmd.ExecuteScalar() is byte[] bytes)
                {
                    using var doc = Json(bytes);
                    int n = 0;
                    foreach (var stall in Elements(Prop(doc.RootElement, "Stalls")))
                    {
                        string id = String(stall, "Id", Id(n++)), key = "guild_market/1/" + id;
                        long remaining = Long(stall, "RemainCount");
                        if (remaining > 0) ScopedHold(new(EconomyAssetKind.GuildResource, 3),
                            GuildOwner(Long(stall, "GuildId")),
                            EconomyCompartment.GuildMarket, key, remaining);
                    }
                    if (Try(doc.RootElement, "RecoveryStalls", out var recovered))
                        foreach (var stall in Elements(recovered))
                        {
                            long remaining = Long(stall, "RemainCount");
                            if (remaining > 0) ScopedHold(new(EconomyAssetKind.GuildResource, 3),
                                new(EconomyOwnerKind.Recovery, "guild:" + Long(stall, "GuildId")),
                                EconomyCompartment.Recovery,
                                "guild_market/1/recovery/" + String(stall, "Id"), remaining);
                        }
                }
            }
            EconomyOwner GuildOwner(long guild)
            {
                using var check = Command("SELECT 1 FROM guild WHERE id=$id");
                check.Parameters.AddWithValue("$id", guild);
                return check.ExecuteScalar() is null
                    ? new(EconomyOwnerKind.Recovery, "guild:" + Id(guild))
                    : new(EconomyOwnerKind.Guild, Id(guild));
            }
        }

        private void ScopedItem(JsonElement item, EconomyOwner owner, EconomyCompartment compartment, string key)
        {
            Fields(item, "Guid Entry Stack Pack Sort Grid IsLocked Attrs StringAttrs BindType BindExpire ExpireTimeStamp Enchantments");
            long guid = Long(item, "Guid");
            if (!catalog.TryAsset(Long(item, "Entry"), out var asset) || asset.Kind != EconomyAssetKind.Item)
                throw new InvalidDataException("Unknown E03 item denomination.");
            ScopedHold(asset, owner, compartment, key,
                Long(item, "Stack"), Fingerprint(item), guid > 0 ? Id(guid) : "");
        }
    }
}
