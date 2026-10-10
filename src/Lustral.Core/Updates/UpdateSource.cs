using System.Text.RegularExpressions;

namespace Lustral.Core.Updates;

/// <summary>
/// The source of a mod's updates (<c>source</c> in the <c>[update]</c> section of mod.toml): <c>github:owner/repository</c>
/// for a GitHub repository's releases, or the HTTPS address of a JSON feed.
/// </summary>
/// <param name="Asset">For GitHub sources: the release asset to download, as a file name with <c>*</c> wildcards. Defaults to the release's only .zip file, or the .zip file whose name contains the mod's ID.</param>
public sealed record UpdateSource(string Text, string? GitHubRepository, Uri? Feed, string? Asset)
{
    private static readonly Regex Repository = new("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    public static bool TryParse(string text, string? asset, out UpdateSource? source, out string? error)
    {
        source = null;
        error = null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            var repository = trimmed.Substring("github:".Length);
            if (!Repository.IsMatch(repository))
            {
                error = $"\"{text}\" must name a repository as github:owner/repository";
                return false;
            }
            source = new UpdateSource(trimmed, repository, null, asset);
            return true;
        }
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var feed) && IsAllowed(feed))
        {
            source = new UpdateSource(trimmed, null, feed, asset);
            return true;
        }
        error = $"\"{text}\" must be github:owner/repository or an https address of a feed";
        return false;
    }

    /// <summary>
    /// Downloads require HTTPS; plain HTTP is accepted only for the local computer, for testing feeds during development.
    /// Redirects to other addresses are rejected as well.
    /// </summary>
    public static bool IsAllowed(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps || (address.Scheme == Uri.UriSchemeHttp && address.IsLoopback);

    public override string ToString() => Text;
}
