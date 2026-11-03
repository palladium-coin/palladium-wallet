using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Net;

namespace PalladiumWallet.Tests.Wallet;

public class FeePolicyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ---- conversion coin/kB → sat/vB ----

    [Theory]
    [InlineData("0.00001", "1")]        // 1,000 sat/kB = 1 sat/vB
    [InlineData("0.00002", "2")]
    [InlineData("0.000015", "1.5")]
    [InlineData("0.0000125", "1.25")]
    [InlineData("0.00000123", "0.12")]  // rounded to 2 decimals
    [InlineData("0.01", "1000")]        // exactly the ceiling
    public void ToSatPerVbyte_converts(string coinPerKb, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            FeePolicy.ToSatPerVbyte(decimal.Parse(coinPerKb, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("-1")]       // "no estimate"
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("0.0100001")] // just above 1000 sat/vB
    [InlineData("50")]        // absurd
    [InlineData("0.0000000001")] // rounds to 0
    public void ToSatPerVbyte_rejects_noestimate_and_implausible(string coinPerKb) =>
        Assert.Null(FeePolicy.ToSatPerVbyte(decimal.Parse(coinPerKb, System.Globalization.CultureInfo.InvariantCulture)));

    // ---- relay floor, default, checks ----

    [Fact]
    public void AtLeastRelay_raises_a_low_estimate_and_keeps_a_high_one()
    {
        Assert.Equal(2m, FeePolicy.AtLeastRelay(1m, 2m));
        Assert.Equal(5m, FeePolicy.AtLeastRelay(5m, 2m));
        Assert.Equal(1m, FeePolicy.AtLeastRelay(1m, null));
        Assert.Null(FeePolicy.AtLeastRelay(null, 2m));
    }

    [Fact]
    public void DefaultRate_is_normal_never_below_relay_and_falls_back_to_one()
    {
        Assert.Equal(1m, FeePolicy.DefaultRate(null));
        Assert.Equal(1m, FeePolicy.DefaultRate(new FeeEstimates(null, null, null, null)));
        Assert.Equal(3m, FeePolicy.DefaultRate(new FeeEstimates(1m, 9m, 3m, 1m)));
        Assert.Equal(4m, FeePolicy.DefaultRate(new FeeEstimates(4m, null, null, null)));
    }

    [Theory]
    [InlineData("0.5", "1", true)]
    [InlineData("1", "1", false)]
    [InlineData("2", "1", false)]
    public void IsBelowRelay_compares_against_the_relay_fee(string rate, string relay, bool expected) =>
        Assert.Equal(expected, FeePolicy.IsBelowRelay(decimal.Parse(rate), decimal.Parse(relay)));

    [Fact]
    public void IsBelowRelay_without_a_known_relay_fee_never_blocks() =>
        Assert.False(FeePolicy.IsBelowRelay(0.01m, null));

    [Theory]
    [InlineData(100, 10_000, false)]   // exactly 1%
    [InlineData(101, 10_000, true)]
    [InlineData(5_000, 10_000, true)]
    [InlineData(1, 0, false)]          // no amount: nothing to compare
    public void IsHighFee_flags_more_than_one_percent(long fee, long amount, bool expected) =>
        Assert.Equal(expected, FeePolicy.IsHighFee(fee, amount));

    [Fact]
    public void Format_is_culture_independent()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("it-IT");
            Assert.Equal("1.5", FeePolicy.Format(1.5m));
            Assert.Equal("2", FeePolicy.Format(2.00m));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    // ---- Build / FetchAsync ----

    [Fact]
    public void Build_applies_the_relay_floor_and_drops_unusable_values()
    {
        var e = FeePolicy.Build(relayCoinPerKb: 0.00002m, fast: 0.00001m, normal: -1m, economy: 0.0001m);

        Assert.Equal(2m, e.Relay);
        Assert.Equal(2m, e.Fast);       // 1 raised to the relay fee 2
        Assert.Null(e.Normal);          // -1: no estimate
        Assert.Equal(10m, e.Economy);
        Assert.True(e.HasAny);
    }

    [Fact]
    public void Build_with_nothing_usable_has_no_estimates()
    {
        var e = FeePolicy.Build(null, -1m, -1m, -1m);
        Assert.False(e.HasAny);
        Assert.Null(e.Relay);
    }

    [Fact]
    public async Task FetchAsync_reads_the_relay_fee_and_the_three_targets()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.relayfee", _ => 0.00001m);
        server.Handle("blockchain.estimatefee", p => p[0].GetInt32() switch
        {
            FeePolicy.FastTarget => 0.00005m,
            FeePolicy.NormalTarget => 0.00002m,
            _ => 0.00001m,
        });
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var e = await FeePolicy.FetchAsync(client).WaitAsync(Timeout);

        Assert.Equal(new FeeEstimates(1m, 5m, 2m, 1m), e);
    }

    [Fact]
    public async Task FetchAsync_tolerates_a_server_without_the_methods_and_a_minus_one()
    {
        await using var server = new FakeElectrumServer(); // no handlers: "unknown method" errors
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var none = await FeePolicy.FetchAsync(client).WaitAsync(Timeout);
        Assert.False(none.HasAny);

        server.Handle("blockchain.relayfee", _ => 0.00001m);
        server.Handle("blockchain.estimatefee", _ => -1);
        var minusOne = await FeePolicy.FetchAsync(client).WaitAsync(Timeout);
        Assert.False(minusOne.HasAny);
        Assert.Equal(1m, minusOne.Relay);
    }

    [Fact]
    public async Task FetchAsync_ignores_an_absurd_estimate_and_a_malformed_answer()
    {
        await using var server = new FakeElectrumServer();
        server.Handle("blockchain.relayfee", _ => "not a number");
        server.Handle("blockchain.estimatefee", p => p[0].GetInt32() == FeePolicy.FastTarget ? 100m : 0.00003m);
        await using var client = await ElectrumClient.ConnectAsync(server.Host, server.Port, useSsl: false);

        var e = await FeePolicy.FetchAsync(client).WaitAsync(Timeout);

        Assert.Null(e.Relay);
        Assert.Null(e.Fast);            // 100 PLM/kB = 10,000,000 sat/vB: implausible, ignored
        Assert.Equal(3m, e.Normal);
        Assert.Equal(3m, e.Economy);
    }
}
