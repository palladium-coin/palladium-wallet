using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;

namespace PalladiumWallet.Tests.Spv;

/// <summary>The shape check of a header-range reply: an untrusted reply is never sliced unless it is exactly what was asked.</summary>
public class HeaderRangeTests
{
    private static readonly string Hex80 = new('a', 160);

    [Fact]
    public void AWellFormedReply_IsSplitIntoItsHeaders()
    {
        var hex = string.Concat(Enumerable.Range(0, 3).Select(i => new string((char)('a' + i), 160)));
        var headers = HeaderRange.Split(new HeaderRangeResponse(3, hex), requested: 5, startHeight: 10);
        Assert.Equal(3, headers.Length);
        Assert.Equal(new string('b', 160), headers[1]);
    }

    [Fact]
    public void ARangeShorterThanRequested_IsAccepted_TheCallerLoopsOnTheRest()
    {
        var headers = HeaderRange.Split(new HeaderRangeResponse(1, Hex80), requested: 2000, startHeight: 0);
        Assert.Single(headers);
    }

    [Theory]
    [InlineData(0, "")]                      // nothing returned
    [InlineData(-1, "aa")]                   // negative count
    [InlineData(3, null)]                    // no hex at all
    [InlineData(2, "aa")]                    // hex far too short for the count
    [InlineData(int.MaxValue, "aa")]         // a count that overflows count * 160 must not wrap into a match
    public void AMalformedReply_IsRejectedAsUntrustworthy(int count, string? hex) =>
        Assert.Throws<SpvVerificationException>(() =>
            HeaderRange.Split(new HeaderRangeResponse(count, hex!), requested: 2, startHeight: 7));

    [Fact]
    public void MoreHeadersThanRequested_AreRejected_EvenWhenTheLengthIsConsistent()
    {
        var hex = string.Concat(Enumerable.Repeat(Hex80, 3));
        var ex = Assert.Throws<SpvVerificationException>(() =>
            HeaderRange.Split(new HeaderRangeResponse(3, hex), requested: 2, startHeight: 7));
        Assert.Contains("height 7", ex.Message);
    }

    [Fact]
    public void ALengthOffByOneCharacter_IsRejected()
    {
        Assert.Throws<SpvVerificationException>(() =>
            HeaderRange.Split(new HeaderRangeResponse(1, Hex80 + "a"), requested: 1, startHeight: 0));
        Assert.Throws<SpvVerificationException>(() =>
            HeaderRange.Split(new HeaderRangeResponse(1, Hex80[1..]), requested: 1, startHeight: 0));
    }
}
