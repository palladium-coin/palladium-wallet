using PalladiumWallet.App;
using PalladiumWallet.App.ViewModels;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Tests.App;

/// <summary><c>--net</c> of the desktop head: mainnet by default, a test network only when asked for by name (ADR 0064).</summary>
public sealed class StartupOptionsTests : VmTestBase
{
    [Fact]
    public void No_argument_means_mainnet()
    {
        Assert.True(StartupOptions.TryParse([], out var error));
        Assert.Null(error);
        Assert.Equal(NetKind.Mainnet, StartupOptions.Net);
    }

    [Theory]
    [InlineData("mainnet", NetKind.Mainnet)]
    [InlineData("testnet", NetKind.Testnet)]
    [InlineData("regtest", NetKind.Regtest)]
    public void Each_network_name_is_accepted_in_both_spellings(string name, NetKind expected)
    {
        Assert.True(StartupOptions.TryParse(["--net", name], out _));
        Assert.Equal(expected, StartupOptions.Net);
        Assert.True(StartupOptions.TryParse([$"--net={name}"], out _));
        Assert.Equal(expected, StartupOptions.Net);
    }

    [Theory]
    [InlineData("--net")]              // name missing
    [InlineData("--net=")]             // name empty
    [InlineData("--net=Regtest")]      // case matters: no silent normalisation
    [InlineData("--net=signet")]
    public void A_missing_or_unknown_name_is_refused_and_leaves_mainnet(string arg)
    {
        Assert.True(StartupOptions.TryParse(["--net", "regtest"], out _)); // start from a test network
        Assert.False(StartupOptions.TryParse([arg], out var error));
        Assert.Contains("--net", error);
        Assert.Equal(NetKind.Mainnet, StartupOptions.Net);
    }

    [Fact]
    public void Other_arguments_are_ignored_and_parsing_restarts_from_mainnet()
    {
        Assert.True(StartupOptions.TryParse(["--foo", "--net", "regtest", "bar"], out _));
        Assert.Equal(NetKind.Regtest, StartupOptions.Net);
        Assert.True(StartupOptions.TryParse(["--foo"], out _));
        Assert.Equal(NetKind.Mainnet, StartupOptions.Net);
    }

    [Fact]
    public Task The_window_title_marks_a_test_network_and_not_mainnet() => UiSession.Run(() =>
    {
        Assert.DoesNotContain("[", new MainWindowViewModel().WindowTitle); // VmTestBase runs on mainnet
        MainWindowViewModel.ProfileOverride = ChainProfiles.Regtest with { BootstrapServers = [], Checkpoints = [] };
        Assert.EndsWith("[REGTEST]", new MainWindowViewModel().WindowTitle);
        MainWindowViewModel.ProfileOverride = ChainProfiles.Testnet with { BootstrapServers = [], Checkpoints = [] };
        Assert.EndsWith("[TESTNET]", new MainWindowViewModel().WindowTitle);
    });
}
