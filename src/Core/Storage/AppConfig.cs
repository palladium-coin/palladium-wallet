using System.Text.Json;

namespace PalladiumWallet.Core.Storage;

/// <summary>
/// Global application configuration (blueprint §8), separate from wallet
/// files: language, display unit, etc. Persisted in config.json
/// in the data root (applies to all networks).
/// </summary>
public sealed class AppConfig
{
    /// <summary>UI language code.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Display unit for amounts (see <see cref="Wallet.CoinAmount.Units"/>).</summary>
    public string Unit { get; set; } = "PLM";

    /// <summary>Colour theme: <c>system</c> (follow the OS, default), <c>light</c> or <c>dark</c>. Unknown values mean <c>system</c>.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Opaque surfaces instead of the translucent glass look (for readability); off by default.</summary>
    public bool SolidSurfaces { get; set; }

    /// <summary>Last successfully connected server, persisted across sessions.</summary>
    public string? LastServerHost { get; set; }
    public int? LastServerPort { get; set; }
    public bool LastServerUseSsl { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Load(string? path = null)
    {
        path ??= AppPaths.ConfigPath();
        if (!File.Exists(path))
            return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch (JsonException)
        {
            // Corrupted config: fall back to defaults without blocking startup.
            return new AppConfig();
        }
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.ConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
