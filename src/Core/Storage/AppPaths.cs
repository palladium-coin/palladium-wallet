using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Core.Storage;

/// <summary>
/// Per-platform data paths (blueprint §8). The data root can be:
/// 1. <b>portable</b>: "palladium-data" folder next to the executable;
/// 2. <b>custom</b>: chosen by the user at first launch and stored in
///    a small "pointer" file at a fixed bootstrap location;
/// 3. <b>default</b>: %APPDATA%\PalladiumWallet (Windows) or ~/.palladium-wallet (Linux/macOS).
/// Under the root there is a per-network subfolder (config, wallet, header, certificates).
/// </summary>
public static class AppPaths
{
    public const string PortableDirName = "palladium-data";

    /// <summary>Application folder name, used in the various paths.</summary>
    public const string AppDirName = "PalladiumWallet";

    /// <summary>Explicit override of the data root (e.g. CLI --data-dir). Takes priority over everything.</summary>
    public static string? OverrideDataRoot { get; set; }

    // Test seams: redirect the machine-global bootstrap locations (APPDATA pointer
    // dir, executable dir, per-user default root) to a sandbox so the resolution
    // precedence can be exercised without touching real user data.
    internal static string? BootstrapDirOverride { get; set; }
    internal static string? PortableBaseOverride { get; set; }
    internal static string? DefaultRootOverride { get; set; }

    /// <summary>
    /// Default data root, following each platform's convention:
    /// Windows → %APPDATA%\PalladiumWallet (PascalCase, like Electrum/Bitcoin);
    /// Linux/macOS → ~/.palladium-wallet (lowercase dotfolder, like ~/.bitcoin).
    /// Per-user and always writable, without administrator privileges.
    /// </summary>
    public static string DefaultDataRoot()
    {
        if (DefaultRootOverride is { } o)
            return o;
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppDirName);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".palladium-wallet");
    }

    /// <summary>Pointer file to the user-chosen data root. Lives at a
    /// bootstrap location that is always writable and independent of the data root.</summary>
    private static string LocationPointerPath() =>
        Path.Combine(
            BootstrapDirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDirName),
            "data-location");

    private static string PortableRoot() =>
        Path.Combine(PortableBaseOverride ?? AppContext.BaseDirectory, PortableDirName);

    private static bool HasData(string root) =>
        Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();

    /// <summary>Effective data root, following the precedence order documented on the class.</summary>
    public static string DataRoot()
    {
        if (!string.IsNullOrEmpty(OverrideDataRoot))
            return OverrideDataRoot;

        var portable = PortableRoot();
        if (Directory.Exists(portable))
            return portable;

        if (ReadPointer() is { } custom)
            return custom;

        return DefaultDataRoot();
    }

    /// <summary>
    /// true if the data location is already determined and need not be asked
    /// of the user: portable mode, override, pointer already written, or
    /// data already present at the default location.
    /// </summary>
    public static bool IsDataLocationConfigured() =>
        !string.IsNullOrEmpty(OverrideDataRoot)
        || Directory.Exists(PortableRoot())
        || ReadPointer() is not null
        || HasData(DefaultDataRoot());

    /// <summary>Stores the user-chosen data root and creates it on disk.</summary>
    public static void ConfigureDataLocation(string root)
    {
        root = Path.GetFullPath(root.Trim());
        Directory.CreateDirectory(root);
        var pointer = LocationPointerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        File.WriteAllText(pointer, root);
    }

    private static string? ReadPointer()
    {
        var pointer = LocationPointerPath();
        if (!File.Exists(pointer))
            return null;
        var path = File.ReadAllText(pointer).Trim();
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>Network data folder (config, wallet, header, certificates).</summary>
    public static string ForNetwork(NetKind net)
    {
        var dir = Path.Combine(DataRoot(), ChainProfiles.For(net).NetName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string WalletsDir(NetKind net)
    {
        var dir = Path.Combine(ForNetwork(net), "wallets");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string DefaultWalletPath(NetKind net) =>
        Path.Combine(WalletsDir(net), "default.wallet.json");

    /// <summary>All wallet files for the network, ordered by name (multi-wallet §8).</summary>
    public static IReadOnlyList<string> WalletFiles(NetKind net)
    {
        var dir = WalletsDir(net);
        return Directory.EnumerateFiles(dir, "*.wallet.json")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string CertificatePinsPath(NetKind net) =>
        Path.Combine(ForNetwork(net), "server-certs.json");

    public static string ServersPath(NetKind net) =>
        Path.Combine(ForNetwork(net), "servers.json");

    public static string ConfigPath() =>
        Path.Combine(DataRoot(), "config.json");
}
