using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AsmResolver.DotNet;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;
using Il2CppInterop.Generator;

namespace Lustral.Generator;

/// <summary>
/// Unity's managed libraries for a specific Unity version, used for unstripping. IL2CPP builds exclude all Unity members
/// that the game does not use (PlayerLoop, Scene.name, and entire classes such as AssetBundle), and Il2CppInterop restores
/// them from these libraries, so that mods and Lustral can use the complete Unity API.
/// </summary>
/// <remarks>
/// The libraries are published in LustralTeam/Lustral.UnityLibraries, with one release per Unity version containing the
/// player libraries for each platform (<c>Managed-Windows.zip</c>, <c>Managed-Linux.zip</c>) and a <c>manifest.json</c>
/// with their SHA-256 hashes. Each package is downloaded once per Unity version to
/// <c>%LOCALAPPDATA%\Lustral\unity\&lt;version&gt;\&lt;package&gt;</c> and shared by all games using that version. All
/// computers unstrip from the same files, so a mod built against one user's interop assemblies finds the same members on
/// another's. Only Unity's libraries are restored; the game's .NET class libraries remain as built (as in MelonLoader).
/// </remarks>
internal sealed class UnityLibraries
{
    public const string DefaultSource = "https://github.com/LustralTeam/Lustral.UnityLibraries/releases/download";

    private const string CompleteMarker = ".sha256";
    private const long MaxPackageSize = 64 << 20;
    private static readonly HttpClient s_http = CreateHttpClient();

    private UnityLibraries(string source, string directory)
    {
        Source = source;
        Directory = directory;
    }

    /// <summary>The source of the libraries, for the log and <c>interop.json</c>.</summary>
    public string Source { get; }

    public string Directory { get; }

    /// <summary>The package for the platform the game runs on.</summary>
    public static string PackageName => OperatingSystem.IsLinux() ? "Managed-Linux.zip" : "Managed-Windows.zip";

