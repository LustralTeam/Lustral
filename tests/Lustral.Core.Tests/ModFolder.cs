using Lustral.Core.Logging;
using Lustral.Core.Mods;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lustral.Core.Tests;

/// <summary>
/// A temporary Mods/ folder with mods compiled from C# source, for the loader tests. Assembly names have a per-folder
/// suffix, because assemblies remain loaded for the entire test run.
/// </summary>
internal sealed class ModFolder : IDisposable
{
    private static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    public ModFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "lustral-tests", Guid.NewGuid().ToString("N"));
        Suffix = Path.GetFileName(Root)[..8];
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Appended to every assembly name in this folder.</summary>
    public string Suffix { get; }

    public MemorySink Log { get; } = new();

    public string Name(string assembly) => $"{assembly}_{Suffix}";

    /// <summary>Compiles <paramref name="source"/> into Mods/&lt;modId&gt;/&lt;assembly&gt;_&lt;suffix&gt;.dll.</summary>
    public string Compile(string modId, string assembly, string source, params string[] references) =>
        CompileExactly(modId, Name(assembly), source, references);

    /// <summary>Compiles <paramref name="source"/> into Mods/&lt;modId&gt;/&lt;name&gt;.dll, the name as given.</summary>
    public string CompileExactly(string modId, string name, string source, params string[] references)
    {
        var directory = Path.Combine(Root, modId);
        Directory.CreateDirectory(directory);
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            Framework.Concat(references.Select(r => MetadataReference.CreateFromFile(r))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var path = Path.Combine(directory, name + ".dll");
        var result = compilation.Emit(path);
        if (!result.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }

    public void Manifest(string modId, string toml) =>
        File.WriteAllText(Path.Combine(Root, modId, ModManifest.FileName), toml);

    public LoadPlan Plan()
    {
        var (mods, problems) = ModDiscovery.Scan(Root);
        Assert.Empty(problems);
        return ModResolver.Resolve(mods);
    }

    public LogHub CreateLogHub()
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        hub.AddSink(Log);
        return hub;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Loaded assemblies keep their files open until the test run ends.
        }
    }
}

internal sealed class MemorySink : ILogSink
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
                return _entries.ToList();
        }
    }

    public IEnumerable<string> From(string source) => Entries.Where(e => e.Source == source).Select(e => e.Message);

    public void Write(in LogEntry entry)
    {
        lock (_entries)
            _entries.Add(entry);
    }

    public void Dispose()
    {
    }
}
