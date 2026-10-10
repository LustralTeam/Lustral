using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Lustral.Core.Config;
using Lustral.Core.Logging;
using Lustral.Core.Mods;
using Lustral.Core.Updates;

namespace Lustral.Core.Tests;

/// <summary>Mod updates from a feed or GitHub, using a simulated web server and a temporary game folder.</summary>
public sealed class ModUpdaterTests : IDisposable
{
    private const string FeedUrl = "https://mods.example/better-hud/feed.json";

    private readonly string _game = Path.Combine(Path.GetTempPath(), "lustral-tests", "updates-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWeb _web = new();
    private readonly MemorySink _log = new();
    private DateTime _now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private string Lustral => Path.Combine(_game, "Lustral");
    private string Mods => Path.Combine(_game, "Mods");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_game, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ModUpdater Updater(LoaderConfig? config = null, ModEnvironment? environment = null) =>
        new(Lustral, config ?? new LoaderConfig(), environment, Hub().For("Updates"), "Lustral/0.1.0", _web, () => _now);

    private LogHub Hub()
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        hub.AddSink(_log);
        return hub;
    }

    private static string Manifest(string version, string extra = "") => $"""
        id = "sxxr0.betterhud"
        name = "Better HUD"
        version = "{version}"
        assembly = "BetterHud.dll"
        {extra}

        [update]
        source = "{FeedUrl}"
        """;

