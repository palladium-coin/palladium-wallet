using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Net;

namespace PalladiumWallet.Core.Spv;

/// <summary>
/// Opens the "witness" connections used to cross-check recent headers (ADR 0042): servers
/// other than the primary one, taken only from the hardcoded bootstrap list (never from the peers
/// a server announces, which a hostile server could fill with its own), each verified to be on the
/// profile's chain before use.
/// </summary>
public static class WitnessConnector
{
    /// <summary>One witness is enough to turn a single lying server into a detectable disagreement.</summary>
    public const int DefaultMax = 1;

    /// <summary>
    /// Connects up to <paramref name="max"/> witnesses among <paramref name="candidates"/>,
    /// skipping the primary server. Unreachable, wrong-chain or otherwise failing candidates are
    /// skipped; an empty result means no cross-check is possible this time.
    /// </summary>
    public static async Task<IReadOnlyList<ElectrumClient>> ConnectAsync(ChainProfile profile,
        IEnumerable<ServerEndpoint> candidates, string primaryHost, int primaryPort, bool useSsl,
        CertificatePinStore? pins, int max = DefaultMax, CancellationToken ct = default)
    {
        var witnesses = new List<ElectrumClient>();
        foreach (var endpoint in candidates)
        {
            if (witnesses.Count >= max)
                break;
            var port = useSsl ? endpoint.SslPort : endpoint.TcpPort;
            if (string.Equals(endpoint.Host, primaryHost, StringComparison.OrdinalIgnoreCase) && port == primaryPort)
                continue;
            ElectrumClient? client = null;
            try
            {
                client = await ElectrumClient.ConnectAsync(endpoint.Host, port, useSsl, pins, ct);
                await client.VerifyChainAsync(profile, ct);
                witnesses.Add(client);
            }
            catch (OperationCanceledException)
            {
                if (client is not null) await client.DisposeAsync();
                throw;
            }
            catch
            {
                // Best effort: this candidate is unusable, try the next one.
                if (client is not null) await client.DisposeAsync();
            }
        }
        return witnesses;
    }
}
