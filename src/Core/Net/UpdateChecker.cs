using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PalladiumWallet.Core.Net;

/// <summary>A GitHub release newer than the running app.</summary>
public sealed record LatestRelease(string Tag, string HtmlUrl);

/// <summary>
/// Checks the latest GitHub release for the project against the running app version.
/// Best-effort only: any network/parse failure or an up-to-date app both resolve to null,
/// so callers never need to distinguish "no update" from "couldn't check".
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/palladium-coin/palladium-wallet/releases/latest";

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }

    public static Task<LatestRelease?> CheckAsync(string currentVersion, CancellationToken ct = default) =>
        CheckAsync(currentVersion, handler: null, ct);

    /// <summary>Test seam: <paramref name="handler"/> replaces the real HTTP transport.</summary>
    internal static async Task<LatestRelease?> CheckAsync(string currentVersion,
        HttpMessageHandler? handler, CancellationToken ct = default)
    {
        try
        {
            using var http = handler is null
                ? new HttpClient()
                : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PalladiumWallet");
            using var response = await http.GetAsync(ReleasesApiUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var release = await response.Content
                .ReadFromJsonAsync<GitHubReleaseResponse>(ct)
                .ConfigureAwait(false);
            if (release?.TagName is not { Length: > 0 } tag) return null;

            if (!TryParse(tag, out var remote) || !TryParse(currentVersion, out var local))
                return null;
            if (remote <= local) return null;

            return new LatestRelease(tag, release.HtmlUrl ?? $"https://github.com/palladium-coin/palladium-wallet/releases/tag/{tag}");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses "v1.2.3" / "1.2.3-beta" style tags into a comparable <see cref="Version"/>.</summary>
    internal static bool TryParse(string raw, out Version version)
    {
        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out version!);
    }
}
