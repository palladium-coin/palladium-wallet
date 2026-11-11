using System.Reflection;
using System.Text.RegularExpressions;
using PalladiumWallet.App.Localization;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// Consistency of the in-code translation table: every key must have a non-empty value for
/// each of the 7 languages, format placeholders must match across languages, and every key
/// referenced from XAML or C# must exist (a missing key silently renders as the raw key).
/// </summary>
[Collection("AppPaths")] // shares the serialized collection for process-wide statics (Loc.Instance, AppPaths)
public partial class LocalizationTests
{
    private static Dictionary<string, string[]> Table() =>
        (Dictionary<string, string[]>)typeof(Loc)
            .GetField("Strings", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void EveryKey_HasOneNonEmptyValuePerLanguage()
    {
        var bad = Table()
            .Where(kv => kv.Value.Length != Loc.Languages.Length || kv.Value.Any(string.IsNullOrWhiteSpace))
            .Select(kv => kv.Key).ToList();
        Assert.True(bad.Count == 0, "Incomplete translations: " + string.Join(", ", bad));
    }

    [Fact]
    public void LanguageNames_MatchLanguages() =>
        Assert.Equal(Loc.Languages.Length, Loc.LanguageNames.Length);

    [Fact]
    public void FormatPlaceholders_AreIdenticalAcrossLanguages()
    {
        var bad = new List<string>();
        foreach (var (key, values) in Table())
        {
            var sets = values.Select(v => string.Join(",", Placeholder().Matches(v).Select(m => m.Value).Distinct().Order())).ToList();
            if (sets.Distinct().Count() > 1) bad.Add($"{key}: {string.Join(" | ", sets)}");
        }
        Assert.True(bad.Count == 0, "Placeholder mismatch: " + string.Join("; ", bad));
    }

    [Fact]
    public void Indexer_ReturnsTranslationPerLanguage_AndFallsBackToKey()
    {
        try
        {
            var table = Table();
            var key = table.Keys.First();
            for (var i = 0; i < Loc.Languages.Length; i++)
                Assert.Equal(table[key][i], Loc.SwitchTo(Loc.Languages[i])[key]);
            Assert.Equal("no.such.key", Loc.Tr("no.such.key"));
            Assert.Equal("en", Loc.SwitchTo("xx").Language); // unknown language → English
        }
        finally { Loc.SwitchTo("en"); }
    }

    [Fact]
    public void KeysUsedInSources_ExistInTable()
    {
        var root = RepoRoot();
        if (root is null) return; // running from a packaged copy without sources
        var keys = Table().Keys.ToHashSet();
        var missing = new SortedSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "App"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".axaml") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 && Path.GetFileName(f) != "Loc.cs")) // Loc.cs only mentions the syntax in comments
        {
            var text = File.ReadAllText(file);
            foreach (Match m in XamlKey().Matches(text)) if (!keys.Contains(m.Groups[1].Value)) missing.Add(m.Groups[1].Value);
            foreach (Match m in CodeKey().Matches(text)) if (!keys.Contains(m.Groups[1].Value)) missing.Add(m.Groups[1].Value);
        }
        Assert.True(missing.Count == 0, "Keys used but not defined: " + string.Join(", ", missing));
    }

    // The About text must not claim what the wizard lets the user turn off (an unencrypted wallet file): it may say the
    // keys never leave the device, and that the file is encrypted once a password is set, but not that they are "always" encrypted.
    [Fact]
    public void The_about_text_does_not_claim_the_wallet_is_always_encrypted()
    {
        var always = new[] { "sempre", "always", "siempre", "toujours", "stets", "始终" };
        foreach (var lang in new[] { "it", "en", "es", "fr", "pt", "de", "zh" })
        {
            Loc.SwitchTo(lang);
            var text = Loc.Tr("help.info");
            Assert.DoesNotContain(always, w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        Loc.SwitchTo("en");
    }

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PalladiumWallet.sln"))) return d.FullName;
        return null;
    }

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")] private static partial Regex Placeholder();
    [GeneratedRegex(@"Loc\[([A-Za-z0-9_.]+)\]")] private static partial Regex XamlKey();
    [GeneratedRegex(@"Loc\.Tr\(""([A-Za-z0-9_.]+)""\)")] private static partial Regex CodeKey();
}
