using Il2CppInterop.Common.Attributes;

namespace Lustral.Samples.Btd6;

/// <summary>Once the game is ready, adds the mod's component to a GameObject that persists across scene loads.</summary>
public sealed class HelloMod : Mod
{
    internal static HelloMod Instance { get; private set; } = null!;

    public HelloMod() => Instance = this;

    public override void OnGameReady()
    {
        var host = new UnityEngine.GameObject((Il2CppSystem.String)"Lustral sample");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<FrameCounter>();
        Log.Info($"Hello from {Game}");
    }
}

/// <summary>
/// The mod's MonoBehaviour on an IL2CPP game: marked <c>[InjectedType]</c> and <c>partial</c>, with
/// <c>[Il2CppMethod]</c> on methods called by Unity and its state in an <c>[Il2CppField]</c> property.
/// </summary>
[InjectedType]
public partial class FrameCounter : UnityEngine.MonoBehaviour
{
    [Il2CppField]
    public partial Il2CppSystem.Int32 Frames { get; set; }

    [Il2CppMethod]
    private void Start() => HelloMod.Instance.Log.Info("FrameCounter started: the mod's component is called by Unity");

    [Il2CppMethod]
    private void Update()
    {
        Frames = Frames + 1;
        if (Frames % 600 == 0)
            HelloMod.Instance.Log.Info($"{(int)Frames} frames");
    }
}
