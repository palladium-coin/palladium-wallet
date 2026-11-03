using System.Globalization;
using System.Text.Json;
using PalladiumWallet.Core.Net;

namespace PalladiumWallet.Core.Wallet;

/// <summary>
/// Fee suggestions from the indexing server (untrusted input): the relay fee and estimates for
/// three confirmation targets, in sat/vB. A null value means the server could not provide one
/// or gave an implausible one (ADR 0039).
/// </summary>
public sealed record FeeEstimates(decimal? Relay, decimal? Fast, decimal? Normal, decimal? Economy)
{
    public bool HasAny => Fast is not null || Normal is not null || Economy is not null;
}

/// <summary>Conversions and checks for the fee rate, kept pure so they can be tested exhaustively.</summary>
public static class FeePolicy
{
    public const int FastTarget = 2;
    public const int NormalTarget = 6;
    public const int EconomyTarget = 24;

    /// <summary>The fee warning threshold: a fee above this share of the amount sent.</summary>
    public const int HighFeePercent = 1;

    private const decimal SatsPerCoin = 100_000_000m;
    private const decimal BytesPerKb = 1000m;

    /// <summary>
    /// Converts a server value in coin/kB to sat/vB (× 100,000,000 / 1,000). Null for "no
    /// estimate" (-1), zero, negative, or above <see cref="CoinAmount.MaxFeeRate"/>, which is
    /// treated as an implausible answer rather than followed.
    /// </summary>
    public static decimal? ToSatPerVbyte(decimal coinPerKb)
    {
        if (coinPerKb <= 0)
            return null;
        var rate = Math.Round(coinPerKb * SatsPerCoin / BytesPerKb, 2, MidpointRounding.AwayFromZero);
        return rate is > 0 and <= CoinAmount.MaxFeeRate ? rate : null;
    }

    /// <summary>An estimate never goes below the relay fee: the node would reject it.</summary>
    public static decimal? AtLeastRelay(decimal? estimate, decimal? relay) =>
        estimate is { } e ? Math.Max(e, relay ?? 0m) : null;

    /// <summary>The preselected rate: the normal estimate, else 1 sat/vB, never below the relay fee.</summary>
    public static decimal DefaultRate(FeeEstimates? estimates) =>
        Math.Max(estimates?.Normal ?? 1m, estimates?.Relay ?? 0m);

    /// <summary>True when a manual rate is below the server's minimum relay fee.</summary>
    public static bool IsBelowRelay(decimal rate, decimal? relay) => relay is { } r && rate < r;

    /// <summary>True when the fee is more than <see cref="HighFeePercent"/>% of the amount sent.</summary>
    public static bool IsHighFee(long feeSats, long amountSats) =>
        amountSats > 0 && feeSats * 100 > amountSats * HighFeePercent;

    public static string Format(decimal rate) => rate.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Asks the server for the relay fee and the three estimates. Every call is optional: a
    /// server error or an unusable value just leaves that field null.
    /// </summary>
    public static async Task<FeeEstimates> FetchAsync(ElectrumClient client, CancellationToken ct = default)
    {
        var relay = await TryAsync(() => client.RelayFeeAsync(ct));
        var fast = await TryAsync(() => client.EstimateFeeAsync(FastTarget, ct));
        var normal = await TryAsync(() => client.EstimateFeeAsync(NormalTarget, ct));
        var economy = await TryAsync(() => client.EstimateFeeAsync(EconomyTarget, ct));
        return Build(relay, fast, normal, economy);
    }

    /// <summary>Builds the estimates from raw server values (coin/kB), applying the sanity rules.</summary>
    public static FeeEstimates Build(decimal? relayCoinPerKb, decimal? fast, decimal? normal, decimal? economy)
    {
        var relay = relayCoinPerKb is { } r ? ToSatPerVbyte(r) : null;
        decimal? Convert(decimal? v) => AtLeastRelay(v is { } x ? ToSatPerVbyte(x) : null, relay);
        return new FeeEstimates(relay, Convert(fast), Convert(normal), Convert(economy));
    }

    private static async Task<decimal?> TryAsync(Func<Task<decimal>> call)
    {
        try { return await call(); }
        catch (Exception ex) when (ex is ElectrumServerException or InvalidOperationException
                                       or FormatException or JsonException) { return null; }
    }
}
