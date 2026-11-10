using PalladiumWallet.Core.Crypto;

namespace PalladiumWallet.Tests.Crypto;

/// <summary>BIP380 descriptor checksums, the ones Bitcoin Core prints in <c>getdescriptorinfo</c>.</summary>
public class DescriptorChecksumTests
{
    [Theory]
    [InlineData("raw(deadbeef)", "89f8spxm")]                                   // the BIP380 test vector
    [InlineData("addr(mkmZxiEcEd8ZqjQWVZuC6so5dFMKEFpN2j)", "02wpgw69")]        // another vector of the BIP380 reference tests
    public void MatchesTheReferenceVectors(string descriptor, string checksum) =>
        Assert.Equal(checksum, DescriptorChecksum.Compute(descriptor));

    [Fact]
    public void ADescriptorWithACharacterOutsideTheCharsetHasNoChecksum() =>
        Assert.Null(DescriptorChecksum.Compute("raw(dead\u00e9beef)"));

    [Fact]
    public void AnyChangeOfTheDescriptorChangesTheChecksum()
    {
        var reference = DescriptorChecksum.Compute("wsh(sortedmulti(2,A,B,C))");
        Assert.NotEqual(reference, DescriptorChecksum.Compute("wsh(sortedmulti(3,A,B,C))"));
        Assert.NotEqual(reference, DescriptorChecksum.Compute("wsh(sortedmulti(2,A,B,D))"));
        Assert.Equal(8, reference!.Length);
    }
}
