#if LUSTRAL_IL2CPP
using Il2CppInterop.Common.Attributes;
#endif

namespace Lustral.Samples.AnyGame;

/// <summary>
/// A single project, built once for Mono games and once for IL2CPP games. <c>LUSTRAL_MONO</c> and <c>LUSTRAL_IL2CPP</c>
/// mark the code that differs between them; Lustral's API is identical on both.
/// </summary>
public sealed class HelloMod : Mod
{
    internal static HelloMod Instance { get; private set; } = null!;

    public HelloMod() => Instance = this;

    public override void OnGameReady()
    {
        Log.Info($"Hello from {Game}");
        var host = new UnityEngine.GameObject("Lustral sample");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<FrameCounter>();
    }

    internal void Counted(int frames)
    {
        if (frames == 1)
            Log.Info("FrameCounter is running: the mod's component is called by Unity");
        else if (frames % 600 == 0)
            Log.Info($"{frames} frames");
    }
}

#if LUSTRAL_IL2CPP
/// <summary>
/// On IL2CPP, the mod's MonoBehaviour is an injected type: it is <c>partial</c>, methods called by Unity are marked
/// <c>[Il2CppMethod]</c>, and its state is stored in <c>[Il2CppField]</c> properties.
/// </summary>
[InjectedType]
public partial class FrameCounter : UnityEngine.MonoBehaviour
{
    [Il2CppField]
    public partial Il2CppSystem.Int32 Frames { get; set; }

    [Il2CppMethod]
    private void Update()
    {
        Frames = Frames + 1;
        HelloMod.Instance.Counted((int)Frames);
    }
}
#else
/// <summary>On Mono, the mod's MonoBehaviour is an ordinary MonoBehaviour.</summary>
public class FrameCounter : UnityEngine.MonoBehaviour
{
    private int _frames;

    private void Update() => HelloMod.Instance.Counted(++_frames);
}
#endif
