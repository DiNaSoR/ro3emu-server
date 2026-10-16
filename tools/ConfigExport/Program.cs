// Exports the client's Lua config tables to JSON by running the decoded bytecode (tools/LuaDecode output, standard
// Lua 5.4) in a real Lua 5.4 VM (src/Ro3Emu.DataExport/LuaConfigExporter.cs).
// usage: ConfigExport <luac root> <out dir> [module dir (default Config\DataConfig)]...
using Ro3Emu.DataExport;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ConfigExport <luac root> <out dir> [relative module dirs...]");
    return 1;
}
string outDir = Path.GetFullPath(args[1]);
var dirs = args.Length > 2 ? args[2..] : [@"Config\DataConfig", @"Config\ComplexConfig"];
using var exporter = new LuaConfigExporter(args[0]);
var (ok, failed) = exporter.ExportDirs(outDir, dirs, Console.Error.WriteLine);
Console.WriteLine($"exported {ok}, failed {failed} -> {outDir}");
return 0;
