using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ro3Emu.Server.Economy;

/// <summary>Frozen ItemConfig denomination classification for one E00 read.</summary>
public sealed class EconomyCatalog
{
    private readonly IReadOnlyDictionary<long, EconomyAssetKind> entries;
    public string Version { get; }
    /// <summary>Denomination identity shared by preloaded gameplay data and the independent file reader.</summary>
    public string DenominationsVersion { get; }
    /// <summary>Legacy auction bonus claim currency, from PublicParamConfig.AUCTION_CURRENCY.</summary>
    public long AuctionBonusCurrency { get; }

    /// <summary>Explicit hand-authored catalogue for isolated decoder fixtures.</summary>
    public EconomyCatalog(IReadOnlyDictionary<long, EconomyAssetKind> entries, long auctionBonusCurrency = 1)
    {
        this.entries = new Dictionary<long, EconomyAssetKind>(entries);
        if (this.entries.Any(e => e.Key <= 0 || e.Value is not (EconomyAssetKind.Item or EconomyAssetKind.Currency)))
            throw new ArgumentException("Fixture catalogue needs positive ItemConfig ids classified as item or currency.", nameof(entries));
        if (!this.entries.TryGetValue(auctionBonusCurrency, out var bonusKind) || bonusKind != EconomyAssetKind.Currency)
            throw new ArgumentException("Auction bonus currency must be catalogued as currency.", nameof(auctionBonusCurrency));
        AuctionBonusCurrency = auctionBonusCurrency;
        Version = Hash("auction-bonus:" + auctionBonusCurrency + "\n" + string.Join("\n", this.entries.OrderBy(e => e.Key)
            .Select(e => $"{e.Key.ToString(CultureInfo.InvariantCulture)}:{(int)e.Value}")));
        DenominationsVersion = Version;
    }

    private EconomyCatalog(IReadOnlyDictionary<long, EconomyAssetKind> entries, long bonusCurrency, string version)
        : this(entries, bonusCurrency)
        => Version = version;

    /// <summary>Pin the exact ItemConfig and denomination-dependent config bytes before scanning a database.</summary>
    public static EconomyCatalog Load(string gameDataDir)
    {
        string config = Directory.Exists(Path.Combine(gameDataDir, "config"))
            ? Path.Combine(gameDataDir, "config") : gameDataDir;
        string[] names = ["ItemConfig.json", "AuctionConfig.json", "GuildSettingConfig.json",
            "GuildTransportConfig.json", "GVGConfig.json", "PublicParamConfig.json"];
        var bytes = names.Select(name => (Name: name, Data: File.ReadAllBytes(Path.Combine(config, name)))).ToArray();
        using var doc = JsonDocument.Parse(bytes[0].Data);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("ItemConfig root is not an object.");
        var entries = new Dictionary<long, EconomyAssetKind>();
        foreach (var row in doc.RootElement.EnumerateObject())
        {
            if (!long.TryParse(row.Name, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0
                || row.Value.ValueKind != JsonValueKind.Object
                || !row.Value.TryGetProperty("_iType", out var type) || type.ValueKind != JsonValueKind.Number)
                throw new InvalidDataException($"Invalid ItemConfig denomination: {row.Name}");
            entries.Add(id, type.GetInt32() == 1 ? EconomyAssetKind.Currency : EconomyAssetKind.Item);
        }
        using var parameters = JsonDocument.Parse(bytes[^1].Data);
        if (!parameters.RootElement.TryGetProperty("AUCTION_CURRENCY", out var bonusRow)
            || bonusRow.ValueKind != JsonValueKind.Number || !bonusRow.TryGetInt64(out long bonusCurrency))
            throw new InvalidDataException("PublicParamConfig.AUCTION_CURRENCY is absent or invalid.");
        using var sha = SHA256.Create();
        foreach (var file in bytes)
        {
            byte[] name = Encoding.UTF8.GetBytes(file.Name + "\0");
            sha.TransformBlock(name, 0, name.Length, null, 0);
            sha.TransformBlock(file.Data, 0, file.Data.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return new EconomyCatalog(entries, bonusCurrency, Convert.ToHexString(sha.Hash!));
    }

    public bool TryAsset(long id, out EconomyAsset asset)
    {
        if (entries.TryGetValue(id, out var kind)) { asset = new(kind, id); return true; }
        asset = default;
        return false;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
