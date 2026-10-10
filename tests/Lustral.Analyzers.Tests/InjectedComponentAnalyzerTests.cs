namespace Lustral.Analyzers.Tests;

public class InjectedComponentAnalyzerTests
{
    // Substitutes for Il2CppInterop's attributes and an interop MonoBehaviour.
    private static readonly Microsoft.CodeAnalysis.MetadataReference Interop = AnalyzerRunner.Library("Il2CppInterop.Common", """
        namespace Il2CppInterop.Common
        {
            public interface IIl2CppType<TSelf> where TSelf : IIl2CppType<TSelf> { }
        }
        namespace Il2CppInterop.Common.Attributes
        {
            public sealed class InjectedTypeAttribute : System.Attribute { }
            public sealed class Il2CppMethodAttribute : System.Attribute { public string? Name { get; set; } }
            public sealed class Il2CppFieldAttribute : System.Attribute { }
            public sealed class ManagedFieldAttribute : System.Attribute { }
        }
        namespace Il2CppSystem
        {
            public struct Int32 : Il2CppInterop.Common.IIl2CppType<Int32> { }
            public interface IObject { }
            public interface IComparable<T> : IObject { }
            public interface IComparable : IObject { }
            public class Object : IObject { }
            public class Holder<T> : Object { }
        }
        """);

    private static readonly Microsoft.CodeAnalysis.MetadataReference Unity = AnalyzerRunner.Library("UnityEngine.CoreModule", """
        namespace UnityEngine
        {
            public class Behaviour { }
            public class MonoBehaviour : Behaviour { }
        }
        """);

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Run(string source, bool withInterop = true) =>
        withInterop
            ? AnalyzerRunner.Run(new InjectedComponentAnalyzer(), source, modKind: null, Interop, Unity)
            : AnalyzerRunner.Run(new InjectedComponentAnalyzer(), source, modKind: null, Unity);

    [Fact]
    public async Task Unity_messages_without_Il2CppMethod_are_reported()
    {
        var diagnostics = await Run("""
            using Il2CppInterop.Common.Attributes;

            [InjectedType]
            public partial class Spinner : UnityEngine.MonoBehaviour
            {
                [Il2CppMethod] private void Awake() { }
                private void Update() { }
                private void OnTriggerEnter(object other) { }
                private void Helper() { }
                private static void Start() { }
            }

            public class Base : UnityEngine.MonoBehaviour { }

            [InjectedType]
            public partial class Derived : Base
            {
                private void LateUpdate() { }
            }
            """);

        Assert.Equal(["LUS0301", "LUS0301", "LUS0301"], diagnostics.Ids());
        Assert.Equal(
            ["Unity will not call Spinner.Update", "Unity will not call Spinner.OnTriggerEnter", "Unity will not call Derived.LateUpdate"],
            diagnostics.Select(d => d.GetMessage()[..d.GetMessage().IndexOf(':')]).ToList());
    }

    [Fact]
    public async Task Generic_methods_marked_Il2CppMethod_are_reported()
    {
        var diagnostics = await Run("""
            using Il2CppInterop.Common.Attributes;

            [InjectedType]
            public partial class Spawner : UnityEngine.MonoBehaviour
            {
                [Il2CppMethod] private void Spawn<T>() { }
                [Il2CppMethod] private void Spawn() => Spawn<int>();
                private T Pick<T>(T value) => value;
            }
            """);

        Assert.Equal(["LUS0303"], diagnostics.Ids());
        Assert.StartsWith("Spawner.Spawn is generic, so [Il2CppMethod] has no effect", diagnostics[0].GetMessage());
    }

