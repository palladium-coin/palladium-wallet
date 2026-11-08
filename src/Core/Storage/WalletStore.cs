using System.Text;

namespace PalladiumWallet.Core.Storage;

/// <summary>
/// Reads and writes the wallet file on disk (blueprint §8): plain JSON without a
/// password, AES-GCM container with one. Writes are atomic (temporary file + rename)
/// so a crash never corrupts the wallet.
/// </summary>
public static class WalletStore
{
    public static bool Exists(string path) => File.Exists(path);

    /// <summary>True when the file requires a password to open.</summary>
    public static bool RequiresPassword(string path) =>
        EncryptedFile.IsEncrypted(File.ReadAllText(path));

    public static WalletDocument Load(string path, string? password = null)
    {
        var content = File.ReadAllText(path);
        if (EncryptedFile.IsEncrypted(content))
        {
            if (string.IsNullOrEmpty(password))
                throw new WrongPasswordException();
            content = EncryptedFile.Decrypt(content, password);
        }
        return WalletDocument.FromJson(content);
    }

    /// <param name="password">Null saves in plaintext. Only omit when the user has
    /// explicitly opted out of encryption (UI must show a clear warning).</param>
    public static void Save(WalletDocument doc, string path, string? password = null)
    {
        var content = doc.ToJson();
        if (!string.IsNullOrEmpty(password))
            content = EncryptedFile.Encrypt(content, password);

        // One save at a time in this process (Save and SaveAsync share the temp name); other
        // processes are kept out by WalletLock. The temp file is created owner-only (0600 on
        // Unix: a plaintext wallet must not be readable by other users) and flushed to disk
        // before the rename, so a power loss leaves the old file or the new one, never an empty one.
        lock (SaveLock)
        {
            var tmp = path + ".tmp";
            File.Delete(tmp); // a leftover from a crash could have looser permissions
            using (var stream = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
    }

    private static readonly object SaveLock = new();

    /// <summary>
    /// Same as <see cref="Save"/> but with the JSON serialization and disk write off the
    /// calling thread — for callers on a UI thread saving a large cache (thousands of cached
    /// transactions/headers), where the synchronous version would block the UI.
    /// </summary>
    public static Task SaveAsync(WalletDocument doc, string path, string? password = null) =>
        Task.Run(() => Save(doc, path, password));
}
