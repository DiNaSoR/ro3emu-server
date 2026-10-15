// Exports the client's Lua config tables to JSON by running the decoded bytecode (tools/LuaDecode output, standard
// Lua 5.4) in a real Lua 5.4 VM.
// DataConfig modules return {m_kValues = {key -> row}, m_kCount}; each row only holds the fields that differ from a
// shared default row reachable through its metatable's __index. The export merges defaults into every row.
// usage: ConfigExport <luac root> <out dir> [module dir (default Config\DataConfig)]...
using System.Text;
using KeraLua;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ConfigExport <luac root> <out dir> [relative module dirs...]");
    return 1;
}
string root = Path.GetFullPath(args[0]);
string outDir = Path.GetFullPath(args[1]);
var dirs = args.Length > 2 ? args[2..] : [@"Config\DataConfig", @"Config\ComplexConfig"];
Directory.CreateDirectory(outDir);

// require() in the client resolves lower-case module names by file name anywhere in the tree.
var index = new StringBuilder("MODULES = {\n");
foreach (var file in Directory.EnumerateFiles(root, "*.luac", SearchOption.AllDirectories))
{
    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
    index.Append($"[\"{name}\"] = [==[{file}]==],\n");
}
index.Append("}\n");

using var lua = new Lua();
lua.Encoding = Encoding.UTF8; // Chinese names in the scene-editor tables
lua.OpenLibs();
Run(lua, index.ToString(), "modules");
Run(lua, Driver, "driver");

int ok = 0, failed = 0;
foreach (var dir in dirs)
{
    var target = Path.Combine(root, dir.Replace('\\', Path.DirectorySeparatorChar));
    var files = File.Exists(target) ? [target] : Directory.EnumerateFiles(target, "*.luac");
    foreach (var file in files)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        lua.GetGlobal("export");
        lua.PushString(file);
        if (lua.PCall(1, 1, 0) != LuaStatus.OK)
        {
            failed++;
            Console.Error.WriteLine($"{name}: {lua.ToString(-1)}");
            lua.Pop(1);
            continue;
        }
        File.WriteAllText(Path.Combine(outDir, name + ".json"), lua.ToString(-1), new UTF8Encoding(false));
        lua.Pop(1);
        ok++;
    }
}
Console.WriteLine($"exported {ok}, failed {failed} -> {outDir}");
return 0;

static void Run(Lua lua, string code, string chunkName)
{
    if (lua.LoadString(code, chunkName) != LuaStatus.OK || lua.PCall(0, 0, 0) != LuaStatus.OK)
        throw new InvalidOperationException(lua.ToString(-1));
}

partial class Program
{
    const string Driver = """
        -- configdefine only supplies metatable helpers for the rows; stub them.
        package.preload["configdefine"] = function()
          return { newIndexFunc = function(t, k, v) rawset(t, k, v) end,
                   pairsFunc = function(t) return next, t, nil end }
        end
        table.insert(package.searchers, 2, function(name)
          local path = MODULES[string.lower(name)]
          if not path then return "\n\tno client module " .. name end
          local chunk, err = loadfile(path, "b")
          if not chunk then error(err) end
          return chunk, path
        end)

        local function isArray(t)
          local n = 0
          for k in next, t do
            if math.type(k) ~= "integer" or k < 1 then return false end
            n = n + 1
          end
          for i = 1, n do if rawget(t, i) == nil then return false end end
          return true, n
        end

        local escapes = { ['"'] = '\\"', ['\\'] = '\\\\', ['\b'] = '\\b', ['\f'] = '\\f', ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t' }
        local function encode(v, out, depth)
          local t = type(v)
          if t == "nil" then out[#out + 1] = "null"
          elseif t == "boolean" then out[#out + 1] = tostring(v)
          elseif t == "number" then
            if v ~= v or v == math.huge or v == -math.huge then out[#out + 1] = "null"
            elseif math.type(v) == "integer" then out[#out + 1] = string.format("%d", v)
            else out[#out + 1] = string.format("%.17g", v) end
          elseif t == "string" then
            out[#out + 1] = '"' .. v:gsub('[%c"\\]', function(c) return escapes[c] or string.format("\\u%04x", c:byte()) end) .. '"'
          elseif t == "table" then
            if depth > 40 then out[#out + 1] = "null"; return end
            local array, n = isArray(v)
            if array and n > 0 then
              out[#out + 1] = "["
              for i = 1, n do
                if i > 1 then out[#out + 1] = "," end
                encode(rawget(v, i), out, depth + 1)
              end
              out[#out + 1] = "]"
            else
              local keys = {}
              for k in next, v do
                if type(k) == "string" or type(k) == "number" then keys[#keys + 1] = k end
              end
              table.sort(keys, function(a, b)
                if type(a) == type(b) then return a < b end
                return type(a) == "number"
              end)
              out[#out + 1] = "{"
              local first = true
              for _, k in ipairs(keys) do
                local val = rawget(v, k)
                if type(val) ~= "function" and type(val) ~= "userdata" and type(val) ~= "thread" then
                  if not first then out[#out + 1] = "," end
                  first = false
                  encode(tostring(k), out, depth + 1)
                  out[#out + 1] = ":"
                  encode(val, out, depth + 1)
                end
              end
              out[#out + 1] = "}"
            end
          else out[#out + 1] = "null" end
        end

        -- A row with its metatable's default row merged in (fields of the row win).
        local function merged(row)
          if type(row) ~= "table" then return row end
          local result = {}
          local mt = getmetatable(row)
          if mt and type(rawget(mt, "__index")) == "function" and rawget(mt, "__pairs") then
            for k, v in pairs(row) do result[k] = v end
            return result
          end
          local defaults = mt and rawget(mt, "__index")
          if type(defaults) == "table" then for k, v in next, defaults do result[k] = v end end
          for k, v in next, row do result[k] = v end
          return result
        end

        function export(path)
          local chunk = assert(loadfile(path, "b"))
          local t = chunk()
          local data = t
          if type(t) == "table" and type(rawget(t, "m_kValues")) == "table" then
            data = {}
            for k, row in next, rawget(t, "m_kValues") do data[k] = merged(row) end
          end
          local out = {}
          encode(data, out, 0)
          return table.concat(out)
        end
        """;
}
