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
