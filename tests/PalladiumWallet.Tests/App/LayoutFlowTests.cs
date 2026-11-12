using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.ViewModels;

namespace PalladiumWallet.Tests.App;

/// <summary>Layout and flow: step counter, inline messages, recipient hint, review split, pinned footer, resizable window.</summary>
[Collection("AppPaths")]
public sealed class LayoutFlowTests : VmTestBase
{
    [Fact]
    public Task The_step_counter_follows_each_creation_flow() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        Assert.Equal("", vm.WizardStepText);                       // the start screen has no counter
        Assert.False(vm.HasWizardStepText);

        vm.WizardStartNewCommand.Execute(null);
        Assert.Equal("Step 1 of 5", vm.WizardStepText);            // seed, confirm, passphrase, script type, password
        vm.WizardNextFromShowSeedCommand.Execute(null);
        Assert.Equal("Step 2 of 5", vm.WizardStepText);

        vm.WizardBackCommand.Execute(null); vm.WizardBackCommand.Execute(null);
        vm.WizardStartRestoreCommand.Execute(null);
        Assert.Equal("Step 1 of 4", vm.WizardStepText);            // words, passphrase, script type, password
        vm.MnemonicInput = Mnemonic;
        vm.WizardNextFromWordsCommand.Execute(null);
        vm.WizardNextFromPassphraseCommand.Execute(null);
        vm.WizardNextFromScriptTypeCommand.Execute(null);
        Assert.Equal("Step 4 of 4", vm.WizardStepText);

        vm.WizardBackCommand.Execute(null); vm.WizardBackCommand.Execute(null); vm.WizardBackCommand.Execute(null); vm.WizardBackCommand.Execute(null);
        vm.WizardStartImportAddressCommand.Execute(null);
        Assert.Equal("Step 1 of 2", vm.WizardStepText);

