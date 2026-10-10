#if LUSTRAL_IL2CPP
using Il2CppInterop.Common.Attributes;
using UnityEngine.EventSystems;

namespace Lustral.Tests.Player;

/// <summary>
/// The test mod's MonoBehaviour on IL2CPP, written as Il2CppInterop V2 documents injected classes: a partial class marked
/// <c>[InjectedType]</c> (its source generator adds the rest), <c>[Il2CppMethod]</c> on methods called by Unity, and state
/// in <c>[Il2CppField]</c> and <c>[ManagedField]</c> properties, which are stored on the IL2CPP object. It also implements
/// an IL2CPP interface, <c>IPointerClickHandler</c>, which the test player's code calls.
/// </summary>
[InjectedType]
public partial class InjectedComponent : UnityEngine.MonoBehaviour, IPointerClickHandler
{
    [Il2CppField]
    public partial Il2CppSystem.Int32 Updates { get; set; }

    [ManagedField]
    public partial List<string> Messages { get; set; }

    // Il2CppInterop V2 beta.8's generated getter throws for a [ManagedField] that has never been set, so it is set first.
    [Il2CppMethod]
    private void Awake() => Messages = [];

    [Il2CppMethod]
    private void Start()
    {
        Messages.Add("started");
        TestPlayerMod.Instance.Log.Info("e2e: injected component started");
    }

    [Il2CppMethod]
    private void Update() => Updates = Updates + 1;

    // Called by Unity's SendMessage, with its argument.
    [Il2CppMethod]
    private void Receive(Il2CppSystem.String text) => Messages.Add(text);

    // Called by the test player through the interface: once directly, once through Unity UI's ExecuteEvents.
    [Il2CppMethod]
    public void OnPointerClick(PointerEventData eventData) => Messages.Add($"click {(int)eventData.clickCount}");
}
#endif