    public static string CacheDirectory(string unityVersion) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lustral", "unity", unityVersion,
            Path.GetFileNameWithoutExtension(PackageName));

    public static UnityLibraries FromDirectory(string directory) => new($"--unstrip {directory}", directory);

    /// <summary>The result of <see cref="Get"/>: the libraries, or the reason none are available and whether a later attempt could succeed.</summary>
    public sealed record Outcome(UnityLibraries? Libraries, string? Problem, bool TryAgainLater);

    /// <summary>
    /// Returns the libraries for this Unity version from the cache, downloading them into the cache from
    /// <paramref name="source"/> if necessary. The source is the releases' download address, or a folder with the same
    /// layout (<c>&lt;version&gt;/manifest.json</c>, for tests).
    /// </summary>
    public static Outcome Get(string unityVersion, string source)
    {
        var directory = CacheDirectory(unityVersion);
        if (File.Exists(Path.Combine(directory, CompleteMarker)))
            return new Outcome(new UnityLibraries(Describe(source, unityVersion, File.ReadAllText(Path.Combine(directory, CompleteMarker)).Trim()), directory), null, false);

        try
        {
            return Download(unityVersion, source, directory);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return new Outcome(null, $"could not download the libraries for Unity {unityVersion}: {Message(e)}", true);
        }
    }

    private static Outcome Download(string unityVersion, string source, string directory)
    {
        var manifestText = Fetch(source, $"{unityVersion}/manifest.json", out var found);
        if (!found)
            return new Outcome(null, $"no libraries for Unity {unityVersion} are available at {source} yet", true);

        PackageEntry? package;
        try
        {
            package = ReadPackage(manifestText!, PackageName);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            return new Outcome(null, $"the manifest.json for Unity {unityVersion} could not be read: {e.Message}", true);
        }
        if (package is null)
            return new Outcome(null, $"the release for Unity {unityVersion} has no {PackageName}", true);
        if (package.Format > 1)
            return new Outcome(null, $"the release for Unity {unityVersion} uses a newer format (update Lustral)", false);
        if (package.State != "built" || package.Sha256 is null)
            return new Outcome(null, $"{PackageName} is not available for Unity {unityVersion}{(package.Reason is null ? "" : $": {package.Reason}")}", false);
        if (package.Size > MaxPackageSize)
            return new Outcome(null, $"{PackageName} for Unity {unityVersion} is {package.Size} bytes, which exceeds the maximum package size", false);

        var parent = Path.GetDirectoryName(directory)!;
        System.IO.Directory.CreateDirectory(parent);
        var download = Path.Combine(parent, $"{PackageName}.{Environment.ProcessId}.download");
        var staging = $"{directory}.{Environment.ProcessId}.tmp";
        try
        {
            FetchToFile(source, $"{unityVersion}/{PackageName}", download);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(download)));
            if (!string.Equals(sha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
                return new Outcome(null, $"the downloaded {PackageName} for Unity {unityVersion} does not match its SHA-256 hash", true);

            if (System.IO.Directory.Exists(staging))
                System.IO.Directory.Delete(staging, recursive: true);
            System.IO.Directory.CreateDirectory(staging);
            Extract(download, staging);
            File.WriteAllText(Path.Combine(staging, CompleteMarker), sha256);
            try
            {
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, recursive: true);
                System.IO.Directory.Move(staging, directory);
            }
            catch (IOException) when (File.Exists(Path.Combine(directory, CompleteMarker)))
            {
                // Another game using this Unity version completed the download first.
            }
            return new Outcome(new UnityLibraries(Describe(source, unityVersion, sha256), directory), null, false);
        }
        finally
        {
            File.Delete(download);
            if (System.IO.Directory.Exists(staging))
                System.IO.Directory.Delete(staging, recursive: true);
        }
    }

    private static string Describe(string source, string unityVersion, string sha256) =>
        $"{(source == DefaultSource ? "LustralTeam/Lustral.UnityLibraries" : source)} {unityVersion} {PackageName} (SHA-256 {sha256[..Math.Min(12, sha256.Length)]})";

    // The zip contains the libraries at its root; any other entry indicates an invalid package.
    private static void Extract(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            if (entry.FullName.Contains('/') || entry.FullName.Contains('\\') || entry.FullName.Contains("..") ||
                !entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{PackageName} has an unexpected entry: {entry.FullName}");
            entry.ExtractToFile(Path.Combine(destination, entry.FullName));
        }
    }

    private sealed record PackageEntry(int Format, string? State, string? Sha256, long Size, string? Reason);

    private static PackageEntry? ReadPackage(string manifestJson, string packageName)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 1;
        if (!root.TryGetProperty("packages", out var packages) || !packages.TryGetProperty(packageName, out var package))
            return null;
        string? Text(string name) => package.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var size = package.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
        return new PackageEntry(format, Text("state"), Text("sha256"), size, Text("reason"));
    }

    // A source is the releases' download address (HTTPS, or HTTP on the local computer for tests) or a folder.
    private static string? Fetch(string source, string path, out bool found)
    {
        if (IsFolder(source))
        {
            var file = Path.Combine(source, path.Replace('/', Path.DirectorySeparatorChar));
            found = File.Exists(file);
            return found ? File.ReadAllText(file) : null;
        }
        using var response = s_http.GetAsync(Address(source, path)).GetAwaiter().GetResult();
        found = response.StatusCode != HttpStatusCode.NotFound;
        if (!found)
            return null;
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static void FetchToFile(string source, string path, string destination)
    {
        if (IsFolder(source))
        {
            File.Copy(Path.Combine(source, path.Replace('/', Path.DirectorySeparatorChar)), destination, overwrite: true);
            return;
        }
        using var response = s_http.GetAsync(Address(source, path), HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPackageSize)
            throw new IOException($"{path} exceeds the maximum package size");
        using var input = response.Content.ReadAsStream();
        using var output = File.Create(destination);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            total += read;
            if (total > MaxPackageSize)
                throw new IOException($"{path} exceeds the maximum package size");
            output.Write(buffer, 0, read);
        }
    }

    private static bool IsFolder(string source) => !source.Contains("://", StringComparison.Ordinal);

    private static Uri Address(string source, string path)
    {
        var uri = new Uri($"{source.TrimEnd('/')}/{path}");
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new HttpRequestException($"{uri} does not use HTTPS");
        return uri;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        var version = typeof(UnityLibraries).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Lustral.Generator/{version}");
        return http;
    }

    private static string Message(Exception e) => e is TaskCanceledException ? "timed out" : e.Message;

    /// <summary>Reads the libraries. The unstrip layer assigns them a resolution context once the game's assemblies are known.</summary>
    public IReadOnlyList<AssemblyDefinition> Load() =>
        System.IO.Directory.GetFiles(Directory, "*.dll", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => AssemblyDefinition.FromFile(g.First(), createRuntimeContext: false))
            .ToList();

    /// <summary>
    /// Provides Il2CppInterop's unstrip layer with Unity's libraries for the assemblies the game contains, reduced by
    /// <see cref="UnityLibraryFilter"/> to what the layer can restore correctly. Must be first in the layer list. The
    /// libraries are resolved only among themselves, never from the local system (AsmResolver's default resolver would
    /// find .NET Framework's mscorlib in the GAC).
    /// </summary>
    public sealed class Layer(IReadOnlyList<AssemblyDefinition> assemblies) : Cpp2IlProcessingLayer
    {
        public override string Name => "Lustral Unity libraries";
        public override string Id => "lustral_unity_libraries";

        public override void PreProcess(ApplicationAnalysisContext context, List<Cpp2IlProcessingLayer> layers)
        {
            var present = assemblies
                .Where(a => a.Name is { } name && context.AssembliesByName.ContainsKey(name))
                .GroupBy(a => a.Name!.ToString(), StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            var filter = new UnityLibraryFilter(context);
            foreach (var assembly in present)
                filter.KeepGameTypes(assembly, context.AssembliesByName[assembly.Name!]);

            var runtimeContext = new RuntimeContext(DotNetRuntimeInfo.NetFramework(4, 7), new OnlyThese(present), KnownCorLibs.MsCorLib_v4_0_0_0, null);
            foreach (var assembly in present)
                runtimeContext.AddAssembly(assembly);
            filter.KeepWorkableMethods(runtimeContext);
            Console.WriteLine($"Unstripping from {present.Count} of Unity's libraries: {filter.MethodsToRestore} methods to restore; " +
                $"excluded {filter.TypesLeftOut} types the game does not contain and {filter.MethodsLeftOut} methods that cannot be restored correctly");
            context.PutExtraData<IReadOnlyList<AssemblyDefinition>>(UnstripBaseProcessingLayer.AssembliesKey, present);
        }

        public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null)
        {
        }

        private sealed class OnlyThese(IEnumerable<AssemblyDefinition> assemblies) : IAssemblyResolver
        {
            private readonly Dictionary<string, AssemblyDefinition> _byName = assemblies
                .GroupBy(a => a.Name!.ToString(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            public ResolutionStatus Resolve(AssemblyDescriptor assembly, ModuleDefinition? originModule, out AssemblyDefinition? result)
            {
                result = assembly.Name is { } name ? _byName.GetValueOrDefault(name.ToString()) : null;
                return result is null ? ResolutionStatus.AssemblyNotFound : ResolutionStatus.Success;
            }
        }
    }
}
