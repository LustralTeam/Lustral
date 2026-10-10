using System.Collections;

namespace Lustral;

/// <summary>
/// A mod's coroutines: methods that run across multiple frames on the game's main thread, with identical behavior on Mono
/// and IL2CPP. A coroutine is an iterator method; each <c>yield return</c> specifies when it resumes:
/// <list type="bullet">
/// <item><c>null</c> or <see cref="Wait.NextFrame"/>: in the next frame, after <c>Update</c></item>
/// <item><see cref="Wait.Seconds"/>, <see cref="Wait.RealSeconds"/>, <see cref="Wait.Frames"/>, <see cref="Wait.Until"/>,
/// <see cref="Wait.While"/>, <see cref="Wait.FixedUpdate"/>, <see cref="Wait.LateUpdate"/></item>
/// <item>another iterator: after that iterator has run to completion</item>
/// <item>a <see cref="CoroutineHandle"/>: after that coroutine ends; a <c>Task</c>: after the task completes</item>
/// <item>Unity yield instructions: <c>WaitForSeconds</c>, <c>WaitForFixedUpdate</c>, <c>WaitForEndOfFrame</c> (treated as
/// LateUpdate), <c>WaitUntil</c>, <c>WaitWhile</c> and other <c>CustomYieldInstruction</c> types, and
/// <c>AsyncOperation</c></item>
/// </list>
/// An exception stops the coroutine and is logged under the mod's ID.
/// </summary>
public sealed class Coroutines
{
    private readonly CoroutineScheduler _scheduler;
    private readonly Action<string, Exception> _onError;

    internal Coroutines(CoroutineScheduler scheduler, Action<string, Exception> onError)
    {
        _scheduler = scheduler;
        _onError = onError;
    }

    /// <summary>The number of this mod's running coroutines.</summary>
    public int Count => _scheduler.CountOf(this);

    /// <summary>
    /// Starts a coroutine. When called on the main thread, the coroutine immediately runs until its first <c>yield</c>, as
    /// with Unity's <c>StartCoroutine</c>. When called from another thread, its first step runs on the main thread in the
    /// next frame.
    /// </summary>
    public CoroutineHandle Start(IEnumerator routine)
    {
        if (routine is null)
            throw new ArgumentNullException(nameof(routine));
        return _scheduler.Start(this, routine);
    }

    /// <summary>Stops a coroutine before its next step. Has no effect if the coroutine has already ended.</summary>
    public void Stop(CoroutineHandle coroutine)
    {
        if (coroutine is null)
            throw new ArgumentNullException(nameof(coroutine));
        coroutine.Stop();
    }

    /// <summary>Stops all coroutines started by this mod.</summary>
    public void StopAll() => _scheduler.StopAll(this);

    internal void ReportError(string name, Exception exception) => _onError(name, exception);
}

/// <summary>A running coroutine. Yielding it from another coroutine waits for it to end.</summary>
public sealed class CoroutineHandle
{
    private volatile bool _stopped;
    private volatile bool _finished;

    internal CoroutineHandle(string name) => Name = name;

    /// <summary>The name of the iterator method, as shown in the log.</summary>
    public string Name { get; }

    /// <summary>Whether the coroutine is still running, that is, it has not completed, been stopped or failed.</summary>
    public bool IsRunning => !_stopped && !_finished;

    internal bool IsStopped => _stopped;

    internal void Stop() => _stopped = true;

    internal void Finish() => _finished = true;

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A condition that a coroutine waits for: <c>yield return Wait.Seconds(1)</c>.</summary>
public sealed class Wait
{
    internal enum Kind
    {
        NextFrame,
        Frames,
        Seconds,
        RealSeconds,
        Until,
        While,
        FixedUpdate,
        LateUpdate,
    }

    private Wait(Kind kind, double amount = 0, Func<bool>? condition = null)
    {
        What = kind;
        Amount = amount;
        Condition = condition;
    }

    internal Kind What { get; }

    internal double Amount { get; }

    internal Func<bool>? Condition { get; }

    /// <summary>The next frame, after the game's <c>Update</c> methods. Equivalent to <c>yield return null</c>.</summary>
    public static Wait NextFrame { get; } = new(Kind.NextFrame);

    /// <summary>The next physics step (<c>FixedUpdate</c>), as Unity's <c>WaitForFixedUpdate</c>.</summary>
    public static Wait FixedUpdate { get; } = new(Kind.FixedUpdate);

    /// <summary>After the game's <c>LateUpdate</c> methods in the current frame, or in the next frame if they have already run.</summary>
    public static Wait LateUpdate { get; } = new(Kind.LateUpdate);

    /// <summary>
    /// The specified duration in game time, as Unity's <c>WaitForSeconds</c>: it is affected by the game's time scale and
    /// does not elapse while the game is paused. In game builds where Lustral cannot read Unity's time, real time is used.
    /// </summary>
    public static Wait Seconds(double seconds) => new(Kind.Seconds, NonNegative(seconds, nameof(seconds)));

    /// <summary>The specified duration in real time, regardless of the game's time scale, as Unity's <c>WaitForSecondsRealtime</c>.</summary>
    public static Wait RealSeconds(double seconds) => new(Kind.RealSeconds, NonNegative(seconds, nameof(seconds)));

    /// <summary>The specified number of frames; 1 is the next frame.</summary>
    public static Wait Frames(int frames) => new(Kind.Frames, NonNegative(frames, nameof(frames)));

    /// <summary>Until the condition is true. The condition is checked immediately, then once per frame after <c>Update</c>.</summary>
    public static Wait Until(Func<bool> condition) =>
        new(Kind.Until, condition: condition ?? throw new ArgumentNullException(nameof(condition)));

    /// <summary>While the condition is true. The condition is checked immediately, then once per frame after <c>Update</c>.</summary>
    public static Wait While(Func<bool> condition) =>
        new(Kind.While, condition: condition ?? throw new ArgumentNullException(nameof(condition)));

    private static double NonNegative(double value, string name) =>
        value >= 0 && !double.IsNaN(value) ? value : throw new ArgumentOutOfRangeException(name, value, "Must be zero or greater");
}
