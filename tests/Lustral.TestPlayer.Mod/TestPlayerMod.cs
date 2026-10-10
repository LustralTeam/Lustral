using System.Collections;
using System.ComponentModel;
using HarmonyLib;
#if LUSTRAL_IL2CPP
using TestDriver = Il2CppLustral.TestPlayer.TestDriver;
#else
using TestDriver = Lustral.TestPlayer.TestDriver;
#endif

namespace Lustral.Tests.Player;

/// <summary>
/// Exercises the APIs that mods use on the test player and logs "e2e: ..." lines for tools/e2e.ps1 to check. With
/// -lustral-e2e throw-on-thread on the player's command line, it throws on a separate thread once the game is ready.
/// </summary>
public sealed class TestPlayerMod : Mod
{
    private static int s_updates;
    private static int s_lateUpdates;
    private static int s_lateAfterUpdate;
    private static int s_fixedUpdates;
    private static int s_guiCalls;
    private static bool s_updatedThisFrame;

    /// <summary>The mod instance, for use outside this class, for example in patches.</summary>
    internal static TestPlayerMod Instance { get; private set; } = null!;

    public TestPlayerMod() => Instance = this;

    /// <summary>The number of the test driver's Update calls, counted by a patch.</summary>
    internal static int DriverFrames;

    public override void OnLoad()
    {
        var settings = Config.Bind<Settings>();
        Harmony.PatchAll(typeof(TestPlayerMod).Assembly);
        Events.SceneLoaded += scene => Log.Info($"e2e: scene loaded {scene.Name} ({scene.BuildIndex})");
        Events.Update += () =>
        {
            s_updates++;
            s_updatedThisFrame = true;
        };
        Events.LateUpdate += () =>
        {
            s_lateUpdates++;
            if (s_updatedThisFrame)
                s_lateAfterUpdate++;
            s_updatedThisFrame = false;
        };
        Events.FixedUpdate += () => s_fixedUpdates++;
        Events.OnGUI += () =>
        {
            s_guiCalls++;
            DrawLabel();
        };
        Log.Info($"e2e: loaded, greeting {settings.Greeting}, {Harmony.GetPatchedMethods().Count()} patched method(s)");
        AssetBundleCheck.BeforeReady(this);
    }

    public override void OnGameReady()
    {
        Log.Info("e2e: game ready");
        Log.Info($"e2e: game {Game.Name} by {Game.Developer}, version {Game.Version ?? "unknown"}, Unity {Game.UnityVersion}, {Game.Backend}");
        Coroutines.Start(EveryWait());
        AssetBundleCheck.Run(this);
#if LUSTRAL_IL2CPP
        Log.Info($"e2e: IL2CPP list enumerated, total length {EnumerateIl2CppList()}");
        Log.Info($"e2e: unstripped: {UnstrippedColorLerp()}");
        AddInjectedComponent();
#else
        AddClickTarget();
#endif
        if (Argument("-lustral-e2e") == "throw-on-thread")
        {
            Log.Info("e2e: throwing on a thread");
            new Thread(() => throw new InvalidOperationException("e2e: unhandled exception in a mod's thread")).Start();
        }
    }

    // Lustral's Update should run once per frame, as often as the game's MonoBehaviour.Update, allowing for the frames
    // before the hooks were installed.
    public override void OnShutdown()
    {
        Log.Info($"e2e: shutdown after {s_updates} updates and {DriverFrames} driver frames");
        Log.Info($"e2e: {s_lateUpdates} late updates ({s_lateAfterUpdate} after an Update), {s_fixedUpdates} fixed updates, {s_guiCalls} GUI calls");
#if LUSTRAL_IL2CPP
        if (s_component is not null)
            Log.Info($"e2e: injected component: {(int)s_component.Updates} updates, messages {string.Join(", ", s_component.Messages)}");
#else
        if (s_clickTarget is not null)
            Log.Info($"e2e: click target: messages {string.Join(", ", s_clickTarget.Messages)}");
#endif
    }

#if LUSTRAL_IL2CPP
    private static InjectedComponent? s_component;

    // A GameObject that persists across scene loads, with the mod's component; SendMessage calls a method with an argument.
    private void AddInjectedComponent()
    {
        var host = new UnityEngine.GameObject((Il2CppSystem.String)"Lustral e2e");
        UnityEngine.Object.DontDestroyOnLoad(host);
        s_component = host.AddComponent<InjectedComponent>();
        host.SendMessage((Il2CppSystem.String)"Receive", (Il2CppSystem.String)"hello", UnityEngine.SendMessageOptions.RequireReceiver);
        Log.Info("e2e: injected component added");
    }

