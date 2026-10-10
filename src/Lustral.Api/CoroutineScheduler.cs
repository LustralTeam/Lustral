using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Lustral;

/// <summary>
/// Runs all mods' coroutines from Lustral's frame events; Lustral.Core's GameLifecycle calls <see cref="Tick"/> after each
/// phase's handlers. Coroutine steps run on the thread that created the scheduler, the game's main thread. Coroutines
/// started from other threads are queued until the next frame.
/// </summary>
internal sealed class CoroutineScheduler
{
    internal enum Phase
    {
        Update,
        FixedUpdate,
        LateUpdate,
    }

    private readonly int _mainThread = Environment.CurrentManagedThreadId;
    private readonly List<Routine> _routines = [];
    private readonly ConcurrentQueue<Routine> _started = new();
    private readonly Func<double> _realTime;
    private long _frames;
    private long _fixedSteps;
    private long _lateSteps;
    private Phase? _ticking;
    private double? _gameTimeThisTick;

    /// <param name="realTime">Real time in seconds. Defaults to a Stopwatch; tests can supply their own clock.</param>
    public CoroutineScheduler(Func<double>? realTime = null)
    {
        if (realTime is null)
        {
            var stopwatch = Stopwatch.StartNew();
            realTime = () => stopwatch.Elapsed.TotalSeconds;
        }
        _realTime = realTime;
    }

    /// <summary>Unity's scaled game time in seconds (<c>Time.time</c>), set by the backend. Real time is used when null.</summary>
    public Func<double>? GameTime { get; set; }

    public CoroutineHandle Start(Coroutines owner, IEnumerator routine)
    {
        var started = new Routine(owner, routine);
        if (Environment.CurrentManagedThreadId == _mainThread)
        {
            lock (_routines)
                _routines.Add(started);
            Step(started);
        }
        else
            _started.Enqueue(started);
        return started.Handle;
    }

    public int CountOf(Coroutines owner)
    {
        lock (_routines)
            return _routines.Count(r => r.Owner == owner && r.Handle.IsRunning) + _started.Count(r => r.Owner == owner && r.Handle.IsRunning);
    }

    public void StopAll(Coroutines owner)
    {
        lock (_routines)
        {
            foreach (var routine in _routines.Where(r => r.Owner == owner))
                routine.Handle.Stop();
        }
        foreach (var routine in _started.Where(r => r.Owner == owner))
            routine.Handle.Stop();
    }

    /// <summary>Runs the coroutines waiting for this phase. Update marks the start of a frame.</summary>
    public void Tick(Phase phase)
    {
        switch (phase)
        {
            case Phase.Update:
                _frames++;
                break;
            case Phase.FixedUpdate:
                _fixedSteps++;
                break;
            default:
                _lateSteps++;
                break;
        }
        _ticking = phase;
        _gameTimeThisTick = null;
        try
        {
            if (phase == Phase.Update)
            {
                while (_started.TryDequeue(out var started))
                {
                    lock (_routines)
                        _routines.Add(started);
                    Step(started);
                }
            }

            Routine[] due;
            lock (_routines)
            {
                _routines.RemoveAll(r => !r.Handle.IsRunning);
                due = _routines.Where(r => r.ResumeIn == phase).ToArray();
            }
            foreach (var routine in due)
            {
                if (routine.Handle.IsRunning && IsReady(routine))
                    Step(routine);
            }
        }
        finally
        {
            _ticking = null;
        }
    }

    private bool IsReady(Routine routine)
    {
        switch (routine.Waiting)
        {
            case Waiting.Frame:
                return _frames >= routine.Until;
            case Waiting.FixedStep:
                return _fixedSteps >= routine.Until;
            case Waiting.LateStep:
                return _lateSteps >= routine.Until;
            case Waiting.GameTime:
                return GameNow() >= routine.Deadline;
            case Waiting.RealTime:
                return _realTime() >= routine.Deadline;
            case Waiting.Condition:
                return Evaluate(routine, routine.Condition!);
            default:
                return true;
        }
    }

