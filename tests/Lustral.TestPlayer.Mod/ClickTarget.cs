#if !LUSTRAL_IL2CPP
using UnityEngine.EventSystems;

namespace Lustral.Tests.Player;

/// <summary>
/// The test mod's MonoBehaviour on Mono, which receives the test player's clicks as <c>InjectedComponent</c> does on
/// IL2CPP.
/// </summary>
public sealed class ClickTarget : UnityEngine.MonoBehaviour, IPointerClickHandler
{
    public List<string> Messages { get; } = [];

    public void OnPointerClick(PointerEventData eventData) => Messages.Add($"click {eventData.clickCount}");
}
#endif
