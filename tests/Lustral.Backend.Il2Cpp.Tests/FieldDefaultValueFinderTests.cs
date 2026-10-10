using System.Runtime.InteropServices;

namespace Lustral.Backend.Il2Cpp.Tests;

/// <summary>
/// Runs against actual game binaries (GameAssembly.dll is loaded but not initialized). Skipped if the game is not installed.
/// </summary>
public class FieldDefaultValueFinderTests
{
    private static string SteamCommon =>
        Environment.GetEnvironmentVariable("LUSTRAL_STEAM_COMMON") ?? @"C:\Program Files (x86)\Steam\steamapps\common";

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string? path);

    /// <summary>
    /// BOMBANANA! (Unity 6000.3.10f1). Field::StaticGetValue inlines StaticGetValueInternal and GetDefaultFieldValue; the
    /// literal path calls GetFieldDefaultValueForField at RVA 0x4562C0 (Il2CppInterop V2 selects 0x44CE0D, in the middle of
    /// an epilogue).
    /// </summary>
    [Fact]
    public void Finds_GetFieldDefaultValue_in_BOMBANANA_when_it_is_inlined()
    {
        var game = Path.Combine(SteamCommon, "BOMBANANA!");
        var gameAssembly = Path.Combine(game, "GameAssembly.dll");
        Assert.SkipUnless(File.Exists(gameAssembly), $"BOMBANANA! is not installed in {game}");
        Assert.SkipUnless(new FileInfo(gameAssembly).Length == 73494528, "BOMBANANA! was updated; the expected address belongs to build 25322963");

        SetDllDirectoryW(game);
        var module = NativeLibrary.Load(gameAssembly);
        var export = NativeLibrary.GetExport(module, "il2cpp_field_static_get_value");

        Assert.Equal(0x4562C0, FieldDefaultValueFinder.Find(export) - module);
    }
}