    // Runs the coroutine until it waits for a condition that is not yet met, ends, or throws.
    private void Step(Routine routine)
    {
        while (true)
        {
            if (routine.Handle.IsStopped)
                return;
            var top = routine.Stack.Peek();
            bool moved;
            try
            {
                moved = top.MoveNext();
            }
            catch (Exception e)
            {
                Fail(routine, e);
                return;
            }
            if (!moved)
            {
                routine.Stack.Pop();
                if (routine.Stack.Count == 0)
                {
                    routine.Handle.Finish();
                    return;
                }
                continue; // The enclosing iterator continues immediately, as with Unity's nested coroutines.
            }

            bool goOn;
            try
            {
                goOn = Await(routine, top.Current);
            }
            catch (Exception e)
            {
                Fail(routine, e); // A Unity yield instruction that could not be read.
                return;
            }
            if (!goOn)
                return; // Waiting.
        }
    }

    // Records the coroutine's wait condition. Returns true if it can continue immediately.
    private bool Await(Routine routine, object? current)
    {
        routine.Condition = null;
        switch (current)
        {
            case null:
                return Suspend(routine, Waiting.Frame, Phase.Update, NextFrame());
            case Wait wait:
                return AwaitWait(routine, wait);
            case CoroutineHandle other:
                return other.IsRunning ? WaitFor(routine, () => !other.IsRunning) : true;
            case Task task:
                return task.IsCompleted || WaitFor(routine, () => task.IsCompleted);
            case IEnumerator nested when !IsUnityYieldInstruction(nested):
                routine.Stack.Push(nested);
                return true;
            default:
                return AwaitUnity(routine, current);
        }
    }

    private bool AwaitWait(Routine routine, Wait wait)
    {
        switch (wait.What)
        {
            case Lustral.Wait.Kind.Frames:
                return wait.Amount < 1 || Suspend(routine, Waiting.Frame, Phase.Update, NextFrame() + (long)wait.Amount - 1);
            case Lustral.Wait.Kind.Seconds:
                routine.Deadline = GameNow() + wait.Amount;
                return wait.Amount <= 0 || Suspend(routine, Waiting.GameTime, Phase.Update, 0);
            case Lustral.Wait.Kind.RealSeconds:
                routine.Deadline = _realTime() + wait.Amount;
                return wait.Amount <= 0 || Suspend(routine, Waiting.RealTime, Phase.Update, 0);
            case Lustral.Wait.Kind.Until:
                return WaitFor(routine, wait.Condition!);
            case Lustral.Wait.Kind.While:
                var condition = wait.Condition!;
                return WaitFor(routine, () => !condition());
            case Lustral.Wait.Kind.FixedUpdate:
                return Suspend(routine, Waiting.FixedStep, Phase.FixedUpdate, _fixedSteps + 1);
            case Lustral.Wait.Kind.LateUpdate:
                // Later in this frame if yielded before LateUpdate, otherwise in the next frame.
                return Suspend(routine, Waiting.LateStep, Phase.LateUpdate, _lateSteps + 1);
            default:
                return Suspend(routine, Waiting.Frame, Phase.Update, NextFrame());
        }
    }

    // Unity yield instructions are matched by name: Lustral.Api cannot reference Unity, and on IL2CPP they are interop types.
    private bool AwaitUnity(Routine routine, object instruction)
    {
        for (var type = instruction.GetType(); type is not null; type = type.BaseType)
        {
            switch (type.FullName)
            {
                case "UnityEngine.WaitForSeconds":
                    var seconds = UnityValues.ToDouble(UnityValues.Member(instruction, "m_Seconds"));
                    if (seconds is null)
                        break; // Unreadable instructions resume in the next frame, like unknown values.
                    return AwaitWait(routine, Lustral.Wait.Seconds(Math.Max(0, seconds.Value)));
                case "UnityEngine.WaitForFixedUpdate":
                    return AwaitWait(routine, Lustral.Wait.FixedUpdate);
                case "UnityEngine.WaitForEndOfFrame":
                    return AwaitWait(routine, Lustral.Wait.LateUpdate);
                case "UnityEngine.CustomYieldInstruction":
                    return WaitFor(routine, () => UnityValues.ToBool(UnityValues.Member(instruction, "keepWaiting")) == false);
                case "UnityEngine.AsyncOperation":
                    return WaitFor(routine, () => UnityValues.ToBool(UnityValues.Member(instruction, "isDone")) != false);
            }
        }
        return Suspend(routine, Waiting.Frame, Phase.Update, NextFrame());
    }

