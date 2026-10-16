// Rewrites the client's obfuscated Lua 5.4 chunks (LuaPayload/*.lua.bytes) into standard Lua 5.4 bytecode
// (src/Ro3Emu.DataExport/LuaChunkDecoder.cs explains the obfuscation).
// usage: LuaDecode <inDir> <outDir>   (mirrors the tree, drops the .bytes suffix to .luac)
using Ro3Emu.DataExport;

if (args.Length < 2) { Console.Error.WriteLine("usage: LuaDecode <inDir> <outDir>"); return 1; }
var (ok, failed) = LuaChunkDecoder.DecodeTree(args[0], args[1], Console.Error.WriteLine);
Console.WriteLine($"decoded {ok}, failed {failed}");
return failed == 0 ? 0 : 2;
