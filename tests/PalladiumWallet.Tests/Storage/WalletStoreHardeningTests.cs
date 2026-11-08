using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Tests.Storage;

/// <summary>Durability, permissions and locking of the wallet file.</summary>
public class WalletStoreHardeningTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"plm-store-{Guid.NewGuid()}.wallet.json");

    private static WalletDocument Doc(string label = "x") => new()
    {
        Network = "regtest", ScriptKind = "NativeSegwit", Mnemonic = "abandon abandon abandon",
        Labels = new() { ["k"] = label },
    };

    [Fact]
    public void A_saved_wallet_is_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows()) return; // permission bits are a Unix notion
        var path = TempPath();
        try
        {
            File.WriteAllText(path + ".tmp", "stale"); // a leftover with default (looser) permissions
            File.SetUnixFileMode(path + ".tmp", (UnixFileMode)0b110_100_100);

            WalletStore.Save(Doc(), path); // plaintext: the case where it matters most

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public void Saving_over_a_wallet_with_loose_permissions_tightens_them()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "old");
            File.SetUnixFileMode(path, (UnixFileMode)0b110_100_100);
            WalletStore.Save(Doc(), path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Concurrent_saves_never_corrupt_the_file_or_throw()
    {
        var path = TempPath();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
                i % 2 == 0 ? WalletStore.SaveAsync(Doc($"v{i}"), path) : Task.Run(() => WalletStore.Save(Doc($"v{i}"), path))));

            var loaded = WalletStore.Load(path); // a torn write would not parse
            Assert.StartsWith("v", loaded.Labels["k"]);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public void The_lock_is_exclusive_and_never_held_by_two_owners_at_once()
    {
        // Acquire/release from many threads: at no instant may two owners hold the same lock
        // (the old delete-after-release let a second and a third owner overlap).
        var path = TempPath();
        var holders = 0;
        var overlap = 0;
        try
        {
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                for (var i = 0; i < 300; i++)
                {
                    using var l = WalletLock.TryAcquire(path);
                    if (l is null) continue;
                    if (Interlocked.Increment(ref holders) > 1) Interlocked.Increment(ref overlap);
                    Thread.SpinWait(50);
                    Interlocked.Decrement(ref holders);
                }
            });
            Assert.Equal(0, overlap);
            Assert.False(File.Exists(path + ".lock")); // and the file goes away with the last owner
        }
        finally { File.Delete(path + ".lock"); }
    }
}
