; Rules added since the last release.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
LUS0001 | Lustral.Mod | Error | ModEntryPointAnalyzer: the mod has no Lustral.Mod class
LUS0002 | Lustral.Mod | Error | ModEntryPointAnalyzer: the mod has more than one Lustral.Mod class
LUS0003 | Lustral.Mod | Error | ModEntryPointAnalyzer: a Mod class has no public parameterless constructor
LUS0101 | Lustral.Harmony | Warning | HarmonyPatchAnalyzer: patch target not found
LUS0102 | Lustral.Harmony | Warning | HarmonyPatchAnalyzer: patch target is an ambiguous overload
LUS0103 | Lustral.Harmony | Warning | HarmonyPatchAnalyzer: patch parameter does not match the target
LUS0201 | Lustral.Config | Warning | ModConfigAnalyzer: setting type cannot be stored in a settings file
LUS0301 | Lustral.Il2Cpp | Warning | InjectedComponentAnalyzer: Unity message in an injected MonoBehaviour without [Il2CppMethod]
LUS0302 | Lustral.Il2Cpp | Error | InjectedComponentAnalyzer: [Il2CppField] whose type is not an IL2CPP type
LUS0303 | Lustral.Il2Cpp | Warning | InjectedComponentAnalyzer: [Il2CppMethod] on a generic method, which Il2CppInterop does not expose
LUS0304 | Lustral.Il2Cpp | Error | InjectedComponentAnalyzer: an injected type that is generic or nested, or derives from or implements a generic type
LUS0305 | Lustral.Il2Cpp | Error | InjectedComponentAnalyzer: an IL2CPP interface method implemented without [Il2CppMethod]
