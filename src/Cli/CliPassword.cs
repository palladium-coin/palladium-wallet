using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

/// <summary>
/// Wallet-password handling for the CLI (ADR 0038). A password never has to be given as a
/// command-line argument (visible in the process list and shell history): it can come from an
/// environment variable, from stdin, or from a no-echo terminal prompt. Plaintext storage is an
/// explicit opt-in (<c>--no-encryption</c>), never the result of a forgotten flag.
/// </summary>
internal static class CliPassword
{
    /// <summary>Password of the wallet opened by this process, reused when the wallet is saved back.</summary>
    internal static string? Current { get; private set; }

    private static bool Interactive => !Console.IsInputRedirected;

    /// <summary>Password to decrypt <paramref name="path"/>; null when the file is not encrypted.</summary>
    internal static string? ForOpening(string[] o, string path)
    {
        if (!EncryptedFile.IsEncrypted(File.ReadAllText(path)))
            return Current = null;
        return Current = Explicit(o) ?? Prompt("Wallet password: ")
            ?? throw new WalletSpendException(
                "The wallet is encrypted: supply the password with --password-env NAME, " +
                "--password-stdin, or run in a terminal to be prompted.");
    }

    /// <summary>Password to encrypt a new wallet; null only with <c>--no-encryption</c>.</summary>
    internal static string? ForNewWallet(string[] o)
    {
        var noEncryption = o.Contains("--no-encryption");
        if (noEncryption)
        {
            if (Opt(o, "--password") is not null || Opt(o, "--password-env") is not null
                || o.Contains("--password-stdin"))
                throw new WalletSpendException("--no-encryption cannot be combined with a password option.");
            Console.Error.WriteLine("WARNING: --no-encryption: the wallet will be stored unencrypted on disk (§17).");
            return Current = null;
        }
        if (Explicit(o) is { } given)
            return Current = given;
        var first = Prompt("New wallet password: ")
            ?? throw new WalletSpendException(
                "A password is required: use --password-env NAME, --password-stdin, run in a terminal " +
                "to be prompted, or pass --no-encryption to store the wallet unencrypted.");
        if (first.Length == 0)
            throw new WalletSpendException("The password cannot be empty (use --no-encryption for no encryption).");
        if (Prompt("Repeat password: ") != first)
            throw new WalletSpendException("The passwords do not match.");
        return Current = first;
    }

    /// <summary>Password from --password, --password-env or --password-stdin; null if none given.</summary>
    private static string? Explicit(string[] o)
    {
        var flag = Opt(o, "--password");
        var env = Opt(o, "--password-env");
        var stdin = o.Contains("--password-stdin");
        if ((flag is not null ? 1 : 0) + (env is not null ? 1 : 0) + (stdin ? 1 : 0) > 1)
            throw new WalletSpendException("Use only one of --password, --password-env, --password-stdin.");

        if (flag is not null)
        {
            Console.Error.WriteLine(
                "WARNING: --password is visible in the process list and the shell history; " +
                "prefer --password-env or --password-stdin.");
            return flag;
        }
        if (env is not null)
            return Environment.GetEnvironmentVariable(env) is { Length: > 0 } v
                ? v
                : throw new WalletSpendException($"Environment variable {env} is not set or empty.");
        if (stdin)
            return Console.In.ReadLine() is { Length: > 0 } line
                ? line
                : throw new WalletSpendException("--password-stdin: no password received on stdin.");
        return null;
    }

    /// <summary>No-echo prompt on the terminal; null when there is no terminal (never hangs).</summary>
    private static string? Prompt(string text)
    {
        if (!Interactive)
            return null;
        Console.Error.Write(text);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    private static string? Opt(string[] options, string name)
    {
        var i = Array.IndexOf(options, name);
        return i >= 0 && i + 1 < options.Length ? options[i + 1] : null;
    }
}
