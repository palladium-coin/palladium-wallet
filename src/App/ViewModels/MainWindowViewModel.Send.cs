using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NBitcoin;
using Avalonia.Threading;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.Services;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private string sendTo = "";

    [ObservableProperty]
    private string sendAmount = "";

    [ObservableProperty]
    private string sendFeeRate = "1";

    [ObservableProperty]
    private bool sendAll;

    // ---- fee presets from the server (ADR 0039) ----

    private FeeEstimates? _feeEstimates;
    private bool _feeRateTouched;
    private bool _settingFeeRate;

    [ObservableProperty]
    private bool hasFeeEstimates;

    [ObservableProperty]
    private string feeEstimateHint = "";

    partial void OnSendFeeRateChanged(string value)
    {
        if (!_settingFeeRate)
            _feeRateTouched = true;
        InvalidatePending(); // the fee rate is shared by Send and Donate
    }

    // The prepared transaction is a snapshot of the form (ADR 0048): anything the user can change
    // that went into it discards it, so what Confirm sends is always what was reviewed.
    partial void OnSendToChanged(string value) => InvalidatePendingSend();

    partial void OnSendAmountChanged(string value) => InvalidatePendingSend();

    partial void OnSendAllChanged(bool value) => InvalidatePendingSend();

    private void SetFeeRate(string value)
    {
        _settingFeeRate = true;
        try { SendFeeRate = value; }
        finally { _settingFeeRate = false; }
    }

    private async Task RefreshFeeEstimatesAsync(ElectrumClient client)
    {
        try
        {
            var estimates = await FeePolicy.FetchAsync(client);
            Dispatcher.UIThread.Post(() => ApplyFeeEstimates(estimates));
        }
        catch
        {
            // Optional information: a failure leaves the manual field and the 1 sat/vB default.
        }
    }

    private void ApplyFeeEstimates(FeeEstimates estimates)
    {
        _feeEstimates = estimates;
        HasFeeEstimates = estimates.HasAny;
        FeeEstimateHint = estimates.HasAny
            ? string.Format(Loc.Tr("send.fee.hint"),
                estimates.Fast is { } f ? FeePolicy.Format(f) : "—",
                estimates.Normal is { } n ? FeePolicy.Format(n) : "—",
                estimates.Economy is { } e ? FeePolicy.Format(e) : "—",
                estimates.Relay is { } r ? FeePolicy.Format(r) : "—")
            : "";
        // Preselect the normal estimate unless the user already typed something.
        if (!_feeRateTouched && estimates.HasAny)
            SetFeeRate(FeePolicy.Format(FeePolicy.DefaultRate(estimates)));
    }

    [RelayCommand]
    private void UseFeePreset(string preset)
    {
        var rate = preset switch
        {
            "fast" => _feeEstimates?.Fast,
            "economy" => _feeEstimates?.Economy,
            _ => _feeEstimates?.Normal,
        };
        if (rate is { } r)
            SetFeeRate(FeePolicy.Format(r));
    }

    [ObservableProperty]
    private string sendPreview = "";

    [ObservableProperty]
    private bool hasPendingSend;

    [ObservableProperty]
    private string pendingPsbtBase64 = "";

    /// <summary>True when the open account cannot sign — Send only prepares an unsigned PSBT to export.</summary>
    public bool IsWatchOnlyAccount => _account?.IsWatchOnly ?? false;

    public void NotifyPsbtCopied() => StatusMessage = Loc.Tr("psbt.copied");

    [RelayCommand]
    private async Task ScanQr()
    {
        if (PlatformServices.ScanQrAsync is not { } scan) return;
        var raw = await scan();
        if (string.IsNullOrWhiteSpace(raw)) return;
        // Handle URIs like "palladium:ADDRESS?amount=X" — extract address only
        var address = raw.Contains(':') ? raw.Split(':')[1] : raw;
        if (address.Contains('?')) address = address.Split('?')[0];
        SendTo = address.Trim();
    }

    [RelayCommand]
    private async Task PrepareSend()
    {
        DiscardPendingSend(); // a Prepare that fails must not leave the previous transaction confirmable
        if (_account is null || _doc?.Cache is null)
        {
            SendPreview = Loc.Tr("msg.send.sync.first");
            return;
        }
        try
        {
            if (_lastTransactions is null)
            {
                SendPreview = Loc.Tr("msg.send.connect.first");
                return;
            }

            var destination = BitcoinAddress.Create(SendTo.Trim(), PalladiumNetworks.For(Net));
            long amount = 0;
            if (!SendAll && (!CoinAmount.TryParseIn(SendAmount, _config.Unit, out amount) || amount <= 0))
            {
                SendPreview = Loc.Tr("msg.amount.invalid");
                return;
            }
            if (!CoinAmount.TryParseFeeRate(SendFeeRate, out var feeRate))
            {
                SendPreview = Loc.Tr("msg.feerate.invalid");
                return;
            }
            if (FeePolicy.IsBelowRelay(feeRate, _feeEstimates?.Relay))
            {
                SendPreview = string.Format(Loc.Tr("msg.feerate.belowrelay"),
                    FeePolicy.Format(_feeEstimates!.Relay!.Value));
                return;
            }

            var pool = SpendPool(out var poolError);
            if (pool is null)
            {
                SendPreview = poolError!;
                return;
            }
            _pendingSend = new TransactionFactory(_account).Build(
                pool, _lastTransactions, destination, amount, feeRate,
                _doc.Cache.NextChangeIndex, _doc.Cache.TipHeight, SendAll);

            SendPreview = DescribePayment(_pendingSend, destination) +
                (_pendingSend.TxidIsFinal
                    ? $"txid {_pendingSend.Txid[..16]}… · "
                    : Loc.Tr("msg.txid.notfinal") + " · ") +
                $"fee {Fmt(_pendingSend.Fee.Satoshi)} " +
                $"({_pendingSend.Transaction.GetVirtualSize()} vB)" +
                (_pendingSend.Signed ? "" : Loc.Tr("msg.unsigned.watchonly")) +
                HighFeeWarning(_pendingSend.Fee.Satoshi,
                    SendAll ? _pendingSend.Transaction.TotalOut.Satoshi : amount);
            HasPendingSend = _pendingSend.Signed;
            PendingPsbtBase64 = _pendingSend.Signed ? "" : _pendingSend.Psbt.ToBase64();
        }
        catch (Exception ex)
        {
            DiscardPendingSend();
            SendPreview = $"{Loc.Tr("msg.error")}: {DescribeError(ex)}";
        }
        await Task.CompletedTask;
    }

    private void DiscardPendingSend()
    {
        _pendingSend = null;
        HasPendingSend = false;
        PendingPsbtBase64 = "";
    }

    /// <summary>Drops the prepared send and its review, because something that went into it changed.</summary>
    private void InvalidatePendingSend()
    {
        if (_pendingSend is null)
            return;
        DiscardPendingSend();
        SendPreview = "";
    }

    /// <summary>The fee rate and the coin selection are shared by Send and Donate.</summary>
    private void InvalidatePending()
    {
        InvalidatePendingSend();
        InvalidatePendingDonate();
    }

    /// <summary>
    /// The first lines of the review, read from the built transaction and not from the form: the
    /// amount and the full address it really pays, and the change that comes back to the wallet.
    /// If the transaction does not pay the requested address there is nothing to confirm, so this
    /// throws instead of showing a review that does not match.
    /// </summary>
    private string DescribePayment(BuiltTransaction built, BitcoinAddress destination)
    {
        var outputs = built.Transaction.Outputs;
        var payment = outputs.FirstOrDefault(o => o.ScriptPubKey == destination.ScriptPubKey)
            ?? throw new InvalidOperationException("The built transaction does not pay the requested address.");
        var change = outputs.Where(o => !ReferenceEquals(o, payment)).Sum(o => o.Value.Satoshi);
        return $"{Fmt(payment.Value.Satoshi)} → {destination}\n" +
            (change > 0 ? string.Format(Loc.Tr("send.review.change"), Fmt(change)) + "\n" : "");
    }

    /// <summary>
    /// After a sync has finished: a prepared transaction whose coins are no longer spendable (spent
    /// elsewhere, back in the mempool after a reorganisation, or frozen) cannot be sent as shown, so
    /// it is dropped with a message. Only the final state of a sync is used, never a partial one.
    /// </summary>
    private void DiscardStalePending(SyncCache cache)
    {
        var spendable = cache.Utxos
            .Where(u => u.IsSpendable(Profile, cache.TipHeight))
            .Select(u => (u.Txid, u.Vout))
            .ToHashSet();
        bool Stale(BuiltTransaction built) =>
            built.Transaction.Inputs.Any(i => !spendable.Contains((i.PrevOut.Hash.ToString(), (int)i.PrevOut.N)));

        if (_pendingSend is { } send && Stale(send))
        {
            DiscardPendingSend();
            SendPreview = Loc.Tr("msg.pending.stale");
        }
        if (_pendingDonate is { } donate && Stale(donate))
        {
            DiscardPendingDonate();
            DonatePreview = Loc.Tr("msg.pending.stale");
        }
    }

    /// <summary>
    /// Advisory text when the absolute fee is large against the amount: the estimate comes from
    /// an untrusted server, so the resulting fee is always shown and flagged, never just followed.
    /// </summary>
    private string HighFeeWarning(long feeSats, long amountSats) =>
        FeePolicy.IsHighFee(feeSats, amountSats)
            ? "\n" + string.Format(Loc.Tr("msg.fee.high"), FeePolicy.HighFeePercent)
            : "";

    [RelayCommand]
    private async Task ConfirmSend()
    {
        var built = _pendingSend;
        var client = _client;
        // The button is disabled for these, but the command must not depend on it: a watch-only or multisig wallet
        // holds a transaction that is not complete (no or too few signatures), and broadcasting it is never right.
        if (built is null || !built.Signed || client is null)
            return;
        try
        {
            var txid = await client.BroadcastVerifiedAsync(built.ToHex(), built.Txid);
            SendPreview = $"{Loc.Tr("msg.broadcasted")}: {txid}";
            await FinishSendAsync();
        }
        catch (BroadcastTxidMismatchException)
        {
            var (message, inNetwork) = await ReconcileBroadcastMismatchAsync(client, built);
            SendPreview = message;
            if (inNetwork)
                await FinishSendAsync();
        }
        catch (Exception ex)
        {
            SendPreview = $"{Loc.Tr("msg.broadcast.error")}: {DescribeError(ex)}";
        }
    }

    /// <summary>The send is done: the form, the pending transaction and the coin selection are cleared and the wallet resyncs.</summary>
    private async Task FinishSendAsync()
    {
        // The pending transaction goes first: clearing the form fires the invalidation hooks, which
        // would otherwise wipe the message that was just set.
        DiscardPendingSend();
        SendTo = SendAmount = "";
        ClearCoinSelection(); // the selected coins are spent now
        await ConnectAndSync();
    }

    /// <summary>
    /// The server answered a broadcast with a txid other than the one we built. The reply is not
    /// evidence, so the server is asked for the transaction itself: when it holds the transaction, it
    /// is in the network and the send is done (the anomaly is still reported); when it does not, the
    /// transaction stays pending and can be sent again, because the same signed bytes are harmless
    /// to repeat. A check that cannot be completed also keeps the transaction pending.
    /// Shared with Donate. Returns the message to show and whether the transaction is in the network.
    /// </summary>
    private async Task<(string Message, bool InNetwork)> ReconcileBroadcastMismatchAsync(
        ElectrumClient client, BuiltTransaction built)
    {
        try
        {
            return await client.HasTransactionAsync(built.Txid, PalladiumNetworks.For(Net))
                ? (string.Format(Loc.Tr("msg.broadcast.innetwork"), built.Txid), true)
                : (Loc.Tr("msg.broadcast.notinnetwork"), false);
        }
        catch (Exception)
        {
            return (Loc.Tr("msg.broadcast.checkfailed"), false);
        }
    }
}
