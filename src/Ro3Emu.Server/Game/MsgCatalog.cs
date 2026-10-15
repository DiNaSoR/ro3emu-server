using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>
/// msgId -> (name, request type, response type), read from the TSV that tools/msgtable.js extracts from the client
/// Lua (NW_Helper_PBDefine & co.), joined with the generated romsg C# types by protobuf full name.
/// </summary>
public sealed class MsgCatalog
{
    public sealed record Entry(uint Id, string Name, MessageDescriptor? Request, MessageDescriptor? Response);

    private readonly Dictionary<uint, Entry> _entries = new();

    public MsgCatalog(IOptions<ServerOptions> options, ILogger<MsgCatalog> log)
    {
        var types = typeof(MsgCatalog).Assembly.GetTypes()
            .Where(t => typeof(IMessage).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as MessageDescriptor)
            .Where(d => d != null)
            .ToDictionary(d => d!.FullName, d => d!);

        // The table is exported from the client like config/ (tools/msgtable.js), so a realm whose DataDir only holds
        // its own state (a fresh realm after a wipe: new DataDir, GameDataDir on the exported data) reads it from
        // GameDataDir. Without it every unrouted request goes unanswered (no empty reply), which leaves waiting
        // client panels empty.
        var path = Path.Combine(options.Value.DataDir, "msgtable.tsv");
        if (!File.Exists(path) && options.Value.GameDataDir is { Length: > 0 } gameData)
            path = Path.Combine(gameData, "msgtable.tsv");
        if (!File.Exists(path))
        {
            log.LogWarning("No {Path}; message names unavailable and unrouted requests get no reply (run tools/msgtable.js)", path);
            return;
        }
        foreach (var line in File.ReadLines(path))
        {
            var cols = line.Split('\t');
            if (cols.Length < 4 || !uint.TryParse(cols[0], out var id)) continue;
            _entries[id] = new Entry(id, cols[1], types.GetValueOrDefault(cols[2]), types.GetValueOrDefault(cols[3]));
        }
        log.LogInformation("Message catalog: {Count} ids", _entries.Count);
    }

    public Entry? Get(uint id) => _entries.GetValueOrDefault(id);

    /// <summary>Every message id of the client's table.</summary>
    public IEnumerable<uint> Ids => _entries.Keys;

    /// <summary>True only when the table is loaded and the client defines no message with this id.</summary>
    public bool UnknownToClient(uint id) => _entries.Count != 0 && !_entries.ContainsKey(id);

    public string Name(uint id) => _entries.TryGetValue(id, out var e) ? e.Name : id.ToString();

    /// <summary>Request body as JSON for logs, or a byte count when the type is unknown.</summary>
    public string Describe(uint id, ReadOnlyMemory<byte> body)
    {
        var descriptor = Get(id)?.Request;
        if (descriptor == null || descriptor.FullName == "romsg.NullArg") return $"{body.Length} bytes";
        try
        {
            return JsonFormatter.Default.Format(descriptor.Parser.ParseFrom(body.Span));
        }
        catch (InvalidProtocolBufferException)
        {
            return $"{body.Length} bytes (unparsable as {descriptor.Name})";
        }
    }

    /// <summary>An empty instance of the response type (result fields default to 0 = success), or null.</summary>
    public IMessage? EmptyResponse(uint id)
    {
        var descriptor = Get(id)?.Response;
        if (descriptor == null || descriptor.FullName == "romsg.NullArg") return null;
        return (IMessage)Activator.CreateInstance(descriptor.ClrType)!;
    }
}
