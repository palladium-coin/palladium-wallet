using System.Globalization;

namespace PalladiumWallet.Core.Wallet;

/// <summary>
/// Satoshi ↔ coin unit conversion (8 decimal places) for display and input.
/// All internal computation uses satoshis; the formatted string is presentation only.
/// </summary>
public static class CoinAmount
{
    public const long SatsPerCoin = 100_000_000;

    /// <summary>Selectable display units (config §8).</summary>
    public static readonly string[] Units = ["PLM", "mPLM", "µPLM", "sat"];

    /// <summary>(satoshis per unit, displayed decimals) for each unit.</summary>
    private static (long Factor, int Decimals) Of(string unit) => unit switch
    {
        "PLM"  => (SatsPerCoin, 8),
        "mPLM" => (100_000, 5),
        "µPLM" => (100, 2),
        "sat"  => (1, 0),
        _ => throw new ArgumentException($"Unknown coin unit: {unit}", nameof(unit)),
    };

    public static string Format(long sats, string unit = "") =>
        (sats / (decimal)SatsPerCoin).ToString("0.00000000", CultureInfo.InvariantCulture)
        + (unit.Length > 0 ? " " + unit : "");

    /// <summary>Formats in the chosen unit (e.g. 150000 sat → "1.50000 mPLM").</summary>
    public static string FormatIn(long sats, string unit, bool withLabel = true)
    {
        var (factor, decimals) = Of(unit);
        var value = (sats / (decimal)factor).ToString(
            decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.InvariantCulture);
        return withLabel ? $"{value} {unit}" : value;
    }

    /// <summary>Parses an amount expressed in the chosen unit into satoshis.</summary>
    public static bool TryParseIn(string text, string unit, out long sats)
    {
        sats = 0;
        var (factor, _) = Of(unit);
        text = text.Trim().Replace(',', '.');
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            || value < 0)
            return false;
        try
        {
            var satsDecimal = value * factor;
            if (satsDecimal % 1 != 0)
                return false;
            sats = (long)satsDecimal;
        }
        catch (OverflowException)
        {
            return false;
        }
        return true;
    }

    /// <summary>Parses a coin amount (decimal point or comma) into satoshis.</summary>
    public static bool TryParseCoins(string text, out long sats)
    {
        sats = 0;
        text = text.Trim().Replace(',', '.');
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var coins)
            || coins < 0)
            return false;
        try
        {
            var satsDecimal = coins * SatsPerCoin;
            if (satsDecimal % 1 != 0)
                return false;
            sats = (long)satsDecimal;
        }
        catch (OverflowException)
        {
            return false;
        }
        return true;
    }

    /// <summary>Upper bound accepted for a fee rate, in sat/vB (guards against typos).</summary>
    public const decimal MaxFeeRate = 1000m;

    /// <summary>
    /// Parses a fee rate in sat/vB, independent of the system culture. Accepts a decimal point or
    /// comma; rejects thousands separators, exponents, signs, zero and values above
    /// <see cref="MaxFeeRate"/>.
    /// </summary>
    public static bool TryParseFeeRate(string? text, out decimal satPerVbyte)
    {
        satPerVbyte = 0;
        if (text is null)
            return false;
        text = text.Trim().Replace(',', '.');
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)
            || rate <= 0 || rate > MaxFeeRate)
            return false;
        satPerVbyte = rate;
        return true;
    }
}
