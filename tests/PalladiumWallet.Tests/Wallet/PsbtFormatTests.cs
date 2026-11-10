using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>The forms a PSBT travels in: binary file (hardware signers, Sparrow), base64 (Electrum, copy and paste), hex.</summary>
public class PsbtFormatTests
{
    private static readonly Network Net = PalladiumNetworks.Regtest;

    private static PSBT Sample()
    {
        var tx = Net.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(Money.Satoshis(1000), new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net));
        return PSBT.FromTransaction(tx, Net);
    }

    [Fact]
    public void TheBinaryBase64AndHexFormsAllGiveTheSamePsbt()
    {
        var psbt = Sample();
        var binary = PsbtFormat.ToBinary(psbt);

        Assert.Equal(new byte[] { 0x70, 0x73, 0x62, 0x74, 0xff }, binary[..5]);       // "psbt" + 0xff
        Assert.Equal(psbt.ToBase64(), PsbtFormat.Parse(binary, Net).ToBase64());
        Assert.Equal(psbt.ToBase64(), PsbtFormat.Parse(psbt.ToBase64(), Net).ToBase64());
        Assert.Equal(psbt.ToBase64(), PsbtFormat.Parse(Convert.ToHexString(binary).ToLowerInvariant(), Net).ToBase64());
        Assert.Equal(psbt.ToBase64(), PsbtFormat.Parse("  " + psbt.ToBase64() + "\r\n", Net).ToBase64());   // a pasted line
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a psbt")]
    [InlineData("cHNidP8")]                  // truncated base64
    [InlineData("70736274ff00")]             // magic, then nothing valid
    public void NotAPsbtIsAFormatException_NeverAnythingElse(string text) =>
        Assert.Throws<FormatException>(() => PsbtFormat.Parse(text, Net));

    [Fact]
    public void ATruncatedBinaryAndAnOversizedFileAreRefused()
    {
        var binary = PsbtFormat.ToBinary(Sample());
        Assert.Throws<FormatException>(() => PsbtFormat.Parse(binary[..(binary.Length - 3)], Net));
        Assert.Throws<FormatException>(() => PsbtFormat.Parse(new byte[PsbtFormat.MaxBytes + 1], Net));
    }
}
