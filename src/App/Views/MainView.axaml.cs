using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PalladiumWallet.App.ViewModels;

namespace PalladiumWallet.App.Views;

/// <summary>
/// App root view, shared between desktop (hosted in <see cref="MainWindow"/>)
/// and mobile (single-view root). All overlays are in-app so no separate windows
/// are needed. Top-level APIs (file picker, clipboard) are reached via
/// <see cref="TopLevel.GetTopLevel"/> because a UserControl does not expose them.
/// </summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    private void OnHistoryRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox lb || DataContext is not MainWindowViewModel vm) return;
        if (lb.SelectedItem is not HistoryRow row) return;

        // In-app overlay: appears immediately with a spinner; data arrives from
        // the server in the background. No top-level window (slow to open/close).
        _ = vm.ShowTransactionDetailsAsync(row.Txid);
    }

    private void OnTxDetailsOverlayBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (DataContext is MainWindowViewModel vm)
            vm.CloseTransactionDetailsCommand.Execute(null);
    }

    private void OnAddressListTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedAddressRow is not { } row)
            return;
        vm.ShowAddressInfo(row);
    }

    private void OnAddressListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed) return;
        if (sender is not ListBox lb || DataContext is not MainWindowViewModel vm) return;

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item is not { DataContext: AddressRow row }) return;

        lb.SelectedItem = row;
        vm.ShowAddressInfo(row);
    }

    // Close the address-detail overlay: click on the dark backdrop
    // (backdrop only, not the card itself) or press Esc.
    private void OnAddressOverlayBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (DataContext is MainWindowViewModel vm)
            vm.AddressInfo = null;
    }

    private void OnServerOverlayBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (DataContext is MainWindowViewModel vm)
            vm.IsServerSettingsOpen = false;
    }

    private async void OnChooseDataFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Cartella dati Palladium Wallet",
            AllowMultiple = false,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            vm.ApplyDataLocation(path);
    }

    private async void OnCopyReceiveAddressClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrEmpty(vm.ReceiveAddress))
            return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.ReceiveAddress);
            vm.NotifyAddressCopied();
        }
    }

    private async void OnCopyPsbtClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrEmpty(vm.PendingPsbtBase64))
            return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.PendingPsbtBase64);
            vm.NotifyPsbtCopied();
        }
    }

    private void OnConnectionStatusTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsServerSettingsOpen = true;
    }

    private void OnWalletInfoOverlayBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (DataContext is MainWindowViewModel vm)
            vm.CloseWalletInfoCommand.Execute(null);
    }

    private void OnSettingsOverlayBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (DataContext is MainWindowViewModel vm)
            vm.IsSettingsOpen = false;
    }

    private void OnHelpOverlayBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (DataContext is MainWindowViewModel vm)
            vm.IsHelpOpen = false;
    }

    private void OnUpdateAvailableOverlayBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (DataContext is MainWindowViewModel vm)
            vm.IsUpdateAvailableOpen = false;
    }

    private async void OnOpenBugReportClick(object? sender, RoutedEventArgs e)
    {
        const string issueUrl = "https://github.com/palladium-coin/palladium-wallet/issues/new?template=bug_report.yml";
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
            await launcher.LaunchUriAsync(new Uri(issueUrl));
    }

    private async void OnOpenUserGuideClick(object? sender, RoutedEventArgs e)
    {
        const string userGuideUrl = "https://github.com/palladium-coin/palladium-wallet/blob/main/USERGUIDE.md";
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
            await launcher.LaunchUriAsync(new Uri(userGuideUrl));
    }

    private async void OnOpenReleasePageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrEmpty(vm.UpdateReleaseUrl)) return;
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
            await launcher.LaunchUriAsync(new Uri(vm.UpdateReleaseUrl));
        vm.IsUpdateAvailableOpen = false;
    }

    // Esc (desktop) or Back (Android) closes the topmost open overlay.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.Key == Key.Escape || e.Key == Key.Back) && DataContext is MainWindowViewModel vm)
        {
            if (vm.IsTxDetailsOpen) { vm.CloseTransactionDetailsCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsPrivKeyPromptOpen) { vm.CancelPrivKeyPromptCommand.Execute(null); e.Handled = true; return; }
            if (vm.AddressInfo is not null) { vm.CloseAddressInfoCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsServerSettingsOpen) { vm.IsServerSettingsOpen = false; e.Handled = true; return; }
            if (vm.IsWalletInfoOpen) { vm.CloseWalletInfoCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsSettingsOpen) { vm.IsSettingsOpen = false; e.Handled = true; return; }
            if (vm.IsHelpOpen) { vm.IsHelpOpen = false; e.Handled = true; return; }
            if (vm.IsUpdateAvailableOpen) { vm.IsUpdateAvailableOpen = false; e.Handled = true; return; }
        }
        base.OnKeyDown(e);
    }
}
