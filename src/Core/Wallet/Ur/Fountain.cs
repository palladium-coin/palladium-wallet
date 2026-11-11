using System.Security.Cryptography;

namespace PalladiumWallet.Core.Wallet.Ur;

/// <summary>
/// The fountain code of BCR-2020-005: a message is cut into equal fragments; the first parts carry one fragment each,
/// the following ones the XOR of a pseudo-randomly chosen few, so a receiver that scanned any sufficient subset of an
/// endless stream of QR frames can rebuild the message, whichever frames it missed.
/// </summary>
internal static class Fountain
{
    /// <summary>The fragment length for a message and a maximum: as even as possible, never above the maximum.</summary>
    public static int FragmentLength(int messageLength, int maxFragmentLength)
    {
        var count = (messageLength + maxFragmentLength - 1) / maxFragmentLength;
        return (messageLength + count - 1) / count;
    }

    /// <summary>The fragments (indexes) that part <paramref name="seqNum"/> combines; the same on both sides.</summary>
    public static List<int> ChooseFragments(uint seqNum, int seqLen, uint checksum)
    {
        if (seqNum <= (uint)seqLen)
            return [(int)seqNum - 1];
        var seed = new byte[8];
        for (var i = 0; i < 4; i++)
        {
            seed[i] = (byte)(seqNum >> (24 - 8 * i));
            seed[4 + i] = (byte)(checksum >> (24 - 8 * i));
        }
        var rng = new Xoshiro256(seed);
        var degree = ChooseDegree(seqLen, rng);
        var remaining = Enumerable.Range(0, seqLen).ToList();
        var chosen = new List<int>(degree);
        while (chosen.Count < degree)
        {
            var at = rng.NextInt(0, remaining.Count - 1);
            chosen.Add(remaining[at]);
            remaining.RemoveAt(at);
        }
        return chosen;
    }

    // Degree d is drawn with weight 1/d (robust-soliton-like), through a Walker alias table as the reference does.
    private static int ChooseDegree(int seqLen, Xoshiro256 rng)
    {
        var probs = new double[seqLen];
        var sum = 0.0;
        for (var i = 0; i < seqLen; i++) { probs[i] = 1.0 / (i + 1); sum += probs[i]; }
        for (var i = 0; i < seqLen; i++) probs[i] = probs[i] * seqLen / sum;

        var small = new Stack<int>();
        var large = new Stack<int>();
        for (var i = seqLen - 1; i >= 0; i--)
            (probs[i] < 1 ? small : large).Push(i);
        var p = new double[seqLen];
        var alias = new int[seqLen];
        while (small.Count > 0 && large.Count > 0)
        {
            var a = small.Pop();
            var g = large.Pop();
            p[a] = probs[a];
            alias[a] = g;
            probs[g] += probs[a] - 1;
            (probs[g] < 1 ? small : large).Push(g);
        }
        while (large.Count > 0) p[large.Pop()] = 1;
        while (small.Count > 0) p[small.Pop()] = 1;

        var r1 = rng.NextDouble();
        var r2 = rng.NextDouble();
        var idx = (int)(seqLen * r1);
        return (r2 < p[idx] ? idx : alias[idx]) + 1;
    }

    public static uint Checksum(byte[] message) => Crc32.Compute(message);

    public static byte[][] Split(byte[] message, int fragmentLength)
    {
        var count = (message.Length + fragmentLength - 1) / fragmentLength;
        var frags = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            frags[i] = new byte[fragmentLength];   // zero padded at the end
            var n = Math.Min(fragmentLength, message.Length - i * fragmentLength);
            Array.Copy(message, i * fragmentLength, frags[i], 0, n);
        }
        return frags;
    }

    public static void Xor(byte[] into, byte[] other)
    {
        for (var i = 0; i < into.Length; i++)
            into[i] ^= other[i];
    }
}
