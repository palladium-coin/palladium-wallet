using NBitcoin;
using NBitcoin.Crypto;

namespace PalladiumWallet.Core.Spv;

/// <summary>
/// Merkle proof verification (blueprint §7.1/§7.4): server replies are not trusted,
/// so every confirmed transaction is checked against the merkle_root of the header
/// of its block (§17).
/// </summary>
public static class MerkleProof
{
    /// <summary>
    /// Walks the proof from the txid up to the root: at each level the sibling node is
    /// concatenated on the left or the right according to the position bit, hashed with
    /// double SHA-256. The nodes are in the order returned by the server (bottom to top),
    /// in display hex (bytes reversed).
    /// </summary>
    public static uint256 ComputeRoot(uint256 txid, int position, IEnumerable<uint256> branch)
    {
        var hash = txid;
        Span<byte> data = stackalloc byte[64];
        foreach (var sibling in branch)
        {
            if ((position & 1) == 0)
            {
                hash.ToBytes(data[..32]);
                sibling.ToBytes(data[32..]);
            }
            else
            {
                sibling.ToBytes(data[..32]);
                hash.ToBytes(data[32..]);
            }
            hash = new uint256(Hashes.DoubleSHA256RawBytes(data.ToArray(), 0, 64));
            position >>= 1;
        }
        return hash;
    }

    public static bool Verify(uint256 txid, int position, IEnumerable<uint256> branch, uint256 merkleRoot) =>
        ComputeRoot(txid, position, branch) == merkleRoot;

    /// <summary>Merkle root of a complete list of txids (for tests and small blocks).</summary>
    public static uint256 ComputeRootFromLeaves(IReadOnlyList<uint256> txids)
    {
        if (txids.Count == 0)
            throw new ArgumentException("Serve almeno una foglia.", nameof(txids));

        var level = txids.ToList();
        while (level.Count > 1)
        {
            var next = new List<uint256>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Count ? level[i + 1] : level[i]; // dispari: duplica l'ultimo
                var data = new byte[64];
                left.ToBytes(data.AsSpan(..32));
                right.ToBytes(data.AsSpan(32..));
                next.Add(new uint256(Hashes.DoubleSHA256RawBytes(data, 0, 64)));
            }
            level = next;
        }
        return level[0];
    }
}
