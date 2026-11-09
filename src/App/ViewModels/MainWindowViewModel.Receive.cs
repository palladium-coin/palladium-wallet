using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalladiumWallet.App.Localization;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;
using QRCoder;

namespace PalladiumWallet.App.ViewModels;

public partial class MainWindowViewModel
{
    // ---- balance and wallet info ----

    [ObservableProperty]
    private string balanceText = "—";

    [ObservableProperty]
    private string unconfirmedText = "";

    [ObservableProperty]
    private string immatureText = "";

    [ObservableProperty]
    private string verifyingText = "";

    [ObservableProperty]
    private string networkInfo = "";

    // ---- receive address and QR ----

    [ObservableProperty]
    private string receiveAddress = "";

    [ObservableProperty]
    private Bitmap? receiveQr;

    partial void OnReceiveAddressChanged(string value)
    {
        var previous = ReceiveQr;
        ReceiveQr = string.IsNullOrEmpty(value) ? null : GenerateQr(value);
        previous?.Dispose();
    }

    public void NotifyAddressCopied() => StatusMessage = Loc.Tr("addr.copied");

    private static Bitmap? GenerateQr(string text)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            return new Bitmap(new MemoryStream(png));
        }
        catch
        {
            return null;
        }
    }

    // ---- addresses tab ----

    [ObservableProperty]
    private AddressRow? selectedAddressRow;

    [ObservableProperty]
    private AddressInfo? addressInfo;

    [ObservableProperty]
    private bool isAddressPrivKeyRevealed;

    // WIF of the address in the open sheet, derived only after the reveal gate and dropped as
    // soon as the key is hidden or the sheet closes. Never stored in rows or in AddressInfo.
    [ObservableProperty]
    private string revealedPrivKey = "";

    // ---- private key password prompt ----

    [ObservableProperty]
    private bool isPrivKeyPromptOpen;

    [ObservableProperty]
    private string privKeyPromptPassword = "";

    [ObservableProperty]
    private string privKeyPromptError = "";

    partial void OnAddressInfoChanged(AddressInfo? value)
    {
        HidePrivKeyInternal();
        ClosePrivKeyPromptInternal();
    }

    private void HidePrivKeyInternal()
    {
        IsAddressPrivKeyRevealed = false;
        RevealedPrivKey = "";
    }

    private void RevealPrivKeyInternal()
    {
        if (AddressInfo is not { HasPrivKey: true } info) return;
        RevealedPrivKey = KeyWif(info.IsChange, info.Index);
        IsAddressPrivKeyRevealed = RevealedPrivKey.Length > 0;
    }

    private void ClosePrivKeyPromptInternal()
    {
        IsPrivKeyPromptOpen = false;
        PrivKeyPromptPassword = "";
        PrivKeyPromptError = "";
    }

    public void ShowAddressInfo(AddressRow row) =>
        AddressInfo = new AddressInfo(Loc, row.Indirizzo, row.DerivPath, row.PubKey,
            row.IsChange, row.Indice, row.HasPrivKey);

    [RelayCommand]
    private void CloseAddressInfo()
    {
        ClosePrivKeyPromptInternal();
        AddressInfo = null;
    }

    [RelayCommand]
    private void RequestPrivKeyReveal()
    {
        if (AddressInfo is null || !AddressInfo.HasPrivKey) return;
        if (string.IsNullOrEmpty(_password))
        {
            // No encryption: reveal directly
            RevealPrivKeyInternal();
        }
        else
        {
            // Encrypted: open password prompt
            PrivKeyPromptPassword = "";
            PrivKeyPromptError = "";
            IsPrivKeyPromptOpen = true;
        }
    }

    [RelayCommand]
    private void ConfirmPrivKeyPassword()
    {
        if (PrivKeyPromptPassword != _password)
        {
            PrivKeyPromptError = Loc.Tr("msg.wrongpassword");
            return;
        }
        RevealPrivKeyInternal();
        ClosePrivKeyPromptInternal();
    }

    [RelayCommand]
    private void CancelPrivKeyPrompt() => ClosePrivKeyPromptInternal();

    [RelayCommand]
    private void HideAddressPrivKey() => HidePrivKeyInternal();

    // ---- transaction detail overlay ----

    [ObservableProperty]
    private bool isTxDetailsOpen;

    [ObservableProperty]
    private bool isTxDetailsLoading;

    [ObservableProperty]
    private TransactionDetailsViewModel? txDetails;

    public async Task ShowTransactionDetailsAsync(string txid)
    {
        if (_client is null || !_client.IsConnected)
        {
            StatusMessage = Loc.Tr("tx.needconnection");
            return;
        }

        _txDetailsCts?.Cancel();
        _txDetailsCts = new CancellationTokenSource();
        var ct = _txDetailsCts.Token;

        TxDetails = null;
        IsTxDetailsLoading = true;
        IsTxDetailsOpen = true;

        var details = await BuildTransactionDetailsAsync(txid, ct);

        if (ct.IsCancellationRequested || !IsTxDetailsOpen)
            return;

        if (details is null)
        {
            IsTxDetailsOpen = false;
            IsTxDetailsLoading = false;
            return;
        }

        TxDetails = details;
        IsTxDetailsLoading = false;
    }

    [RelayCommand]
    private void CloseTransactionDetails()
    {
        _txDetailsCts?.Cancel();
        IsTxDetailsOpen = false;
        IsTxDetailsLoading = false;
        TxDetails = null;
    }

    public async Task<TransactionDetailsViewModel?> BuildTransactionDetailsAsync(
        string txid, CancellationToken ct = default)
    {
        if (_client is null || !_client.IsConnected)
        {
            StatusMessage = Loc.Tr("tx.needconnection");
            return null;
        }
        if (_doc?.Cache is not { } cache)
            return null;

        var client = _client;
        var network = PalladiumNetworks.For(Net);
        var row = cache.History.FirstOrDefault(t => t.Txid == txid);
        var owned = cache.Addresses.Select(a => a.Address).ToHashSet();
        var tipHeight = cache.TipHeight;
        var height = row?.Height ?? 0;
        var delta = row?.DeltaSats ?? 0;
        var verified = row?.Verified ?? false;
        var transactions = _lastTransactions;
        var loc = _loc;
        var unit = _config.Unit;
        var unanchored = Profile.IsAboveNewestCheckpoint(height);

        try
        {
            return await Task.Run(async () =>
            {
                var details = await TransactionInspector.FetchAsync(
                    client, network, txid, tipHeight, height, owned, delta, verified, transactions, ct);
                return new TransactionDetailsViewModel(details, loc, unit, unanchored);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"{Loc.Tr("msg.error")}: {ex.Message}";
            return null;
        }
    }

    // ---- update display from the sync result ----

    private void ApplyCache(SyncCache? cache)
    {
        if (_account is null)
            return;
        if (cache is null)
        {
            BalanceText = $"0.00000000 {Profile.CoinUnit}";
            UnconfirmedText = "";
            ImmatureText = "";
            VerifyingText = "";
            ReceiveAddress = _account.GetReceiveAddress(0).ToString();
            History.Clear();
            RefreshCoins(null);
            Addresses.Clear();
            for (var i = 0; i < 10; i++)
                Addresses.Add(new AddressRow(_loc["addr.receive"], i,
                    _account.GetReceiveAddress(i).ToString(), "—", "—",
                    false,
                    _account.GetPublicKey(false, i)?.ToHex() ?? "",
                    $"m/{_doc!.AccountPath}/0/{i}",
                    !_account.IsWatchOnly));
            return;
        }
        // Not "Confirmed - Immature - PendingVerification": those two can overlap (an
        // immature coinbase can also be unverified), which double-subtracts and can go
        // negative. SpendableSats is computed directly from the same IsSpendable gate
        // coin selection uses, so it's always correct.
        BalanceText = Fmt(cache.SpendableSats);
        RefreshCoins(cache);
        var pending = cache.History.Where(t => t.Height <= 0).Sum(t => t.DeltaSats);
        UnconfirmedText = pending != 0
            ? $"{Loc.Tr("msg.pending")}: {(pending > 0 ? "+" : "")}{Fmt(pending)} — {Loc.Tr("msg.notspendable")}"
            : "";
        ImmatureText = cache.ImmatureSats != 0
            ? $"{Loc.Tr("msg.immature")}: {Fmt(cache.ImmatureSats)} — {Loc.Tr("msg.notspendable")}"
            : "";
        // Progressive verification (§7.4): funds confirmed by the server but whose Merkle
        // proof background verification hasn't reached yet — same "not spendable" treatment
        // as immature/pending, distinct wording so it doesn't read as a maturity/confirmation problem.
        VerifyingText = cache.PendingVerificationSats != 0
            ? $"{Loc.Tr("msg.verifying")}: {Fmt(cache.PendingVerificationSats)} — {Loc.Tr("msg.notspendable")}"
            : "";
        ReceiveAddress = _account.GetReceiveAddress(cache.NextReceiveIndex).ToString();
        History.Clear();
        foreach (var tx in cache.History)
            History.Add(new HistoryRow(
                tx.Height > 0 ? tx.Height.ToString() : "mempool",
                (tx.DeltaSats >= 0 ? "+" : "") + Fmt(tx.DeltaSats, withLabel: false),
                tx.Txid,
                tx.Verified,
                Profile.IsAboveNewestCheckpoint(tx.Height)));

        Addresses.Clear();
        foreach (var a in cache.Addresses)
            Addresses.Add(new AddressRow(
                a.IsChange ? _loc["addr.change"] : _loc["addr.receive"],
                a.Index,
                a.Address,
                a.BalanceSats > 0 ? Fmt(a.BalanceSats, withLabel: false) : (a.TxCount > 0 ? "0" : "—"),
                a.TxCount > 0 ? a.TxCount.ToString() : "—",
                a.IsChange,
                _account.GetPublicKey(a.IsChange, a.Index)?.ToHex() ?? "",
                $"m/{_doc!.AccountPath}/{(a.IsChange ? 1 : 0)}/{a.Index}",
                !_account.IsWatchOnly));
    }
}
