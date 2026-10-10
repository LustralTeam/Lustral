using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Lustral.Core.Assets;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// Mods' asset bundles on IL2CPP, through the engine's native functions (icalls). These exist in every Unity player, even
/// if the game's build stripped the managed <c>AssetBundle</c> class: IL2CPP includes only the C# code a game uses, and
/// many games (including the test players) include little or none of it.
/// </summary>
/// <remarks>
/// <para>
/// Two binding styles are supported, located by name as in <see cref="NativeSceneInfo"/>. Unity 6's <c>_Injected</c>
/// functions take strings and bytes as a <c>ManagedSpanWrapper</c> (UTF-16 characters or bytes, and a length) and the
/// bundle as its native object (<c>UnityEngine.Object.m_CachedPtr</c>), and return a Unity object as a GC handle to its
/// managed wrapper. Unity 2022.3 and earlier take IL2CPP strings, byte arrays and the bundle's managed object, and return
/// objects. Both take the asset type as a <c>System.Type</c> object and return arrays as IL2CPP arrays.
/// </para>
/// <para>
/// Native functions report errors by raising IL2CPP exceptions, which cannot propagate into .NET and crash the game.
/// Lustral therefore checks every condition they raise exceptions for (a destroyed bundle, a null or empty name, a type
/// that is not a Unity object, a scene bundle) beforehand, and calls them only on the main thread.
/// </para>
/// </remarks>
internal sealed unsafe class Il2CppAssetBundles : IAssetBundleBackend
{
    private const string AssetBundle = "UnityEngine.AssetBundle::";

    // Il2CppArray layout: object header (class, monitor), bounds, max_length, then the elements.
    private static readonly int s_arrayData = 4 * IntPtr.Size;

    [StructLayout(LayoutKind.Sequential)]
    private struct ManagedSpanWrapper
    {
        public void* Begin;
        public int Length;
    }

    /// <summary>A loaded bundle: a GC handle that keeps the bundle's managed object alive.</summary>
    private sealed class Bundle(nint handle)
    {
        public nint Handle = handle;
    }

    private readonly Dictionary<Type, nint> _classes = [];
    private bool _resolved;
    private string? _unsupported;
    private bool _injected;
    private Type _unityObject = null!;
    private int _cachedPtrOffset;
    private nint _byteClass;

    // Unity 6 and earlier versions take the same kinds of arguments here (a pointer for a string, bytes or the bundle), so
    // a single signature serves both styles.
    private delegate* unmanaged<nint, uint, ulong, nint> _loadFromFile;
    private delegate* unmanaged<nint, uint, nint> _loadFromMemory;
    private delegate* unmanaged<nint, byte> _isStreamedSceneBundle;
    private delegate* unmanaged<nint, nint, byte> _contains;
    private delegate* unmanaged<nint, nint, nint, nint> _loadAsset;
    private delegate* unmanaged<nint, nint, nint, nint> _loadAssetWithSubAssets;
    private delegate* unmanaged<nint, byte, void> _unload;
    private delegate* unmanaged<nint, nint> _getAllAssetNames;

    // Looked up when a mod first uses asset bundles, after the game is ready and Unity has registered its functions.
    public string? Unsupported
    {
        get
        {
            if (!_resolved)
            {
                _unsupported = Resolve();
                _resolved = true;
            }
            return _unsupported;
        }
    }

    public string? CheckAssetType(Type type)
    {
        if (!_unityObject.IsAssignableFrom(type))
            return $"{type.FullName} is not a Unity object type; load assets as UnityEngine.Object or a derived type (from the game's interop assemblies)";
        return Class(type) == 0 ? $"{type.FullName} has no IL2CPP class in this game" : null;
    }

    public object? LoadFromFile(string path) =>
        WithString(path, p => Loaded(Result(_loadFromFile(p, 0, 0))));

