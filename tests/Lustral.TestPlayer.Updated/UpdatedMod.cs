namespace Lustral.Tests.Updated;

/// <summary>
/// Reports which version is running, using the file it ships in its folder and the number of times it has run (counted in
/// its data folder, which is preserved across updates), or fails to load when built as the broken version.
/// </summary>
public sealed class UpdatedMod : Mod
{
    public override void OnLoad()
    {
#if E2E_BROKEN
        throw new InvalidOperationException("e2e update: this version is broken");
#else
        var asset = File.ReadAllText(Path.Combine(Info.Directory, "Assets", "note.txt")).Trim();
        var runs = Path.Combine(Info.DataDirectory, "runs.txt");
        var count = (File.Exists(runs) ? int.Parse(File.ReadAllText(runs)) : 0) + 1;
        File.WriteAllText(runs, count.ToString());
        Log.Info($"e2e update: running {Info.Version}, asset \"{asset}\", run {count}");
#endif
    }
}
