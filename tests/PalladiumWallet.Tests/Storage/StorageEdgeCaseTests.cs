using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Tests.Storage;

[Collection("AppPaths")] // DefaultDataRoot reads the static override seam
public class StorageEdgeCaseTests
{
    [Fact]
    public void SyncCache_RoundTripsEveryField_ThroughEncryptedStore()
    {
        var doc = new WalletDocument
        {
            Network = "regtest",
            ScriptKind = nameof(ScriptKind.NativeSegwit),
            Cache = new SyncCache
            {
                TipHeight = 123, ConfirmedSats = 5_000, UnconfirmedSats = 7, ImmatureSats = 100,
                PendingVerificationSats = 200, SpendableSats = 4_700, NextReceiveIndex = 3, NextChangeIndex = 2,
                History = [new CachedTx { Txid = "aa", Height = 10, DeltaSats = -50, Verified = true }],
                Utxos = [new CachedUtxo { Txid = "bb", Vout = 1, ValueSats = 99, Address = "a", IsChange = true,
                    AddressIndex = 4, Height = 11, IsCoinbase = true, Frozen = true, Verified = true }],
                Addresses = [new CachedAddress { Address = "a", IsChange = true, Index = 4, BalanceSats = 99, TxCount = 2 }],
                RawTxHex = new() { ["aa"] = "0102" },
                VerifiedAt = new() { ["aa"] = 10 },
                BlockHeaders = new() { [10] = "00" },
                AnchoredDownTo = new() { [100] = 150 },
            },
        };
        var path = Path.Combine(Path.GetTempPath(), $"plm-cache-{Guid.NewGuid()}.json");
        try
        {
            WalletStore.Save(doc, path, "pw");
            var c = WalletStore.Load(path, "pw").Cache!;

            Assert.Equal((123, 5_000, 7, 100, 200, 4_700, 3, 2),
                (c.TipHeight, c.ConfirmedSats, c.UnconfirmedSats, c.ImmatureSats, c.PendingVerificationSats,
                 c.SpendableSats, c.NextReceiveIndex, c.NextChangeIndex));
            Assert.True(c.History[0].Verified);
            Assert.True(c.Utxos[0].Frozen && c.Utxos[0].IsCoinbase && c.Utxos[0].IsChange);
            Assert.Equal(2, c.Addresses[0].TxCount);
            Assert.Equal("0102", c.RawTxHex!["aa"]);
            Assert.Equal(10, c.VerifiedAt!["aa"]);
            Assert.Equal("00", c.BlockHeaders![10]);
            Assert.Equal(150, c.AnchoredDownTo![100]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    public void AppConfig_FallsBackToDefaults_OnCorruptedOrEmptyFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plm-config-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, content);
            var config = AppConfig.Load(path);
            Assert.NotNull(config);
            Assert.Null(config.LastServerHost);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AppConfig_Load_ReturnsDefaultsWhenFileIsMissing() =>
        Assert.NotNull(AppConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.json")));

    [Fact]
    public void DefaultDataRoot_FollowsPlatformConvention()
    {
        var saved = AppPaths.DefaultRootOverride;
        AppPaths.DefaultRootOverride = null;
        try
        {
            var root = AppPaths.DefaultDataRoot();
            if (OperatingSystem.IsWindows())
                Assert.EndsWith(AppPaths.AppDirName, root);
            else
                Assert.EndsWith(".palladium-wallet", root);
        }
        finally { AppPaths.DefaultRootOverride = saved; }
    }
}
