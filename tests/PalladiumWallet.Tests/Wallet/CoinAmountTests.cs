using System;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

public class CoinAmountTests
{
    // ---- fee rate (sat/vB): culture-independent ----

    [Theory]
    [InlineData("1", "1")]
    [InlineData("1.5", "1.5")]
    [InlineData("1,5", "1.5")]
    [InlineData("0.5", "0.5")]
    [InlineData("  2 ", "2")]
    [InlineData("2.25", "2.25")]
    [InlineData("1000", "1000")]
    public void TryParseFeeRate_accepts_valid(string text, string expected)
    {
        Assert.True(CoinAmount.TryParseFeeRate(text, out var rate));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), rate);
    }

    [Theory]
    [InlineData("1.000")] // looks like a thousands separator in it-IT/de-DE but is 1.000 → accepted as 1
    public void TryParseFeeRate_dot_is_always_decimal(string text)
    {
        Assert.True(CoinAmount.TryParseFeeRate(text, out var rate));
        Assert.Equal(1m, rate);
    }

    [Theory]
    [InlineData("1e3")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("1000.01")]
    [InlineData("1,000.5")]
    [InlineData("1 000")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseFeeRate_rejects_invalid(string? text)
    {
        Assert.False(CoinAmount.TryParseFeeRate(text, out var rate));
        Assert.Equal(0m, rate);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void TryParseFeeRate_ignores_current_culture(string culture)
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            Assert.True(CoinAmount.TryParseFeeRate("1.5", out var a));
            Assert.True(CoinAmount.TryParseFeeRate("2,25", out var b));
            Assert.Equal(1.5m, a);
            Assert.Equal(2.25m, b);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    // ---- valid amounts: all units ----

    [Theory]
    [InlineData("1",          "sat",  1)]
    [InlineData("0",          "sat",  0)]
    [InlineData("0",          "PLM",  0)]
    [InlineData("0",          "mPLM", 0)]
    [InlineData("0",          "µPLM", 0)]
    [InlineData("1.5",        "PLM",  150_000_000)]
    [InlineData("0.00000001", "PLM",  1)]
    [InlineData("1",          "PLM",  100_000_000)]
    [InlineData("1.00000",    "mPLM", 100_000)]
    [InlineData("0.00001",    "mPLM", 1)]
    [InlineData("1.00",       "µPLM", 100)]
    [InlineData("0.01",       "µPLM", 1)]
    [InlineData("100000000",  "sat",  100_000_000)]
    public void Importo_valido_viene_accettato(string input, string unit, long expectedSats)
    {
        Assert.True(CoinAmount.TryParseIn(input, unit, out var sats));
        Assert.Equal(expectedSats, sats);
    }

    // ---- decimale con virgola (locale italiano) ----

    [Theory]
    [InlineData("1,5",        "PLM",  150_000_000)]
    [InlineData("1,00000",    "mPLM", 100_000)]
    [InlineData("0,00000001", "PLM",  1)]
    public void Virgola_italiana_viene_accettata(string input, string unit, long expectedSats)
    {
        Assert.True(CoinAmount.TryParseIn(input, unit, out var sats));
        Assert.Equal(expectedSats, sats);
    }

    // ---- spazi iniziali/finali ----

    [Theory]
    [InlineData("  1  ", "sat", 1)]
    [InlineData(" 1.5 ", "PLM", 150_000_000)]
    public void Spazi_iniziali_e_finali_vengono_ignorati(string input, string unit, long expectedSats)
    {
        Assert.True(CoinAmount.TryParseIn(input, unit, out var sats));
        Assert.Equal(expectedSats, sats);
    }

    // ---- importi con troppi decimali ----

    [Theory]
    [InlineData("1.9",          "sat")]
    [InlineData("1.1",          "sat")]
    [InlineData("0.001",        "µPLM")]
    [InlineData("1.500000001",  "PLM")]
    [InlineData("0.000000001",  "PLM")]
    [InlineData("0.000001",     "mPLM")]
    public void Importo_con_troppi_decimali_viene_rifiutato(string input, string unit)
    {
        Assert.False(CoinAmount.TryParseIn(input, unit, out _));
    }

    // ---- negativi ----

    [Theory]
    [InlineData("-1",   "sat")]
    [InlineData("-0.1", "PLM")]
    [InlineData("-1",   "PLM")]
    public void Importo_negativo_viene_rifiutato(string input, string unit)
    {
        Assert.False(CoinAmount.TryParseIn(input, unit, out _));
    }

    // ---- stringa vuota e non numerica ----

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e5")]
    [InlineData("∞")]
    public void Stringa_non_numerica_viene_rifiutata(string input)
    {
        Assert.False(CoinAmount.TryParseIn(input, "PLM", out _));
    }

    // ---- overflow ----

    [Fact]
    public void Overflow_viene_rifiutato()
    {
        // 92233720368.54775807 PLM supera long.MaxValue in satoshi
        Assert.False(CoinAmount.TryParseIn("99999999999", "PLM", out _));
    }

    // ---- unknown unit throws ArgumentException ----

    [Theory]
    [InlineData("banana")]
    [InlineData("BTC")]
    [InlineData("")]
    [InlineData("plm")]   // case-sensitive
    public void Unita_sconosciuta_lancia_ArgumentException(string unit)
    {
        Assert.Throws<ArgumentException>(() => CoinAmount.TryParseIn("1", unit, out _));
    }

    [Fact]
    public void FormatIn_unita_sconosciuta_lancia_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CoinAmount.FormatIn(1, "banana"));
    }

    // ---- roundtrip FormatIn → TryParseIn ----

    [Theory]
    [InlineData(0,              "PLM")]
    [InlineData(1,              "sat")]
    [InlineData(1,              "PLM")]
    [InlineData(150_000_000,    "PLM")]
    [InlineData(100_000,        "mPLM")]
    [InlineData(100,            "µPLM")]
    [InlineData(99_999_999,     "PLM")]
    public void Roundtrip_format_parse_conserva_i_satoshi(long sats, string unit)
    {
        var formatted = CoinAmount.FormatIn(sats, unit, withLabel: false);
        Assert.True(CoinAmount.TryParseIn(formatted, unit, out var parsed));
        Assert.Equal(sats, parsed);
    }

    // ---- TryParseCoins ----

    [Fact]
    public void TryParseCoins_accetta_precisione_massima()
    {
        Assert.True(CoinAmount.TryParseCoins("0.00000001", out var sats));
        Assert.Equal(1L, sats);
    }

    [Fact]
    public void TryParseCoins_virgola_italiana()
    {
        Assert.True(CoinAmount.TryParseCoins("1,5", out var sats));
        Assert.Equal(150_000_000L, sats);
    }

    [Fact]
    public void TryParseCoins_rifiuta_sotto_al_satoshi()
    {
        Assert.False(CoinAmount.TryParseCoins("0.000000001", out _));
    }

    [Fact]
    public void TryParseCoins_rifiuta_importo_negativo()
    {
        Assert.False(CoinAmount.TryParseCoins("-1", out _));
    }

    [Fact]
    public void TryParseCoins_rifiuta_overflow()
    {
        Assert.False(CoinAmount.TryParseCoins("99999999999", out _));
    }

    // ---- Format (PLM con 8 decimali) ----

    [Theory]
    [InlineData(100_000_000, "1.00000000")]
    [InlineData(1,           "0.00000001")]
    [InlineData(0,           "0.00000000")]
    public void Format_produce_stringa_con_8_decimali(long sats, string expected)
    {
        Assert.Equal(expected, CoinAmount.Format(sats));
    }
}
