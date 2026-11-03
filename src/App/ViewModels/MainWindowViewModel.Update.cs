using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalladiumWallet.Core.Net;

namespace PalladiumWallet.App.ViewModels;

public partial class MainWindowViewModel
{
    // ---- update-available overlay ----

    [ObservableProperty]
    private bool isUpdateAvailableOpen;

    [ObservableProperty]
    private string updateAvailableTag = "";

    public string UpdateReleaseUrl { get; private set; } = "";

    [RelayCommand]
    private void CloseUpdateAvailable() => IsUpdateAvailableOpen = false;

    /// <summary>Test seam: replaces the real GitHub check so tests never touch the network.</summary>
    internal static System.Func<string, Task<LatestRelease?>> UpdateCheck { get; set; } =
        version => UpdateChecker.CheckAsync(version);

    /// <summary>
    /// Fire-and-forget check run once at startup. Best-effort: any failure or an
    /// up-to-date app silently does nothing (see <see cref="UpdateChecker"/>).
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        var latest = await UpdateCheck(AppVersion);
        if (latest is null) return;

        UpdateReleaseUrl = latest.HtmlUrl;
        UpdateAvailableTag = latest.Tag;
        IsUpdateAvailableOpen = true;
    }
}
