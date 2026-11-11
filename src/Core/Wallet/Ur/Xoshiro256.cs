using System.Security.Cryptography;

namespace PalladiumWallet.Core.Wallet.Ur;

/// <summary>
/// The deterministic generator behind the fountain code (xoshiro256**): sender and receiver seed it from the part number
/// and the message checksum, so both derive the same choice of fragments without it being sent.
/// </summary>
internal sealed class Xoshiro256
{
    private readonly ulong[] _s = new ulong[4];

    public Xoshiro256(byte[] seed)
    {
        var digest = SHA256.HashData(seed);
        for (var i = 0; i < 4; i++)
        {
            ulong v = 0;
            for (var n = 0; n < 8; n++)
                v = v << 8 | digest[8 * i + n];
            _s[i] = v;
        }
    }

    private static ulong Rotl(ulong x, int k) => x << k | x >> (64 - k);

    public ulong Next()
    {
        var result = Rotl(_s[1] * 5, 7) * 9;
        var t = _s[1] << 17;
        _s[2] ^= _s[0];
        _s[3] ^= _s[1];
        _s[1] ^= _s[2];
        _s[0] ^= _s[3];
        _s[2] ^= t;
        _s[3] = Rotl(_s[3], 45);
        return result;
    }

    /// <summary>A double in [0, 1).</summary>
    public double NextDouble() => Next() / (ulong.MaxValue + 1.0);

    /// <summary>An integer in [low, high], both included.</summary>
    public int NextInt(int low, int high) => (int)(NextDouble() * (high - low + 1)) + low;
}
