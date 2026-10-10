using System.Net.Http;
using System.Text.RegularExpressions;
using Lustral.Core.Mods;

namespace Lustral.Core.Updates;

/// <summary>A version offered by an update source.</summary>
/// <param name="Sha256">The download's SHA-256 hash (hexadecimal), if the source provides one: the GitHub asset digest or the feed's <c>sha256</c>.</param>
public sealed record UpdateRelease(SemanticVersion Version, Uri Download, string? Sha256, long? Size, bool Prerelease);

/// <summary>Reads the versions offered by an <see cref="UpdateSource"/>.</summary>
internal static class UpdateSources
{
    private const int MaxListingBytes = 4 * 1024 * 1024;

    /// <summary>Returns the releases, newest first. Releases without a recognized version or without exactly one package to download are excluded.</summary>
    /// <param name="backend">The game's backend, "mono" or "il2cpp". Mods built for both backends publish a package for each, named after the backend.</param>
    /// <exception cref="HttpRequestException">The source could not be reached or responded with an error.</exception>
    /// <exception cref="FormatException">The response does not have the expected format.</exception>
    public static async Task<IReadOnlyList<UpdateRelease>> GetAsync(UpdateSource source, string modId, string? backend, HttpClient http, CancellationToken cancellation)
    {
        var releases = source.GitHubRepository is { } repository
            ? GitHub(Json.Parse(await GetTextAsync(http, new Uri($"https://api.github.com/repos/{repository}/releases?per_page=20"), cancellation)), source.Asset, modId, backend)
            : Feed(Json.Parse(await GetTextAsync(http, source.Feed!, cancellation)), source.Feed!, backend);
        return releases.OrderByDescending(r => r.Version).ToList();
    }

    // The GitHub releases API returns an array of { tag_name, draft, prerelease, assets: [{ name, browser_download_url, size, digest }] }.
    internal static IEnumerable<UpdateRelease> GitHub(object? listing, string? assetPattern, string modId, string? backend = null)
    {
        if (listing is not List<object?> releases)
            throw new FormatException("the GitHub response is not a list of releases");
        var pattern = assetPattern is null ? null : Glob(assetPattern);
        foreach (var item in releases)
        {
            var release = Json.Object(item);
            if (release is null || Json.Bool(release, "draft") || !TryVersion(Json.String(release, "tag_name"), out var version))
                continue;
            var assets = Json.Array(release.TryGetValue("assets", out var list) ? list : null).Select(Json.Object).OfType<Dictionary<string, object?>>().ToList();
            var asset = pattern is not null
                ? ForBackend(assets.Where(a => pattern.IsMatch(Json.String(a, "name") ?? "")).ToList(), backend).FirstOrDefault()
                : PickZip(assets, modId, backend);
            if (asset is null || !Uri.TryCreate(Json.String(asset, "browser_download_url"), UriKind.Absolute, out var download))
                continue;
            var digest = Json.String(asset, "digest");
            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7) : null;
            yield return new UpdateRelease(version, download, sha256, Json.Number(asset, "size"), Json.Bool(release, "prerelease"));
        }
    }

    // Selects the release's only .zip file, or the one whose name contains the mod's ID. For mods with packages for both
    // backends (<id>-<version>-mono.zip and -il2cpp.zip), selects the package for the game's backend.
    private static Dictionary<string, object?>? PickZip(List<Dictionary<string, object?>> assets, string modId, string? backend)
    {
        var zips = ForBackend(assets.Where(a => (Json.String(a, "name") ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList(), backend);
        if (zips.Count <= 1)
            return zips.FirstOrDefault();
        var named = zips.Where(a => (Json.String(a, "name") ?? "").IndexOf(modId, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static readonly string[] Backends = ["mono", "il2cpp"];

    // Excludes files named after the other backend; if any file is named after the game's backend, keeps only those.
    private static List<Dictionary<string, object?>> ForBackend(List<Dictionary<string, object?>> assets, string? backend)
    {
        if (backend is null)
            return assets;
        var others = Backends.Where(b => !string.Equals(b, backend, StringComparison.OrdinalIgnoreCase)).ToList();
        var fitting = assets.Where(a => !others.Any(other => NamesBackend(Json.String(a, "name"), other))).ToList();
        var named = fitting.Where(a => NamesBackend(Json.String(a, "name"), backend)).ToList();
        return named.Count > 0 ? named : fitting;
    }

    // Matches the backend as a separate word in the name, as in "betterhud-1.2.0-il2cpp.zip" or "BetterHud_Mono.zip".
    private static bool NamesBackend(string? name, string backend) =>
        name is not null && Regex.IsMatch(name, $@"(^|[-_. ]){backend}([-_. ]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Feed format: { "releases": [{ "version": "1.2.0", "url": "https://...zip", "sha256": "...", "size": 123, "prerelease": false }] }.
    // Relative URLs are resolved against the feed's address. Mods built for both backends list a package per backend for
    // each version, with a "backend" value of "mono" or "il2cpp".
    internal static IEnumerable<UpdateRelease> Feed(object? document, Uri feed, string? backend = null)
    {
        var releases = Json.Object(document) is { } root && root.TryGetValue("releases", out var list) ? list as List<object?> : null;
        if (releases is null)
            throw new FormatException("the feed has no \"releases\" list");
        foreach (var item in releases)
        {
            var release = Json.Object(item);
            if (release is null || !TryVersion(Json.String(release, "version"), out var version) || Json.String(release, "url") is not { } url)
                continue;
            if (backend is not null && Json.String(release, "backend") is { } only && !string.Equals(only, backend, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(feed, url, out var download))
                continue;
            yield return new UpdateRelease(version, download, Json.String(release, "sha256"), Json.Number(release, "size"), Json.Bool(release, "prerelease"));
        }
    }

    private static async Task<string> GetTextAsync(HttpClient http, Uri address, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        CheckAddress(response, address);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{address.Host} responded with {(int)response.StatusCode} {response.ReasonPhrase}");
        if (response.Content.Headers.ContentLength > MaxListingBytes)
            throw new FormatException($"the response from {address} is too large for a list of releases");
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return text.Length <= MaxListingBytes ? text : throw new FormatException($"the response from {address} is too large for a list of releases");
    }

    /// <summary>Rejects a response that was redirected to an address Lustral does not download from.</summary>
    public static void CheckAddress(HttpResponseMessage response, Uri requested)
    {
        var final = response.RequestMessage?.RequestUri ?? requested;
        if (!UpdateSource.IsAllowed(final))
            throw new HttpRequestException($"{requested} redirected to {final}, which does not use HTTPS");
    }

    // For example "v1.2.0", "1.2" or "V2.0.0-beta.1"
    private static bool TryVersion(string? tag, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        var text = tag!.Trim();
        if (text.Length > 1 && text[0] is 'v' or 'V' && char.IsDigit(text[1]))
            text = text.Substring(1);
        return SemanticVersion.TryParse(text, out version);
    }

    private static Regex Glob(string pattern) =>
        new("^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
