using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;

namespace PalladiumWallet.Tests.App;

/// <summary>
/// The mobile layout (Android/iOS), rendered headlessly at phone width through the layout seam: the tab strip
/// stays on one row and the Send form is a vertical stack with a usable address field. (The desktop layout is not
/// meant for a 400px window; judging it there is a false alarm.)
/// </summary>
[Collection("AppPaths")]
public sealed class MobileLayoutTests : VmTestBase
{
    private static Point Origin(Visual v, Visual root) => v.TranslatePoint(new Point(0, 0), root) ?? default;

    [Fact]
    public Task At_phone_width_the_tabs_fit_one_row_and_the_send_form_is_usable() => UiSession.Run(async () =>
    {
        var account = HdAccount.FromMnemonic(ParseMnemonic(), null, ScriptKind.NativeSegwit, ChainProfiles.Mainnet);
        WireFundedChain(account.GetReceiveAddress(0), 10_000_000);
        MainWindowViewModel.MobileLayoutOverride = true;
        var vm = new MainWindowViewModel();
        Assert.True(vm.IsMobile);
        RestoreFlow(vm, Mnemonic, null, "mobile");
        await Eventually(() => !vm.IsSyncing && vm.Coins.Any(c => c.IsSelectable));
        vm.SelectedTabIndex = 1; // Send

        var view = new PalladiumWallet.App.Views.MainView { DataContext = vm };
        var window = new Window { Width = 400, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = view.GetVisualDescendants().OfType<TabItem>().ToList();
        Assert.Equal(6, tabs.Count);
        Assert.True(tabs.Select(t => Math.Round(Origin(t, view).Y)).Distinct().Count() == 1, string.Join(",", tabs.Select(t => $"{Origin(t, view)} {t.Bounds.Size}")));
        Assert.All(tabs, t => Assert.InRange(Origin(t, view).X + t.Bounds.Width, 0, 401));

        var address = view.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.IsEffectivelyVisible && t.PlaceholderText == Loc.Tr("send.to"));
        Assert.True(address.Bounds.Width >= 200, $"recipient field is {address.Bounds.Width}px wide");
        var amount = view.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.IsEffectivelyVisible && t.PlaceholderText == "0.00000000");
        Assert.True(amount.Bounds.Width >= 150, $"amount field is {amount.Bounds.Width}px wide");
        Assert.True(Origin(amount, view).Y > Origin(address, view).Y); // stacked below the recipient, not beside it
        window.Close();
    });

    [Fact]
    public Task The_desktop_layout_is_still_the_default() => UiSession.Run(() =>
    {
        Assert.True(new MainWindowViewModel().IsDesktop);
    });
}
