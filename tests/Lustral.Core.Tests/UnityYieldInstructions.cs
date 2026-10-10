// Substitutes for Unity's yield instructions, matching UnityEngine.CoreModule's names and the members Lustral reads, for
// CoroutineTests. Lustral.Api recognizes them by name.
namespace UnityEngine;

public class YieldInstruction;

public sealed class WaitForSeconds(float seconds) : YieldInstruction
{
#pragma warning disable IDE0052 // Read by Lustral through reflection, as Unity's own field.
    internal float m_Seconds = seconds;
#pragma warning restore IDE0052
}

public sealed class WaitForFixedUpdate : YieldInstruction;

public abstract class CustomYieldInstruction : System.Collections.IEnumerator
{
    public abstract bool keepWaiting { get; }

    public object? Current => null;

    public bool MoveNext() => keepWaiting;

    public void Reset()
    {
    }
}

public sealed class WaitUntil(Func<bool> predicate) : CustomYieldInstruction
{
    public override bool keepWaiting => !predicate();
}
