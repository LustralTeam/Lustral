using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;
using MonoMod.Core;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// Native detours for Il2CppInterop's runtime hooks, implemented with MonoMod so that Lustral ships a single hooking
/// library.
/// </summary>
internal sealed class MonoModDetourProvider : IDetourProvider
{
    // Il2CppInterop discards the returned handle, so the detour, the target delegate and the trampoline are kept reachable
    // for the entire session; otherwise a finalizer or the GC could remove an active hook.
    private static readonly List<object> KeepAlive = [];

    public IDisposable Create<TDelegate>(nint original, TDelegate target, out TDelegate trampoline) where TDelegate : Delegate
    {
        var detour = DetourFactory.Current.CreateNativeDetour(original, Marshal.GetFunctionPointerForDelegate(target), applyByDefault: true);
        if (!detour.HasOrigEntrypoint)
        {
            detour.Dispose();
            throw new InvalidOperationException($"MonoMod returned no trampoline for the native detour at 0x{original:X}");
        }

        trampoline = Marshal.GetDelegateForFunctionPointer<TDelegate>(detour.OrigEntrypoint);
        lock (KeepAlive)
            KeepAlive.Add((detour, target, trampoline));
        return detour;
    }
}
