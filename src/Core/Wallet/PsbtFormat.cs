using NBitcoin;

namespace PalladiumWallet.Core.Wallet;

/// <summary>
/// The ways a PSBT travels: the binary file (<c>.psbt</c>, what hardware signers read from and write to an SD card and
/// what Sparrow saves), base64 text (what Electrum and most wallets copy and paste) and hex.
/// </summary>
public static class PsbtFormat
{
    private static readonly byte[] Magic = [0x70, 0x73, 0x62, 0x74, 0xff];   // "psbt" + 0xff

    /// <summary>Largest file read as a PSBT: a PSBT of a very large transaction is still far below it.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>Parses a PSBT given as raw bytes (binary format) or as text (base64 or hex).</summary>
    /// <exception cref="FormatException">Not a PSBT of this network.</exception>
    public static PSBT Parse(byte[] data, Network network)
    {
        if (data.Length > MaxBytes)
            throw new FormatException("Too large to be a PSBT.");
        try
        {
            if (data.AsSpan().StartsWith(Magic))
                return PSBT.Load(data, network);
            var text = System.Text.Encoding.ASCII.GetString(data).Trim();
            return IsHex(text) ? PSBT.Load(Convert.FromHexString(text), network) : PSBT.Parse(text, network);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or EndOfStreamException or InvalidDataException)
        {
            throw new FormatException("Not a valid PSBT (binary, base64 or hex, for this network).", ex);
        }
    }

    /// <summary>Parses PSBT text (base64 or hex).</summary>
    public static PSBT Parse(string text, Network network) => Parse(System.Text.Encoding.ASCII.GetBytes(text), network);

    /// <summary>The binary form, as written to a <c>.psbt</c> file.</summary>
    public static byte[] ToBinary(PSBT psbt) => psbt.ToBytes();

    private static bool IsHex(string s) =>
        s.Length >= 10 && s.Length % 2 == 0 && s.StartsWith("70736274ff", StringComparison.OrdinalIgnoreCase)
        && s.All(Uri.IsHexDigit);
}