    // Installs a mod folder and returns the result of mod discovery.
    private List<DiscoveredMod> Install(string version, string folder = "sxxr0.betterhud", string extra = "")
    {
        var directory = Path.Combine(Mods, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ModManifest.FileName), Manifest(version, extra));
        File.WriteAllText(Path.Combine(directory, "BetterHud.dll"), "dll " + version);
        return ModDiscovery.Scan(Mods).Mods;
    }

    private static byte[] Package(string manifest, string dll = "new dll", string? root = null, string? extraEntry = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
            var prefix = root is null ? "" : root + "/";
            Add(prefix + ModManifest.FileName, manifest);
            Add(prefix + "BetterHud.dll", dll);
            if (extraEntry is not null)
                Add(extraEntry, "sneaky");
        }
        return stream.ToArray();
    }

    // A feed offering one package for a version, with its checksum unless specified otherwise.
    private void Offer(string version, byte[] package, string? sha256 = "auto", bool prerelease = false, string url = "https://mods.example/better-hud/{0}.zip")
    {
        var download = string.Format(url, version);
        var hash = sha256 == "auto" ? Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant() : sha256;
        _web.Routes[FeedUrl] = () => JsonResponse($$"""
            { "releases": [ { "version": "{{version}}", "url": "{{download}}", {{(hash is null ? "" : $"\"sha256\": \"{hash}\",")}} "prerelease": {{(prerelease ? "true" : "false")}} } ] }
            """);
        _web.Routes[download] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
    }

    private static HttpResponseMessage JsonResponse(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private string InstalledVersion() => ModManifest.Parse(File.ReadAllText(Path.Combine(Mods, "sxxr0.betterhud", ModManifest.FileName))).Version.ToString();

    [Fact]
    public async Task A_newer_version_is_downloaded_then_installed_at_the_next_start_keeping_the_old_one()
    {
        var installed = Install("1.0.0");
        Offer("1.1.0", Package(Manifest("1.1.0")));

        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", InstalledVersion());
        Assert.Contains(_log.From("Updates"), m => m.Contains("Downloaded Better HUD 1.1.0") && m.Contains("next time the game starts"));

        var applied = Updater().ApplyStaged(ModDiscovery.Scan(Mods).Mods);

        Assert.Equal(new AppliedUpdate("sxxr0.betterhud", "Better HUD", SemanticVersion.Parse("1.0.0"), SemanticVersion.Parse("1.1.0"), false), Assert.Single(applied));
        Assert.Equal("1.1.0", InstalledVersion());
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(Mods, "sxxr0.betterhud", "BetterHud.dll")));
        Assert.Equal("dll 1.0.0", File.ReadAllText(Path.Combine(Lustral, "updates", "previous", "sxxr0.betterhud", "BetterHud.dll")));
        Assert.Empty(Updater().ApplyStaged(ModDiscovery.Scan(Mods).Mods));
    }

    [Fact]
    public async Task A_package_with_its_mod_in_a_folder_works_too()
    {
        var installed = Install("1.0.0", folder: "BetterHud");
        Offer("1.1.0", Package(Manifest("1.1.0"), root: "BetterHud-1.1.0"));

        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);
        Updater().ApplyStaged(ModDiscovery.Scan(Mods).Mods);

        Assert.Equal("1.1.0", ModManifest.Parse(File.ReadAllText(Path.Combine(Mods, "BetterHud", ModManifest.FileName))).Version.ToString());
    }

    [Fact]
    public async Task Notify_only_says_it_once_and_off_never_asks()
    {
        var installed = Install("1.0.0");
        Offer("1.1.0", Package(Manifest("1.1.0")));
        var notify = new LoaderConfig { ModUpdates = new Dictionary<string, UpdateMode> { ["sxxr0.betterhud"] = UpdateMode.Notify } };

        await Updater(notify).CheckAsync(installed, TestContext.Current.CancellationToken);
        _now = _now.AddDays(1);
        await Updater(notify).CheckAsync(installed, TestContext.Current.CancellationToken);

        Assert.Single(_log.From("Updates"), m => m.Contains("Better HUD 1.1.0 is available"));
        Assert.All(_web.Requests, r => Assert.Equal(FeedUrl, r));

        _web.Requests.Clear();
        await Updater(new LoaderConfig { Updates = UpdateMode.Off }).CheckAsync(installed, TestContext.Current.CancellationToken);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task A_source_is_asked_at_most_every_six_hours()
    {
        var installed = Install("1.0.0");
        Offer("1.0.0", Package(Manifest("1.0.0")));

        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);
        _now = _now.AddHours(5);
        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);
        Assert.Single(_web.Requests);

        _now = _now.AddHours(2);
        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);
        Assert.Equal(2, _web.Requests.Count);
    }

    [Fact]
    public async Task A_version_that_doesnt_fit_is_skipped_for_good()
    {
        var installed = Install("1.0.0");
        Offer("2.0.0", Package(Manifest("2.0.0", "[requires]\nlustral = \">=9.0\"")));
        var environment = new ModEnvironment(SemanticVersion.Create(0, 1, 0), "mono", new PlatformTarget("windows", "x64"), "Game", null, null, null, null);

        await Updater(environment: environment).CheckAsync(installed, TestContext.Current.CancellationToken);
        Assert.Contains(_log.From("Updates"), m => m.Contains("Not updating Better HUD to 2.0.0") && m.Contains("requires Lustral >=9.0"));
        Assert.Empty(Updater().ApplyStaged(installed));

        _web.Requests.Clear();
        _now = _now.AddDays(1);
        await Updater(environment: environment).CheckAsync(installed, TestContext.Current.CancellationToken);
        Assert.Equal([FeedUrl], _web.Requests); // asked again, but 2.0.0 isn't downloaded again
    }

    [Fact]
    public async Task A_version_that_would_break_a_dependent_mod_is_skipped()
    {
        Install("1.0.0");
        var dependent = Path.Combine(Mods, "someone.addon");
        Directory.CreateDirectory(dependent);
        File.WriteAllText(Path.Combine(dependent, ModManifest.FileName),
            "id = \"someone.addon\"\nname = \"Addon\"\nversion = \"1.0.0\"\nassembly = \"Addon.dll\"\n[dependencies]\n\"sxxr0.betterhud\" = \"^1.0\"");
        Offer("2.0.0", Package(Manifest("2.0.0")));

        await Updater().CheckAsync(ModDiscovery.Scan(Mods).Mods, TestContext.Current.CancellationToken);

        Assert.Contains(_log.From("Updates"), m => m.Contains("would break Addon (someone.addon), which requires sxxr0.betterhud ^1.0"));
        Assert.Equal("1.0.0", InstalledVersion());
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", null, "SHA-256")]
    [InlineData("auto", "../../escape.txt", "outside its folder")]
    public async Task A_bad_package_is_refused(string sha256, string? extraEntry, string expected)
    {
        var installed = Install("1.0.0");
        Offer("1.1.0", Package(Manifest("1.1.0"), extraEntry: extraEntry), sha256);

        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);

        Assert.Contains(_log.From("Updates"), m => m.Contains("Not updating Better HUD to 1.1.0") && m.Contains(expected));
        Assert.Empty(Updater().ApplyStaged(installed));
        Assert.False(File.Exists(Path.Combine(_game, "escape.txt")));
    }

    [Fact]
    public async Task Plain_http_is_refused_except_on_this_computer()
    {
        var installed = Install("1.0.0");
        Offer("1.1.0", Package(Manifest("1.1.0")), url: "http://mods.example/better-hud/{0}.zip");

        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);

        Assert.Contains(_log.From("Updates"), m => m.Contains("does not use HTTPS"));
        Assert.DoesNotContain("http://mods.example/better-hud/1.1.0.zip", _web.Requests);
        Assert.True(UpdateSource.IsAllowed(new Uri("http://localhost:8765/feed.json")));
    }

    [Fact]
    public async Task An_update_that_doesnt_load_goes_back_and_isnt_installed_again()
    {
        var installed = Install("1.0.0");
        Offer("1.1.0", Package(Manifest("1.1.0")));
        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);
        var applied = Updater().ApplyStaged(ModDiscovery.Scan(Mods).Mods);

        Updater().RollBackFailed(applied, _ => false);
        Assert.Contains(_log.From("Updates"), m => m.Contains("Better HUD 1.1.0 failed to load; Lustral restores 1.0.0 the next time the game starts"));
        var rolledBack = Assert.Single(Updater().ApplyStaged(ModDiscovery.Scan(Mods).Mods));

        Assert.True(rolledBack.Rollback);
        Assert.Equal("1.0.0", InstalledVersion());
        _now = _now.AddDays(1);
        _web.Requests.Clear();
        await Updater().CheckAsync(ModDiscovery.Scan(Mods).Mods, TestContext.Current.CancellationToken);
        Assert.Equal([FeedUrl], _web.Requests);
    }

    [Fact]
    public async Task A_download_for_a_mod_removed_meanwhile_is_dropped()
    {
        var installed = Install("1.0.0");
        Offer("1.1.0", Package(Manifest("1.1.0")));
        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);
        Directory.Delete(Path.Combine(Mods, "sxxr0.betterhud"), recursive: true);

        Assert.Empty(Updater().ApplyStaged(ModDiscovery.Scan(Mods).Mods));
        Assert.False(Directory.Exists(Path.Combine(Mods, "sxxr0.betterhud")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Lustral, "updates", "staged")));
    }

    [Fact]
    public async Task Prereleases_need_the_players_say_so()
    {
        var installed = Install("1.0.0");
        Offer("1.1.0-beta.1", Package(Manifest("1.1.0-beta.1")), prerelease: true);

        await Updater().CheckAsync(installed, TestContext.Current.CancellationToken);
        Assert.Empty(Updater().ApplyStaged(installed));

        _now = _now.AddDays(1);
        await Updater(new LoaderConfig { PrereleaseUpdates = true }).CheckAsync(installed, TestContext.Current.CancellationToken);
        Assert.Single(Updater().ApplyStaged(installed));
    }

    [Fact]
    public void GitHub_releases_pick_the_versioned_zip_and_its_digest()
    {
        var listing = Json.Parse("""
            [
              { "tag_name": "v2.0.0", "draft": true, "prerelease": false, "assets": [] },
              { "tag_name": "v1.2.0", "draft": false, "prerelease": false, "assets": [
                  { "name": "BetterHud-1.2.0.zip", "browser_download_url": "https://github.com/d/b/releases/download/v1.2.0/BetterHud-1.2.0.zip",
                    "size": 1234, "digest": "sha256:abcdef" },
                  { "name": "notes.txt", "browser_download_url": "https://github.com/d/b/notes.txt", "size": 1 } ] },
              { "tag_name": "1.3.0-rc.1", "draft": false, "prerelease": true, "assets": [
                  { "name": "a.zip", "browser_download_url": "https://x/a.zip" }, { "name": "sxxr0.betterhud.zip", "browser_download_url": "https://x/b.zip" } ] },
              { "tag_name": "nightly", "draft": false, "prerelease": false, "assets": [] }
            ]
            """);

        var releases = UpdateSources.GitHub(listing, null, "sxxr0.betterhud").ToList();

        Assert.Equal(2, releases.Count);
        Assert.Equal(new UpdateRelease(SemanticVersion.Parse("1.2.0"), new Uri("https://github.com/d/b/releases/download/v1.2.0/BetterHud-1.2.0.zip"), "abcdef", 1234, false), releases[0]);
        Assert.Equal(("1.3.0-rc.1", "https://x/b.zip", true), (releases[1].Version.ToString(), releases[1].Download.ToString(), releases[1].Prerelease));
        Assert.Equal("https://x/a.zip", UpdateSources.GitHub(listing, "a.*", "x").Last().Download.ToString());
    }

    [Fact]
    public void A_mod_built_for_both_backends_updates_from_the_games_package()
    {
        var listing = Json.Parse("""
            [
              { "tag_name": "v1.1.0", "assets": [
                  { "name": "sxxr0.fps-1.1.0-mono.zip", "browser_download_url": "https://x/mono.zip" },
                  { "name": "sxxr0.fps-1.1.0-il2cpp.zip", "browser_download_url": "https://x/il2cpp.zip" } ] },
              { "tag_name": "v1.0.0", "assets": [ { "name": "sxxr0.fps-1.0.0-il2cpp.zip", "browser_download_url": "https://x/old-il2cpp.zip" } ] }
            ]
            """);

        Assert.Equal(["https://x/il2cpp.zip", "https://x/old-il2cpp.zip"], UpdateSources.GitHub(listing, null, "sxxr0.fps", "il2cpp").Select(r => r.Download.ToString()));
        // A release with only the other backend's package offers nothing to a Mono game.
        Assert.Equal(["https://x/mono.zip"], UpdateSources.GitHub(listing, null, "sxxr0.fps", "mono").Select(r => r.Download.ToString()));
        Assert.Equal(["https://x/mono.zip"], UpdateSources.GitHub(listing, "sxxr0.fps-*.zip", "sxxr0.fps", "mono").Select(r => r.Download.ToString()));
        // Names that merely contain the word do not indicate a backend.
        var monolith = Json.Parse("""[ { "tag_name": "2.0.0", "assets": [ { "name": "monolith-2.0.0.zip", "browser_download_url": "https://x/m.zip" } ] } ]""");
        Assert.Single(UpdateSources.GitHub(monolith, null, "sxxr0.monolith", "il2cpp"));

        var feed = Json.Parse("""
            { "releases": [
                { "version": "1.1.0", "url": "fps-mono.zip", "backend": "mono" },
                { "version": "1.1.0", "url": "fps-il2cpp.zip", "backend": "il2cpp" },
                { "version": "1.0.0", "url": "fps.zip" } ] }
            """);
        Assert.Equal(["https://mods.example/fps-il2cpp.zip", "https://mods.example/fps.zip"],
            UpdateSources.Feed(feed, new Uri("https://mods.example/feed.json"), "il2cpp").Select(r => r.Download.ToString()));
    }

    [Theory]
    [InlineData("github:sxxr0/better-hud", true)]
    [InlineData("https://mods.example/feed.json", true)]
    [InlineData("http://localhost:8765/feed.json", true)]
    [InlineData("http://mods.example/feed.json", false)]
    [InlineData("github:sxxr0", false)]
    [InlineData("ftp://mods.example/feed.json", false)]
    public void Update_sources_are_GitHub_repositories_or_https_feeds(string text, bool valid) =>
        Assert.Equal(valid, UpdateSource.TryParse(text, null, out _, out _));

    [Fact]
    public void Loader_toml_says_what_to_do_with_updates()
    {
        var problems = new List<string>();
        var config = LoaderConfig.Parse("""
            [updates]
            mode = "notify"
            prereleases = true
            mods = { "sxxr0.betterhud" = "auto", "someone.quiet" = "off", "someone.typo" = "sometimes" }
            """, problems);

        Assert.Equal(UpdateMode.Notify, config.Updates);
        Assert.True(config.PrereleaseUpdates);
        Assert.Equal(UpdateMode.Auto, config.UpdatesFor("sxxr0.betterhud"));
        Assert.Equal(UpdateMode.Off, config.UpdatesFor("someone.quiet"));
        Assert.Equal(UpdateMode.Notify, config.UpdatesFor("someone.typo"));
        Assert.Contains(problems, p => p.Contains("someone.typo"));
        Assert.Equal(UpdateMode.Auto, LoaderConfig.Parse(LoaderConfig.DefaultText, []).Updates);
    }

    [Fact]
    public void Json_reads_what_sources_send()
    {
        var value = Json.Object(Json.Parse("""{ "a": [1, 2.5, -3e2], "b": "x\"é\n", "c": true, "d": null, "e": {} }"""))!;

        Assert.Equal([1.0, 2.5, -300.0], Json.Array(value["a"]).Cast<double>());
        Assert.Equal("x\"é\n", value["b"]);
        Assert.Equal(true, value["c"]);
        Assert.Null(value["d"]);
        Assert.Throws<FormatException>(() => Json.Parse("{ \"a\": }"));
        Assert.Throws<FormatException>(() => Json.Parse("[1, 2"));
    }

    private sealed class FakeWeb : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var response = Routes.TryGetValue(url, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
