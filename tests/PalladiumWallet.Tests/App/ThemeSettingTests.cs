using Avalonia;
using Avalonia.Styling;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Tests.App;

/// <summary>The theme choice in Settings (system / light / dark): applied to the running app, saved, restored at startup.</summary>
[Collection("AppPaths")]
public sealed class ThemeSettingTests : VmTestBase
{
    private static ThemeVariant? Current => Application.Current!.RequestedThemeVariant;

    [Fact]
    public Task Choosing_a_theme_applies_it_and_saves_it() => UiSession.Run(() =>
    {
        try
        {
            var vm = new MainWindowViewModel();
            Assert.True(vm.IsThemeSystem);
            Assert.Equal(ThemeVariant.Default, Current);

            vm.SetThemeCommand.Execute("dark");
            Assert.Equal(ThemeVariant.Dark, Current);
            Assert.True(vm.IsThemeDark);
            Assert.False(vm.IsThemeSystem);
            Assert.Equal("dark", AppConfig.Load().Theme);        // persisted

            vm.SetThemeCommand.Execute("light");
            Assert.Equal(ThemeVariant.Light, Current);
            Assert.True(vm.IsThemeLight);

            vm.SetThemeCommand.Execute("system");
            Assert.Equal(ThemeVariant.Default, Current);
            Assert.True(vm.IsThemeSystem);
        }
        finally { Application.Current!.RequestedThemeVariant = ThemeVariant.Default; }
    });

    [Fact]
    public Task The_saved_theme_is_applied_at_startup() => UiSession.Run(() =>
    {
        try
        {
            var config = AppConfig.Load();
            config.Theme = "dark";
            config.Save();
            _ = new MainWindowViewModel();
            Assert.Equal(ThemeVariant.Dark, Current);
        }
        finally { Application.Current!.RequestedThemeVariant = ThemeVariant.Default; }
    });

    [Fact]
    public Task An_unknown_theme_value_means_system() => UiSession.Run(() =>
    {
        try
        {
            var vm = new MainWindowViewModel();
            vm.SetThemeCommand.Execute("neon");
            Assert.True(vm.IsThemeSystem);
            Assert.Equal("system", vm.CurrentConfig.Theme);
            Assert.Equal(ThemeVariant.Default, Current);
        }
        finally { Application.Current!.RequestedThemeVariant = ThemeVariant.Default; }
    });

    [Fact]
    public void Every_language_has_the_theme_labels()
    {
        foreach (var lang in new[] { "it", "en", "es", "fr", "pt", "de", "zh" })
        {
            Loc.SwitchTo(lang);
            foreach (var key in new[] { "settings.theme", "settings.theme.system", "settings.theme.light", "settings.theme.dark" })
            {
                Assert.False(string.IsNullOrWhiteSpace(Loc.Tr(key)));
                Assert.NotEqual(key, Loc.Tr(key));
            }
        }
        Loc.SwitchTo("en");
    }

    [Fact]
    public void A_config_without_a_theme_defaults_to_system()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plm-cfg-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """{ "Language": "it", "Unit": "PLM" }""");
            Assert.Equal("system", AppConfig.Load(path).Theme);
        }
        finally { File.Delete(path); }
    }
}
