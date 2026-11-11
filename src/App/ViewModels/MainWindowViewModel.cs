using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.App.ViewModels;

/// <summary>Transaction history row for the view.</summary>
public sealed record HistoryRow(string Conferma, string Importo, string Txid, bool Verified = true,
    bool Unanchored = false);

/// <summary>
/// Address view row: public data and derivation path only. The private key is never held here;
/// it is derived on demand when the user reveals it (see <c>RevealedPrivKey</c>).
/// </summary>
public sealed record AddressRow(
    string Tipo, int Indice, string Indirizzo, string Saldo, string NumTx,
    bool IsChange = false, string PubKey = "", string DerivPath = "", bool HasPrivKey = false);

/// <summary>Address data passed to the detail overlay: derivation coordinates, never the key.</summary>
public sealed record AddressInfo(
    Loc Loc,
    string Address, string DerivPath, string PubKey, bool IsChange, int Index, bool HasPrivKey);

/// <summary>Address book contact: name + blockchain address.</summary>
public sealed record ContactEntry(string Name, string Address);

/// <summary>Wallet list entry: display name + full file path.</summary>
public sealed record WalletFileEntry(string Name, string Path);

/// <summary>
/// Single application ViewModel (wizard + dashboard). Split into partial files
/// by area: Wizard, Settings, Sync, Send, Contacts, Receive.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    // ---- wallet session state ----
    private WalletDocument? _doc;
    private IWalletAccount? _account;
    private string? _walletPath;
    private string? _password;
    private WalletLock? _walletLock;

    // ---- network ----
    private ElectrumClient? _client;
    private WalletSynchronizer? _synchronizer;
    private IReadOnlyDictionary<string, Transaction>? _lastTransactions;
    private bool _resyncRequested;

    // ---- send ----
    private BuiltTransaction? _pendingSend;

    // ---- transaction detail ----
    private CancellationTokenSource? _txDetailsCts;

    // ---- configuration and localisation ----
    private AppConfig _config = AppConfig.Load();
    private Loc _loc = Loc.Instance;
    public Loc Loc => _loc;

    // ---- wizard ----
    private string? _pendingOpenPath;

    // ---- keep-alive ----
    private bool _autoReconnect;
    private bool _syncFailed;
    private bool _resumeRecovering;
    private readonly DispatcherTimer _keepAliveTimer;

    // ---- server UI sync ----
    private bool _syncingServerFields;

    // ---- app properties ----

    public static string AppVersion =>
        typeof(MainWindowViewModel).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "";

    /// <summary>Marks a test network in the title so it is never mistaken for the real chain.</summary>
    public string WindowTitle => Net == NetKind.Mainnet
        ? $"Palladium Wallet {AppVersion}"
        : $"Palladium Wallet {AppVersion} [{Net.ToString().ToUpperInvariant()}]";

    /// <summary>True on desktop; false on Android/iOS — hides filesystem-dependent features.</summary>
    public bool IsDesktop => MobileLayoutOverride is { } mobile
        ? !mobile
        : !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>Test seam: renders the mobile layout (Android/iOS) on a desktop machine, so it can be checked headlessly.</summary>
    internal static bool? MobileLayoutOverride { get; set; }

    public bool IsMobile => !IsDesktop;

    // Tab bar sizing: compact on mobile so all 5 tabs fit in one row.
    public double TabIconSize  => IsMobile ? 20 : 24;
    public double TabFontSize  => IsMobile ? 10 : 13;
    public double TabSpacing   => IsMobile ? 4  : 4;

    public string UnitLabel => _config.Unit;
    public AppConfig CurrentConfig => _config;

    // ---- panel state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupVisible))]
    private bool isWalletOpen;

    public bool IsSetupVisible => !IsWalletOpen;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private int selectedTabIndex;

    [RelayCommand]
    private void SelectTab(string index) => SelectedTabIndex = int.Parse(index);

    // ---- dashboard collections ----

    public ObservableCollection<HistoryRow> History { get; } = [];
    public ObservableCollection<AddressRow> Addresses { get; } = [];

    // ---- helpers ----

    // Mainnet unless the desktop head was started with --net (StartupOptions); the test seam below may select another profile.
    private NetKind Net => ProfileOverride?.Kind ?? StartupOptions.Net;
    /// <summary>Test seam: lets tests drop the real bootstrap servers so nothing reaches the internet.</summary>
    internal static ChainProfile? ProfileOverride { get; set; }

    private ChainProfile Profile => ProfileOverride ?? ChainProfiles.For(Net);
    private ServerRegistry Registry => new(Profile, AppPaths.ServersPath(Net));

    private string Fmt(long sats, bool withLabel = true) =>
        CoinAmount.FormatIn(sats, _config.Unit, withLabel);

    private string KeyWif(bool isChange, int index)
    {
        if (_account is null or { IsWatchOnly: true }) return "";
        try
        {
            return _account.GetPrivateKey(isChange, index)
                ?.GetWif(PalladiumNetworks.For(Net)).ToString() ?? "";
        }
        catch { return ""; }
    }

    // ---- constructor ----

    public MainWindowViewModel()
    {
        _loc = Loc.SwitchTo(_config.Language);
        ApplyTheme();
        if (!AppPaths.IsDataLocationConfigured())
            SetupStep = StepDataLocation;
        else
            RefreshSetupState();
        _keepAliveTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(20) };
        _keepAliveTimer.Tick += async (_, _) => await KeepAliveTickAsync();
        _keepAliveTimer.Start();
        _ = CheckForUpdatesAsync();
    }

    private bool _keepAliveRunning;

    private async System.Threading.Tasks.Task KeepAliveTickAsync()
    {
        // Re-entrancy guard: without it, a ping stuck on a half-open socket (see
        // below) would let every subsequent 20s tick pile up another concurrent
        // ping/reconnect attempt on top of it.
        if (IsSyncing || _keepAliveRunning)
            return;
        _keepAliveRunning = true;
        try
        {
            if (_client is { IsConnected: true } client)
            {
                // If the wallet is open and the last sync failed, retry automatically.
                if (_syncFailed && _account is not null)
                {
                    await ConnectAndSync();
                    return;
                }
                try
                {
                    // A "half-open" TCP connection (remote end gone with no FIN/RST
                    // ever delivered — the common outcome of Android Doze/mobile-radio
                    // suspend killing the route silently) never fails the write, so
                    // PingAsync would otherwise await a response that never arrives.
                    // Bound it explicitly instead of relying on it to throw.
                    using var timeoutCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(8));
                    await client.PingAsync(timeoutCts.Token);
                }
                catch
                {
                    // TcpClient.Connected only reflects the last known socket state, so a
                    // connection killed silently while the app was suspended still reports
                    // IsConnected == true. A failed/timed-out ping is the only reliable
                    // signal here: tear the dead client down so the next tick reconnects
                    // instead of retrying forever on a dead socket.
                    await DisconnectAsync();
                    if (_autoReconnect)
                    {
                        SetConn(ConnState.Reconnecting);
                        await ConnectAndSync();
                    }
                }
            }
            else if (_autoReconnect)
            {
                SetConn(ConnState.Reconnecting);
                await ConnectAndSync();
            }
        }
        finally
        {
            _keepAliveRunning = false;
        }
    }

    /// <summary>Forces an immediate connection health check, bypassing the 20s timer.
    /// Called when the app resumes from background/lock screen, since the socket may
    /// have died silently while suspended and the UI would otherwise show a stale
    /// "connected" state until the next scheduled tick (or forever, if it never fires
    /// during Doze).</summary>
    public async System.Threading.Tasks.Task CheckConnectionOnResumeAsync()
    {
        if (IsSyncing)
        {
            // A sync in progress when the phone locked may be stuck awaiting a
            // response on a socket that died silently while suspended (same
            // half-open-TCP scenario as the keep-alive ping, but sync requests have
            // no timeout of their own). Cancel it and tear the client down instead
            // of leaving it hung forever. _resumeRecovering tells ConnectAndSync's
            // error handling that this interruption is self-inflicted and expected,
            // so it shows "reconnecting" and restarts immediately instead of
            // flashing a scary error message and waiting for the next keep-alive tick.
            _resumeRecovering = true;
            SetConn(ConnState.Reconnecting);
            _syncCts.Cancel();
            // Deliberately not DisconnectAsync() here: it also nulls _synchronizer,
            // which still holds whatever this sync already downloaded/verified (e.g.
            // thousands of Merkle proofs on a large wallet). Nulling it before the
            // cancelled ConnectAndSync unwinds would discard that progress, forcing a
            // full restart instead of a resume. Only tear down the dead socket here;
            // ConnectAndSync's own cancellation handling persists the partial cache
            // (PersistPartialTxCache) before it recreates the synchronizer — same
            // sequencing already used for the "server changed mid-sync" case.
            if (_client is { } deadClient)
            {
                _client = null;
                try { await deadClient.DisposeAsync(); } catch { }
            }
            return;
        }
        await KeepAliveTickAsync();
    }

    // ---- wallet lifecycle ----

    [RelayCommand]
    private void CloseWallet()
    {
        _walletLock?.Dispose();
        _walletLock = null;
        _ = _client?.DisposeAsync().AsTask();
        DisposeWitnesses();
        _client = null;
        _synchronizer = null;
        _autoReconnect = false;
        _resyncRequested = false;
        _syncFailed = false;
        _doc = null;
        _account = null;
        _lastTransactions = null;
        _pendingSend = null;
        HasPendingSend = false;
        PendingPsbtBase64 = "";
        OnPropertyChanged(nameof(IsWatchOnlyAccount));
        History.Clear();
        RefreshCoins(null);
        Contacts.Clear();
        SelectedContactInList = null;
        SendToContact = null;
        SelectedAddressRow = null;
        AddressInfo = null; // also drops a revealed private key
        IsWalletOpen = false;
        IsConnected = false;
        SetConn(ConnState.None);
        RefreshSetupState();
    }
}
