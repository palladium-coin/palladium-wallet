namespace PalladiumWallet.Core.Crypto;

/// <summary>
/// The 8-character checksum of an output descriptor (BIP380), as Bitcoin Core computes it for
/// <c>getdescriptorinfo</c>. It covers every character of the descriptor, so for a multisig wallet it is a short
/// code that changes if any cosigner key, the threshold or the order of the key list changes: the cosigners compare
/// it to be sure they all built the same wallet.
/// </summary>
public static class DescriptorChecksum
{
    private const string InputCharset =
        "0123456789()[],'/*abcdefgh@:$%{}IJKLMNOPQRSTUVWXYZ&+-.;<=>?!^_|~ijklmnopqrstuvwxyzABCDEFGH`#\"\\ ";
    private const string ChecksumCharset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>The checksum of <paramref name="descriptor"/> (without a "#checksum" suffix); null if it holds a character a descriptor cannot.</summary>
    public static string? Compute(string descriptor)
    {
        ulong c = 1;
        var cls = 0;
        var classCount = 0;
        foreach (var ch in descriptor)
        {
            var position = InputCharset.IndexOf(ch);
            if (position < 0)
                return null;
            c = PolyMod(c, (ulong)(position & 31));
            cls = cls * 3 + (position >> 5);
            if (++classCount == 3)
            {
                c = PolyMod(c, (ulong)cls);
                cls = 0;
                classCount = 0;
            }
        }
        if (classCount > 0)
            c = PolyMod(c, (ulong)cls);
        for (var i = 0; i < 8; i++)
            c = PolyMod(c, 0);
        c ^= 1;
        return string.Create(8, c, (span, value) =>
        {
            for (var i = 0; i < 8; i++)
                span[i] = ChecksumCharset[(int)((value >> (5 * (7 - i))) & 31)];
        });
    }

    private static ulong PolyMod(ulong c, ulong value)
    {
        var top = c >> 35;
        c = ((c & 0x7ffffffffUL) << 5) ^ value;
        if ((top & 1) != 0) c ^= 0xf5dee51989UL;
        if ((top & 2) != 0) c ^= 0xa9fdca3312UL;
        if ((top & 4) != 0) c ^= 0x1bab10e32dUL;
        if ((top & 8) != 0) c ^= 0x3706b1677aUL;
        if ((top & 16) != 0) c ^= 0x644d626ffdUL;
        return c;
    }
}