    public object? LoadFromMemory(byte[] data)
    {
        if (_loadFromMemory == null)
            throw new NotSupportedException("This game's Unity build has no AssetBundle.LoadFromMemory; ship the bundle as a file in the mod's folder");
        nint result;
        if (_injected)
        {
            fixed (byte* bytes = data)
            {
                var span = new ManagedSpanWrapper { Begin = data.Length == 0 ? (void*)1 : bytes, Length = data.Length };
                result = _loadFromMemory((nint)(&span), 0);
            }
        }
        else
        {
            var array = IL2CPP.il2cpp_array_new(_byteClass, (ulong)data.Length);
            Marshal.Copy(data, 0, array + s_arrayData, data.Length);
            result = _loadFromMemory(array, 0);
        }
        return Loaded(Result(result));
    }

    public bool IsLoaded(object bundle)
    {
        var handle = ((Bundle)bundle).Handle;
        if (handle == 0)
            return false;
        var target = IL2CPP.il2cpp_gchandle_get_target(handle);
        return target != 0 && *(nint*)(target + _cachedPtrOffset) != 0;
    }

    public bool IsStreamedSceneBundle(object bundle) => _isStreamedSceneBundle(Self(bundle)) != 0;

    public string[] GetAllAssetNames(object bundle)
    {
        var array = _getAllAssetNames(Self(bundle));
        var names = new string[array == 0 ? 0 : IL2CPP.il2cpp_array_length(array)];
        for (var i = 0; i < names.Length; i++)
            names[i] = Il2CppString(*(nint*)(array + s_arrayData + i * IntPtr.Size));
        return names;
    }

    public bool Contains(object bundle, string name)
    {
        if (_contains == null)
            return GetAllAssetNames(bundle).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        var self = Self(bundle);
        return WithString(name, n => _contains(self, n) != 0);
    }

    public object? LoadAsset(object bundle, string name, Type type)
    {
        var self = Self(bundle);
        var typeObject = TypeObject(type);
        return Il2CppObjectPool.Get(WithString(name, n => Result(_loadAsset(self, n, typeObject))));
    }

