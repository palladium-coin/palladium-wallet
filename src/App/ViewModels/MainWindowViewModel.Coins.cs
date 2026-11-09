using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalladiumWallet.App.Localization;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.App.ViewModels;

/// <summary>One coin (UTXO) in the Coins tab. The texts are computed when the row is built.</summary>
public sealed record CoinRow(
    string Txid, int Vout, string Amount, string Address, string Status,
    bool IsFrozen, bool IsSelectable, bool IsSelected, string FreezeText)
{
    public string Outpoint => $"{Txid}:{Vout}";
}

public partial class MainWindowViewModel
{
    public ObservableCollection<CoinRow> Coins { get; } = [];

    // Coin control: while non-empty, the next send or donation may spend only these coins. Session
    // state, never written to the wallet file, and pruned to the coins that survive each sync.
    private readonly HashSet<(string Txid, int Vout)> _selectedCoins = [];

    [ObservableProperty]
    private bool hasCoins;

    [ObservableProperty]
    private bool hasCoinSelection;

    [ObservableProperty]
    private string coinSelectionText = "";

    /// <summary>Rebuilds the Coins tab from a cache (null when no wallet is open or none is synced yet).</summary>
    private void RefreshCoins(SyncCache? cache)
    {
        Coins.Clear();
        if (cache is null || _account is null)
        {
            _selectedCoins.Clear();
            HasCoins = false;
            UpdateCoinSelection(null);
            return;
        }

        _selectedCoins.IntersectWith(cache.Utxos.Select(u => (u.Txid, u.Vout)).ToHashSet());
        foreach (var u in cache.Utxos.OrderByDescending(u => u.ValueSats))
        {
            var selectable = !u.Frozen && u.IsSpendable(Profile, cache.TipHeight);
            Coins.Add(new CoinRow(
                u.Txid, u.Vout, Fmt(u.ValueSats), u.Address, CoinStatus(u, cache.TipHeight),
                u.Frozen, selectable, _selectedCoins.Contains((u.Txid, u.Vout)),
                Loc.Tr(u.Frozen ? "coins.unfreeze" : "coins.freeze")));
        }
        HasCoins = Coins.Count > 0;
        UpdateCoinSelection(cache);
    }

    private string CoinStatus(CachedUtxo u, int tipHeight)
    {
        if (u.Frozen)
            return Loc.Tr("coins.status.frozen");
        if (u.Height <= 0)
            return Loc.Tr("coins.status.unconfirmed");
        if (u.Confirmations(tipHeight) < u.RequiredConfirmations(Profile))
            return Loc.Tr(u.IsCoinbase ? "coins.status.immature" : "coins.status.confirming");
        if (!u.Verified)
            return Loc.Tr("coins.status.unverified");
        return Loc.Tr("coins.status.spendable");
    }

    private void UpdateCoinSelection(SyncCache? cache)
    {
        HasCoinSelection = _selectedCoins.Count > 0;
        if (cache is null || !HasCoinSelection)
        {
            CoinSelectionText = "";
            return;
        }
        var selected = cache.Utxos.Where(u => _selectedCoins.Contains((u.Txid, u.Vout))).ToList();
        CoinSelectionText = string.Format(Loc.Tr("coins.selection"), selected.Count,
            Fmt(selected.Sum(u => u.ValueSats)));
    }

    /// <summary>
    /// Freezes or unfreezes one coin. A frozen coin leaves the selection, the balance is updated at
    /// once, and the wallet file is saved so that the decision survives a restart.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFreezeCoin(CoinRow? row)
    {
        if (row is null || _doc?.Cache is null)
            return;
        var freeze = !row.IsFrozen;
        if (!CoinControl.SetFrozen(_doc.Cache, Profile, row.Txid, row.Vout, freeze))
            return;
        if (freeze)
            _selectedCoins.Remove((row.Txid, row.Vout));
        ApplyCache(_doc.Cache);
        if (_walletPath is null)
            return;
        try
        {
            await WalletStore.SaveAsync(_doc, _walletPath, _password);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{Loc.Tr("msg.error")}: {ex.Message}";
        }
    }

    /// <summary>Adds or removes a coin from the coin-control selection. Only spendable, unfrozen coins can be selected.</summary>
    [RelayCommand]
    private void ToggleCoinSelected(CoinRow? row)
    {
        if (row is null || _doc?.Cache is null)
            return;
        var key = (row.Txid, row.Vout);
        if (_selectedCoins.Remove(key))
        {
            RefreshCoins(_doc.Cache);
            return;
        }
        var coin = _doc.Cache.Utxos.FirstOrDefault(u => u.Txid == row.Txid && u.Vout == row.Vout);
        if (coin is null || coin.Frozen || !coin.IsSpendable(Profile, _doc.Cache.TipHeight))
        {
            StatusMessage = Loc.Tr("msg.coins.notspendable");
            return;
        }
        _selectedCoins.Add(key);
        RefreshCoins(_doc.Cache);
    }

    [RelayCommand]
    private void ClearCoinSelection()
    {
        _selectedCoins.Clear();
        RefreshCoins(_doc?.Cache);
    }

    /// <summary>
    /// The coins the next spend may use: all of them, or only the selected ones (coin control).
    /// Null, with the reason in <paramref name="error"/>, when the selection holds a coin that cannot
    /// be spent now. Callers check that the cache exists.
    /// </summary>
    private IReadOnlyList<CachedUtxo>? SpendPool(out string? error)
    {
        error = null;
        var cache = _doc!.Cache!;
        if (_selectedCoins.Count == 0)
            return cache.Utxos;
        var selected = cache.Utxos.Where(u => _selectedCoins.Contains((u.Txid, u.Vout))).ToList();
        if (selected.Count != _selectedCoins.Count || selected.Any(u => !u.IsSpendable(Profile, cache.TipHeight)))
        {
            error = Loc.Tr("msg.coins.notspendable");
            return null;
        }
        return selected;
    }
}
