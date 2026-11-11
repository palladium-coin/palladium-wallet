using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.App.ViewModels;

public partial class MainWindowViewModel
{
    public const string DevAddress = "plm1qdq3gu2zvg9lyr8gxd6yln4wavc5tlp8prmvfay";

    /// <summary>Test seam: the fixed address is a mainnet one, so a regtest run pays another one instead.</summary>
    internal static string? DonationAddressOverride { get; set; }

    [ObservableProperty]
    private string donateAmount = "";

    [ObservableProperty]
    private string donatePreview = "";

    [ObservableProperty]
    private bool hasPendingDonate;

    private BuiltTransaction? _pendingDonate;

    // Same snapshot rule as Send (ADR 0048): changing the amount discards the prepared donation.
    partial void OnDonateAmountChanged(string value) => InvalidatePendingDonate();

    private void DiscardPendingDonate()
    {
        _pendingDonate = null;
        HasPendingDonate = false;
    }

    private void InvalidatePendingDonate()
    {
        if (_pendingDonate is null)
            return;
        DiscardPendingDonate();
        DonatePreview = "";
    }

    [RelayCommand]
    private async Task PrepareDonate()
    {
        DiscardPendingDonate();

        if (_account is null || _doc?.Cache is null || _lastTransactions is null)
        {
            DonatePreview = Loc.Tr("msg.send.sync.first");
            return;
        }
        try
        {
            if (!TryParseDestination(DonationAddressOverride ?? DevAddress, out var destination))
            {
                DonatePreview = Loc.Tr("msg.address.invalid");
                return;
            }
            if (!CoinAmount.TryParseIn(DonateAmount.Trim(), _config.Unit, out var amount) || amount <= 0)
            {
                DonatePreview = Loc.Tr("msg.amount.invalid");
                return;
            }
            if (!CoinAmount.TryParseFeeRate(SendFeeRate, out var feeRate))
            {
                DonatePreview = Loc.Tr("msg.feerate.invalid");
                return;
            }
            if (FeePolicy.IsBelowRelay(feeRate, _feeEstimates?.Relay))
            {
                DonatePreview = string.Format(Loc.Tr("msg.feerate.belowrelay"),
                    FeePolicy.Format(_feeEstimates!.Relay!.Value));
                return;
            }

            var pool = SpendPool(out var poolError);
            if (pool is null)
            {
                DonatePreview = poolError!;
                return;
            }
            _pendingDonate = new TransactionFactory(_account).Build(
                pool, _lastTransactions, destination, amount, feeRate,
                _doc.Cache.NextChangeIndex, _doc.Cache.TipHeight, sendAll: false);

            DonatePreview = DescribePayment(_pendingDonate, destination) +
                (_pendingDonate.TxidIsFinal
                    ? $"txid {_pendingDonate.Txid[..16]}… · "
                    : Loc.Tr("msg.txid.notfinal") + " · ") +
                $"fee {Fmt(_pendingDonate.Fee.Satoshi)} " +
                $"({_pendingDonate.Transaction.GetVirtualSize()} vB)" +
                (_pendingDonate.Signed ? "" : " · watch-only") +
                HighFeeWarning(_pendingDonate.Fee.Satoshi, amount);
            HasPendingDonate = _pendingDonate.Signed;
        }
        catch (Exception ex)
        {
            DiscardPendingDonate();
            DonatePreview = $"{Loc.Tr("msg.error")}: {DescribeError(ex)}";
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConfirmDonate()
    {
        var built = _pendingDonate;
        var client = _client;
        if (built is null || client is null)
            return;
        if (!built.Signed)
        {
            DonatePreview = Loc.Tr("msg.confirm.incomplete"); // same rule as Send: never broadcast an incomplete transaction
            return;
        }
        try
        {
            var txid = await client.BroadcastVerifiedAsync(built.ToHex(), built.Txid);
            DonatePreview = $"{Loc.Tr("msg.donate.thanks")}: {txid}";
            await FinishDonateAsync();
        }
        catch (BroadcastTxidMismatchException)
        {
            // Same reconciliation as Send: the transaction the server holds decides, not its reply.
            var (message, inNetwork) = await ReconcileBroadcastMismatchAsync(client, built);
            DonatePreview = message;
            if (inNetwork)
                await FinishDonateAsync();
        }
        catch (Exception ex)
        {
            DonatePreview = $"{Loc.Tr("msg.broadcast.error")}: {DescribeError(ex)}";
        }
    }

    private async Task FinishDonateAsync()
    {
        // The pending donation goes first: clearing the amount fires the invalidation hook, which
        // would otherwise wipe the message that was just set.
        DiscardPendingDonate();
        DonateAmount = "";
        ClearCoinSelection(); // the selected coins are spent now
        await ConnectAndSync();
    }

    private void ResetDonate()
    {
        DiscardPendingDonate();
        DonateAmount = "";
        DonatePreview = "";
    }
}
