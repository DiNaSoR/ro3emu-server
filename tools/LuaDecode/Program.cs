// Rewrites the client's obfuscated Lua 5.4 chunks (LuaPayload/*.lua.bytes) into standard Lua 5.4 bytecode.
// Obfuscation, as read from xlua.dll lundump (FUN_180032420 and callees):
//   - signature byte 0x1E instead of ESC (0x1B);
//   - every instruction of a function is XORed with (sizecode << 16 | sizecode);
//   - every string payload s[0..n) is chained: s[k] ^= enc[k-1] for k = n-1..1, then s[0] ^= (byte)n.
// Everything else (opcodes, constant tags, layout) is stock 5.4.
// usage: LuaDecode <inDir> <outDir>   (mirrors the tree, drops the .bytes suffix to .luac)

if (args.Length < 2) { Console.Error.WriteLine("usage: LuaDecode <inDir> <outDir>"); return 1; }
int ok = 0, failed = 0;
foreach (var path in Directory.EnumerateFiles(args[0], "*.bytes", SearchOption.AllDirectories))
{
    var rel = Path.GetRelativePath(args[0], path);
    var outPath = Path.Combine(args[1], rel[..^".lua.bytes".Length] + ".luac");
    try
    {
        var data = File.ReadAllBytes(path);
        new Chunk(data).Decode();
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, data);
        ok++;
    }
    catch (Exception e)
    {
        failed++;
        Console.Error.WriteLine($"{rel}: {e.Message}");
    }
}
Console.WriteLine($"decoded {ok}, failed {failed}");
return failed == 0 ? 0 : 2;

sealed class Chunk(byte[] d)
{
    // Client opcode -> stock Lua 5.4 opcode, read case by case from luaV_execute (xlua.dll FUN_180033f40)
    // and the permuted luaP_opmodes at file offset 0xaf4b0. Opcodes from CLOSE (54) up are unchanged.
    static readonly byte[] OpMap = BuildOpMap();

    static byte[] BuildOpMap()
    {
        var m = new byte[128];
        for (int i = 0; i < 128; i++) m[i] = (byte)i;
        m[0] = 0;                                          // MOVE
        for (int i = 1; i <= 12; i++) m[i] = (byte)(33 + i);   // ADD..SHR      -> 34..45
        for (int i = 13; i <= 15; i++) m[i] = (byte)(33 + i);  // MMBIN..MMBINK -> 46..48
        for (int i = 16; i <= 20; i++) m[i] = (byte)(33 + i);  // UNM..CONCAT   -> 49..53
        for (int i = 21; i <= 39; i++) m[i] = (byte)(i - 20);  // LOADI..NEWTABLE -> 1..19
        for (int i = 40; i <= 53; i++) m[i] = (byte)(i - 20);  // SELF, ADDI..SHLI -> 20..33
        m[83] = 12;                                            // extra case 0x53: a second GETTABLE
        return m;
    }

    int p;

    public void Decode()
    {
        if (d[0] != 0x1E && d[0] != 0x1B) throw new InvalidDataException($"unexpected signature 0x{d[0]:X2}");
        d[0] = 0x1B;
        if (d[4] != 0x54) throw new InvalidDataException("not Lua 5.4");
        p = 31;     // header: sig4 ver fmt data6 sizes3 int8 num8
        p++;        // main closure sizeupvalues
        Function();
        if (p != d.Length) throw new InvalidDataException($"trailing bytes: parsed {p} of {d.Length}");
    }

    ulong Varint()
    {
        ulong x = 0;
        byte b;
        do { b = d[p++]; x = (x << 7) | (uint)(b & 0x7F); } while ((b & 0x80) == 0);
        return x;
    }

    void Str()
    {
        var size = (int)Varint();
        if (size == 0) return;
        int n = size - 1;
        for (int k = n - 1; k >= 1; k--) d[p + k] ^= d[p + k - 1];
        if (n > 0) d[p] ^= (byte)n;
        p += n;
    }

    void Function()
    {
        Str();          // source
        Varint();       // linedefined
        Varint();       // lastlinedefined
        p += 3;         // numparams, is_vararg, maxstacksize
        var sizecode = (uint)Varint();
        uint key = (sizecode << 16) | sizecode;
        for (uint i = 0; i < sizecode; i++, p += 4)
        {
            uint w = BitConverter.ToUInt32(d, p) ^ key;
            if ((w & 0x7F) > 83) throw new InvalidDataException($"unknown opcode {w & 0x7F} at {p}");
            w = (w & ~0x7Fu) | OpMap[w & 0x7F];
            BitConverter.TryWriteBytes(d.AsSpan(p, 4), w);
        }
        var sizek = (int)Varint();
        for (int i = 0; i < sizek; i++)
        {
            byte tag = d[p++];
            switch (tag)
            {
                case 0x00: case 0x01: case 0x11: break;
                case 0x03: case 0x13: p += 8; break;
                case 0x04: case 0x14: Str(); break;
                default: throw new InvalidDataException($"constant tag 0x{tag:X2} at {p - 1}");
            }
        }
        var sizeup = (int)Varint();
        p += 3 * sizeup;
        var sizep = (int)Varint();
        for (int i = 0; i < sizep; i++) Function();
        var lineinfo = (int)Varint();
        p += lineinfo;
        var abslines = (int)Varint();
        for (int i = 0; i < abslines; i++) { Varint(); Varint(); }
        var locvars = (int)Varint();
        for (int i = 0; i < locvars; i++) { Str(); Varint(); Varint(); }
        var upnames = (int)Varint();
        for (int i = 0; i < upnames; i++) Str();
    }
}