        Loc.SwitchTo("it");
        vm.SetLanguageCommand.Execute("it");
        Assert.Equal("Passo 1 di 2", vm.WizardStepText);           // re-rendered on a language change
        vm.SetLanguageCommand.Execute("en");
    });

    [Theory]
    [InlineData("", "", false)]
    [InlineData("not an address", "msg.address.invalid", true)]
    public Task The_recipient_hint_reports_what_the_box_holds(string text, string key, bool error) => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.SendTo = text;
        Assert.Equal(key.Length == 0 ? "" : Loc.Tr(key), vm.RecipientHint);
        Assert.Equal(error, vm.IsRecipientHintError);
    });

    [Fact]
    public Task A_valid_address_is_named_by_its_kind() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        var account = PalladiumWallet.Core.Crypto.HdAccount.FromMnemonic(ParseMnemonic(), null,
            PalladiumWallet.Core.Chain.ScriptKind.NativeSegwit, PalladiumWallet.Core.Chain.ChainProfiles.Mainnet);
        vm.SendTo = "  " + account.GetReceiveAddress(0) + " ";
        Assert.Equal($"{Loc.Tr("send.to.ok")} · P2WPKH", vm.RecipientHint);
        Assert.False(vm.IsRecipientHintError);
    });

    [Fact]
    public Task The_review_is_split_into_a_headline_and_the_rest() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.SendPreview = "0.1 PLM → plm1qabc\nChange: 0.9 PLM\ntxid abcd · fee 0.00001 PLM\n";
        Assert.Equal("0.1 PLM → plm1qabc", vm.SendPreviewHeadline);
        Assert.Equal("Change: 0.9 PLM\ntxid abcd · fee 0.00001 PLM", vm.SendPreviewDetail);

        vm.SendPreview = "Insufficient funds.";                       // a one-line message is all headline
        Assert.Equal("Insufficient funds.", vm.SendPreviewHeadline);
        Assert.Equal("", vm.SendPreviewDetail);

        vm.SendPreview = "";
        Assert.Equal("", vm.SendPreviewHeadline);
    });

    [Fact]
    public Task A_rejected_contact_is_explained_next_to_the_form_until_the_person_edits_it() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "contact-msg");
        vm.NewContactName = "bob";
        vm.NewContactAddress = "definitely not an address";
        vm.AddContactCommand.Execute(null);
        Assert.Equal(Loc.Tr("msg.contact.address.invalid"), vm.ContactFormMessage);
        Assert.Empty(vm.Contacts);

        vm.NewContactAddress = "typing again";                         // an edit withdraws the message
        Assert.Equal("", vm.ContactFormMessage);
    });

    [Fact]
    public void The_new_labels_exist_in_every_language()
    {
        foreach (var lang in new[] { "it", "en", "es", "fr", "pt", "de", "zh" })
        {
            Loc.SwitchTo(lang);
            foreach (var key in new[] { "wiz.step", "wiz.password.label", "wiz.password.confirm.label", "send.to.ok" })
            {
                Assert.False(string.IsNullOrWhiteSpace(Loc.Tr(key)));
                Assert.NotEqual(key, Loc.Tr(key));
            }
            Assert.Contains("{0}", Loc.Tr("wiz.step"));
            Assert.Contains("{1}", Loc.Tr("wiz.step"));
        }
        Loc.SwitchTo("en");
    }

    [Fact]
    public Task The_main_window_can_be_resized() => UiSession.Run(() =>
    {
        var window = new PalladiumWallet.App.Views.MainWindow();
        Assert.True(window.CanResize);
        Assert.True(window.MinWidth < 1020 && window.MinHeight < 720);
        Assert.True(double.IsPositiveInfinity(window.MaxWidth) && double.IsPositiveInfinity(window.MaxHeight));
    });

    [Fact]
    public Task The_send_buttons_stay_in_view_at_the_smallest_window() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "footer");
        vm.SelectedTabIndex = 1;
        vm.SendPreview = "0.1 PLM → plm1qabc\nChange: 0.9 PLM\n";
        var view = new PalladiumWallet.App.Views.MainView { DataContext = vm };
        var window = new Window { Width = 760, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var key in new[] { "send.prepare", "send.confirm", "psbt.open.btn" })
        {
            var button = view.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, Loc.Tr(key)));
            var bottom = button.TranslatePoint(new Avalonia.Point(0, button.Bounds.Height), view)!.Value.Y;
            Assert.True(bottom <= 600, $"{key} ends at {bottom}px in a 600px window");
        }
        window.Close();
    });

    [Fact]
    public Task The_multisig_step_keeps_back_and_next_in_view_at_the_smallest_window() => UiSession.Run(() =>
    {
        var vm = new MainWindowViewModel();
        vm.WizardStartMultisigCommand.Execute(null);
        var view = new PalladiumWallet.App.Views.MainView { DataContext = vm };
        var window = new Window { Width = 760, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var key in new[] { "wiz.back", "wiz.next" })
        {
            var button = view.GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && Equals(b.Content, Loc.Tr(key)));
            var bottom = button.TranslatePoint(new Point(0, button.Bounds.Height), view)!.Value.Y;
            Assert.True(bottom <= 600, $"{key} ends at {bottom}px in a 600px window");
        }
        window.Close();
    });

    [Fact]
    public Task The_receive_copy_button_stays_in_view_on_a_phone_even_with_a_two_line_status_bar() => UiSession.Run(() =>
    {
        MainWindowViewModel.MobileLayoutOverride = true;
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "receive-phone");
        vm.SelectedTabIndex = 2; // Receive
        vm.StatusMessage = "Synchronized: height 554783, 0 SPV-verified transactions. Real-time updates active."; // wraps at 400px
        var view = new PalladiumWallet.App.Views.MainView { DataContext = vm };
        var window = new Window { Width = 400, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var copy = view.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && b.Classes.Contains("accent") && b.Content is Panel);
        var bottom = copy.TranslatePoint(new Point(0, copy.Bounds.Height), view)!.Value.Y;
        var statusBar = view.GetVisualDescendants().OfType<Border>().Last(b => b.IsEffectivelyVisible && b.Bounds.Width >= 399 && b.Bounds.Height < 80);
        var statusTop = statusBar.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        Assert.True(bottom <= statusTop + 1, $"Copy ends at {bottom}px, the status bar starts at {statusTop}px");
        window.Close();
    });

    [Fact]
    public Task The_balance_shrinks_instead_of_being_clipped_on_a_phone() => UiSession.Run(() =>
    {
        MainWindowViewModel.MobileLayoutOverride = true;
        var vm = new MainWindowViewModel();
        RestoreFlow(vm, Mnemonic, null, "balance-phone");
        vm.BalanceText = "12,345,678.90123456 PLN";
        var view = new PalladiumWallet.App.Views.MainView { DataContext = vm };
        var window = new Window { Width = 360, Height = 740, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var amount = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == vm.BalanceText);
        // A TextBlock reports the width it was offered, not the width of its text, so the bounds cannot show clipping:
        // what keeps a long amount on screen is the Viewbox that scales it down (and never up) to the card.
        var viewbox = amount.GetVisualAncestors().OfType<Viewbox>().FirstOrDefault();
        Assert.NotNull(viewbox);
        Assert.Equal(Avalonia.Media.StretchDirection.DownOnly, viewbox!.StretchDirection);
        var right = viewbox.TranslatePoint(new Point(viewbox.Bounds.Width, 0), view)!.Value.X;
        Assert.True(right <= 360, $"the balance ends at {right}px in a 360px window");
        window.Close();
    });
}
