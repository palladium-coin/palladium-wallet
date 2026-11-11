using System;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.App;

/// <summary>
/// Command-line options of the desktop head. The only one is <c>--net mainnet|testnet|regtest</c>:
/// without it the wallet is on mainnet, so a test network can never be selected by accident
/// (no environment variable, no persisted setting).
/// </summary>
public static class StartupOptions
{
    /// <summary>The network the GUI runs on; mainnet unless <see cref="TryParse"/> selected another.</summary>
    public static NetKind Net { get; internal set; } = NetKind.Mainnet;

    /// <summary>
    /// Reads <c>--net NAME</c> (or <c>--net=NAME</c>) from <paramref name="args"/> and sets <see cref="Net"/>.
    /// Other arguments are left to Avalonia. Returns false with a message for a missing or unknown name,
    /// leaving <see cref="Net"/> on mainnet.
    /// </summary>
    public static bool TryParse(string[] args, out string? error)
    {
        error = null;
        Net = NetKind.Mainnet;
        for (var i = 0; i < args.Length; i++)
        {
            string? value;
            if (args[i] == "--net") value = i + 1 < args.Length ? args[++i] : null;
            else if (args[i].StartsWith("--net=", StringComparison.Ordinal)) value = args[i]["--net=".Length..];
            else continue;

            switch (value)
            {
                case "mainnet": Net = NetKind.Mainnet; break;
                case "testnet": Net = NetKind.Testnet; break;
                case "regtest": Net = NetKind.Regtest; break;
                default:
                    Net = NetKind.Mainnet;
                    error = $"Unknown network '{value}': use --net mainnet, testnet or regtest.";
                    return false;
            }
        }
        return true;
    }
}
