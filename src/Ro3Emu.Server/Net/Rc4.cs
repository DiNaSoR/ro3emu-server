namespace Ro3Emu.Server.Net;

/// <summary>Plain RC4 keystream, one instance per direction (the client keeps separate encrypt/decrypt states).</summary>
public sealed class Rc4
{
    private readonly byte[] _s = new byte[256];
    private int _i;
    private int _j;

    public Rc4(ReadOnlySpan<byte> key)
    {
        for (int k = 0; k < 256; k++) _s[k] = (byte)k;
        for (int k = 0, j = 0; k < 256; k++)
        {
            j = (j + _s[k] + key[k % key.Length]) & 0xFF;
            (_s[k], _s[j]) = (_s[j], _s[k]);
        }
    }

    public void Process(Span<byte> data)
    {
        for (int k = 0; k < data.Length; k++)
        {
            _i = (_i + 1) & 0xFF;
            _j = (_j + _s[_i]) & 0xFF;
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
            data[k] ^= _s[(_s[_i] + _s[_j]) & 0xFF];
        }
    }
}
