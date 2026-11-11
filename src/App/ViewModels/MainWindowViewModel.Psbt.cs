using System;
using System.Linq;
using System.Text;
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
    // ---- sign a PSBT made elsewhere (a cosigner's, a watch-only copy's), ADR 0062

    [ObservableProperty]
    private bool isPsbtOpen;

    /// <summary>The PSBT to sign: pasted text (base64 or hex), or what a loaded file held.</summary>
    [ObservableProperty]
    private string psbtInput = "";

    /// <summary>What the PSBT does, as read before the user decides: inputs, outputs (own change marked), fee.</summary>
    [ObservableProperty]
    private string psbtSummary = "";

    /// <summary>The PSBT after this wallet signed it, for the next cosigner.</summary>
    [ObservableProperty]
    private string signedPsbtBase64 = "";

    /// <summary>The signatures are complete: the transaction can be broadcast.</summary>
    [ObservableProperty]
    private bool hasFinalPsbt;

    private PSBT? _signedPsbt;
    private Transaction? _finalPsbtTx;

    // Another PSBT makes the previous result stale: it must not be saved or broadcast under the new text.
    partial void OnPsbtInputChanged(string value) => ClearPsbtResult();

    private void ClearPsbtResult()
    {
        _signedPsbt = null;
        _finalPsbtTx = null;
        SignedPsbtBase64 = "";
        HasFinalPsbt = false;
        PsbtSummary = "";
    }

    [RelayCommand]
    private void OpenPsbt()
    {
        PsbtInput = "";
        ClearPsbtResult();
        IsPsbtOpen = true;
    }

    [RelayCommand]
    private void ClosePsbt() => IsPsbtOpen = false;

    /// <summary>Loads a PSBT file (binary, or text inside) chosen in the file picker.</summary>
    public void LoadPsbtBytes(byte[] data)
    {
        try
        {
            PsbtInput = PsbtFormat.Parse(data, PalladiumNetworks.For(Net)).ToBase64();
        }
        catch (FormatException)
        {
            PsbtInput = "";
            PsbtSummary = Loc.Tr("msg.psbt.invalid");
        }
    }

    /// <summary>The signed PSBT as the binary file a signer reads, or null before anything is signed.</summary>
    public byte[]? SignedPsbtBytes() => _signedPsbt is null ? null : PsbtFormat.ToBinary(_signedPsbt);

    [RelayCommand]
    private void SignPsbt()
    {
        ClearPsbtResult();
        if (_account is null || _doc is null)
            return;
        PSBT psbt;
        try
        {
            psbt = PsbtFormat.Parse(PsbtInput.Trim(), PalladiumNetworks.For(Net));
        }
        catch (FormatException)
        {
            PsbtSummary = Loc.Tr("msg.psbt.invalid");
            return;
        }

        PsbtSignResult result;
        try
        {
            var scan = Math.Max(PsbtSigner.DefaultScan,
                Math.Max(_doc.Cache?.NextReceiveIndex ?? 0, _doc.Cache?.NextChangeIndex ?? 0) + _doc.GapLimit);
            result = PsbtSigner.Sign(psbt, _account, scan);
        }
        catch (WalletSpendException ex)
        {
            PsbtSummary = $"{Loc.Tr("msg.error")}: {ex.Message}";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{Loc.Tr("psbt.inputs")} ({result.Inputs.Count})");
        foreach (var i in result.Inputs)
            sb.AppendLine($"  {i.PrevTxid[..12]}…:{i.PrevIndex}  "
                + (i.AmountSats is { } a ? Fmt(a) : Loc.Tr("psbt.amount.unknown"))
                + (i.AmountSats is not null && !i.AmountVerified ? "  " + Loc.Tr("psbt.unverified") : "")
                + "  " + Loc.Tr(i.Signed ? "psbt.signed" : "psbt.notsigned"));
        sb.AppendLine($"{Loc.Tr("psbt.outputs")} ({result.Outputs.Count})");
        foreach (var o in result.Outputs)
            sb.AppendLine($"  {o.Address ?? "?"}  {Fmt(o.AmountSats)}" + (o.IsMine ? "  " + Loc.Tr("psbt.change") : ""));
        sb.AppendLine(result.FeeSats is { } fee ? $"{Loc.Tr("psbt.fee")}: {Fmt(fee)}" : $"{Loc.Tr("psbt.fee")}: {Loc.Tr("psbt.amount.unknown")}");
        sb.Append(Loc.Tr(result.Complete ? "msg.psbt.complete" : "msg.psbt.incomplete"));

        _signedPsbt = result.Psbt;
        _finalPsbtTx = result.FinalTransaction;
        SignedPsbtBase64 = result.Psbt.ToBase64();
        HasFinalPsbt = result.Complete;
        PsbtSummary = sb.ToString();
    }

    [RelayCommand]
    private async Task BroadcastPsbt()
    {
        var tx = _finalPsbtTx;
        var client = _client;
        if (tx is null || client is null)
        {
            PsbtSummary = Loc.Tr("msg.send.sync.first");
            return;
        }
        var txid = tx.GetHash().ToString();
        try
        {
            await client.BroadcastVerifiedAsync(tx.ToHex(), txid);
            PsbtSummary = $"{Loc.Tr("msg.broadcasted")}: {txid}";
            HasFinalPsbt = false;
            await ConnectAndSync();
        }
        catch (BroadcastTxidMismatchException)
        {
            // Same rule as Send: the transaction the server holds decides, not its reply.
            try
            {
                var held = await client.HasTransactionAsync(txid, PalladiumNetworks.For(Net));
                PsbtSummary = held ? string.Format(Loc.Tr("msg.broadcast.innetwork"), txid) : Loc.Tr("msg.broadcast.notinnetwork");
                if (held)
                    HasFinalPsbt = false;
            }
            catch (Exception)
            {
                PsbtSummary = Loc.Tr("msg.broadcast.checkfailed");
            }
        }
        catch (Exception ex)
        {
            PsbtSummary = $"{Loc.Tr("msg.broadcast.error")}: {DescribeError(ex)}";
        }
    }
}
