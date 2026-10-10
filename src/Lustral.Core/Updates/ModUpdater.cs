using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Lustral.Core.Config;
using Lustral.Core.Mods;
using Tomlyn;

namespace Lustral.Core.Updates;

/// <param name="Rollback">True if a previous version was restored.</param>
public sealed record AppliedUpdate(string Id, string Name, SemanticVersion From, SemanticVersion To, bool Rollback);

/// <summary>
/// Updates mods from their update sources (<c>[update]</c> in mod.toml), as configured in the <c>[updates]</c> section of
/// loader.toml. After the mods load, a background check downloads newer versions that are compatible with the game,
/// Lustral and the other mods; they are installed on the next start, before the mods load, because assemblies cannot be
/// replaced while loaded. The replaced version is kept and restored if the new version fails to load or the game exits
/// before it is ready. Packages are not signed; each download is verified against the SHA-256 hash provided by its
/// source over HTTPS.
/// </summary>
/// <remarks>
/// <c>Lustral/updates</c> contains <c>staged/&lt;id&gt;/</c> and <c>staged/&lt;id&gt;.toml</c> (a version pending
/// installation on the next start), <c>previous/&lt;id&gt;/</c> (the version replaced by the last update),
/// <c>downloads/</c> (temporary files) and <c>state.toml</c> (<see cref="UpdateState"/>).
/// </remarks>
public sealed class ModUpdater : IDisposable
{
    /// <summary>The minimum interval between checks of a mod's update source.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const int MaxPackageEntries = 10_000;

    private readonly LoaderConfig _config;
    private readonly ModEnvironment? _environment;
    private readonly ILog _log;
    private readonly string _userAgent;
    private readonly HttpMessageHandler? _handler;
    private readonly Func<DateTime> _now;
    private readonly string _stateFile;
    private readonly UpdateState _state;
    private HttpClient? _http;

    /// <param name="lustralDirectory">The game's <c>Lustral</c> folder.</param>
    /// <param name="environment">The environment that a new version's <c>[requires]</c> must match; null skips these checks.</param>
    /// <param name="userAgent">The User-Agent sent with every request (required by GitHub), for example <c>Lustral/0.1.0</c>.</param>
    /// <param name="handler">The HTTP handler; tests supply their own.</param>
    public ModUpdater(string lustralDirectory, LoaderConfig config, ModEnvironment? environment, ILog log, string userAgent,
        HttpMessageHandler? handler = null, Func<DateTime>? now = null)
    {
        Directory = Path.Combine(lustralDirectory, "updates");
        _config = config;
        _environment = environment;
        _log = log;
        _userAgent = userAgent;
        _handler = handler;
        _now = now ?? (() => DateTime.UtcNow);
        _stateFile = Path.Combine(Directory, "state.toml");
        _state = UpdateState.Load(_stateFile);
    }

    /// <summary><c>Lustral/updates</c>.</summary>
    public string Directory { get; }

    private string StagedDirectory => Path.Combine(Directory, "staged");
    private string PreviousDirectory => Path.Combine(Directory, "previous");
    private string DownloadsDirectory => Path.Combine(Directory, "downloads");

