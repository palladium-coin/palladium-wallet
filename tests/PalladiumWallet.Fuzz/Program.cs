using System.Text;
using PalladiumWallet.Fuzz;

// Fuzzing entry point. One binary, one target per process (fuzzers want a
// single deterministic execution path). Modes:
//
//   <target> --afl                 run under afl-fuzz (instrumented Core, see fuzz.sh)
//   <target> --libfuzzer           run under libfuzzer-dotnet
//   <target> --random N [seed]     built-in dumb fuzzer: N corpus mutations (no tooling needed)
//   <target> <file|dir> ...        replay saved inputs (used by the regression test / triage)
//   --make-seeds <dir>             regenerate the seed corpus
//
// A "crash" is any exception escaping the target: each target already swallows
// the exception types that are part of the parser's documented contract.

if (args.Length >= 2 && args[0] == "--make-seeds")
{
    SeedCorpus.WriteAll(args[1]);
    Console.WriteLine($"Seed corpus written to {args[1]}");
    return 0;
}

if (args.Length < 2 || !FuzzTargets.All.TryGetValue(args[0], out var target))
{
    Console.Error.WriteLine("Usage: PalladiumWallet.Fuzz <target> --afl | --libfuzzer | --random N [seed] | <file|dir>...");
    Console.Error.WriteLine("       PalladiumWallet.Fuzz --make-seeds <dir>");
    Console.Error.WriteLine($"Targets: {string.Join(' ', FuzzTargets.All.Keys)}");
    return 2;
}

switch (args[1])
{
    case "--afl":
        SharpFuzz.Fuzzer.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            target(ms.ToArray());
        });
        return 0;

    case "--libfuzzer":
        SharpFuzz.Fuzzer.LibFuzzer.Run(span => target(span.ToArray()));
        return 0;

    case "--random":
    {
        var iterations = args.Length > 2 ? int.Parse(args[2]) : 100_000;
        var seed = args.Length > 3 ? ulong.Parse(args[3]) : 0xF022_5EEDUL;
        return RandomFuzz.Run(args[0], target, iterations, seed);
    }

    default:
    {
        var failures = 0;
        foreach (var path in args[1..])
        foreach (var file in Directory.Exists(path)
            ? Directory.EnumerateFiles(path).OrderBy(f => f, StringComparer.Ordinal)
            : (IEnumerable<string>)[path])
        {
            var data = File.ReadAllBytes(file);
            try
            {
                target(data);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"CRASH {args[0]} on {file}:");
                Console.Error.WriteLine(ex);
            }
        }
        return failures == 0 ? 0 : 1;
    }
}

/// <summary>
/// Minimal corpus-mutation fuzzer for smoke runs and CI: not coverage-guided,
/// but catches shallow contract violations without afl++/libFuzzer installed.
/// Deterministic for a given seed; on crash it saves the input for replay.
/// </summary>
internal static class RandomFuzz
{
    public static int Run(string name, Action<byte[]> target, int iterations, ulong seed)
    {
        var corpus = SeedCorpus.For(name);
        var rng = seed;
        ulong Next() { rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17; return rng; }

        for (var i = 0; i < iterations; i++)
        {
            var data = (byte[])corpus[(int)(Next() % (ulong)corpus.Count)].Clone();
            var mutations = 1 + (int)(Next() % 8);
            for (var m = 0; m < mutations && data.Length > 0; m++)
                switch (Next() % 4)
                {
                    case 0: data[Next() % (ulong)data.Length] = (byte)Next(); break;
                    case 1: data[Next() % (ulong)data.Length] ^= (byte)(1 << (int)(Next() % 8)); break;
                    case 2: Array.Resize(ref data, (int)(Next() % 512) + 1); break;
                    case 3: data = [.. data, .. data[..(int)(Next() % (ulong)data.Length)]]; break;
                }
            try
            {
                target(data);
            }
            catch (Exception ex)
            {
                var crashFile = $"crash-{name}-{i}.bin";
                File.WriteAllBytes(crashFile, data);
                Console.Error.WriteLine(
                    $"CRASH {name} at iteration {i}: {ex.GetType().Name}: {ex.Message} (input saved to {crashFile})");
                return 1;
            }
        }
        Console.WriteLine($"{name}: {iterations} random mutations, no contract violations");
        return 0;
    }
}