    [Fact]
    public async Task Types_that_Il2CppInterop_cannot_inject_are_errors()
    {
        var diagnostics = await Run("""
            using Il2CppInterop.Common.Attributes;

            [InjectedType]
            public partial class Box<T> : Il2CppSystem.Object { }

            public static class Outer
            {
                [InjectedType]
                public partial class Inner : Il2CppSystem.Object { }
            }

            [InjectedType]
            public partial class FromGeneric : Il2CppSystem.Holder<int> { }

            [InjectedType]
            public partial class Comparable : Il2CppSystem.Object, Il2CppSystem.IComparable<Comparable>, Il2CppSystem.IComparable,
                System.IEquatable<Comparable>
            {
                public bool Equals(Comparable? other) => false;
            }

            [InjectedType]
            public partial class Fine : Il2CppSystem.Object, Il2CppSystem.IComparable { }
            """);

        Assert.Equal(
        [
            "Box cannot be injected into IL2CPP: generic types cannot be injected",
            "Inner cannot be injected into IL2CPP: nested types cannot be injected; move it out of Outer",
            "FromGeneric cannot be injected into IL2CPP: its base type Holder<int> is generic",
            "Comparable cannot be injected into IL2CPP: it implements the generic IL2CPP interface IComparable<Comparable>",
        ], diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public async Task IL2CPP_interface_methods_without_Il2CppMethod_are_errors()
    {
        var diagnostics = await Run("""
            using Il2CppInterop.Common.Attributes;

            namespace Game
            {
                public interface IEventHandler : Il2CppSystem.IObject { }
                public interface IClickHandler : IEventHandler { void OnClick(Il2CppSystem.Object data); }
            }

            [InjectedType]
            public partial class Clickable : UnityEngine.MonoBehaviour, Game.IClickHandler
            {
                public void OnClick(Il2CppSystem.Object data) { }
            }

            [InjectedType]
            public partial class Explicit : UnityEngine.MonoBehaviour, Game.IClickHandler
            {
                void Game.IClickHandler.OnClick(Il2CppSystem.Object data) { }
            }

            [InjectedType]
            public partial class Exposed : UnityEngine.MonoBehaviour, Game.IClickHandler, System.IEquatable<Exposed>
            {
                [Il2CppMethod] public void OnClick(Il2CppSystem.Object data) { }
                public bool Equals(Exposed? other) => false;
            }

            // Stands for an interop class, whose implementation is an IL2CPP method.
            public class GameButton : Il2CppSystem.Object, Game.IClickHandler
            {
                public void OnClick(Il2CppSystem.Object data) { }
            }

            [InjectedType]
            public partial class Inherited : GameButton { }
            """);

        Assert.Equal(
        [
            "Clickable.OnClick implements IClickHandler.OnClick for IL2CPP, so it requires [Il2CppMethod]: without it, Il2CppInterop cannot register Clickable, and the mod fails to load",
            "Explicit.Game.IClickHandler.OnClick implements IClickHandler.OnClick for IL2CPP, so it requires [Il2CppMethod]: without it, Il2CppInterop cannot register Explicit, and the mod fails to load",
        ], diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public async Task Classes_that_arent_injected_MonoBehaviours_are_left_alone()
    {
        Assert.Empty(await Run("""
            using Il2CppInterop.Common.Attributes;

            public class NotInjected : UnityEngine.MonoBehaviour
            {
                private void Update() { }
            }

            [InjectedType]
            public partial class NotABehaviour
            {
                private void Update() { }
            }
            """));
        Assert.Empty(await Run("""
            public class MonoGameBehaviour : UnityEngine.MonoBehaviour
            {
                private void Update() { }
            }
            """, withInterop: false));
    }

    [Fact]
    public async Task Il2CppFields_of_dotnet_types_are_reported_with_the_interop_type_to_use()
    {
        var diagnostics = await Run("""
            using Il2CppInterop.Common.Attributes;

            [InjectedType]
            public partial class Tracker : UnityEngine.MonoBehaviour
            {
                [Il2CppField] public partial int Hits { get; set; }
                [Il2CppField] public partial string Name { get; set; }
                [Il2CppField] public partial System.Collections.Generic.List<int> Seen { get; set; }
                [Il2CppField] public partial Il2CppSystem.Int32 Fine { get; set; }
                [ManagedField] public partial System.Collections.Generic.List<int> AlsoFine { get; set; }
            }

            // The code added by Il2CppInterop's source generator.
            public partial class Tracker
            {
                public partial int Hits { get => 0; set { } }
                public partial string Name { get => ""; set { } }
                public partial System.Collections.Generic.List<int> Seen { get => null!; set { } }
                public partial Il2CppSystem.Int32 Fine { get => default; set { } }
                public partial System.Collections.Generic.List<int> AlsoFine { get => null!; set { } }
            }
            """);

        Assert.Equal(["LUS0302", "LUS0302", "LUS0302"], diagnostics.Ids());
        Assert.Equal(
            [
                "Tracker.Hits is int, which an [Il2CppField] cannot store: use Il2CppSystem.Int32, which converts to and from int",
                "Tracker.Name is string, which an [Il2CppField] cannot store: use Il2CppSystem.String, which converts to and from string",
                "Tracker.Seen is System.Collections.Generic.List<int>, which an [Il2CppField] cannot store: use a type from the game's interop assemblies, or make it a [ManagedField] to keep a .NET object",
            ],
            diagnostics.Select(d => d.GetMessage()).ToList());
    }
}