    private static bool IsUnityYieldInstruction(object value)
    {
        for (var type = value.GetType(); type is not null; type = type.BaseType)
        {
            if (type.FullName is "UnityEngine.CustomYieldInstruction" or "UnityEngine.YieldInstruction")
                return true;
        }
        return false;
    }

    // Checked immediately, then once per frame after Update.
    private bool WaitFor(Routine routine, Func<bool> condition)
    {
        if (Evaluate(routine, condition))
            return true;
        if (!routine.Handle.IsRunning)
            return false; // The condition threw.
        routine.Condition = condition;
        routine.Waiting = Waiting.Condition;
        routine.ResumeIn = Phase.Update;
        return false;
    }

    private bool Evaluate(Routine routine, Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (Exception e)
        {
            Fail(routine, e);
            return false;
        }
    }

    private static bool Suspend(Routine routine, Waiting waiting, Phase phase, long until)
    {
        routine.Waiting = waiting;
        routine.ResumeIn = phase;
        routine.Until = until;
        return false;
    }

    // The next Update tick. The frame counter increments at the start of each Update, so yielding at any point in frame N
    // (or before the first frame) resumes in the Update of frame N + 1.
    private long NextFrame() => _frames + 1;

    private double GameNow()
    {
        if (GameTime is not { } gameTime)
            return _realTime();
        if (_ticking is not null)
            return _gameTimeThisTick ??= gameTime();
        return gameTime();
    }

    private void Fail(Routine routine, Exception exception)
    {
        routine.Handle.Stop();
        routine.Owner.ReportError(routine.Handle.Name, exception is TargetInvocationException { InnerException: { } inner } ? inner : exception);
    }

    private enum Waiting
    {
        None,
        Frame,
        FixedStep,
        LateStep,
        GameTime,
        RealTime,
        Condition,
    }

    private sealed class Routine(Coroutines owner, IEnumerator iterator)
    {
        public Coroutines Owner { get; } = owner;
        public CoroutineHandle Handle { get; } = new(NameOf(iterator));
        public Stack<IEnumerator> Stack { get; } = new([iterator]);
        public Phase ResumeIn { get; set; }
        public Waiting Waiting { get; set; }
        public long Until { get; set; }
        public double Deadline { get; set; }
        public Func<bool>? Condition { get; set; }

        // An iterator method compiles to a class named "<Method>d__N"; a local function's to "<<Method>g__Local|1_0>d".
        private static string NameOf(IEnumerator iterator)
        {
            var name = iterator.GetType().Name;
            var local = name.IndexOf(">g__", StringComparison.Ordinal);
            if (local >= 0 && name.IndexOf('|', local) is var bar and > 0)
                return name.Substring(local + 4, bar - local - 4);
            return name.StartsWith("<", StringComparison.Ordinal) && name.IndexOf('>') is var end and > 1 ? name.Substring(1, end - 1) : name;
        }
    }

    /// <summary>Reads a Unity object's field or property by name and converts the result (a Mono value or an IL2CPP interop wrapper) to a .NET value.</summary>
    private static class UnityValues
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static object? Member(object target, string name)
        {
            for (var type = target.GetType(); type is not null; type = type.BaseType)
            {
                if (type.GetField(name, Instance | BindingFlags.DeclaredOnly) is { } field)
                    return field.GetValue(target);
                if (type.GetProperty(name, Instance | BindingFlags.DeclaredOnly) is { } property)
                    return property.GetValue(target);
            }
            return null;
        }

        public static double? ToDouble(object? value) => value switch
        {
            null => null,
            IConvertible convertible => convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert(value, typeof(double)) is double d ? d : Convert(value, typeof(float)) is float f ? f : null,
        };

        public static bool? ToBool(object? value) => value switch
        {
            null => null,
            bool b => b,
            _ => Convert(value, typeof(bool)) as bool?,
        };

        // An IL2CPP interop primitive (Il2CppSystem.Single, Il2CppSystem.Boolean) converts implicitly to its .NET type.
        private static object? Convert(object value, Type to)
        {
            var conversion = value.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name is "op_Implicit" or "op_Explicit" && m.ReturnType == to && m.GetParameters().Length == 1);
            return conversion?.Invoke(null, [value]);
        }
    }
}
