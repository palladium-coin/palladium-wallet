using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalladiumWallet.App.Localization;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.App.ViewModels;

public partial class MainWindowViewModel
{
    // ---- server and connection ----

    [ObservableProperty]
    private string serverHost = "";

    [ObservableProperty]
    private string serverPort = "";

    [ObservableProperty]
    private bool useSsl = true;

    [ObservableProperty]
    private bool isServerSettingsOpen;

    [RelayCommand]
    private async Task OpenServerSettings()
    {
        IsSettingsOpen = false;
        RefreshServers();
        IsServerSettingsOpen = true;
        if (_client is { IsConnected: true })
            await DiscoverServers();
    }

    [RelayCommand]
    private void CloseServerSettings() => IsServerSettingsOpen = false;

    [ObservableProperty]
    private string connectionStatus = Loc.Tr("conn.none");

    [ObservableProperty]
    private string connectionStatusShort = Loc.Tr("conn.none");

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isSyncing;

    private CancellationTokenSource _syncCts = new();

    public ObservableCollection<KnownServer> KnownServers { get; } = [];

    [ObservableProperty]
    private KnownServer? selectedKnownServer;

    partial void OnSelectedKnownServerChanged(KnownServer? value)
    {
        if (value is null)
            return;
        _syncingServerFields = true;
        ServerHost = value.Host;
        ServerPort = value.PortFor(UseSsl).ToString();
        _syncingServerFields = false;
    }

    partial void OnUseSslChanged(bool value)
    {
        if (_syncingServerFields)
            return;
        _syncingServerFields = true;
        ServerPort = SelectedKnownServer is { } server
            ? server.PortFor(value).ToString()
            : (value ? Profile.DefaultSslPort : Profile.DefaultTcpPort).ToString();
        _syncingServerFields = false;
    }

    partial void OnServerPortChanged(string value)
    {
        if (_syncingServerFields)
            return;
        if (!int.TryParse(value.Trim(), out var port))
            return;
        bool? wantSsl =
            SelectedKnownServer is { } s && port == s.SslPort ? true :
            SelectedKnownServer is { } t && port == t.TcpPort ? false :
            port == Profile.DefaultSslPort ? true :
            port == Profile.DefaultTcpPort ? false :
            null;
        if (wantSsl is bool b && b != UseSsl)
        {
            _syncingServerFields = true;
            UseSsl = b;
            _syncingServerFields = false;
        }
    }

    private void RefreshServers()
    {
        KnownServers.Clear();
        foreach (var server in Registry.All)
            KnownServers.Add(server);

        if (_config.LastServerHost is { Length: > 0 } savedHost && _config.LastServerPort is { } savedPort)
        {
            _syncingServerFields = true;
            UseSsl = _config.LastServerUseSsl;
            ServerHost = savedHost;
            ServerPort = savedPort.ToString();
            SelectedKnownServer = KnownServers.FirstOrDefault(s => s.Host == savedHost);
            _syncingServerFields = false;
        }
        else
        {
            SelectedKnownServer = KnownServers.FirstOrDefault();
            if (SelectedKnownServer is null)
            {
                ServerHost = "127.0.0.1";
                ServerPort = (UseSsl ? Profile.DefaultSslPort : Profile.DefaultTcpPort).ToString();
            }
        }
    }

    private (string Host, int Port) ParseServer()
    {
        var host = ServerHost.Trim();
        var port = int.TryParse(ServerPort.Trim(), out var p)
            ? p
            : UseSsl ? Profile.DefaultSslPort : Profile.DefaultTcpPort;
        return (host, port);
    }

