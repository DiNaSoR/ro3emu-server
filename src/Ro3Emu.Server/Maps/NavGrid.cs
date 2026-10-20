namespace Ro3Emu.Server.Maps;

/// <summary>
/// The client's walkability grid of one map (<c>*_dynamicmapdata.bytes</c>): 1 m cells, world x,z =
/// cell index, each cell walkable or not with a height; a few maps carry extra height layers. Little-endian, uncompressed:
/// a 12 x int32 header, a block table (30x30-cell blocks), 4 bytes per cell (byte0 bit7 walkable, int16 height in cm)
/// and 8-byte extra layers {cell, u16 x, u16 z}.
/// </summary>
public sealed class NavGrid
{
    /// <summary>The client accepts a walkable layer within this height of the queried y (SceneSpaceDynamic._isStaticWalkable).</summary>
    public const float WalkHeightTolerance = 2f;

    private const int HeaderSize = 48;
    private readonly byte[] _data;
    private readonly int _blockSize;
    private readonly int _blocksX;

    public int Width { get; }
    public int Height { get; }

    private NavGrid(byte[] data)
    {
        _data = data;
        Width = BitConverter.ToInt32(data, 20);
        Height = BitConverter.ToInt32(data, 24);
        _blockSize = BitConverter.ToInt32(data, 28);
        _blocksX = (Width + _blockSize - 1) / _blockSize;
    }

    public static NavGrid Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Height layers of cell floor(x), floor(z): (walkable, height in m).</summary>
    public IEnumerable<(bool Walkable, float Height)> Layers(float x, float z)
    {
        int cx = (int)MathF.Floor(x), cz = (int)MathF.Floor(z);
        if (cx < 0 || cz < 0 || cx >= Width || cz >= Height) yield break;
        int block = cz / _blockSize * _blocksX + cx / _blockSize;
        int entry = HeaderSize + block * 16;
        int normalPos = BitConverter.ToInt32(_data, entry);
        int otherPos = BitConverter.ToInt32(_data, entry + 8);
        int otherCount = BitConverter.ToInt32(_data, entry + 12);
        int cell = normalPos + (cz % _blockSize * _blockSize + cx % _blockSize) * 4;
        yield return ((_data[cell] & 0x80) != 0, BitConverter.ToInt16(_data, cell + 2) * 0.01f);
        for (int k = 0; k < otherCount; k++)
        {
            int p = otherPos + k * 8;
            if (BitConverter.ToUInt16(_data, p + 4) == cx && BitConverter.ToUInt16(_data, p + 6) == cz)
                yield return ((_data[p] & 0x80) != 0, BitConverter.ToInt16(_data, p + 2) * 0.01f);
        }
    }

    public bool IsWalkable(float x, float y, float z) =>
        Layers(x, z).Any(l => l.Walkable && MathF.Abs(l.Height - y) <= WalkHeightTolerance);

    /// <summary>Height of the walkable layer nearest to <paramref name="yHint"/>, or null when the cell is not walkable.</summary>
    public float? WalkHeight(float x, float z, float yHint)
    {
        float? best = null;
        foreach (var (walkable, h) in Layers(x, z))
            if (walkable && (best == null || MathF.Abs(h - yHint) < MathF.Abs(best.Value - yHint))) best = h;
        return best;
    }

    /// <summary>Height of the layer of any kind (water, cliff, floor) nearest to <paramref name="yHint"/>, or null off the grid.</summary>
    public float? FloorHeight(float x, float z, float yHint)
    {
        float? best = null;
        foreach (var (_, h) in Layers(x, z))
            if (best == null || MathF.Abs(h - yHint) < MathF.Abs(best.Value - yHint)) best = h;
        return best;
    }

    /// <summary>
    /// The client's skill line-of-sight test (Lua_SceneSpace.CheckIndirectPass -> PF_AStar.CheckRayLine): from the cell
    /// of a to the cell of b, one-metre steps along the line, every visited cell must be walkable or carry a GridLevel
    /// (cell byte0 bits 3-6) no higher than the skill's <c>_iPassBlock</c>. A cell off the map blocks. The client walks
    /// the base layer of the start cell; extra height layers and runtime air walls are not modelled here (inferred).
    /// </summary>
    public bool CanPass(float ax, float az, float bx, float bz, int passLevel)
    {
        // Lua floors both positions before the C# ray; the C# side then rounds (x.5 down), a no-op on whole numbers.
        int sx = (int)MathF.Floor(ax), sz = (int)MathF.Floor(az), ex = (int)MathF.Floor(bx), ez = (int)MathF.Floor(bz);
        if (!CellPasses(sx, sz, passLevel)) return false;
        if (sx == ex && sz == ez) return true;
        float dx = ex - sx, dz = ez - sz, length = MathF.Sqrt(dx * dx + dz * dz);
        float ux = dx / length, uz = dz / length;
        // A segment of at most ~2 cells checks only its end cell (Obf_Cd.Obf_xiA).
        if ((dx - ux) * (dx - ux) + (dz - uz) * (dz - uz) <= 1.00001f) return CellPasses(ex, ez, passLevel);
        float x = sx, z = sz;
        for (int guard = 0; guard < 100000; guard++)
        {
            int cx = Round(x), cz = Round(z);
            if (!CellPasses(cx, cz, passLevel)) return false;
            if (cx == ex && cz == ez) break;
            float remaining = (ex - x) * ux + (ez - z) * uz;
            if (MathF.Abs(remaining) < 1e-5f || remaining < 0) break;
            x += ux;
            z += uz;
        }
        return true;
    }

    private static int Round(float v)
    {
        int f = (int)MathF.Floor(v);
        return v - f <= 0.5f ? f : f + 1;
    }

    private bool CellPasses(int cx, int cz, int passLevel)
    {
        if (cx < 0 || cz < 0 || cx >= Width || cz >= Height) return false;
        int block = cz / _blockSize * _blocksX + cx / _blockSize;
        int cell = BitConverter.ToInt32(_data, HeaderSize + block * 16) + (cz % _blockSize * _blockSize + cx % _blockSize) * 4;
        byte flags = _data[cell];
        return (flags & 0x80) != 0 || (flags & 0x78) >> 3 <= passLevel;
    }

    /// <summary>
    /// Whether the straight 2D segment from a to b crosses only walkable cells (sampled every half metre, heights
    /// followed cell by cell like the client's direct-walk check).
    /// </summary>
    public bool CanWalk(float ax, float ay, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / 0.5f));
        float y = ay;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps, x = ax + dx * t, z = az + dz * t;
            if (WalkHeight(x, z, y) is not { } h || MathF.Abs(h - y) > WalkHeightTolerance) return false;
            y = h;
        }
        return true;
    }
}