    /// <summary>
    /// Installs the versions pending in <c>staged</c> (downloads and rollbacks) over the installed mods, before mod
    /// discovery. Mods that the user has removed in the meantime are not reinstalled. Folders that are in use are left
    /// for the next start.
    /// </summary>
    public IReadOnlyList<AppliedUpdate> ApplyStaged(IReadOnlyList<DiscoveredMod> installed)
    {
        var applied = new List<AppliedUpdate>();
        if (!System.IO.Directory.Exists(StagedDirectory))
            return applied;
        foreach (var marker in System.IO.Directory.GetFiles(StagedDirectory, "*.toml").OrderBy(m => m, StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(marker);
            var staged = Path.Combine(StagedDirectory, id);
            var (version, rollback, reason) = ReadMarker(marker);
            var mod = installed.FirstOrDefault(m => m.Id == id);
            try
            {
                if (mod is null || version is null || !File.Exists(Path.Combine(staged, ModManifest.FileName)))
                {
                    if (mod is null && version is not null)
                        _log.Info($"{id} has been removed; the downloaded version {version} is not installed");
                    DeleteDirectory(staged);
                    File.Delete(marker);
                    continue;
                }

                var previous = Path.Combine(PreviousDirectory, id);
                DeleteDirectory(previous);
                System.IO.Directory.CreateDirectory(PreviousDirectory);
                System.IO.Directory.Move(mod.Directory, previous);
                try
                {
                    System.IO.Directory.Move(staged, mod.Directory);
                }
                catch
                {
                    System.IO.Directory.Move(previous, mod.Directory);
                    throw;
                }
                File.Delete(marker);
                applied.Add(new AppliedUpdate(id, mod.Manifest.Name, mod.Manifest.Version, version.Value, rollback));
                _log.Info(rollback
                    ? $"Restored {mod.Manifest.Name} {version} ({id}){(reason is null ? "" : ": " + reason)}"
                    : $"Updated {mod.Manifest.Name} from {mod.Manifest.Version} to {version} ({id})");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not install {id} {version} ({e.Message}); retrying the next time the game starts");
            }
        }
        return applied;
    }

    /// <summary>
    /// Restores the previous version of updated mods that failed to load (excluding mods disabled by the user) the next
    /// time the game starts. The failed version is not downloaded again.
    /// </summary>
    public void RollBackFailed(IReadOnlyList<AppliedUpdate> applied, Func<string, bool> loaded)
    {
        foreach (var update in applied.Where(u => !u.Rollback && !loaded(u.Id) && !_config.DisabledMods.Contains(u.Id)))
            RollBack(update.Id, update.Name, update.To, "failed to load", "the next time the game starts");
    }

    /// <summary>
    /// Restores the version replaced by an update. The version is staged, so <see cref="ApplyStaged"/> installs it on the
    /// next start, or immediately if it has not run yet.
    /// </summary>
    /// <param name="when">When the version is restored, for the log: "now" or "the next time the game starts".</param>
    public void RollBack(string id, string name, SemanticVersion bad, string reason, string when)
    {
        _state.For(id).Skipped.Add(bad.ToString());
        SaveState();
        var previous = Path.Combine(PreviousDirectory, id);
        try
        {
            if (!File.Exists(Path.Combine(previous, ModManifest.FileName)))
            {
                _log.Warn($"{name} {bad} {reason}, and the previous version is no longer available, so it cannot be restored");
                return;
            }
            var manifest = ModManifest.Parse(File.ReadAllText(Path.Combine(previous, ModManifest.FileName)));
            var staged = Path.Combine(StagedDirectory, id);
            DeleteDirectory(staged);
            System.IO.Directory.CreateDirectory(StagedDirectory);
            System.IO.Directory.Move(previous, staged);
            WriteMarker(id, manifest.Version, rollback: true, $"{bad} {reason}");
            _log.Warn($"{name} {bad} {reason}; Lustral restores {manifest.Version} {when} and will not install {bad} again");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ManifestException)
        {
            _log.Warn($"{name} {bad} {reason}, and restoring the previous version failed: {e.Message}");
        }
    }

    /// <summary>
    /// Checks each mod's update source for a newer version, at most once per <see cref="CheckInterval"/>, and downloads it
    /// or reports it, depending on the configured mode. Runs in the background and never throws; problems are logged.
    /// </summary>
    public async Task CheckAsync(IReadOnlyList<DiscoveredMod> installed, CancellationToken cancellation = default)
    {
        foreach (var mod in installed.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (mod.Manifest.Update is not { } source || cancellation.IsCancellationRequested)
                continue;
            var mode = _config.UpdatesFor(mod.Id);
            var state = _state.For(mod.Id);
            if (mode == UpdateMode.Off || (state.Checked is { } last && _now() - last < CheckInterval) || File.Exists(Marker(mod.Id)))
                continue;
            try
            {
                await CheckOneAsync(mod, source, mode, state, installed, cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested)
            {
                _log.Info($"Could not check {mod.Manifest.Name} ({mod.Id}) for updates: {Describe(e)}");
            }
            state.Checked = _now();
            SaveState();
        }
    }

    private async Task CheckOneAsync(DiscoveredMod mod, UpdateSource source, UpdateMode mode, ModUpdateState state, IReadOnlyList<DiscoveredMod> installed, CancellationToken cancellation)
    {
        var releases = await UpdateSources.GetAsync(source, mod.Id, _environment?.Backend, Http, cancellation).ConfigureAwait(false);
        var prereleases = _config.PrereleaseUpdates || mod.Manifest.Version.Prerelease.Length > 0;
        var offered = releases.Where(r => prereleases || !r.Prerelease).ToList();
        state.Latest = offered.FirstOrDefault()?.Version.ToString() ?? state.Latest;
        var release = offered.FirstOrDefault(r => r.Version > mod.Manifest.Version && !state.Skipped.Contains(r.Version.ToString()));
        if (release is null)
            return;
        if (mode == UpdateMode.Notify)
        {
            if (state.Notified != release.Version.ToString())
                _log.Info($"{mod.Manifest.Name} {release.Version} is available ({mod.Id}; installed: {mod.Manifest.Version}). Updates for this mod are set to notify in loader.toml.");
            state.Notified = release.Version.ToString();
            return;
        }
        await StageAsync(mod, release, state, installed, cancellation).ConfigureAwait(false);
    }

    private async Task StageAsync(DiscoveredMod mod, UpdateRelease release, ModUpdateState state, IReadOnlyList<DiscoveredMod> installed, CancellationToken cancellation)
    {
        System.IO.Directory.CreateDirectory(DownloadsDirectory);
        var package = Path.Combine(DownloadsDirectory, $"{mod.Id}-{release.Version}.zip");
        var extracted = Path.Combine(DownloadsDirectory, $"{mod.Id}-{release.Version}");
        try
        {
            await DownloadAsync(release, package, cancellation).ConfigureAwait(false);
            DeleteDirectory(extracted);
            Extract(package, extracted);
            var root = ModRoot(extracted) ?? throw new InvalidDataException($"the package contains no {ModManifest.FileName}");
            var manifest = ModManifest.Parse(File.ReadAllText(Path.Combine(root, ModManifest.FileName)));
            if (manifest.Id != mod.Id)
                throw new InvalidDataException($"the package contains {manifest.Id} instead of {mod.Id}");
            if (manifest.Version <= mod.Manifest.Version)
                throw new InvalidDataException($"the package contains version {manifest.Version}, which is not newer than {mod.Manifest.Version}");
            if (Misfit(manifest, installed) is { } reason)
            {
                state.Skipped.Add(release.Version.ToString());
                _log.Info($"Not updating {mod.Manifest.Name} to {manifest.Version} ({mod.Id}): it {reason}");
                return;
            }

            var staged = Path.Combine(StagedDirectory, mod.Id);
            DeleteDirectory(staged);
            System.IO.Directory.CreateDirectory(StagedDirectory);
            System.IO.Directory.Move(root, staged);
            WriteMarker(mod.Id, manifest.Version, rollback: false, null);
            _log.Info($"Downloaded {mod.Manifest.Name} {manifest.Version} ({mod.Id}); it will be installed the next time the game starts");
        }
        catch (Exception e) when (e is InvalidDataException or ManifestException)
        {
            state.Skipped.Add(release.Version.ToString());
            _log.Info($"Not updating {mod.Manifest.Name} to {release.Version} ({mod.Id}): {e.Message}");
        }
        finally
        {
            TryDelete(package);
            TryDelete(package + ".part");
            DeleteDirectory(extracted);
        }
    }

    // Returns why a new version cannot replace the installed one, or null: its [requires] section, its dependencies, or
    // installed mods that depend on it.
    private string? Misfit(ModManifest manifest, IReadOnlyList<DiscoveredMod> installed)
    {
        if (_environment?.Check(manifest, new List<string>()) is { } reason)
            return reason;
        foreach (var dependency in manifest.Dependencies)
        {
            var present = installed.FirstOrDefault(m => m.Id == dependency.Key);
            if (present is null)
                return $"requires {dependency.Key} {dependency.Value}, which is not installed";
            if (!dependency.Value.IsSatisfiedBy(present.Manifest.Version))
                return $"requires {dependency.Key} {dependency.Value}, but version {present.Manifest.Version} is installed";
        }
        foreach (var dependent in installed)
        {
            if (dependent.Manifest.Dependencies.TryGetValue(manifest.Id, out var range) && !range.IsSatisfiedBy(manifest.Version))
                return $"would break {dependent.Manifest.Name} ({dependent.Id}), which requires {manifest.Id} {range}";
        }
        return null;
    }

    private async Task DownloadAsync(UpdateRelease release, string path, CancellationToken cancellation)
    {
        if (!UpdateSource.IsAllowed(release.Download))
            throw new InvalidDataException($"{release.Download} does not use HTTPS");
        if (release.Size > MaxPackageBytes)
            throw new InvalidDataException($"the package is {release.Size / (1024 * 1024)} MB, which exceeds Lustral's download limit");
        using var response = await Http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        UpdateSources.CheckAddress(response, release.Download);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{release.Download.Host} responded with {(int)response.StatusCode} {response.ReasonPhrase}");
        if (response.Content.Headers.ContentLength > MaxPackageBytes)
            throw new InvalidDataException("the package exceeds Lustral's download limit");

        using var sha256 = SHA256.Create();
        var partial = path + ".part";
        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var output = File.Create(partial))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes)
                    throw new InvalidDataException("the package exceeds Lustral's download limit");
                sha256.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer, 0, read, cancellation).ConfigureAwait(false);
            }
        }
        sha256.TransformFinalBlock([], 0, 0);
        var hash = ToHex(sha256.Hash!);
        if (release.Sha256 is { } expected && !string.Equals(hash, expected.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"the download's SHA-256 hash is {hash}, but its source specifies {expected}");
        TryDelete(path);
        File.Move(partial, path);
    }

    // Extracts a package, rejecting entries whose paths resolve outside the target folder.
    private static void Extract(string package, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            using var archive = ZipFile.OpenRead(package);
            if (archive.Entries.Count > MaxPackageEntries)
                throw new InvalidDataException("the package contains too many files");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('\\', '/')));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"the package contains a file outside its folder ({entry.FullName})");
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                {
                    System.IO.Directory.CreateDirectory(target);
                    continue;
                }
                total += entry.Length;
                if (total > MaxPackageBytes * 4)
                    throw new InvalidDataException("the extracted package exceeds Lustral's size limit");
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        catch (IOException e)
        {
            throw new InvalidDataException($"the package could not be extracted ({e.Message})");
        }
    }

    // The mod's folder in a package: the root, or the single folder at the root, containing mod.toml.
    private static string? ModRoot(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, ModManifest.FileName)))
            return extracted;
        var folders = System.IO.Directory.GetDirectories(extracted);
        return folders.Length == 1 && File.Exists(Path.Combine(folders[0], ModManifest.FileName)) ? folders[0] : null;
    }

    private string Marker(string id) => Path.Combine(StagedDirectory, id + ".toml");

    private void WriteMarker(string id, SemanticVersion version, bool rollback, string? reason)
    {
        var text = new StringBuilder()
            .Append("# A version of ").Append(id).Append(" Lustral installs the next time the game starts.\n")
            .Append("version = \"").Append(version).Append("\"\n")
            .Append("rollback = ").Append(rollback ? "true" : "false").Append('\n');
        if (reason is not null)
            text.Append("reason = \"").Append(reason.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");
        File.WriteAllText(Marker(id), text.ToString());
    }

    private static (SemanticVersion? Version, bool Rollback, string? Reason) ReadMarker(string path)
    {
        try
        {
            var document = Toml.Parse(File.ReadAllText(path), path);
            if (document.HasErrors)
                return (null, false, null);
            var table = document.ToModel();
            SemanticVersion? version = table.TryGetValue("version", out var v) && v is string text && SemanticVersion.TryParse(text, out var parsed) ? parsed : null;
            return (version, table.TryGetValue("rollback", out var r) && r is true, table.TryGetValue("reason", out var reason) ? reason as string : null);
        }
        catch (IOException)
        {
            return (null, false, null);
        }
    }

    private HttpClient Http
    {
        get
        {
            if (_http is not null)
                return _http;
            var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            return _http = http;
        }
    }

    private void SaveState()
    {
        try
        {
            _state.Save(_stateFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Debug($"Could not save {_stateFile}: {e.Message}");
        }
    }

    private static string Describe(Exception e) => e switch
    {
        TaskCanceledException => "the request timed out",
        HttpRequestException { InnerException: { } inner } => $"{e.Message} ({inner.Message})",
        _ => e.Message,
    };

    private static string ToHex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private static void DeleteDirectory(string path)
    {
        if (System.IO.Directory.Exists(path))
            System.IO.Directory.Delete(path, recursive: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => _http?.Dispose();
}