    /// <summary>
    /// Returns servers to try in order: first the one selected in the UI
    /// (or manually typed), then the remaining KnownServers, with duplicates skipped.
    /// </summary>
    private IEnumerable<(string Host, int Port)> BuildServerCandidates(string selectedHost, int selectedPort)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1. Current server (selected or manually typed).
        if (!string.IsNullOrWhiteSpace(selectedHost))
        {
            var key = $"{selectedHost}:{selectedPort}";
            if (seen.Add(key))
                yield return (selectedHost, selectedPort);
        }
        // 2. The shipped (bootstrap) servers, in list order. Servers discovered from peers are
        // never tried automatically: someone else named them (see ServerRegistry.Trusted).
        foreach (var s in Registry.Trusted)
        {
            var p = s.PortFor(UseSsl);
            var key = $"{s.Host}:{p}";
            if (seen.Add(key))
                yield return (s.Host, p);
        }
    }

    [RelayCommand]
    private async Task ConnectAndSync()
    {
        if (IsSyncing)
        {
            var (newHost, newPort) = ParseServer();
            bool serverChanged = _client is null
                || _client.Host != newHost
                || _client.Port != newPort
                || _client.UseSsl != UseSsl;
            if (serverChanged)
                _syncCts.Cancel();
            else
                _resyncRequested = true;
            return;
        }

        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;
        IsSyncing = true;
        StatusMessage = "";
        bool cancelled = false;
        try
        {
            var (host, port) = ParseServer();

            if (_client is { } current &&
                (current.Host != host || current.Port != port || current.UseSsl != UseSsl))
            {
                await DisconnectAsync();
            }

            if (_client is null || !_client.IsConnected)
            {
                // Persist caches before destroying the synchronizer: the new
                // synchronizer will use a different client and must be recreated,
                // but already-downloaded data is preserved via _doc.Cache.
                PersistPartialTxCache();
                // Release the dead connection's resources first: its witness connections and the
                // dead client itself (DisconnectAsync also clears the synchronizer). Without this the
                // witnesses opened for the previous primary stay open until the next explicit disconnect.
                await DisconnectAsync();

                // Try all known servers in order; starts with the selected one
                // and walks the list until the first that responds.
                var candidates = BuildServerCandidates(host, port);
                Exception? lastError = null;
                foreach (var (h, p) in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    ConnectionStatus = $"{Loc.Tr("conn.connectingto")} {h}:{p}…";
                    ConnectionStatusShort = Loc.Tr("conn.connectingto") + "…";
                    try
                    {
                        var pins = new CertificatePinStore(AppPaths.CertificatePinsPath(Net));
                        var connected = await ElectrumClient.ConnectAsync(h, p, UseSsl, pins, ct);
                        try { await connected.VerifyChainAsync(Profile, ct); }
                        catch
                        {
                            await connected.DisposeAsync();
                            throw;
                        }
                        _client = connected;
                        _client.NotificationReceived += OnServerNotification;
                        _client.Disconnected += _ => Dispatcher.UIThread.Post(() =>
                        {
                            IsConnected = false;
                            ConnectionStatus = Loc.Tr("conn.none");
                            ConnectionStatusShort = Loc.Tr("conn.none");
                        });
                        IsConnected = true;
                        _autoReconnect = true;
                        ConnectionStatus = $"{Loc.Tr("conn.connectedto")} {h}:{p}";
                        ConnectionStatusShort = Loc.Tr("conn.connectedto");
                        // Update the UI to reflect the server actually connected.
                        _syncingServerFields = true;
                        ServerHost = h;
                        ServerPort = p.ToString();
                        SelectedKnownServer = KnownServers.FirstOrDefault(s => s.Host == h)
                            ?? SelectedKnownServer;
                        _syncingServerFields = false;
                        // Persist the last-used server so it is restored on next launch.
                        _config.LastServerHost = h;
                        _config.LastServerPort = p;
                        _config.LastServerUseSsl = UseSsl;
                        _config.Save();
                        lastError = null;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        _client = null;
                    }
                }
                if (lastError is not null)
                    throw lastError;
                if (_client is { } fresh)
                    _ = RefreshFeeEstimatesAsync(fresh);
            }

            if (_account is null || _doc is null)
                return;

            if (_synchronizer is null)
            {
                _synchronizer = new WalletSynchronizer(_account, _client!, _doc.GapLimit);
                var primary = _client!;
                _synchronizer.WitnessProvider = _ => ConnectWitnessesAsync(primary);
                // Reload from disk cache: avoids re-downloading already known transactions
                // (essential for wallets with thousands of historical txs — prevents -101).
                var net = PalladiumNetworks.For(_account.Profile.Kind);
                _synchronizer.PreloadCaches(
                    _doc.Cache?.RawTxHex ?? [],
                    _doc.Cache?.VerifiedAt ?? [],
                    _doc.Cache?.BlockHeaders,
                    _doc.Cache?.NextReceiveIndex ?? 0,
                    _doc.Cache?.NextChangeIndex ?? 0,
                    net,
                    _doc.Cache?.AnchoredDownTo);
                _synchronizer.Progress += msg => Dispatcher.UIThread.Post(() => StatusMessage = msg);
                // Progressive verification (§7.4): the wallet becomes usable as soon as
                // transaction downloads finish, without waiting for every historical Merkle
                // proof — critical on mobile, where verifying thousands of proofs can take far
                // longer than the download itself. Never persisted to disk on its own (only the
                // final, fully-verified snapshot below is saved); coin selection still refuses
                // any UTXO whose proof isn't checked yet (CachedUtxo.Verified), so showing this
                // early can't be exploited to spend a server-fabricated balance.
                // A partial snapshot posted to the UI thread can run after the sync's final snapshot was
                // applied (UI thread busy, or a test that pumps jobs late): it would then overwrite the
                // verified rows and cache with the older unverified ones. Tag it with the run it
                // belongs to and drop it once that run has been finalised.
                _synchronizer.PartialResult += r =>
                {
                    var run = Volatile.Read(ref _syncRun);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (run == Volatile.Read(ref _syncRun))
                            ApplyPartialResult(r);
                    });
                };
            }

            do
            {
                _resyncRequested = false;
                // Off the UI thread: sync does heavy CPU work (LINQ over thousands of
                // cached txs/UTXOs) and blocking JSON/disk I/O between awaits, negligible
                // on desktop but enough to freeze the UI (ANR) on slower mobile hardware.
                _synchronizer.FrozenOutpoints = _doc.Cache?.FrozenOutpoints() ?? [];
                var (result, rawHex, verifiedAt, blockHeaders, anchoredDownTo) = await Task.Run(async () =>
                {
                    var r = await _synchronizer.SyncOnceAsync(ct);
                    var caches = _synchronizer.ExportCaches(PalladiumNetworks.For(_account.Profile.Kind));
                    return (r, caches.RawTxHex, caches.VerifiedAt, caches.BlockHeaders, caches.AnchoredDownTo);
                }, ct);
                _lastTransactions = result.Transactions;

                var cache = new SyncCache
                {
                    RawTxHex = rawHex, VerifiedAt = verifiedAt, BlockHeaders = blockHeaders,
                    AnchoredDownTo = anchoredDownTo,
                };
                FillDisplayFields(cache, result);
                // The freezes as they are now: the user may have changed one while the sync ran.
                CoinControl.ApplyFrozen(cache, Profile, _doc.Cache?.FrozenOutpoints() ?? new HashSet<(string Txid, int Vout)>());
                // Finalise this run BEFORE the save's await: a stale partial snapshot still queued on the
                // UI thread would otherwise run during the save and replace _doc.Cache with unverified rows.
                Interlocked.Increment(ref _syncRun);
                _doc.Cache = cache;
                ApplyCache(cache);
                DiscardStalePending(cache); // final state only: a partial snapshot may still be unverified
                await WalletStore.SaveAsync(_doc, _walletPath!, _password);
                _syncFailed = false;
                StatusMessage = $"{Loc.Tr("msg.synced")}: {Loc.Tr("msg.height")} {result.TipHeight}, " +
                    $"{result.History.Count} {Loc.Tr("msg.synced.detail")}";
            } while (_resyncRequested && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // Intentional cancellation: a server change request, or CheckConnectionOnResumeAsync
            // recovering a sync stuck on a socket that died while the app was suspended
            // (_resumeRecovering) — neither is an error the user needs to see as one.
            cancelled = true;
            IsConnected = false;
            ConnectionStatus = _resumeRecovering ? Loc.Tr("conn.reconnecting") : Loc.Tr("conn.none");
            ConnectionStatusShort = _resumeRecovering ? Loc.Tr("conn.reconnecting") : Loc.Tr("conn.none");
            StatusMessage = "";
        }
        catch (CertificatePinMismatchException ex)
        {
            IsConnected = false;
            ConnectionStatus = Loc.Tr("conn.none");
            ConnectionStatusShort = Loc.Tr("conn.none");
            StatusMessage = DescribeError(ex);
        }
        catch (Exception ex)
        {
            IsConnected = _client?.IsConnected == true;
            if (_resumeRecovering && !IsConnected)
            {
                // We tore the connection down ourselves (see CheckConnectionOnResumeAsync);
                // whether that surfaces here as a cancellation or as a transport exception
                // is a race, not a real failure — show "reconnecting" and restart right away
                // instead of a scary error message and a wait for the next keep-alive tick.
                cancelled = true;
                ConnectionStatus = Loc.Tr("conn.reconnecting");
                ConnectionStatusShort = Loc.Tr("conn.reconnecting");
            }
            else
            {
                ConnectionStatus = IsConnected ? ConnectionStatus : Loc.Tr("conn.none");
                ConnectionStatusShort = IsConnected ? Loc.Tr("conn.connectedto") : Loc.Tr("conn.none");
                StatusMessage = $"{Loc.Tr("msg.error")}: {DescribeError(ex)}";
            }
            if (_account is not null)
            {
                _syncFailed = true;
                // Persist already-downloaded transactions: on retry the synchronizer
                // resumes from here instead of starting from scratch (e.g. after -101).
                PersistPartialTxCache();
            }
        }
        finally
        {
            IsSyncing = false;
            _resumeRecovering = false;
        }

        // If cancelled due to a server change, restart immediately with the new server.
        if (cancelled)
            _ = ConnectAndSync();
    }

    /// <summary>
    /// Applies a provisional (not-yet-fully-verified) snapshot fired mid-sync: updates the
    /// in-memory display cache only — never persisted to disk on its own, so an interrupted
    /// sync can't leave behind a cache file whose VerifiedAt/RawTxHex/BlockHeaders (needed to
    /// resume without re-downloading) were never written.
    /// </summary>
    private void ApplyPartialResult(SyncResult result)
    {
        if (_doc is null)
            return;
        var previous = _doc.Cache;
        var cache = new SyncCache
        {
            RawTxHex = previous?.RawTxHex,
            VerifiedAt = previous?.VerifiedAt,
            BlockHeaders = previous?.BlockHeaders,
        };
        FillDisplayFields(cache, result);
        _doc.Cache = cache;
        _lastTransactions = result.Transactions;
        ApplyCache(cache);
    }

    private static void FillDisplayFields(SyncCache cache, SyncResult result)
    {
        cache.TipHeight = result.TipHeight;
        cache.ConfirmedSats = result.ConfirmedSats;
        cache.UnconfirmedSats = result.UnconfirmedSats;
        cache.ImmatureSats = result.ImmatureSats;
        cache.PendingVerificationSats = result.PendingVerificationSats;
        cache.SpendableSats = result.SpendableSats;
        cache.NextReceiveIndex = result.NextReceiveIndex;
        cache.NextChangeIndex = result.NextChangeIndex;
        cache.History = [.. result.History];
        cache.Utxos = [.. result.Utxos];
        cache.Addresses = [.. result.AddressRows];
    }

    /// <summary>
    /// Peer discovery. Always clickable: if the wallet is already connected, it reuses
    /// that connection; otherwise it opens a short-lived connection to a candidate server
    /// just to query its peer list, without touching the wallet's connection state.
    /// </summary>
    [RelayCommand]
    private async Task DiscoverServers()
    {
        if (_client is { IsConnected: true } connected)
        {
            await DiscoverServersUsing(connected);
            return;
        }

        var (host, port) = ParseServer();
        ElectrumClient? temp = null;
        Exception? lastError = null;
        foreach (var (h, p) in BuildServerCandidates(host, port))
        {
            try
            {
                var pins = new CertificatePinStore(AppPaths.CertificatePinsPath(Net));
                var fresh = await ElectrumClient.ConnectAsync(h, p, UseSsl, pins);
                try { await fresh.VerifyChainAsync(Profile); }
                catch
                {
                    await fresh.DisposeAsync();
                    throw;
                }
                temp = fresh;
                lastError = null;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        if (temp is null)
        {
            StatusMessage = $"{Loc.Tr("msg.peer.discovery.error")}: " +
                (lastError is null ? Loc.Tr("conn.none") : DescribeError(lastError));
            return;
        }
        try
        {
            await DiscoverServersUsing(temp);
        }
        finally
        {
            await temp.DisposeAsync();
        }
    }

    private async Task DiscoverServersUsing(ElectrumClient client)
    {
        try
        {
            var added = await Registry.DiscoverAsync(client);
            var selected = SelectedKnownServer;
            RefreshServers();
            SelectedKnownServer = KnownServers.FirstOrDefault(s => s.Host == selected?.Host)
                ?? KnownServers.FirstOrDefault();
            StatusMessage = added > 0
                ? string.Format(Loc.Tr("msg.peer.discovery.found"), added, KnownServers.Count)
                : string.Format(Loc.Tr("msg.peer.discovery.none"), KnownServers.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{Loc.Tr("msg.peer.discovery.error")}: {DescribeError(ex)}";
        }
    }

    /// <summary>
    /// Translates known Core exception types to the current UI language; falls back to the
    /// exception's own message (Core has no localization, so it is English by default).
    /// </summary>
    private static string DescribeError(Exception ex) => ex switch
    {
        CertificatePinMismatchException cert =>
            string.Format(Loc.Tr("msg.cert.mismatch"), $"{cert.Host}:{cert.Port}"),
        WrongPasswordException => Loc.Tr("msg.wrongpassword"),
        BroadcastTxidMismatchException => Loc.Tr("msg.broadcast.txid.mismatch"),
        WrongChainException chain =>
            string.Format(Loc.Tr("msg.chain.wrong"), $"{chain.Host}:{chain.Port}"),
        _ => ex.Message,
    };

    private void OnServerNotification(string method, System.Text.Json.JsonElement payload)
    {
        if (method is "blockchain.scripthash.subscribe" or "blockchain.headers.subscribe")
            Dispatcher.UIThread.Post(() =>
            {
                if (IsSyncing)
                    _resyncRequested = true;
                else
                    _ = ConnectAndSync();
            });
    }

    [RelayCommand]
    private void ResetCertificates()
    {
        new CertificatePinStore(AppPaths.CertificatePinsPath(Net)).ResetAll();
        StatusMessage = Loc.Tr("msg.certreset");
    }

    /// <summary>
    /// Persists the transactions and Merkle proofs already accumulated by the synchronizer
    /// into SyncCache, even if sync is not yet complete. Allows the next retry
    /// (or app restart) to resume without re-downloading from scratch.
    /// </summary>
    private void PersistPartialTxCache()
    {
        if (_synchronizer is null || _doc is null || _walletPath is null || _account is null)
            return;
        try
        {
            var net = PalladiumNetworks.For(_account.Profile.Kind);
            var (rawHex, verifiedAt, blockHeaders, anchoredDownTo) = _synchronizer.ExportCaches(net);
            if (rawHex.Count == 0 && verifiedAt.Count == 0)
                return;
            (_doc.Cache ??= new SyncCache()).RawTxHex = rawHex;
            _doc.Cache.VerifiedAt = verifiedAt;
            _doc.Cache.BlockHeaders = blockHeaders;
            _doc.Cache.AnchoredDownTo = anchoredDownTo;
            WalletStore.Save(_doc, _walletPath, _password);
        }
        catch { /* non-fatal: the next full save will recover */ }
    }

    // Witness servers for cross-checking recent headers (ADR 0042): opened lazily by the
    // synchronizer, only from the hardcoded bootstrap list, and closed with the primary connection.
    private readonly List<ElectrumClient> _witnesses = [];
    private int _syncRun; // bumped each time a sync's final snapshot is applied (see PartialResult)

    private async Task<IReadOnlyList<ElectrumClient>> ConnectWitnessesAsync(ElectrumClient primary)
    {
        if (Profile.BootstrapServers.Count == 0)
            return [];
        var pins = new CertificatePinStore(AppPaths.CertificatePinsPath(Net));
        var list = await WitnessConnector.ConnectAsync(Profile, Profile.BootstrapServers,
            primary.Host, primary.Port, UseSsl, pins);
        lock (_witnesses)
            _witnesses.AddRange(list);
        return list;
    }

    private void DisposeWitnesses()
    {
        ElectrumClient[] toClose;
        lock (_witnesses)
        {
            toClose = [.. _witnesses];
            _witnesses.Clear();
        }
        foreach (var w in toClose)
            _ = w.DisposeAsync().AsTask();
    }

    private async Task DisconnectAsync()
    {
        DisposeWitnesses();
        if (_client is { } client)
        {
            _client = null;
            _synchronizer = null;
            try { await client.DisposeAsync(); } catch { }
        }
        IsConnected = false;
    }
}
