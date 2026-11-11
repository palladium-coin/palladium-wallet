using PalladiumWallet.Core.Net;

namespace PalladiumWallet.Core.Spv;

/// <summary>
/// Shape check of a <c>blockchain.block.headers</c> reply. The reply is untrusted: a broken or hostile
/// server may return more headers than asked, a count that does not match the hex, or nothing at all.
/// Kept apart from the synchronizer so that it is a parser with a stated error contract (and a fuzz target).
/// </summary>
public static class HeaderRange
{
    /// <summary>
    /// Splits <paramref name="range"/> into its raw header hex strings, or throws.
    /// Contract: for any input it returns exactly <c>range.Count</c> strings of 160 hex characters, with
    /// <c>1 &lt;= Count &lt;= requested</c>, or throws <see cref="SpvVerificationException"/>; nothing else escapes.
    /// The characters themselves are not checked here: <see cref="BlockHeaderInfo.Parse(string)"/> does that.
    /// </summary>
    public static string[] Split(HeaderRangeResponse range, int requested, int startHeight)
    {
        if (range.Count == 0)
            throw new SpvVerificationException(
                $"Server returned no headers starting at height {startHeight}: server is not trustworthy.");
        // More headers than asked, or a hex string that is not exactly count * 80 bytes, is a
        // broken or hostile reply: never slice it, never let it spill past the requested range.
        if (range.Count < 0 || range.Count > requested
            || range.Hex is null || range.Hex.Length != range.Count * BlockHeaderInfo.Size * 2)
            throw new SpvVerificationException(
                $"Malformed header range at height {startHeight} (count {range.Count}, requested {requested}): server is not trustworthy.");

        const int HexSize = BlockHeaderInfo.Size * 2;
        var headers = new string[range.Count];
        for (var i = 0; i < range.Count; i++)
            headers[i] = range.Hex.Substring(i * HexSize, HexSize);
        return headers;
    }
}
