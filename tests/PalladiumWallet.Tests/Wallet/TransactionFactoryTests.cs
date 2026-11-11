using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

public class TransactionFactoryTests
{
    private static readonly ChainProfile Profile = ChainProfiles.Regtest;
    private static readonly Network Net = PalladiumNetworks.Regtest;

    private static HdAccount Account()
    {
        Assert.True(Bip39.TryParse(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            out var mnemonic));
        return HdAccount.FromMnemonic(mnemonic!, null, ScriptKind.NativeSegwit, Profile);
    }

    /// <summary>Fake transaction that credits <paramref name="sats"/> to the receiving/0 address.</summary>
    private static (List<CachedUtxo>, Dictionary<string, Transaction>) Fund(HdAccount account, long sats)
    {
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(sats), account.GetReceiveAddress(0));
        var txid = funding.GetHash().ToString();

        var utxos = new List<CachedUtxo>
        {
            new()
            {
                Txid = txid, Vout = 0, ValueSats = sats,
                Address = account.GetReceiveAddress(0).ToString(),
                IsChange = false, AddressIndex = 0, Height = 100, Verified = true,
            },
        };
        return (utxos, new Dictionary<string, Transaction> { [txid] = funding });
    }

    private static void AddFund(
        HdAccount account,
        List<CachedUtxo> utxos,
        Dictionary<string, Transaction> transactions,
        int index,
        long sats)
    {
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, (uint)index)));
        funding.Outputs.Add(Money.Satoshis(sats), account.GetReceiveAddress(index));
        var txid = funding.GetHash().ToString();
        transactions[txid] = funding;
        utxos.Add(new CachedUtxo
        {
            Txid = txid,
            Vout = 0,
            ValueSats = sats,
            Address = account.GetReceiveAddress(index).ToString(),
            IsChange = false,
            AddressIndex = index,
            Height = 100,
            Verified = true,
        });
    }

    [Fact]
    public void Un_utxo_confermato_ma_non_ancora_verificato_non_e_spendibile()
    {
        // Confirmed by the server, but its Merkle proof hasn't been checked yet (progressive
        // background verification, §7.4): must never be treated as spendable — otherwise a
        // malicious server could get a fabricated balance spent before the forgery is caught.
        var account = Account();
        var (utxos, txs) = Fund(account, 1_000_000);
        utxos[0].Verified = false;

        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(5), amountSats: 400_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 100));
        Assert.Contains("awaiting Merkle-proof verification", ex.Message);
    }

    [Fact]
    public void Una_spesa_firmata_verifica_e_paga_la_fee_attesa()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1_000_000);
        var destination = BitcoinAddress.Create(account.GetReceiveAddress(5).ToString(), Net);

        var built = new TransactionFactory(account).Build(
            utxos, txs, destination, amountSats: 400_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 100);

        Assert.True(built.Signed);
        // Output: recipient + change.
        Assert.Equal(2, built.Transaction.Outputs.Count);
        Assert.Contains(built.Transaction.Outputs,
            o => o.ScriptPubKey == destination.ScriptPubKey && o.Value.Satoshi == 400_000);

        // Fee consistent with the requested rate (±20% for estimation rounding).
        var vsize = built.Transaction.GetVirtualSize();
        var expected = vsize * 2;
        Assert.InRange(built.Fee.Satoshi, expected * 0.8, expected * 1.5);

        // RBF enabled (§6.6).
        Assert.All(built.Transaction.Inputs, i => Assert.True(i.Sequence < Sequence.Final));
    }

    [Fact]
    public void Invia_tutto_sottrae_la_fee_e_non_produce_change()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 500_000);
        var destination = account.GetReceiveAddress(7);

        var built = new TransactionFactory(account).Build(
            utxos, txs, destination, amountSats: 0,
            feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 100, sendAll: true);

        var output = Assert.Single(built.Transaction.Outputs);
        Assert.Equal(500_000, output.Value.Satoshi + built.Fee.Satoshi);
    }

    [Fact]
    public void Fondi_insufficienti_danno_un_errore_chiaro()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1_000);

        Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(1), amountSats: 900_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 100));
    }

    [Fact]
    public void Gli_utxo_in_mempool_non_sono_spendibili()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1_000_000);
        utxos[0].Height = 0; // in mempool: visible in pending balance, not spendable

        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(1), amountSats: 100_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 100));
        Assert.Contains("unconfirmed", ex.Message);
    }

    [Fact]
    public void Una_fee_oltre_la_policy_standard_viene_rifiutata_prima_del_broadcast()
    {
        // An absurd fee rate produces a fee far above NBitcoin's standard policy
        // cap: the builder.Verify safety net must refuse the transaction instead
        // of letting a fat-finger fee reach the network.
        var account = Account();
        var (utxos, txs) = Fund(account, 100_000_000); // 1 PLM

        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(1), amountSats: 1_000_000,
            feeRateSatPerVByte: 500_000, changeIndex: 0, tipHeight: 100));
        Assert.Contains("Invalid transaction", ex.Message);
    }

    [Fact]
    public void Gli_utxo_congelati_sono_esclusi_dalla_spesa()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1_000_000);
        utxos[0].Frozen = true; // frozen (§6.2)

        Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(1), amountSats: 100_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 100));
    }

    [Fact]
    public void Gli_utxo_coinbase_immaturi_non_sono_spendibili()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1_000_000);
        utxos[0].IsCoinbase = true;
        utxos[0].Height = 100;
        // Threshold = COINBASE_MATURITY + 1 = 121 (mirrors the Qt wallet: consensus rule is
        // nSpendHeight - nHeight >= 120, plus one block of safety margin).
        // At height=100, tip=219 → confs = 219-100+1 = 120 < 121 → immature.
        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(1), amountSats: 100_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 219));
        Assert.Contains("coinbase", ex.Message);
        Assert.Contains("120/121", ex.Message);

        // tip=220 → confs = 121 ≥ 121 → mature and spendable.
        var built = new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(1), amountSats: 100_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 220);
        Assert.True(built.Signed);
    }

    [Fact]
    public void Gli_utxo_con_meno_di_minconf_non_sono_spendibili_su_mainnet()
    {
        var mainnetAccount = HdAccount.FromMnemonic(
            Bip39.TryParse("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about", out var m) ? m! : throw new Exception(),
            null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);

        var funding = PalladiumNetworks.Mainnet.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(1_000_000), mainnetAccount.GetReceiveAddress(0));
        var txid = funding.GetHash().ToString();
        var utxos = new List<CachedUtxo>
        {
            new() { Txid = txid, Vout = 0, ValueSats = 1_000_000,
                    Address = mainnetAccount.GetReceiveAddress(0).ToString(),
                    IsChange = false, AddressIndex = 0, Height = 100, IsCoinbase = false, Verified = true },
        };
        var txs = new Dictionary<string, Transaction> { [txid] = funding };

        // 5 confirmations (tipHeight=104): below the mainnet minimum of 6.
        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(mainnetAccount).Build(
            utxos, txs, mainnetAccount.GetReceiveAddress(1), amountSats: 100_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 104));
        Assert.Contains("5 so far", ex.Message);

        // 6 confirmations (tipHeight=105): exactly at the threshold → spendable.
        var built = new TransactionFactory(mainnetAccount).Build(
            utxos, txs, mainnetAccount.GetReceiveAddress(1), amountSats: 100_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 105);
        Assert.True(built.Signed);
    }

    [Fact]
    public void Un_account_watch_only_produce_una_psbt_non_firmata()
    {
        var full = Account();
        var (utxos, txs) = Fund(full, 1_000_000);

        Assert.True(Slip132.TryDecodePublic(full.ToSlip132(), Profile, out var xpub, out var kind));
        var watchOnly = HdAccount.FromAccountXpub(xpub!, kind, Profile);

        var built = new TransactionFactory(watchOnly).Build(
            utxos, txs, full.GetReceiveAddress(5), amountSats: 400_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 100);

        Assert.False(built.Signed);
        // Air-gapped flow (§6.5): the online-machine PSBT is signed
        // offline with the keys and then finalised.
        var psbt = built.Psbt;
        psbt.SignWithKeys(full.GetExtPrivateKey(false, 0));
        psbt.Finalize();
        var tx = psbt.ExtractTransaction();
        Assert.Contains(tx.Outputs, o => o.Value.Satoshi == 400_000);
    }

    [Fact]
    public void Un_account_di_soli_indirizzi_watch_only_produce_una_psbt_non_firmata()
    {
        var full = Account();
        var (utxos, txs) = Fund(full, 1_000_000);

        // Pure address import: no private key at all (unlike xpub watch-only, which
        // can still derive public keys/scriptPubKeys — this account can't upgrade
        // to spendable without the user separately importing the matching key).
        var watchOnly = new ImportedKeyAccount(
            [(full.GetReceiveAddress(0), (Key?)null)], ScriptKind.NativeSegwit, Profile);

        var built = new TransactionFactory(watchOnly).Build(
            utxos, txs, full.GetReceiveAddress(5), amountSats: 400_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 100);

        Assert.False(built.Signed);
        Assert.True(watchOnly.IsWatchOnly);
        Assert.Null(watchOnly.GetPrivateKey(false, 0));

        var psbt = built.Psbt;
        psbt.SignWithKeys(full.GetExtPrivateKey(false, 0));
        psbt.Finalize();
        var tx = psbt.ExtractTransaction();
        Assert.Contains(tx.Outputs, o => o.Value.Satoshi == 400_000);
    }

    [Theory]
    [InlineData("0.00000001", 1L)]
    [InlineData("1", 100_000_000L)]
    [InlineData("0,5", 50_000_000L)]
    [InlineData("21.12345678", 2_112_345_678L)]
    public void Gli_importi_si_parsano_in_satoshi(string text, long expected)
    {
        Assert.True(CoinAmount.TryParseCoins(text, out var sats));
        Assert.Equal(expected, sats);
    }

    [Fact]
    public void Gli_importi_invalidi_vengono_rifiutati()
    {
        Assert.False(CoinAmount.TryParseCoins("abc", out _));
        Assert.False(CoinAmount.TryParseCoins("-1", out _));
    }

    // 1.5 PLM = 150_000_000 sat, expressed in each unit (§8).
    [Theory]
    [InlineData("PLM", "1.50000000 PLM", "1.5")]
    [InlineData("mPLM", "1500.00000 mPLM", "1500")]
    [InlineData("µPLM", "1500000.00 µPLM", "1500000")]
    [InlineData("sat", "150000000 sat", "150000000")]
    public void Le_unita_formattano_e_parsano_in_modo_coerente(string unit, string formatted, string input)
    {
        const long sats = 150_000_000;
        Assert.Equal(formatted, CoinAmount.FormatIn(sats, unit));
        Assert.True(CoinAmount.TryParseIn(input, unit, out var parsed));
        Assert.Equal(sats, parsed);
    }

    [Fact]
    public void La_config_globale_fa_roundtrip_su_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plm-config-{Guid.NewGuid()}.json");
        try
        {
            var config = new PalladiumWallet.Core.Storage.AppConfig { Language = "en", Unit = "sat" };
            config.Save(path);
            var loaded = PalladiumWallet.Core.Storage.AppConfig.Load(path);
            Assert.Equal("en", loaded.Language);
            Assert.Equal("sat", loaded.Unit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ScriptPubKeyType.Legacy)]
    [InlineData(ScriptPubKeyType.SegwitP2SH)]
    [InlineData(ScriptPubKeyType.Segwit)]
    public void Si_puo_pagare_ogni_tipo_di_indirizzo_standard(ScriptPubKeyType kind)
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1_000_000);
        var destination = new Key().PubKey.GetAddress(kind, Net);

        var built = new TransactionFactory(account).Build(
            utxos, txs, destination, amountSats: 400_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 100);

        Assert.True(built.Signed);
        Assert.Contains(built.Transaction.Outputs,
            o => o.ScriptPubKey == destination.ScriptPubKey && o.Value.Satoshi == 400_000);
    }

    [Fact]
    public void Piu_utxo_vengono_combinati_quando_uno_solo_non_basta()
    {
        var account = Account();
        var allUtxos = new List<CachedUtxo>();
        var allTxs = new Dictionary<string, Transaction>();
        for (var i = 0; i < 3; i++)
        {
            var funding = Net.CreateTransaction();
            funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, (uint)i)));
            funding.Outputs.Add(Money.Satoshis(300_000), account.GetReceiveAddress(i));
            var txid = funding.GetHash().ToString();
            allTxs[txid] = funding;
            allUtxos.Add(new CachedUtxo
            {
                Txid = txid, Vout = 0, ValueSats = 300_000,
                Address = account.GetReceiveAddress(i).ToString(),
                IsChange = false, AddressIndex = i, Height = 100, Verified = true,
            });
        }

        var built = new TransactionFactory(account).Build(
            allUtxos, allTxs, account.GetReceiveAddress(5), amountSats: 700_000,
            feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 100);

        // 700k > any pair? No: it needs all three 300k coins (600k < 700k + fee).
        Assert.Equal(3, built.Transaction.Inputs.Count);
        Assert.True(built.Signed);
        Assert.Contains(built.Transaction.Outputs, o => o.Value.Satoshi == 700_000);
    }

    [Fact]
    public void Automatic_coin_selection_uses_large_utxos_before_dust()
    {
        var account = Account();
        var allUtxos = new List<CachedUtxo>();
        var allTxs = new Dictionary<string, Transaction>();
        AddFund(account, allUtxos, allTxs, index: 0, sats: 2_000_000);
        for (var i = 1; i <= 1_200; i++)
            AddFund(account, allUtxos, allTxs, i, sats: 10_000);

        var built = new TransactionFactory(account).Build(
            allUtxos, allTxs, account.GetReceiveAddress(1_250), amountSats: 500_000,
            feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 100);

        Assert.Single(built.Transaction.Inputs);
        Assert.True(built.Transaction.GetVirtualSize() < 100_000);
        Assert.Contains(built.Transaction.Outputs, o => o.Value.Satoshi == 500_000);
    }

    [Fact]
    public void Spending_more_than_the_standard_input_limit_reports_a_clear_error()
    {
        var account = Account();
        var allUtxos = new List<CachedUtxo>();
        var allTxs = new Dictionary<string, Transaction>();
        for (var i = 0; i < 1_600; i++)
            AddFund(account, allUtxos, allTxs, i, sats: 10_000);

        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            allUtxos, allTxs, account.GetReceiveAddress(1_650), amountSats: 15_300_000,
            feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 100));

        Assert.Contains("standard relay limit", ex.Message);
        Assert.Contains("multiple smaller transactions", ex.Message);
    }

    [Fact]
    public void Un_resto_sotto_la_soglia_dust_viene_assorbito_nella_fee()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 100_000);

        // Leaves ~200 sats after the fee: below the P2WPKH dust threshold,
        // so no change output must be created and the remainder goes to the fee.
        var built = new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(5), amountSats: 99_650,
            feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 100);

        var output = Assert.Single(built.Transaction.Outputs);
        Assert.Equal(99_650, output.Value.Satoshi);
        Assert.Equal(100_000 - 99_650, built.Fee.Satoshi);
    }

    [Fact]
    public void La_firma_di_una_tx_fissa_produce_il_txid_golden()
    {
        // Golden vector for the signing path (derivation → sighash → witness):
        // the factory shuffles outputs for privacy, so the vector is anchored one
        // level below, on a transaction with fixed structure signed through PSBT
        // (the same flow used air-gapped, §6.5). RFC 6979 makes it deterministic:
        // any change in this txid is a blocking regression.
        var account = Account();

        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(1_000_000), account.GetReceiveAddress(0));

        var spend = Net.CreateTransaction();
        spend.Version = 2;
        spend.Inputs.Add(new TxIn(new OutPoint(funding, 0)) { Sequence = 0xfffffffd });
        spend.Outputs.Add(Money.Satoshis(600_000), account.GetReceiveAddress(5));
        spend.Outputs.Add(Money.Satoshis(390_000), account.GetChangeAddress(0));

        var psbt = PSBT.FromTransaction(spend, Net);
        psbt.AddCoins(new Coin(funding, 0));
        psbt.SignWithKeys(account.GetExtPrivateKey(isChange: false, 0));
        psbt.Finalize();
        var signed = psbt.ExtractTransaction();

        Assert.Equal(
            "a943cf6bf606fa0050e490cb76ed9313959d228fb0ffa235b7e8b7f6834610b6",
            signed.GetHash().ToString());
    }

    [Fact]
    public void Una_config_corrotta_torna_ai_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plm-config-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "{ rotto ");
            var loaded = PalladiumWallet.Core.Storage.AppConfig.Load(path);
            Assert.Equal("en", loaded.Language);
            Assert.Equal("PLM", loaded.Unit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_wrapped_segwit_account_can_build_and_sign_a_spend()
    {
        // P2SH-P2WPKH inputs need the redeem script on the coin; without it NBitcoin cannot even
        // estimate the input size. Found by spending from a wrapped wallet on regtest.
        Assert.True(Bip39.TryParse(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            out var mnemonic));
        var account = HdAccount.FromMnemonic(mnemonic!, null, ScriptKind.WrappedSegwit, Profile);
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(2_000_000), account.GetReceiveAddress(0));
        var txid = funding.GetHash().ToString();
        var utxos = new List<CachedUtxo>
        {
            new() { Txid = txid, Vout = 0, ValueSats = 2_000_000, Address = account.GetReceiveAddress(0).ToString(),
                    Height = 100, Verified = true },
        };
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net);

        var built = new TransactionFactory(account).Build(
            utxos, new Dictionary<string, Transaction> { [txid] = funding }, destination,
            1_000_000, 3m, changeIndex: 0, tipHeight: 200);

        Assert.True(built.Signed);
        Assert.True(built.Transaction.Inputs[0].WitScript.PushCount > 0); // segwit witness present
        Assert.NotEqual(Script.Empty, built.Transaction.Inputs[0].ScriptSig);     // the P2SH redeem-script push
        Assert.Contains(built.Transaction.Outputs, o => o.Value == Money.Satoshis(1_000_000));

        var watchOnly = HdAccount.FromAccountXpub(account.AccountXpub, ScriptKind.WrappedSegwit, Profile);
        var unsigned = new TransactionFactory(watchOnly).Build(
            utxos, new Dictionary<string, Transaction> { [txid] = funding }, destination,
            1_000_000, 3m, changeIndex: 0, tipHeight: 200);
        Assert.False(unsigned.Signed);
    }
}
