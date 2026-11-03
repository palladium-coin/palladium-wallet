using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

// Wallet CLI (blueprint §13): Core use cases exposed via command line,
// for scripting, testing and comparison against the reference wallet.

try
{
    return args switch
    {
        ["newseed", .. var rest] => NewSeed(rest),
        ["addresses", var words, .. var rest] => Addresses(words, rest),
        ["create", .. var rest] => Create(rest),
        ["restore", var words, .. var rest] => Restore(words, rest),
        ["restore-xpub", var xpub, .. var rest] => RestoreXpub(xpub, rest),
        ["restore-address", var addrs, .. var rest] => RestoreAddress(addrs, rest),
        ["info", .. var rest] => Info(rest),
        ["sync", .. var rest] => await Sync(rest),
        ["send", .. var rest] => await Send(rest),
        ["reset-certs", .. var rest] => ResetCerts(rest),
        ["servers", .. var rest] => await Servers(rest),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is WalletSpendException or WrongPasswordException
    or CertificatePinMismatchException or ElectrumServerException or SpvVerificationException
    or WrongChainException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int NewSeed(string[] o)
{
    var length = Opt(o, "--words") == "24" ? MnemonicLength.TwentyFour : MnemonicLength.Twelve;
    Console.WriteLine(Bip39.Generate(length));
    return 0;
}

static int Addresses(string words, string[] o)
{
    var account = AccountFromWords(words, o);
    if (account is null)
        return 1;
    var count = int.TryParse(Opt(o, "--count"), out var n) ? n : 5;
    Console.WriteLine($"network: {account.Profile.NetName}   kind: {account.Kind}   path: m/{account.AccountPath}");
    Console.WriteLine($"account: {account.ToSlip132()}");
    for (var i = 0; i < count; i++)
        Console.WriteLine($"  receiving/{i}: {account.GetReceiveAddress(i)}");
    for (var i = 0; i < Math.Min(count, 2); i++)
        Console.WriteLine($"  change/{i}:    {account.GetChangeAddress(i)}");
    return 0;
}

static int Create(string[] o)
{
    var mnemonic = Bip39.Generate(Opt(o, "--words") == "24" ? MnemonicLength.TwentyFour : MnemonicLength.Twelve);
    Console.WriteLine("New mnemonic (write it on paper, it is NOT shown again):");
    Console.WriteLine($"  {mnemonic}");
    return SaveWallet(mnemonic.ToString(), o);
}

static int Restore(string words, string[] o)
{
    if (!Bip39.TryParse(words, out _))
    {
        Console.Error.WriteLine("Invalid mnemonic (wrong words or checksum).");
        return 1;
    }
    return SaveWallet(words.Trim(), o);
}

static int RestoreXpub(string xpubText, string[] o)
{
    var profile = Profile(o);
    if (!Slip132.TryDecodePublic(xpubText, profile, out var xpub, out var kind))
    {
        Console.Error.WriteLine("Extended key not recognised for this network.");
        return 1;
    }
    var account = HdAccount.FromAccountXpub(xpub!, kind, profile);
    var doc = new WalletDocument
    {
        Network = profile.NetName,
        ScriptKind = kind.ToString(),
        AccountPath = account.AccountPath.ToString(),
        AccountXpub = account.ToSlip132(),
    };
    var path = WalletPath(o, profile);
    WalletStore.Save(doc, path, CliPassword.ForNewWallet(o));
    Console.WriteLine($"Watch-only wallet saved to {path}");
    return 0;
}

static int RestoreAddress(string addrsText, string[] o)
{
    var profile = Profile(o);
    var addresses = addrsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (addresses.Length == 0)
    {
        Console.Error.WriteLine("At least one address is required.");
        return 1;
    }
    WalletDocument doc;
    try
    {
        (doc, _) = WalletLoader.NewFromAddresses(addresses, profile);
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    var path = WalletPath(o, profile);
    WalletStore.Save(doc, path, CliPassword.ForNewWallet(o));
    Console.WriteLine($"Watch-only wallet saved to {path} ({addresses.Length} address(es), cannot sign)");
    return 0;
}

static int Info(string[] o)
{
    var (doc, account, path) = OpenWallet(o);
    Console.WriteLine($"file:     {path}");
    Console.WriteLine($"network:  {doc.Network}   kind: {doc.ScriptKind}   watch-only: {doc.IsWatchOnly}");
    Console.WriteLine($"path:     m/{doc.AccountPath}");
    Console.WriteLine($"xpub:     {doc.AccountXpub}");
    if (doc.Cache is { } cache)
    {
        Console.WriteLine($"balance:  {CoinAmount.Format(cache.SpendableSats, account.Profile.CoinUnit)} spendable"
            + (cache.ImmatureSats != 0 ? $" + {CoinAmount.Format(cache.ImmatureSats)} maturing (not spendable)" : "")
            + (cache.PendingVerificationSats != 0 ? $" + {CoinAmount.Format(cache.PendingVerificationSats)} awaiting SPV verification (not spendable)" : "")
            + (cache.UnconfirmedSats != 0 ? $" + {CoinAmount.Format(cache.UnconfirmedSats)} pending confirmation (not spendable)." : ""));
        Console.WriteLine($"sync:     height {cache.TipHeight}, {cache.History.Count} transactions");
        Console.WriteLine($"receive:  {account.GetReceiveAddress(cache.NextReceiveIndex)}");
    }
    else
    {
        Console.WriteLine($"receive:  {account.GetReceiveAddress(0)}   (never synchronized)");
    }

    if (o.Contains("--addresses") && doc.Cache is { } c)
    {
        Console.WriteLine("addresses:");
        foreach (var a in c.Addresses)
            Console.WriteLine($"  {(a.IsChange ? "change " : "receive")}{a.Index,3}  {a.Address}  " +
                $"{CoinAmount.Format(a.BalanceSats),18}  ({a.TxCount} tx)");
    }
    return 0;
}

static async Task<int> Sync(string[] o)
{
    var (doc, account, path) = OpenWallet(o);
    await using var client = await Connect(o, account.Profile);

    var sync = new WalletSynchronizer(account, client, doc.GapLimit);
    sync.Progress += msg => Console.WriteLine($"  {msg}");
    var net = PalladiumNetworks.For(account.Profile.Kind);
    sync.PreloadCaches(
        doc.Cache?.RawTxHex ?? [],
        doc.Cache?.VerifiedAt ?? [],
        doc.Cache?.BlockHeaders,
        doc.Cache?.NextReceiveIndex ?? 0,
        doc.Cache?.NextChangeIndex ?? 0,
        net,
        doc.Cache?.AnchoredUpTo);
    var result = await sync.SyncOnceAsync();

    var (rawHex, verifiedAt, blockHeaders, anchoredUpTo) = sync.ExportCaches(net);
    doc.Cache = new SyncCache
    {
        TipHeight = result.TipHeight,
        ConfirmedSats = result.ConfirmedSats,
        UnconfirmedSats = result.UnconfirmedSats,
        ImmatureSats = result.ImmatureSats,
        PendingVerificationSats = result.PendingVerificationSats,
        SpendableSats = result.SpendableSats,
        NextReceiveIndex = result.NextReceiveIndex,
        NextChangeIndex = result.NextChangeIndex,
        History = [.. result.History],
        Utxos = [.. result.Utxos],
        Addresses = [.. result.AddressRows],
        RawTxHex = rawHex,
        VerifiedAt = verifiedAt,
        BlockHeaders = blockHeaders,
        AnchoredUpTo = anchoredUpTo,
    };
    WalletStore.Save(doc, path, CliPassword.Current);

    Console.WriteLine($"Balance: {CoinAmount.Format(result.SpendableSats, account.Profile.CoinUnit)} spendable"
        + (result.ImmatureSats != 0 ? $" + {CoinAmount.Format(result.ImmatureSats)} maturing (not spendable)" : "")
        + (result.PendingVerificationSats != 0 ? $" + {CoinAmount.Format(result.PendingVerificationSats)} awaiting SPV verification (not spendable)" : "")
        + (result.UnconfirmedSats != 0 ? $" + {CoinAmount.Format(result.UnconfirmedSats)} pending confirmation (not spendable)" : ""));
    Console.WriteLine($"History ({result.History.Count}):");
    foreach (var tx in result.History)
        Console.WriteLine($"  {(tx.Height > 0 ? tx.Height.ToString() : "mempool"),7}  " +
            $"{(tx.DeltaSats >= 0 ? "+" : "")}{CoinAmount.Format(tx.DeltaSats)}  {tx.Txid}" +
            (tx.Verified ? "" : "  (unverified)"));
    return 0;
}

static async Task<int> Send(string[] o)
{
    var (doc, account, path) = OpenWallet(o);
    if (doc.Cache is null)
    {
        Console.Error.WriteLine("Wallet never synchronized: run 'sync' first.");
        return 1;
    }
    var to = Opt(o, "--to") ?? throw new WalletSpendException("--to missing.");
    var destination = BitcoinAddress.Create(to, PalladiumNetworks.For(account.Profile.Kind));
    var sendAll = o.Contains("--all");
    long amount = 0;
    if (!sendAll && !CoinAmount.TryParseCoins(Opt(o, "--amount") ?? "", out amount))
        throw new WalletSpendException("--amount missing or invalid (or use --all).");
    var feeRate = 1m;
    if (Opt(o, "--feerate") is { } feeText && !CoinAmount.TryParseFeeRate(feeText, out feeRate))
        throw new WalletSpendException($"--feerate invalid (expected sat/vB in (0, {CoinAmount.MaxFeeRate}]).");

    // Source transactions of the UTXOs: needed for amounts and signing.
    await using var client = await Connect(o, account.Profile);
    var network = PalladiumNetworks.For(account.Profile.Kind);
    var transactions = new Dictionary<string, Transaction>();
    foreach (var txid in doc.Cache.Utxos.Select(u => u.Txid).Distinct())
        transactions[txid] = Transaction.Parse(await client.GetTransactionAsync(txid), network);

    var built = new TransactionFactory(account).Build(
        doc.Cache.Utxos, transactions, destination, amount, feeRate,
        doc.Cache.NextChangeIndex, doc.Cache.TipHeight, sendAll);

    Console.WriteLine($"txid:  {built.Txid}");
    Console.WriteLine($"fee:   {CoinAmount.Format(built.Fee.Satoshi, account.Profile.CoinUnit)} " +
        $"({feeRate} sat/vB, {built.Transaction.GetVirtualSize()} vB)");

    if (!built.Signed)
    {
        Console.WriteLine("Watch-only wallet: PSBT to sign offline (§6.5):");
        Console.WriteLine(built.Psbt.ToBase64());
        return 0;
    }

    if (!o.Contains("--broadcast"))
    {
        Console.WriteLine("Signed transaction (NOT broadcast, add --broadcast):");
        Console.WriteLine(built.ToHex());
        return 0;
    }

    var txid2 = await client.BroadcastAsync(built.ToHex());
    Console.WriteLine($"Broadcast: {txid2}");
    return 0;
}

static int ResetCerts(string[] o)
{
    var profile = Profile(o);
    new CertificatePinStore(AppPaths.CertificatePinsPath(profile.Kind)).ResetAll();
    Console.WriteLine($"Saved SSL certificates for {profile.NetName} cleared.");
    return 0;
}

static async Task<int> Servers(string[] o)
{
    var profile = Profile(o);
    var registry = new ServerRegistry(profile, AppPaths.ServersPath(profile.Kind));

    if (o.Contains("--discover"))
    {
        await using var client = await Connect(o, profile);
        var added = await registry.DiscoverAsync(client);
        Console.WriteLine($"Discovered {added} new servers from peers.");
    }

    Console.WriteLine($"Known servers ({profile.NetName}):");
    foreach (var server in registry.All)
        Console.WriteLine($"  {server}");
    return 0;
}

// ----- shared helpers -----

static ChainProfile Profile(string[] o) => Opt(o, "--net") switch
{
    "testnet" => ChainProfiles.Testnet,
    "regtest" => ChainProfiles.Regtest,
    _ => ChainProfiles.Mainnet,
};

static ScriptKind Kind(string[] o) => Opt(o, "--kind") switch
{
    "legacy" => ScriptKind.Legacy,
    "wrapped" => ScriptKind.WrappedSegwit,
    _ => ScriptKind.NativeSegwit,
};

static string WalletPath(string[] o, ChainProfile profile) =>
    Opt(o, "--file") ?? AppPaths.DefaultWalletPath(profile.Kind);

static HdAccount? AccountFromWords(string words, string[] o)
{
    if (!Bip39.TryParse(words, out var mnemonic))
    {
        Console.Error.WriteLine("Invalid mnemonic (wrong words or checksum).");
        return null;
    }
    var profile = Profile(o);
    var kind = Kind(o);
    var passphrase = Opt(o, "--passphrase");
    return Opt(o, "--path") is { } path
        ? HdAccount.FromSeed(Bip39.ToSeed(mnemonic!, passphrase), kind, profile, KeyPath.Parse(path))
        : HdAccount.FromMnemonic(mnemonic!, passphrase, kind, profile);
}

static int SaveWallet(string words, string[] o)
{
    var profile = Profile(o);
    var (doc, account) = WalletLoader.NewFromMnemonic(
        words, Opt(o, "--passphrase"), Kind(o), profile,
        Opt(o, "--path") is { } p ? KeyPath.Parse(p) : null);
    var path = WalletPath(o, profile);
    WalletStore.Save(doc, path, CliPassword.ForNewWallet(o));
    Console.WriteLine($"Wallet saved to {path}");
    Console.WriteLine($"First address: {account.GetReceiveAddress(0)}");
    return 0;
}

static (WalletDocument, IWalletAccount, string) OpenWallet(string[] o)
{
    var path = WalletPath(o, Profile(o));
    if (!WalletStore.Exists(path))
        throw new WalletSpendException($"No wallet at {path}: use 'create' or 'restore'.");
    var doc = WalletStore.Load(path, CliPassword.ForOpening(o, path));
    return (doc, WalletLoader.ToAccount(doc), path);
}

static async Task<ElectrumClient> Connect(string[] o, ChainProfile profile)
{
    var useSsl = o.Contains("--ssl");
    string host;
    int port;
    if (Opt(o, "--server") is { } server)
    {
        var parts = server.Split(':');
        host = parts[0];
        port = parts.Length > 1 ? int.Parse(parts[1])
            : useSsl ? profile.DefaultSslPort : profile.DefaultTcpPort;
    }
    else
    {
        // Without --server, use the first known server (bootstrap §3 or discovered §9).
        var known = new ServerRegistry(profile, AppPaths.ServersPath(profile.Kind)).Default
            ?? throw new WalletSpendException("No known server for this network: use --server host:port.");
        host = known.Host;
        port = known.PortFor(useSsl);
    }

    var pins = new CertificatePinStore(AppPaths.CertificatePinsPath(profile.Kind));
    Console.WriteLine($"Connecting to {host}:{port}{(useSsl ? " (TLS)" : "")}…");
    var client = await ElectrumClient.ConnectAsync(host, port, useSsl, pins);
    try { await client.VerifyChainAsync(profile); }
    catch
    {
        await client.DisposeAsync();
        throw;
    }
    return client;
}

static string? Opt(string[] options, string name)
{
    var i = Array.IndexOf(options, name);
    return i >= 0 && i + 1 < options.Length ? options[i + 1] : null;
}

static int Usage()
{
    Console.WriteLine("""
        PalladiumWallet CLI

        Wallet:
          create        [--words 12|24] [--kind segwit|wrapped|legacy] [--net mainnet|testnet|regtest]
                        [--passphrase W] [--no-encryption] [--file PATH]
          restore       "<mnemonic>" [same options as create] [--path m/...]
          restore-xpub  <slip132 xpub> [--net ...] [--file PATH]   (watch-only)
          restore-address <addr1,addr2,...> [--net ...] [--file PATH]   (watch-only, no keys)
          info          [--net ...] [--file PATH]

        Wallet password (create/restore ask for one, encrypted wallets need it to open):
          --password-env NAME   read it from an environment variable
          --password-stdin      read it from the first line of stdin
          (neither)             prompt on the terminal, no echo; fails if there is no terminal
          --no-encryption       create/restore only: store the wallet UNENCRYPTED (explicit opt-in)
          --password P          still accepted but unsafe (shell history, process list): warns

        Network (indexing server; without --server the first known server is used):
          sync          [--server host[:port]] [--ssl] [--net ...] [--file PATH]
          send          --to ADDRESS (--amount X | --all) [--feerate sat/vB]
                        [--server host[:port]] [--ssl] [--broadcast] [...]
          servers       [--discover] [--server host[:port]] [--ssl] [--net ...]
          reset-certs   [--net ...]

        Tools:
          newseed       [--words 12|24]
          addresses     "<mnemonic>" [--kind ...] [--net ...] [--count N] [--passphrase W] [--path m/...]
        """);
    return 1;
}