/// <summary>
/// Seed inputs per target: small valid (or near-valid) examples that give the
/// mutation engines a meaningful starting shape. Regenerate with --make-seeds.
/// </summary>
internal static class SeedCorpus
{
    private static readonly Dictionary<string, (string Name, byte[] Data)[]> Seeds = new()
    {
        ["header"] =
        [
            ("genesis", Convert.FromHexString(
                "0100000000000000000000000000000000000000000000000000000000000000000000003ba3edfd7a7b12b27ac72c3e67768f617fc81bc3888a51323a9fb8aa4b1e5e4a29ab5f49ffff001d1dac2b7c")),
            ("genesis-hex", Encoding.UTF8.GetBytes(
                "0100000000000000000000000000000000000000000000000000000000000000000000003ba3edfd7a7b12b27ac72c3e67768f617fc81bc3888a51323a9fb8aa4b1e5e4a29ab5f49ffff001d1dac2b7c")),
            ("short", [0x01, 0x00, 0x00, 0x00]),
        ],
        ["merkle"] =
        [
            ("one-level", [.. new byte[32], .. BitConverter.GetBytes(1), .. new byte[32], .. Enumerable.Repeat((byte)0xAA, 32)]),
            ("no-branch", [.. Enumerable.Repeat((byte)0x11, 32), .. BitConverter.GetBytes(0), .. Enumerable.Repeat((byte)0x11, 32)]),
        ],
        ["slip132"] =
        [
            ("zpub", Encoding.UTF8.GetBytes(
                "zpub6rFR7y4Q2AijBEqTUquhVz398htDFrtymD9xYYfG1m4wAcvPhXNfE3EfH1r1ADqtfSdVCToUG868RvUUkgDKf31mGDtKsAYz2oz2AGutZYs")),
            ("xpub-prefix", Encoding.UTF8.GetBytes("xpub661MyMwAqRbcF")),
            ("garbage", Encoding.UTF8.GetBytes("not-a-key")),
        ],
        ["bip39"] =
        [
            ("english-12", Encoding.UTF8.GetBytes(
                "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")),
            ("spanish-word", Encoding.UTF8.GetBytes("ábaco ábaco ábaco")),
            ("empty", []),
            // Regression: NBitcoin AutoDetect → "Unknown" language → NotSupportedException
            // escaped TryParse (fixed by catching it).
            ("regression-notsupported", Convert.FromHexString(
                "c3a162ca636f20c3a16261636f30c3a16261636fc3a162ca")),
        ],
        ["address"] =
        [
            ("bech32", Encoding.UTF8.GetBytes("plm1qdq3gu2zvg9lyr8gxd6yln4wavc5tlp8prmvfay")),
            ("legacy-ish", Encoding.UTF8.GetBytes("PB6q3PB6q3PB6q3PB6q3PB6q3PB6q3PB6q")),
            ("garbage", Encoding.UTF8.GetBytes("hello world")),
        ],
        ["coinamount"] =
        [
            ("plain", Encoding.UTF8.GetBytes("1.50000000")),
            ("comma", Encoding.UTF8.GetBytes("0,001")),
            ("huge", Encoding.UTF8.GetBytes("79228162514264337593543950335")),
        ],
        ["walletdoc"] =
        [
            ("minimal", Encoding.UTF8.GetBytes(
                """{"Version":1,"Network":"mainnet","ScriptKind":"NativeSegwit"}""")),
            ("bad-version", Encoding.UTF8.GetBytes("""{"Version":99}""")),
            ("not-json", Encoding.UTF8.GetBytes("hello")),
        ],
        ["encfile"] =
        [
            // Well-formed container with a tiny iteration count so every replay and
            // mutated descendant skips the full PBKDF2 cost (decryption then fails
            // with the typed WrongPasswordException — exactly the contract under test).
            ("container", Encoding.UTF8.GetBytes(
                """{"Format":"plm-wallet-aesgcm-v1","Iterations":2,"Salt":"AAAAAAAAAAAAAAAAAAAAAA==","Nonce":"AAAAAAAAAAAAAAAA","Tag":"AAAAAAAAAAAAAAAAAAAAAA==","Data":"AAAA"}""")),
            ("wrong-format", Encoding.UTF8.GetBytes(
                """{"Format":"other","Iterations":2,"Salt":"","Nonce":"","Tag":"","Data":""}""")),
            ("not-json", Encoding.UTF8.GetBytes("hello")),
        ],
        ["headerrange"] =
        [
            ("two-headers", [.. BitConverter.GetBytes(2), .. BitConverter.GetBytes(2),
                .. Encoding.UTF8.GetBytes(new string('0', 320))]),
            ("count-zero", [.. BitConverter.GetBytes(0), .. BitConverter.GetBytes(10), .. Encoding.UTF8.GetBytes("")]),
            ("more-than-requested", [.. BitConverter.GetBytes(3), .. BitConverter.GetBytes(2),
                .. Encoding.UTF8.GetBytes(new string('0', 480))]),
            ("negative-count", [.. BitConverter.GetBytes(-1), .. BitConverter.GetBytes(5), .. Encoding.UTF8.GetBytes("00")]),
            ("hex-too-short", [.. BitConverter.GetBytes(1), .. BitConverter.GetBytes(1),
                .. Encoding.UTF8.GetBytes(new string('0', 159))]),
            // Regression-style boundary: a count whose count * 160 overflows int must be rejected, not wrap into a match.
            ("count-overflow", [.. BitConverter.GetBytes(int.MaxValue), .. BitConverter.GetBytes(2000),
                .. Encoding.UTF8.GetBytes("00")]),
        ],
        ["cosignerkey"] =
        [
            ("valid", Encoding.UTF8.GetBytes(
                "[73c5da0a/48'/1'/0'/2']Vpub5n95dMZrDHj6SeBgJ1oz4Fae2N2eJNuWK3VTKDb2dzGpMFLUHLmtyDfen7AaQxwQ5mZnMyXdVrkEaoMLVTH8FmVBRVWPGFYWhmtDUGehGmq")),
            ("h-markers", Encoding.UTF8.GetBytes(
                "[73c5da0a/48h/1h/0h/2h]Vpub5n95dMZrDHj6SeBgJ1oz4Fae2N2eJNuWK3VTKDb2dzGpMFLUHLmtyDfen7AaQxwQ5mZnMyXdVrkEaoMLVTH8FmVBRVWPGFYWhmtDUGehGmq")),
            ("depth-mismatch", Encoding.UTF8.GetBytes(
                "[73c5da0a/48'/1'/0']Vpub5n95dMZrDHj6SeBgJ1oz4Fae2N2eJNuWK3VTKDb2dzGpMFLUHLmtyDfen7AaQxwQ5mZnMyXdVrkEaoMLVTH8FmVBRVWPGFYWhmtDUGehGmq")),
            ("huge-index", Encoding.UTF8.GetBytes(
                "[73c5da0a/99999999999/1'/0'/2']Vpub5n95dMZrDHj6SeBgJ1oz4Fae2N2eJNuWK3VTKDb2dzGpMFLUHLmtyDfen7AaQxwQ5mZnMyXdVrkEaoMLVTH8FmVBRVWPGFYWhmtDUGehGmq")),
            ("no-brackets", Encoding.UTF8.GetBytes("73c5da0a/48'/1'/0'/2'Vpub5n95")),
            ("empty", []),
        ],
        ["peers"] =
        [
            ("typical", Encoding.UTF8.GetBytes(
                """[["1.2.3.4","peer.example",["v1.4.2","p10","t50001","s50002"]]]""")),
            ("odd-shapes", Encoding.UTF8.GetBytes("""[[1,2,3],{"a":1},"x",[["h","n",[42,"t"]]]]""")),
            // Regression: raw invalid-UTF-8 byte inside a JSON string parses but cannot
            // be transcoded by GetString() → InvalidOperationException (fixed in AsString).
            ("regression-bad-utf8", Convert.FromHexString(
                "5b5b22312e322e332e34222c22706565722e6578616d706c65222c5b2276312e342e32222c22703130222c227435303030" +
                "31222c22873530303032225d5d5d")),
        ],
    };

    public static IReadOnlyList<byte[]> For(string target) =>
        Seeds[target].Select(s => s.Data).ToList();

    public static void WriteAll(string root)
    {
        foreach (var (target, seeds) in Seeds)
        {
            var dir = Path.Combine(root, target);
            Directory.CreateDirectory(dir);
            foreach (var (name, data) in seeds)
                File.WriteAllBytes(Path.Combine(dir, name + ".bin"), data);
        }
    }
}
