using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// Reads a scene's name and build index directly from Unity's native code, for games whose builds stripped
/// <c>Scene.name</c> and <c>Scene.buildIndex</c>. IL2CPP includes only the managed code the game uses, but the engine's
/// native functions are always present.
/// </summary>
/// <remarks>
/// Two binding styles are supported, located by name: Unity 6's
/// <c>GetNameInternal_Injected(ref SceneHandle, out ManagedSpanWrapper)</c>, whose UTF-16 buffer the caller frees with
/// <c>BindingsAllocator.Free</c> (as Unity's OutStringMarshaller does), and the <c>GetNameInternal(int)</c> of earlier
/// versions, which returns an IL2CPP string. In earlier versions, the handle is an <c>int</c>, which Il2CppInterop V2's
/// interop types declare as <c>Il2CppSystem.Int32</c>.
/// </remarks>
internal static unsafe class NativeSceneInfo
{
    private const string Scene = "UnityEngine.SceneManagement.Scene::";

    [StructLayout(LayoutKind.Sequential)]
    private struct ManagedSpanWrapper
    {
        public char* Begin;
        public int Length;
    }

    private static readonly delegate* unmanaged<void*, ManagedSpanWrapper*, void> s_getNameByHandle =
        (delegate* unmanaged<void*, ManagedSpanWrapper*, void>)Resolve(Scene + "GetNameInternal_Injected");
    private static readonly delegate* unmanaged<void*, int> s_getBuildIndexByHandle =
        (delegate* unmanaged<void*, int>)Resolve(Scene + "GetBuildIndexInternal_Injected");
    private static readonly delegate* unmanaged<void*, void> s_free =
        (delegate* unmanaged<void*, void>)Resolve("UnityEngine.Bindings.BindingsAllocator::Free");
    private static readonly delegate* unmanaged<int, nint> s_getNameByInt =
        (delegate* unmanaged<int, nint>)Resolve(Scene + "GetNameInternal", Scene + "GetNameInternal(System.Int32)");
    private static readonly delegate* unmanaged<int, int> s_getBuildIndexByInt =
        (delegate* unmanaged<int, int>)Resolve(Scene + "GetBuildIndexInternal", Scene + "GetBuildIndexInternal(System.Int32)");

    /// <param name="scene">An interop <c>UnityEngine.SceneManagement.Scene</c>, boxed.</param>
    public static bool TryDescribe(object scene, out string? name, out int? buildIndex)
    {
        name = null;
        buildIndex = null;
        var handleField = scene.GetType().GetField("m_Handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (handleField?.GetValue(scene) is not { } handle)
            return false;

        if (handle is int id || TryReadInt32(handle, out id))
        {
            if (s_getNameByInt != null)
                name = Il2CppString(s_getNameByInt(id));
            if (s_getBuildIndexByInt != null)
                buildIndex = s_getBuildIndexByInt(id);
            return name is not null || buildIndex is not null;
        }

        // Unity 6: a SceneHandle struct, passed by reference. Interop value types are blittable copies of the native layout.
        GCHandle pinned;
        try
        {
            pinned = GCHandle.Alloc(handle, GCHandleType.Pinned);
        }
        catch (ArgumentException)
        {
            return false;
        }
        try
        {
            var address = (void*)pinned.AddrOfPinnedObject();
            if (s_getNameByHandle != null)
            {
                // As in Unity's OutStringMarshaller.GetStringAndDispose, an empty string points to a static sentinel
                // rather than an allocation, so only non-empty strings are freed. Freeing the sentinel corrupts the heap;
                // Unity 6000.3 returns it for the unnamed scene unloaded during startup.
                ManagedSpanWrapper span = default;
                s_getNameByHandle(address, &span);
                name = span.Length > 0 ? new string(span.Begin, 0, span.Length) : "";
                if (span.Length > 0 && s_free != null)
                    s_free(span.Begin);
            }
            if (s_getBuildIndexByHandle != null)
                buildIndex = s_getBuildIndexByHandle(address);
        }
        finally
        {
            pinned.Free();
        }
        return name is not null || buildIndex is not null;
    }

    // Il2CppSystem.Int32 is a blittable copy of the native int.
    private static bool TryReadInt32(object handle, out int value)
    {
        value = 0;
        if (handle.GetType().FullName != "Il2CppSystem.Int32")
            return false;
        var pinned = GCHandle.Alloc(handle, GCHandleType.Pinned);
        try
        {
            value = *(int*)pinned.AddrOfPinnedObject();
            return true;
        }
        finally
        {
            pinned.Free();
        }
    }

    private static nint Resolve(params string[] names)
    {
        foreach (var name in names)
        {
            var function = IL2CPP.il2cpp_resolve_icall(name);
            if (function != 0)
                return function;
        }
        return 0;
    }

    // Il2CppString layout: object header (class, monitor), int32 length, UTF-16 characters.
    private static string? Il2CppString(nint pointer) =>
        pointer == 0 ? null : new string((char*)(pointer + 2 * IntPtr.Size + sizeof(int)), 0, *(int*)(pointer + 2 * IntPtr.Size));
}
