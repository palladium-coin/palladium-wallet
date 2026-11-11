using Avalonia;
using Avalonia.Media;
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

    private static byte GlassAlpha(ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource("GlassBrush", variant, out var brush));
        return ((ISolidColorBrush)brush!).Color.A;
    }

    private static void ResetSurfaces()
    {
        // The merged dictionary is process-wide: remove it so another test sees the glass look.
        var merged = Application.Current!.Resources.MergedDictionaries;
        foreach (var d in merged.OfType<Avalonia.Markup.Xaml.Styling.ResourceInclude>().ToList())
            if (d.Source?.OriginalString.Contains("SolidSurfaces") == true) merged.Remove(d);
        Application.Current.RequestedThemeVariant = ThemeVariant.Default;
    }

    [Fact]
    public Task Solid_surfaces_swap_the_glass_for_opaque_colours_live_and_back() => UiSession.Run(() =>
    {
        try
        {
            var vm = new MainWindowViewModel();
            Assert.False(vm.SolidSurfaces);
            Assert.True(GlassAlpha(ThemeVariant.Light) < 255);   // glass: translucent
            Assert.True(GlassAlpha(ThemeVariant.Dark) < 255);

            vm.SolidSurfaces = true;
            Assert.Equal(255, GlassAlpha(ThemeVariant.Light));   // solid: opaque in both variants
            Assert.Equal(255, GlassAlpha(ThemeVariant.Dark));
            Assert.True(AppConfig.Load().SolidSurfaces);          // persisted

            vm.SolidSurfaces = false;
            Assert.True(GlassAlpha(ThemeVariant.Light) < 255);
            Assert.False(AppConfig.Load().SolidSurfaces);
        }
        finally { ResetSurfaces(); }
    });

    [Fact]
    public Task Turning_solid_surfaces_on_twice_merges_the_dictionary_once() => UiSession.Run(() =>
    {
        try
        {
            var vm = new MainWindowViewModel();
            vm.SolidSurfaces = true;
            vm.SetThemeCommand.Execute("dark");                   // another settings change while it is on
            vm.SetLanguageCommand.Execute("en");
            var merged = Application.Current!.Resources.MergedDictionaries;
            Assert.Equal(1, merged.OfType<Avalonia.Markup.Xaml.Styling.ResourceInclude>()
                .Count(d => d.Source?.OriginalString.Contains("SolidSurfaces") == true));
        }
        finally { ResetSurfaces(); }
    });

    [Fact]
    public Task The_saved_choice_of_solid_surfaces_is_applied_at_startup() => UiSession.Run(() =>
    {
        try
        {
            var config = AppConfig.Load();
            config.SolidSurfaces = true;
            config.Save();
            _ = new MainWindowViewModel();
            Assert.Equal(255, GlassAlpha(ThemeVariant.Light));
        }
        finally { ResetSurfaces(); }
    });

    [Fact]
    public void A_config_without_the_field_keeps_the_glass_look()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plm-cfg-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """{ "Language": "it" }""");
            Assert.False(AppConfig.Load(path).SolidSurfaces);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Every_language_has_the_solid_surfaces_labels()
    {
        foreach (var lang in new[] { "it", "en", "es", "fr", "pt", "de", "zh" })
        {
            Loc.SwitchTo(lang);
            foreach (var key in new[] { "settings.solid", "settings.solid.hint" })
            {
                Assert.False(string.IsNullOrWhiteSpace(Loc.Tr(key)));
                Assert.NotEqual(key, Loc.Tr(key));
            }
        }
        Loc.SwitchTo("en");
    }

    [Fact]
    public Task A_toggle_button_has_the_size_and_shape_of_a_button() => UiSession.Run(() =>
    {
        var button = new Avalonia.Controls.Button { Content = "x" };
        var toggle = new Avalonia.Controls.Primitives.ToggleButton { Content = "x" };
        var window = new Avalonia.Controls.Window { Content = new Avalonia.Controls.StackPanel { Children = { button, toggle } } };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(button.MinHeight, toggle.MinHeight);
        Assert.Equal(button.CornerRadius, toggle.CornerRadius);
        window.Close();
    });
}
