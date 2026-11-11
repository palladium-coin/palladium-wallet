using System.Text;
using PalladiumWallet.Core.Wallet.Ur;
using Xunit;

namespace PalladiumWallet.Tests.Wallet;

public class UrTests
{
    [Fact]
    public void WordList_has_256_sorted_unique_words_with_unique_minimal_codes()
    {
        var w = Bytewords.WordList;
        Assert.Equal(256, w.Length);
        Assert.All(w, x => Assert.Equal(4, x.Length));
        Assert.Equal(w.OrderBy(x => x, StringComparer.Ordinal), w);
        Assert.Equal(256, w.Select(x => (x[0], x[3])).Distinct().Count());
        Assert.Equal("able", w[0]);
        Assert.Equal("zoom", w[255]);
    }

    // Reference vector of the UR test suite: Xoshiro256** seeded with SHA-256("Wolf"), first outputs modulo 100.
    [Fact]
    public void Xoshiro_matches_the_reference_sequence()
    {
        var rng = new Xoshiro256(Encoding.UTF8.GetBytes("Wolf"));
        var got = Enumerable.Range(0, 6).Select(_ => (int)(rng.Next() % 100)).ToArray();
        Assert.Equal(new[] { 42, 81, 85, 8, 82, 84 }, got);
    }

    [Fact]
    public void Crc32_matches_the_standard_check_value()
        => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void Bytewords_roundtrip_and_detect_corruption()
    {
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var text = Bytewords.EncodeMinimal(data);
        Assert.Equal(data, Bytewords.DecodeMinimal(text));
        Assert.Equal(data, Bytewords.DecodeMinimal(text.ToUpperInvariant()));
        var bad = (text[0] == 'a' ? 'b' : 'a') + text[1..];
        Assert.ThrowsAny<FormatException>(() => Bytewords.DecodeMinimal(bad));
    }

    private static byte[] Sample(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void Short_psbt_is_one_frame()
    {
        var psbt = Sample(60, 1);
        var enc = UrEncoder.ForPsbt(psbt, 200);
        Assert.True(enc.IsSinglePart);
        var d = new UrDecoder();
        Assert.True(d.Receive(enc.NextPart()));
        Assert.True(d.IsComplete);
        Assert.Equal(psbt, d.Psbt);
    }

    [Theory]
    [InlineData(500, 100)]
    [InlineData(1000, 33)]
    [InlineData(4097, 250)]
    [InlineData(101, 100)]
    public void Multipart_roundtrips_in_order(int size, int fragment)
    {
        var psbt = Sample(size, size);
        var enc = UrEncoder.ForPsbt(psbt, fragment);
        var d = new UrDecoder();
        for (var i = 0; i < enc.PartCount * 20 && !d.IsComplete; i++)
            Assert.True(d.Receive(enc.NextPart()));
        Assert.True(d.IsComplete);
        Assert.Equal(psbt, d.Psbt);
    }

    [Fact]
    public void Multipart_survives_lost_frames_and_a_late_start()
    {
        var psbt = Sample(2000, 7);
        var enc = UrEncoder.ForPsbt(psbt, 80);
        var frames = Enumerable.Range(0, enc.PartCount * 40).Select(_ => enc.NextPart()).ToList();
        var rnd = new Random(3);
        var d = new UrDecoder();
        // skip the first frames (the reader pointed the camera late), then drop 2 frames out of 3
        foreach (var f in frames.Skip(5).Where(_ => rnd.Next(3) == 0))
        {
            d.Receive(f);
            if (d.IsComplete) break;
        }
        Assert.True(d.IsComplete);
        Assert.Equal(psbt, d.Psbt);
    }

    [Fact]
    public void Decoder_accepts_uppercase_frames_as_a_QR_returns_them()
    {
        var psbt = Sample(700, 9);
        var enc = UrEncoder.ForPsbt(psbt, 90);
        var d = new UrDecoder();
        while (!d.IsComplete)
            Assert.True(d.Receive(enc.NextPart().ToUpperInvariant()));
        Assert.Equal(psbt, d.Psbt);
    }

    [Fact]
    public void Decoder_refuses_frames_of_another_message_and_garbage()
    {
        var a = UrEncoder.ForPsbt(Sample(600, 1), 80);
        var b = UrEncoder.ForPsbt(Sample(600, 2), 80);
        var d = new UrDecoder();
        Assert.True(d.Receive(a.NextPart()));
        Assert.False(d.Receive(b.NextPart()));
        foreach (var junk in new[] { "", "ur:", "ur:crypto-psbt", "ur:crypto-psbt/1-2/zz", "ur:crypto-psbt/0-3/aeae", "bitcoin:abc",
                     "ur:crypto-psbt/1-99999999/aeaeaeaeaeaeaeae", "ur:x/y/z/w" })
            Assert.False(d.Receive(junk));
        Assert.False(d.IsComplete);
    }

    [Fact]
    public void Non_psbt_type_does_not_yield_a_psbt()
    {
        var cbor = new List<byte> { 0x41, 0x00 };
        var d = new UrDecoder();
        Assert.True(d.Receive("ur:other/" + Bytewords.EncodeMinimal(cbor.ToArray())));
        Assert.True(d.IsComplete);
        Assert.Null(d.Psbt);
    }
}