    public object[] LoadAssetWithSubAssets(object bundle, string name, Type type)
    {
        if (_loadAssetWithSubAssets == null)
            throw new NotSupportedException("This game's Unity build has no AssetBundle.LoadAssetWithSubAssets");
        var self = Self(bundle);
        var typeObject = TypeObject(type);
        var array = WithString(name, n => _loadAssetWithSubAssets(self, n, typeObject));
        var count = array == 0 ? 0 : (int)IL2CPP.il2cpp_array_length(array);
        var assets = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            if (Il2CppObjectPool.Get(*(nint*)(array + s_arrayData + i * IntPtr.Size)) is { } asset)
                assets.Add(asset);
        }
        return [.. assets];
    }

    public void Unload(object bundle, bool unloadAllLoadedObjects) => _unload(Self(bundle), unloadAllLoadedObjects ? (byte)1 : (byte)0);

    public void Release(object bundle)
    {
        var loaded = (Bundle)bundle;
        if (loaded.Handle != 0)
            IL2CPP.il2cpp_gchandle_free(loaded.Handle);
        loaded.Handle = 0;
    }

    private string? Resolve()
    {
        var injected = Icall("LoadFromFile_Internal_Injected");
        _injected = injected != 0;
        var suffix = _injected ? "_Injected" : "";
        _loadFromFile = (delegate* unmanaged<nint, uint, ulong, nint>)(_injected ? injected : Icall("LoadFromFile_Internal"));
        _loadFromMemory = (delegate* unmanaged<nint, uint, nint>)Icall("LoadFromMemory_Internal" + suffix);
        _isStreamedSceneBundle = (delegate* unmanaged<nint, byte>)Icall("get_isStreamedSceneAssetBundle" + suffix);
        _contains = (delegate* unmanaged<nint, nint, byte>)Icall("Contains" + suffix);
        _loadAsset = (delegate* unmanaged<nint, nint, nint, nint>)Icall("LoadAsset_Internal" + suffix);
        _loadAssetWithSubAssets = (delegate* unmanaged<nint, nint, nint, nint>)Icall("LoadAssetWithSubAssets_Internal" + suffix);
        _unload = (delegate* unmanaged<nint, byte, void>)Icall("Unload" + suffix);
        _getAllAssetNames = (delegate* unmanaged<nint, nint>)Icall("GetAllAssetNames" + suffix);
        if (_loadFromFile == null || _isStreamedSceneBundle == null || _loadAsset == null || _unload == null || _getAllAssetNames == null)
            return "This game's Unity build has no supported asset bundle functions (UnityEngine.AssetBundle's LoadFromFile_Internal and LoadAsset_Internal)";

        _unityObject = Type.GetType("UnityEngine.Object, UnityEngine.CoreModule")
            ?? throw new InvalidOperationException("The interop assemblies have no UnityEngine.Object");
        var cachedPtr = IL2CPP.il2cpp_class_get_field_from_name(Class(_unityObject), "m_CachedPtr");
        if (cachedPtr == 0)
            return "This game's UnityEngine.Object has no m_CachedPtr, which Lustral requires to call Unity's asset bundle functions";
        _cachedPtrOffset = (int)IL2CPP.il2cpp_field_get_offset(cachedPtr);
        _byteClass = IL2CPP.il2cpp_class_from_name(IL2CPP.il2cpp_get_corlib(), "System", "Byte");
        return null;
    }

    private static nint Icall(string name) => IL2CPP.il2cpp_resolve_icall(AssetBundle + name);

    private nint Class(Type type)
    {
        if (_classes.TryGetValue(type, out var pointer))
            return pointer;
        try
        {
            pointer = Il2CppType.GetClassPointer(type);
        }
        catch (ArgumentException)
        {
            pointer = 0; // Not an interop type.
        }
        _classes[type] = pointer;
        return pointer;
    }

    // The System.Type object, as Unity's functions take it. IL2CPP never frees type objects.
    private nint TypeObject(Type type) => IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(Class(type)));

    // The bundle argument for native functions: its native object on Unity 6, or its managed object on earlier versions.
    private nint Self(object bundle)
    {
        var target = IL2CPP.il2cpp_gchandle_get_target(((Bundle)bundle).Handle);
        var native = target == 0 ? 0 : *(nint*)(target + _cachedPtrOffset);
        if (native == 0)
            throw new ObjectDisposedException("AssetBundle", "The asset bundle has been unloaded");
        return _injected ? native : target;
    }

    // Unity 6 returns a GC handle to the object's managed wrapper, which Unity owns and which must not be freed; earlier
    // versions return the object.
    private nint Result(nint result) => _injected && result != 0 ? IL2CPP.il2cpp_gchandle_get_target(result) : result;

    private static Bundle? Loaded(nint bundle) => bundle == 0 ? null : new Bundle(IL2CPP.il2cpp_gchandle_new(bundle, false));

    // Unity 6 reads the string's characters (an empty string uses a non-null pointer, as Unity's StringMarshaller does).
    // Earlier versions take an IL2CPP string, which remains on this thread's stack until the call returns.
    private T WithString<T>(string text, Func<nint, T> call)
    {
        fixed (char* chars = text)
        {
            if (!_injected)
                return call(IL2CPP.il2cpp_string_new_utf16(chars, text.Length));
            var span = new ManagedSpanWrapper { Begin = text.Length == 0 ? (void*)1 : chars, Length = text.Length };
            return call((nint)(&span));
        }
    }

    // Il2CppString layout: object header (class, monitor), int32 length, UTF-16 characters.
    private static string Il2CppString(nint pointer) =>
        pointer == 0 ? "" : new string((char*)(pointer + 2 * IntPtr.Size + sizeof(int)), 0, *(int*)(pointer + 2 * IntPtr.Size));
}
