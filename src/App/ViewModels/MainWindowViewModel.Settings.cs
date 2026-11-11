using CommunityToolkit.Mvvm.Input;

namespace PalladiumWallet.App.ViewModels;

public partial class MainWindowViewModel
{
    // ---- spunte del menu Impostazioni (ToggleType Radio) ----

    public bool IsLangIt => _config.Language == "it";
    public bool IsLangEn => _config.Language == "en";
    public bool IsLangEs => _config.Language == "es";
    public bool IsLangFr => _config.Language == "fr";
    public bool IsLangPt => _config.Language == "pt";
    public bool IsLangDe => _config.Language == "de";
    public bool IsLangZh => _config.Language == "zh";
    public bool IsThemeSystem => _config.Theme is not ("light" or "dark");
    public bool IsThemeLight => _config.Theme == "light";
    public bool IsThemeDark => _config.Theme == "dark";
    public bool IsUnitPlm => _config.Unit == "PLM";
    public bool IsUnitMilli => _config.Unit == "mPLM";
    public bool IsUnitMicro => _config.Unit == "µPLM";
    public bool IsUnitSat => _config.Unit == "sat";

    [RelayCommand]
    private void SetLanguage(string language)
    {
        _config.Language = language;
        ApplySettings(_config);
    }

    [RelayCommand]
    private void SetTheme(string theme)
    {
        _config.Theme = theme is "light" or "dark" ? theme : "system";
        ApplySettings(_config);
    }

    /// <summary>Sets the Avalonia theme variant from the saved choice; "system" follows the OS.</summary>
    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = _config.Theme switch
            {
                "light" => Avalonia.Styling.ThemeVariant.Light,
                "dark" => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
    }

    [RelayCommand]
    private void SetUnit(string unit)
    {
        _config.Unit = unit;
        ApplySettings(_config);
    }

    public void ApplySettings(PalladiumWallet.Core.Storage.AppConfig config)
    {
        _config = config;
        _config.Save();
        _loc = Localization.Loc.SwitchTo(config.Language);
        OnPropertyChanged(nameof(Loc));
        OnPropertyChanged(nameof(UnitLabel));
        ApplyTheme();
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsLangIt));
        OnPropertyChanged(nameof(IsLangEn));
        OnPropertyChanged(nameof(IsLangEs));
        OnPropertyChanged(nameof(IsLangFr));
        OnPropertyChanged(nameof(IsLangPt));
        OnPropertyChanged(nameof(IsLangDe));
        OnPropertyChanged(nameof(IsLangZh));
        OnPropertyChanged(nameof(IsUnitPlm));
        OnPropertyChanged(nameof(IsUnitMilli));
        OnPropertyChanged(nameof(IsUnitMicro));
        OnPropertyChanged(nameof(IsUnitSat));
        ApplyCache(_doc?.Cache);
        RefreshRecipientHint();
        OnPropertyChanged(nameof(WizardStepText));
        OnPropertyChanged(nameof(HasWizardStepText));
        // Texts built from translated parts when they were produced: render them again in the new language/unit.
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(ConnectionStatusShort));
        RenderFeeHint();
        if (_pendingSend is not null && _renderSendPreview is { } send) SendPreview = send();
        if (_pendingDonate is not null && _renderDonatePreview is { } donate) DonatePreview = donate();
        StatusMessage = Localization.Loc.Tr("msg.settings.saved");
    }

    // ---- overlay impostazioni, server, help ----

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool isSettingsOpen;

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool isHelpOpen;

    [RelayCommand]
    private void OpenHelp() => IsHelpOpen = true;

    [RelayCommand]
    private static void QuitApp() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.Shutdown();

    [RelayCommand]
    private void CloseHelp() { IsHelpOpen = false; ResetDonate(); }
}
