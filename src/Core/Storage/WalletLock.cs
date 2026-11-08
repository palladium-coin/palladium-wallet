namespace PalladiumWallet.Core.Storage;

/// <summary>
/// Exclusive lock on a wallet file held for the lifetime of the session.
/// The real lock is the open FileStream with FileShare.None — the .lock file
/// is just its vessel. The OS releases it automatically on process exit or crash.
/// </summary>
public sealed class WalletLock : IDisposable
{
    private FileStream? _stream;

    private WalletLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Tries to acquire an exclusive lock for <paramref name="walletPath"/>.
    /// Returns null if another process already holds the lock (IOException).
    /// Lets UnauthorizedAccessException propagate so callers can show a distinct message.
    /// </summary>
    public static WalletLock? TryAcquire(string walletPath)
    {
        var lockPath = walletPath + ".lock";
        try
        {
            // DeleteOnClose: the file disappears together with the lock, in one step. Deleting it
            // after releasing the lock left a window in which another process could take the lock
            // on the old file while a third created and locked a new one at the same path.
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return new WalletLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
