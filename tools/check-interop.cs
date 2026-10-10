// Loads all types in a folder of generated interop assemblies, as CoreCLR does in the game, and reports the types that
// the type loader rejects. No game code runs: static constructors (which call into IL2CPP) are not triggered.
// Usage: dotnet run tools/check-interop.cs -- <interop folder> [--all]
#:package ds5678.Il2CppInterop.Runtime
#:property PublishAot=false
using System.Reflection;
using System.Runtime.Loader;

var folder = Path.GetFullPath(args[0]);
var showAll = args.Contains("--all");
// The same setup as Lustral's IL2CPP backend: all assemblies in the default context, with interop assemblies resolved
// from the folder.
var context = AssemblyLoadContext.Default;
context.Resolving += (ctx, name) =>
{
    var path = Path.Combine(folder, name.Name + ".dll");
    return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
};

var failures = new List<(string Assembly, string Message)>();
var typeCount = 0;
foreach (var path in Directory.GetFiles(folder, "*.dll").Order())
{
    Assembly assembly;
    try
    {
        assembly = context.LoadFromAssemblyPath(path);
    }
    catch (Exception e)
    {
        failures.Add((Path.GetFileName(path), $"assembly failed to load: {e.Message}"));
        continue;
    }

    try
    {
        typeCount += assembly.GetTypes().Length;
    }
    catch (ReflectionTypeLoadException e)
    {
        typeCount += e.Types.Count(t => t is not null);
        foreach (var loaderException in e.LoaderExceptions.OfType<Exception>().DistinctBy(x => x.Message))
            failures.Add((Path.GetFileName(path), loaderException.Message));
    }
}

foreach (var group in failures.GroupBy(f => Category(f.Message)).OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"{group.Count(),5} x {group.Key}");
    foreach (var (assembly, message) in group.Take(showAll ? int.MaxValue : 3))
        Console.WriteLine($"        {assembly}: {message}");
}
Console.WriteLine($"Loaded {typeCount} types; {failures.Count} failed.");
return failures.Count == 0 ? 0 : 1;

// Removes type and member names so that failures with the same cause are grouped together.
static string Category(string message) =>
    System.Text.RegularExpressions.Regex.Replace(message, "'[^']*'", "'…'");
