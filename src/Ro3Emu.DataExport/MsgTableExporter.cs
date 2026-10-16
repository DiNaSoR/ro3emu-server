using System.Globalization;
using System.Text;
using KeraLua;

namespace Ro3Emu.DataExport;

/// <summary>
/// The romsg message table (id, name, request type, response type) the server logs and answers with, read from the
/// client's Network/NW_Helper_PBDefine, NW_Helper_EZPBDefine and NW_AutoSyncClient_Define chunks. They fill
/// NW_Helper.ProtobufMap[id] = {msg_name, msg_id, req_name, res_name}; the chunks are run in a Lua 5.4 VM with an
/// environment that creates any other global they touch, and the map is read back. Same output as the former
/// tools/msgtable.js over the decompiled source.
/// </summary>
public static class MsgTableExporter
{
    public static readonly string[] Modules = ["NW_Helper_PBDefine", "NW_Helper_EZPBDefine", "NW_AutoSyncClient_Define"];

    public sealed record Row(long Id, string Name, string Request, string Response);

    /// <summary>Reads the table from the decoded tree (<paramref name="luacRoot"/>/Network/*.luac).</summary>
    public static IReadOnlyList<Row> Read(string luacRoot)
    {
        using var lua = new Lua();
        lua.Encoding = Encoding.UTF8;
        lua.OpenLibs();
        Run(lua, Driver, "msgtable");
        foreach (var module in Modules)
        {
            var path = Path.Combine(luacRoot, "Network", module + ".luac");
            if (!File.Exists(path)) continue;
            lua.GetGlobal("run");
            lua.PushString(path);
            if (lua.PCall(1, 0, 0) != LuaStatus.OK)
                throw new InvalidDataException($"{module}: {lua.ToString(-1)}");
        }

        // rows() returns one flat array: id, name, req, res, id, name, ...
        lua.GetGlobal("rows");
        if (lua.PCall(0, 1, 0) != LuaStatus.OK) throw new InvalidDataException(lua.ToString(-1));
        var rows = new List<Row>();
        long n = lua.RawLen(-1);
        for (long i = 1; i + 3 <= n; i += 4)
        {
            lua.RawGetInteger(-1, i);
            long id = lua.ToInteger(-1);
            lua.Pop(1);
            rows.Add(new Row(id, Field(lua, i + 1), Field(lua, i + 2), Field(lua, i + 3)));
        }
        lua.Pop(1);
        rows.Sort((a, b) => a.Id.CompareTo(b.Id));
        return rows;
    }

    /// <summary>Writes the TSV the server reads (data/msgtable.tsv): "id\tname\treq\tres" per line, by id.</summary>
    public static int Write(string luacRoot, string outFile)
    {
        var rows = Read(luacRoot);
        var text = new StringBuilder();
        foreach (var r in rows)
            text.Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.Name).Append('\t')
                .Append(r.Request).Append('\t').Append(r.Response).Append('\n');
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, text.ToString(), new UTF8Encoding(false));
        return rows.Count;
    }

    private static string Field(Lua lua, long index)
    {
        lua.RawGetInteger(-1, index);
        var value = lua.ToString(-1) ?? "";
        lua.Pop(1);
        return value;
    }

    private static void Run(Lua lua, string code, string chunkName)
    {
        if (lua.LoadString(code, chunkName) != LuaStatus.OK || lua.PCall(0, 0, 0) != LuaStatus.OK)
            throw new InvalidOperationException(lua.ToString(-1));
    }

    // Every global the chunks read that is not set yet is a fresh table (NW_Helper and whatever else they index), so
    // the defines run without the rest of the client. A row needs a name, an integer id and a response type, as in
    // msgtable.js; a later chunk's row for the same id wins.
    private const string Driver = """
        local env = setmetatable({}, { __index = function(t, k)
          local v = _G[k]
          if v == nil then v = {}; rawset(t, k, v) end
          return v
        end })

        function run(path)
          local chunk = assert(loadfile(path, "b", env))
          chunk()
        end

        function rows()
          local helper = rawget(env, "NW_Helper")
          local map = helper and rawget(helper, "ProtobufMap")
          local byId = {}
          if type(map) == "table" then
            for _, row in next, map do
              if type(row) == "table" then
                local id, name, res = rawget(row, "msg_id"), rawget(row, "msg_name"), rawget(row, "res_name")
                if math.type(id) == "integer" and type(name) == "string" and name ~= "" and res ~= nil then
                  byId[id] = row
                end
              end
            end
          end
          local out = {}
          for id, row in next, byId do
            out[#out + 1] = id
            out[#out + 1] = tostring(rawget(row, "msg_name"))
            local req = rawget(row, "req_name")
            out[#out + 1] = req == nil and "" or tostring(req)
            out[#out + 1] = tostring(rawget(row, "res_name"))
          end
          return out
        end
        """;
}