    // Color.Lerp and Color.grayscale, which are excluded from the test player's build. With unstripped interop assemblies,
    // they run Unity's code (halfway between black and white has a grayscale value of 0.50). They are looked up by
    // reflection, because the mod is also built against interop assemblies that are not unstripped.
    private static string UnstrippedColorLerp()
    {
        var color = typeof(UnityEngine.Color);
        var lerp = color.GetMethod("Lerp", [color, color, typeof(Il2CppSystem.Single)]);
        var grayscale = color.GetProperty("grayscale");
        if (lerp is null || grayscale is null)
            return "Color.Lerp isn't there";
        var mixed = lerp.Invoke(null, [color.GetProperty("black")!.GetValue(null), color.GetProperty("white")!.GetValue(null), (Il2CppSystem.Single)0.5f]);
        return "Color.Lerp grayscale " + ((float)(Il2CppSystem.Single)grayscale.GetValue(mixed)!).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }

    // List<string>.Enumerator is an interop type nested in a generic instance, which Il2CppInterop registers through
    // Lustral's fix for its nested type lookup. Enumeration must return the added items (total length 6).
    private static int EnumerateIl2CppList()
    {
        var list = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.String>();
        for (var length = 1; length <= 3; length++)
            list.Add(new string('x', length)); // Converted to Il2CppSystem.String
        var total = 0;
        var enumerator = list.GetEnumerator();
        while (enumerator.MoveNext())
            total += enumerator.Current.Length;
        return total;
    }
#else
    private static ClickTarget? s_clickTarget;

    // A GameObject that persists across scene loads, with the mod's component, which the test player clicks.
    private void AddClickTarget()
    {
        var host = new UnityEngine.GameObject("Lustral e2e");
        UnityEngine.Object.DontDestroyOnLoad(host);
        s_clickTarget = host.AddComponent<ClickTarget>();
        Log.Info("e2e: click target added");
    }
#endif

#if LUSTRAL_IL2CPP
    private static System.Reflection.MethodInfo? s_label;
    private static bool s_labelLooked;
#endif

    // An actual IMGUI call, which throws outside a valid GUI event; the log's "no warnings or errors" check detects this.
    // IL2CPP builds strip GUI methods that the game does not use (the 6000.5 test player has no GUI.Label), so on IL2CPP the
    // method is looked up by reflection.
    private static void DrawLabel()
    {
#if LUSTRAL_IL2CPP
        if (!s_labelLooked)
        {
            s_labelLooked = true;
            s_label = typeof(UnityEngine.GUI).GetMethod("Label", [typeof(UnityEngine.Rect), typeof(Il2CppSystem.String)]);
            Instance.Log.Info($"e2e: GUI.Label {(s_label is null ? "stripped from this build" : "found")}");
        }
        s_label?.Invoke(null, [new UnityEngine.Rect(10, 10, 300, 20), (Il2CppSystem.String)"Lustral e2e: OnGUI"]);
#else
        UnityEngine.GUI.Label(new UnityEngine.Rect(10, 10, 300, 20), "Lustral e2e: OnGUI");
#endif
    }

    // Each type of wait a coroutine can yield, in order; e2e checks the list and that it spanned several frames.
    private IEnumerator EveryWait()
    {
        var steps = new List<string>();
        var startedAt = s_updates;
        yield return null;
        steps.Add("frame");
        yield return Wait.Frames(3);
        steps.Add("frames");
        yield return Wait.FixedUpdate;
        steps.Add("fixed");
        yield return Wait.LateUpdate;
        steps.Add("late");
        yield return Wait.Seconds(0.1);
        steps.Add("seconds");
        yield return Nested(steps);
#if !LUSTRAL_IL2CPP
        // Unity's own type (IL2CPP builds strip its constructor unless the game uses it).
        yield return new UnityEngine.WaitForSeconds(0.05f);
        steps.Add("unity");
#endif
        Log.Info($"e2e: coroutine ran {string.Join(", ", steps)} over {s_updates - startedAt} updates");
    }

    private static IEnumerator Nested(List<string> steps)
    {
        yield return null;
        steps.Add("nested");
    }

    private static string? Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

public sealed class Settings
{
    [Description("Logged when the mod loads.")]
    public string Greeting { get; set; } = "hello";
}

[HarmonyPatch(typeof(TestDriver), "Awake")]
internal static class TestDriverAwakePatch
{
    private static void Postfix() => TestPlayerMod.Instance.Log.Info("e2e: TestDriver.Awake patched");
}

[HarmonyPatch(typeof(TestDriver), "Update")]
internal static class TestDriverUpdatePatch
{
    private static void Postfix() => TestPlayerMod.DriverFrames++;
}
