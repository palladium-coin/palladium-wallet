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
        ["restore-wif", var keys, .. var rest] => RestoreWif(keys, rest),
        ["info", .. var rest] => Info(rest),
        ["sync", .. var rest] => await Sync(rest),
        ["send", .. var rest] => await Send(rest),
        ["sign-psbt", var psbt, .. var rest] => await SignPsbt(psbt, rest),
        ["cosigner-key", var words, .. var rest] => CosignerKey(words, rest),
        ["create-multisig", .. var rest] => CreateMultisig(rest),
        ["export-multisig", .. var rest] => ExportMultisig(rest),
        ["combine-psbt", .. var rest] => await CombinePsbt(rest),
        ["utxos", .. var rest] => Utxos(rest),
        ["freeze", var outpoint, .. var rest] => Freeze(outpoint, rest, frozen: true),
        ["unfreeze", var outpoint, .. var rest] => Freeze(outpoint, rest, frozen: false),
        ["reset-certs", .. var rest] => ResetCerts(rest),
        ["servers", .. var rest] => await Servers(rest),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is WalletSpendException or WrongPasswordException
    or CertificatePinMismatchException or ElectrumServerException or SpvVerificationException
    or WrongChainException or BroadcastTxidMismatchException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException
    or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
{
    // Environment failures (server unreachable or dropped, unreadable or corrupt wallet file): a
    // clean one-line error, not a stack trace. A bug (any other exception) still surfaces loudly.
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
    // The origin of the key (master fingerprint + account path) is not in the xpub: it must be
    // supplied for the PSBT to carry BIP32 origin info that an external signer can match.
    HDFingerprint? fingerprint = null;
    if (Opt(o, "--fingerprint") is { } fpText)
    {
        fingerprint = HdAccount.ParseFingerprint(fpText.Trim().ToLowerInvariant());
        if (fingerprint is null)
        {
            Console.Error.WriteLine("--fingerprint must be 8 hex digits (the master key fingerprint).");
            return 1;
        }
    }
    KeyPath? accountPath = null;
    if (Opt(o, "--path") is { } pathText && !DerivationPaths.TryParse(pathText, out accountPath))
    {
        Console.Error.WriteLine("--path is not a valid derivation path (e.g. m/84'/746'/0').");
        return 1;
    }
    var account = HdAccount.FromAccountXpub(xpub!, kind, profile, accountPath, fingerprint);
    var doc = new WalletDocument
    {
        Network = profile.NetName,
        ScriptKind = kind.ToString(),
        AccountPath = account.AccountPath.ToString(),
        AccountXpub = account.ToSlip132(),
        MasterFingerprint = fingerprint is { } fp ? Convert.ToHexString(fp.ToBytes()).ToLowerInvariant() : null,
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

/// <summary>
/// Imports one or more WIF private keys as a spendable wallet (no seed, no derivation). The keys are
/// the argument, comma separated, or <c>-</c> to read them from standard input (one per line or comma
/// separated), which keeps them out of the process list and the shell history.
/// </summary>
static int RestoreWif(string keysArg, string[] o)
{
    var profile = Profile(o);
    var text = keysArg == "-" ? Console.In.ReadToEnd() : keysArg;
    var keys = text.Split([',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    WalletDocument doc;
    try
    {
        (doc, _) = WalletLoader.NewFromWif(keys, Kind(o), profile);
    }
    catch (InvalidDataException ex)
    {
        throw new WalletSpendException(ex.Message);
    }
    var path = WalletPath(o, profile);
    WalletStore.Save(doc, path, CliPassword.ForNewWallet(o));
    Console.WriteLine($"Wallet saved to {path} ({doc.WifKeys?.Count ?? 0} key(s), {doc.ScriptKind})");
    return 0;
}

static int Info(string[] o)
{
    var (doc, account, path) = OpenWallet(o);
    Console.WriteLine($"file:     {path}");
    Console.WriteLine($"network:  {doc.Network}   kind: {doc.ScriptKind}   watch-only: {doc.IsWatchOnly}");
    if (account is MultisigAccount multisig)
    {
        Console.WriteLine($"multisig: {multisig.Threshold}-of-{multisig.Cosigners.Count}, {(multisig.IsWrapped ? "P2SH-P2WSH" : "P2WSH")} sortedmulti (BIP67)" +
            (multisig.OwnCosignerIndex is { } own ? $"; this wallet holds cosigner {own + 1}'s key" : "; holds no key (cannot sign)"));
        for (var i = 0; i < multisig.Cosigners.Count; i++)
            Console.WriteLine($"cosigner {i + 1}: {multisig.Cosigners[i].ToText(multisig.Kind, account.Profile)}");
        Console.WriteLine($"wallet id: {multisig.WalletId}   (compare it with every other cosigner)");
        Console.WriteLine($"descriptor: {multisig.ToDescriptor()}");
    }
    else if (doc.AccountXpub is { Length: > 0 })
    {
        Console.WriteLine($"path:     m/{doc.AccountPath}");
        Console.WriteLine($"xpub:     {doc.AccountXpub}");
    }
    else
    {
        // Imported keys or addresses: nothing is derived, so there is no path or extended key to show.
        Console.WriteLine($"imported: {(doc.WifKeys?.Count > 0 ? $"{doc.WifKeys.Count} private key(s)" : "addresses only")}");
    }
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
    var result = await SyncWallet(doc, account, path, client);

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

/// <summary>
/// Synchronizes the wallet with the server and saves the result. Used by `sync`, and by `send`
/// before it builds anything: a spend must start from the chain's current state, not from a cache
/// written before the last transaction (an output already spent would be selected again).
/// </summary>
static async Task<SyncResult> SyncWallet(WalletDocument doc, IWalletAccount account, string path,
    ElectrumClient client)
{
    var sync = new WalletSynchronizer(account, client, doc.GapLimit);
    IReadOnlyList<ElectrumClient> witnesses = [];
    sync.WitnessProvider = async ct =>
    {
        var pins = new CertificatePinStore(AppPaths.CertificatePinsPath(account.Profile.Kind));
        return witnesses = await WitnessConnector.ConnectAsync(account.Profile,
            account.Profile.BootstrapServers, client.Host, client.Port, client.UseSsl, pins, ct: ct);
    };
    sync.Progress += msg => Console.WriteLine($"  {msg}");
    sync.FrozenOutpoints = doc.Cache?.FrozenOutpoints() ?? [];
    var net = PalladiumNetworks.For(account.Profile.Kind);
    sync.PreloadCaches(
        doc.Cache?.RawTxHex ?? [],
        doc.Cache?.VerifiedAt ?? [],
        doc.Cache?.BlockHeaders,
        doc.Cache?.NextReceiveIndex ?? 0,
        doc.Cache?.NextChangeIndex ?? 0,
        net,
        doc.Cache?.AnchoredDownTo);
    SyncResult result;
    try { result = await sync.SyncOnceAsync(); }
    finally
    {
        foreach (var w in witnesses)
            await w.DisposeAsync();
    }

    var (rawHex, verifiedAt, blockHeaders, anchoredDownTo) = sync.ExportCaches(net);
    var cache = new SyncCache
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
        AnchoredDownTo = anchoredDownTo,
    };
    doc.Cache = cache;
    WalletStore.Save(doc, path, CliPassword.Current);
    return result;
}

/// <summary>Parses a destination for the wallet's network, with a clean error for another network's address.</summary>
static BitcoinAddress ParseAddress(string text, ChainProfile profile)
{
    try
    {
        return BitcoinAddress.Create(text, PalladiumNetworks.For(profile.Kind));
    }
    catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
    {
        throw new WalletSpendException($"'{text}' is not a valid address for {profile.NetName}.");
    }
}

static async Task<int> Send(string[] o)
{
    var (doc, account, path) = OpenWallet(o);
    var to = Opt(o, "--to") ?? throw new WalletSpendException("--to missing.");
    var destination = ParseAddress(to, account.Profile);
    var sendAll = o.Contains("--all");
    long amount = 0;
    if (!sendAll && !CoinAmount.TryParseCoins(Opt(o, "--amount") ?? "", out amount))
        throw new WalletSpendException("--amount missing or invalid (or use --all).");
    var feeRate = 1m;
    if (Opt(o, "--feerate") is { } feeText && !CoinAmount.TryParseFeeRate(feeText, out feeRate))
        throw new WalletSpendException($"--feerate invalid (expected sat/vB in (0, {CoinAmount.MaxFeeRate}]).");

    await using var client = await Connect(o, account.Profile);
    await SyncWallet(doc, account, path, client);
    var cache = doc.Cache!; // set by SyncWallet just above

    // Source transactions of the UTXOs: needed for amounts and signing.
    var network = PalladiumNetworks.For(account.Profile.Kind);
    var transactions = new Dictionary<string, Transaction>();
    foreach (var txid in cache.Utxos.Select(u => u.Txid).Distinct())
        transactions[txid] = await client.GetVerifiedTransactionAsync(txid, network);

    var built = new TransactionFactory(account).Build(
        cache.Utxos, transactions, destination, amount, feeRate,
        cache.NextChangeIndex, cache.TipHeight, sendAll);

    Console.WriteLine(built.TxidIsFinal
        ? $"txid:  {built.Txid}"
        : "txid:  not final until signed (a legacy or wrapped input changes the txid when signed)");
    Console.WriteLine($"fee:   {CoinAmount.Format(built.Fee.Satoshi, account.Profile.CoinUnit)} " +
        $"({feeRate} sat/vB, {built.Transaction.GetVirtualSize()} vB)");

    if (!built.Signed)
    {
        if (account is MultisigAccount ms)
        {
            var signed = built.Psbt.Inputs.Count > 0 && built.Psbt.Inputs[0].PartialSigs.Count > 0;
            Console.WriteLine($"Multisig {ms.Threshold}-of-{ms.Cosigners.Count}: PSBT " +
                (signed ? "signed by this wallet; pass it to the other cosigners (sign-psbt), then combine-psbt:"
                        : "to be signed by the cosigners (sign-psbt), then combine-psbt:"));
        }
        else
            Console.WriteLine("Watch-only wallet: PSBT to sign offline (§6.5):");
        Console.WriteLine(built.Psbt.ToBase64());
        WritePsbtFile(built.Psbt, o);
        return 0;
    }

    if (!o.Contains("--broadcast"))
    {
        Console.WriteLine("Signed transaction (NOT broadcast, add --broadcast):");
        Console.WriteLine(built.ToHex());
        return 0;
    }

    return await BroadcastSigned(client, built.Transaction, network);
}

/// <summary>Prints the key a cosigner shares with the others: <c>[fingerprint/48'/746'/0'/2']Zpub…</c>.</summary>
static int CosignerKey(string words, string[] o)
{
    if (!Bip39.TryParse(words, out var mnemonic))
        throw new WalletSpendException("Invalid mnemonic (wrong words or checksum).");
    var profile = Profile(o);
    var accountIndex = 0;
    if (Opt(o, "--account") is { } accountText && (!int.TryParse(accountText, out accountIndex) || accountIndex < 0))
        throw new WalletSpendException("--account must be a non-negative number.");
    var root = ExtKey.CreateFromSeed(Bip39.ToSeed(mnemonic!, Opt(o, "--passphrase")));
    var kind = MultisigKind(o);
    var path = DerivationPaths.AccountPath(kind, profile, accountIndex);
    var cosigner = new Cosigner(root.Neuter().PubKey.GetHDFingerPrint(), path, root.Derive(path).Neuter());
    Console.WriteLine(cosigner.ToText(kind, profile));
    return 0;
}

/// <summary>
/// Prints a multisig wallet's configuration for other tools: the text file a signer registers (Coldcard, Sparrow), or
/// the descriptors with their checksums. <c>--out FILE</c> writes the text file instead of printing it.
/// </summary>
static int ExportMultisig(string[] o)
{
    var (_, account, _) = OpenWallet(o);
    if (account is not MultisigAccount multisig)
        throw new WalletSpendException("This wallet is not a multisig wallet.");
    var name = Opt(o, "--name") ?? "Palladium multisig";
    if (Opt(o, "--format") is "descriptor")
    {
        var (receive, change) = MultisigConfig.Descriptors(multisig);
        Console.WriteLine($"receive: {receive}");
        Console.WriteLine($"change:  {change}");
        return 0;
    }
    var text = MultisigConfig.ToText(multisig, name);
    if (Opt(o, "--out") is { } file)
    {
        File.WriteAllText(file, text);
        Console.WriteLine($"Configuration written to {file} (wallet ID {multisig.WalletId})");
    }
    else
        Console.Write(text);
    return 0;
}

/// <summary>P2SH-P2WSH when --wrapped is given, else native P2WSH.</summary>
static ScriptKind MultisigKind(string[] o) =>
    o.Contains("--wrapped") ? ScriptKind.WrappedSegwitMultisig : ScriptKind.NativeSegwitMultisig;

/// <summary>Every value of a repeated option (<c>--cosigner A --cosigner B</c>).</summary>
static List<string> OptAll(string[] options, string name)
{
    var values = new List<string>();
    for (var i = 0; i < options.Length - 1; i++)
        if (options[i] == name)
            values.Add(options[i + 1]);
    return values;
}

/// <summary>Creates an M-of-N P2WSH multisig wallet from the cosigners' keys; with --mnemonic it can sign its own share.</summary>
static int CreateMultisig(string[] o)
{
    var profile = Profile(o);
    var threshold = 0;
    if (Opt(o, "--config") is null && !int.TryParse(Opt(o, "--threshold"), out threshold))
        throw new WalletSpendException("--threshold M missing (signatures required).");
    var cosigners = OptAll(o, "--cosigner");
    ScriptKind? configKind = null;
    if (Opt(o, "--config") is { } configFile)
    {
        // The text file other tools export: it carries the policy, the format and the keys, and replaces those options.
        try
        {
            var (m, kind, keys) = MultisigConfig.Parse(File.ReadAllText(configFile), profile);
            (threshold, configKind, cosigners) = (m, kind, [.. keys]);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new WalletSpendException($"Cannot use the configuration file: {ex.Message}");
        }
    }
    if (cosigners.Count == 0)
        throw new WalletSpendException("--cosigner KEY missing (give all N keys, this wallet's own included; see 'cosigner-key').");
    (WalletDocument doc, MultisigAccount account) created;
    try
    {
        created = WalletLoader.NewMultisig(threshold, cosigners, Opt(o, "--mnemonic"), Opt(o, "--passphrase"), profile, configKind ?? MultisigKind(o));
    }
    catch (InvalidDataException ex)
    {
        throw new WalletSpendException(ex.Message);
    }
    // The wallet cannot authenticate the cosigners' keys by itself: a key altered on its way would build another
    // wallet. The wallet ID covers every key and the threshold, so the cosigners compare it, and --expect-id makes
    // the comparison part of the command: the wallet is not written unless it is the one the others see.
    var walletId = created.account.WalletId;
    var expected = Opt(o, "--expect-id");
    if (expected is not null && !string.Equals(expected.Trim(), walletId, StringComparison.OrdinalIgnoreCase))
        throw new WalletSpendException(
            $"The wallet ID is {walletId}, not {expected.Trim()}: the cosigner keys given here are not the ones the other " +
            "cosigner saw. Nothing was written; check each key and its fingerprint with its owner.");

    var path = WalletPath(o, profile);
    WalletStore.Save(created.doc, path, CliPassword.ForNewWallet(o));
    Console.WriteLine($"Multisig wallet {created.account.Threshold}-of-{created.account.Cosigners.Count} saved to {path}" +
        (created.account.IsWatchOnly ? " (holds no key: cannot sign)" : ""));
    Console.WriteLine($"Wallet ID: {walletId}");
    Console.WriteLine(expected is not null
        ? "The wallet ID matches the one you were given."
        : "NOT VERIFIED: compare the wallet ID with every other cosigner (or give it to them to pass --expect-id) before receiving funds.");
    Console.WriteLine($"First address: {created.account.GetReceiveAddress(0)}");
    return 0;
}

/// <summary>
/// Combines the cosigners' copies of one PSBT. The PSBTs are arguments (base64) or <c>-</c> for standard input,
/// one per line. When the result holds enough signatures it prints the final transaction, and
/// <c>--broadcast</c> sends it.
/// </summary>
static async Task<int> CombinePsbt(string[] args)
{
    var network = PalladiumNetworks.For(Profile(args).Kind);
    var inputs = args.TakeWhile(a => !a.StartsWith("--")).ToList();
    if (inputs.Contains("-"))
        inputs = [.. inputs.Where(a => a != "-"), .. Console.In.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    if (inputs.Count < 2)
        throw new WalletSpendException("combine-psbt needs at least two PSBTs (base64, or '-' for one per line on stdin).");
    var psbts = inputs.Select(text => ReadPsbt(text, network)).ToList();

    var (combined, final) = PsbtSigner.Combine(psbts);
    Console.WriteLine("PSBT:");
    Console.WriteLine(combined.ToBase64());
    WritePsbtFile(combined, args);
    if (final is null)
    {
        Console.WriteLine("Incomplete: not enough signatures yet.");
        return args.Contains("--broadcast") ? 1 : 0;
    }
    Console.WriteLine($"txid:  {final.GetHash()}");
    Console.WriteLine("Final transaction:");
    Console.WriteLine(final.ToHex());
    if (!args.Contains("--broadcast"))
        return 0;
    await using var client = await Connect(args, Profile(args));
    return await BroadcastSigned(client, final, network);
}

/// <summary>Lists the cached coins with their state (offline: run <c>sync</c> to refresh them).</summary>
static int Utxos(string[] o)
{
    var (doc, account, _) = OpenWallet(o);
    if (doc.Cache is not { } cache)
        throw new WalletSpendException("The wallet has never been synchronized: run 'sync' first.");
    var profile = account.Profile;
    Console.WriteLine($"Coins at height {cache.TipHeight} (from the last sync):");
    foreach (var u in cache.Utxos.OrderBy(u => u.Txid, StringComparer.Ordinal).ThenBy(u => u.Vout))
    {
        var state = u.Frozen ? "frozen"
            : u.Height <= 0 ? "unconfirmed"
            : !u.Verified ? "awaiting SPV proof"
            : u.Confirmations(cache.TipHeight) < u.RequiredConfirmations(profile) ? "not yet spendable"
            : "spendable";
        Console.WriteLine($"  {u.Txid}:{u.Vout}  {CoinAmount.Format(u.ValueSats, profile.CoinUnit),18}  " +
            $"{u.Address}  {u.Confirmations(cache.TipHeight)} conf  {state}");
    }
    var frozenSats = cache.Utxos.Where(u => u.Frozen).Sum(u => u.ValueSats);
    Console.WriteLine($"{cache.Utxos.Count} coin(s), {CoinAmount.Format(cache.SpendableSats, profile.CoinUnit)} spendable" +
        (frozenSats != 0 ? $", {CoinAmount.Format(frozenSats, profile.CoinUnit)} frozen" : ""));
    return 0;
}

/// <summary>Freezes or unfreezes one coin (<c>txid:vout</c>): a frozen coin is kept out of every spend.</summary>
static int Freeze(string outpoint, string[] o, bool frozen)
{
    var (doc, account, path) = OpenWallet(o);
    var parts = outpoint.Split(':');
    if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)
        || !int.TryParse(parts[1], out var vout) || vout < 0)
        throw new WalletSpendException("Expected a coin as <txid>:<vout> (see 'utxos').");
    var txid = parts[0].ToLowerInvariant();
    if (doc.Cache is not { } cache || !CoinControl.SetFrozen(cache, account.Profile, txid, vout, frozen))
        throw new WalletSpendException($"{txid}:{vout} is not a coin of this wallet (run 'sync', then 'utxos').");
    WalletStore.Save(doc, path, CliPassword.Current);
    Console.WriteLine($"{(frozen ? "Frozen" : "Unfrozen")}: {txid}:{vout}");
    return 0;
}

/// <summary>
/// Broadcasts a signed transaction. When the server replies with a txid other than the one computed
/// locally, the reply is not trusted either way: the server is asked for the transaction itself. If it
/// holds it, the transaction is in the network (reported, exit 0); if not, the signed hex is printed so
/// it can be sent again (the same bytes are harmless to repeat) and the exit code is 1.
/// </summary>
static async Task<int> BroadcastSigned(ElectrumClient client, Transaction tx, Network network)
{
    var hex = tx.ToHex();
    var txid = tx.GetHash().ToString();
    try
    {
        Console.WriteLine($"Broadcast: {await client.BroadcastVerifiedAsync(hex, txid)}");
        return 0;
    }
    catch (BroadcastTxidMismatchException mismatch)
    {
        bool inNetwork;
        try
        {
            inNetwork = await client.HasTransactionAsync(txid, network);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: the server replied with txid {mismatch.ReportedTxid} and the network " +
                $"could not be checked ({ex.Message}). The signed transaction {txid} is:");
            Console.WriteLine(hex);
            return 1;
        }

        if (inNetwork)
        {
            Console.WriteLine($"Broadcast: {txid} is in the network. The server replied with txid " +
                $"{mismatch.ReportedTxid}: report it to the server operator.");
            return 0;
        }
        Console.Error.WriteLine($"Error: the server replied with txid {mismatch.ReportedTxid} and {txid} " +
            "is not in the network. The signed transaction is printed below: send it again (same bytes).");
        Console.WriteLine(hex);
        return 1;
    }
}

/// <summary>
/// A PSBT given as an argument: the path of a file (binary <c>.psbt</c>, or one holding base64 or hex), or the text
/// itself (base64 or hex), or <c>-</c> for standard input.
/// </summary>
static PSBT ReadPsbt(string arg, Network network)
{
    try
    {
        if (arg == "-")
            return PsbtFormat.Parse(Console.In.ReadToEnd(), network);
        if (arg.Length < 1000 && File.Exists(arg))
            return PsbtFormat.Parse(File.ReadAllBytes(arg), network);
        return PsbtFormat.Parse(arg, network);
    }
    catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
    {
        throw new WalletSpendException(ex is FormatException
            ? "Not a valid PSBT (a .psbt file, or base64 or hex text, for this network expected)."
            : $"Cannot read the PSBT file: {ex.Message}");
    }
}

/// <summary>With <c>--psbt-out FILE</c> also writes the PSBT as a binary <c>.psbt</c> file, which signers read from an SD card.</summary>
static void WritePsbtFile(PSBT psbt, string[] o)
{
    if (Opt(o, "--psbt-out") is not { } file)
        return;
    File.WriteAllBytes(file, PsbtFormat.ToBinary(psbt));
    Console.WriteLine($"PSBT written to {file}");
}

static async Task<int> SignPsbt(string text, string[] o)
{
    var (doc, account, _) = OpenWallet(o);
    var network = PalladiumNetworks.For(account.Profile.Kind);
    var psbt = ReadPsbt(text, network);

    var scan = Opt(o, "--scan") is { } scanText
        ? int.TryParse(scanText, out var scanCount) && scanCount is > 0 and <= 100_000
            ? scanCount
            : throw new WalletSpendException("--scan must be a number of addresses in 1-100000.")
        : Math.Max(PsbtSigner.DefaultScan,
            Math.Max(doc.Cache?.NextReceiveIndex ?? 0, doc.Cache?.NextChangeIndex ?? 0) + doc.GapLimit);
    var result = PsbtSigner.Sign(psbt, account, scan);

    var unit = account.Profile.CoinUnit;
    Console.WriteLine($"Inputs ({result.Inputs.Count}):");
    foreach (var i in result.Inputs)
        Console.WriteLine($"  {i.PrevTxid}:{i.PrevIndex}  "
            + (i.AmountSats is { } a ? CoinAmount.Format(a, unit) : "amount unknown")
            + (i.AmountSats is not null && !i.AmountVerified ? "  (amount not verified: no previous transaction)" : "")
            + (i.Signed ? "  signed" : "  NOT signed"));
    Console.WriteLine($"Outputs ({result.Outputs.Count}):");
    foreach (var out_ in result.Outputs)
        Console.WriteLine($"  {out_.Address ?? "(non-standard script)"}  {CoinAmount.Format(out_.AmountSats, unit)}"
            + (out_.IsMine ? "  (yours: change)" : ""));
    Console.WriteLine(result.FeeSats is { } fee
        ? $"Fee: {CoinAmount.Format(fee, unit)}"
        : "Fee: unknown (an input amount is missing)");

    Console.WriteLine("PSBT:");
    Console.WriteLine(result.Psbt.ToBase64());
    WritePsbtFile(result.Psbt, o);
    if (result.FinalTransaction is not { } final)
    {
        Console.WriteLine("Incomplete: not every input could be signed by this wallet.");
        return o.Contains("--broadcast") ? 1 : 0;
    }
    Console.WriteLine($"txid:  {final.GetHash()}");
    Console.WriteLine("Final transaction:");
    Console.WriteLine(final.ToHex());
    if (!o.Contains("--broadcast"))
        return 0;

    await using var client = await Connect(o, account.Profile);
    return await BroadcastSigned(client, final, network);
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

// An unknown value is an error, never a silent default: a typo must not create a wallet of another type.
static ScriptKind Kind(string[] o) => Opt(o, "--kind") switch
{
    null or "segwit" => ScriptKind.NativeSegwit,
    "legacy" => ScriptKind.Legacy,
    "wrapped" => ScriptKind.WrappedSegwit,
    "taproot" => ScriptKind.Taproot,
    var other => throw new WalletSpendException($"--kind '{other}' is not one of segwit, wrapped, legacy, taproot."),
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
        ? HdAccount.FromSeed(Bip39.ToSeed(mnemonic!, passphrase), kind, profile, ParsePath(path))
        : HdAccount.FromMnemonic(mnemonic!, passphrase, kind, profile);
}

static int SaveWallet(string words, string[] o)
{
    var profile = Profile(o);
    var (doc, account) = WalletLoader.NewFromMnemonic(
        words, Opt(o, "--passphrase"), Kind(o), profile,
        Opt(o, "--path") is { } p ? ParsePath(p) : null);
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
    WalletDocument doc;
    try { doc = WalletStore.Load(path, CliPassword.ForOpening(o, path)); }
    catch (System.Text.Json.JsonException)
    {
        throw new WalletSpendException($"{path} is not a valid wallet file.");
    }
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
        port = parts.Length > 1
            ? int.TryParse(parts[1], out var p) && p is > 0 and <= 65535
                ? p
                : throw new WalletSpendException("--server must be host[:port] with a port in 1-65535.")
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

/// <summary>Parses a derivation path option, with a clean error instead of a parser exception.</summary>
static KeyPath ParsePath(string text) =>
    DerivationPaths.TryParse(text, out var keyPath) && keyPath is not null
        ? keyPath
        : throw new WalletSpendException("--path is not a valid derivation path (e.g. m/84'/746'/0').");

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
          create        [--words 12|24] [--kind segwit|wrapped|legacy|taproot] [--net mainnet|testnet|regtest]
                        [--passphrase W] [--no-encryption] [--file PATH]
          restore       "<mnemonic>" [same options as create] [--path m/...]
          restore-xpub  <slip132 xpub> [--net ...] [--file PATH] [--fingerprint HEX8 --path m/...]   (watch-only)
          restore-wif   <wif1,wif2,...|-> [--kind segwit|wrapped|legacy|taproot] [--net ...] [--file PATH]
                        import WIF private keys (spendable, no seed); '-' reads them from stdin
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
          utxos         [--net ...] [--file PATH]     list the coins of the last sync (offline)
          freeze        <txid>:<vout> [--net ...] [--file PATH]     keep a coin out of every spend (offline)
          unfreeze      <txid>:<vout> [--net ...] [--file PATH]
          create-multisig --threshold M --cosigner KEY (repeat N times, own included) [--wrapped] [--config FILE] [--mnemonic "<words>"]
                        [--passphrase W] [--expect-id ID] [--net ...] [--file PATH]   M-of-N wallet (BIP48, sortedmulti): P2WSH, or P2SH-P2WSH with --wrapped
          cosigner-key  "<mnemonic>" [--wrapped] [--account N] [--passphrase W] [--net ...]   the key to hand to the other cosigners
          export-multisig [--format descriptor] [--name N] [--out FILE] [--net ...] [--file PATH]   the configuration for a signer or another wallet
          combine-psbt  <base64|-> <base64> ... [--broadcast --server host[:port] [--ssl]] [--net ...]
          sign-psbt     <base64|hex|file.psbt|-> [--psbt-out FILE] [--scan N] [--broadcast --server host[:port] [--ssl]] [--net ...] [--file PATH]
                        sign a PSBT with this wallet's keys (offline half of the watch-only flow)
          servers       [--discover] [--server host[:port]] [--ssl] [--net ...]
          reset-certs   [--net ...]

        Tools:
          newseed       [--words 12|24]
          addresses     "<mnemonic>" [--kind ...] [--net ...] [--count N] [--passphrase W] [--path m/...]
        """);
    return 1;
}
