using System.Collections;
using Phase = Lustral.CoroutineScheduler.Phase;

namespace Lustral.Core.Tests;

public class CoroutineTests
{
    private readonly List<string> _steps = [];
    private readonly List<(string Name, Exception Error)> _errors = [];
    private double _realTime;
    private double _gameTime;
    private readonly CoroutineScheduler _scheduler;
    private readonly Coroutines _coroutines;

    public CoroutineTests()
    {
        _scheduler = new CoroutineScheduler(() => _realTime) { GameTime = () => _gameTime };
        _coroutines = new Coroutines(_scheduler, (name, e) => _errors.Add((name, e)));
    }

    // A frame as Unity runs it: physics step, Update and LateUpdate.
    private void Frame()
    {
        _scheduler.Tick(Phase.FixedUpdate);
        _scheduler.Tick(Phase.Update);
        _scheduler.Tick(Phase.LateUpdate);
    }

    private IEnumerator Steps(params object?[] yields)
    {
        _steps.Add("start");
        for (var i = 0; i < yields.Length; i++)
        {
            yield return yields[i];
            _steps.Add($"after {i}");
        }
    }

    [Fact]
    public void Runs_to_the_first_yield_at_once_then_a_step_per_frame()
    {
        var handle = _coroutines.Start(Steps(null, null));

        Assert.Equal(["start"], _steps);
        Assert.True(handle.IsRunning);
        Frame();
        Assert.Equal(["start", "after 0"], _steps);
        Frame();
        Assert.Equal(["start", "after 0", "after 1"], _steps);
        Assert.False(handle.IsRunning);
        Assert.Equal(0, _coroutines.Count);
    }

    [Fact]
    public void Frames_counts_updates()
    {
        _coroutines.Start(Steps(Wait.Frames(3)));

        Frame();
        Frame();
        Assert.Equal(["start"], _steps);
        Frame();
        Assert.Equal(["start", "after 0"], _steps);
    }

    [Fact]
    public void Seconds_follow_game_time_and_RealSeconds_real_time()
    {
        _coroutines.Start(Steps(Wait.Seconds(1)));
        _coroutines.Start(Steps(Wait.RealSeconds(1)));

        _realTime = 5; // The game is paused: real time passes, game time doesn't.
        Frame();
        Assert.Equal(["start", "start", "after 0"], _steps);

        _gameTime = 1;
        Frame();
        Assert.Equal(["start", "start", "after 0", "after 0"], _steps);
    }

    [Fact]
    public void Until_and_While_are_checked_at_once_then_every_frame()
    {
        var ready = false;
        _coroutines.Start(Steps(Wait.Until(() => true)));
        _coroutines.Start(Steps(Wait.While(() => !ready)));

        Assert.Equal(["start", "after 0", "start"], _steps);
        Frame();
        Assert.Equal(3, _steps.Count);
        ready = true;
        Frame();
        Assert.Equal(["start", "after 0", "start", "after 0"], _steps);
    }

    [Fact]
    public void FixedUpdate_and_LateUpdate_resume_in_their_phase()
    {
        _coroutines.Start(Steps(Wait.FixedUpdate));
        _coroutines.Start(Steps(Wait.LateUpdate));

        _scheduler.Tick(Phase.FixedUpdate);
        Assert.Equal(["start", "start", "after 0"], _steps);
        _scheduler.Tick(Phase.Update);
        Assert.Equal(3, _steps.Count);
        _scheduler.Tick(Phase.LateUpdate);
        Assert.Equal(["start", "start", "after 0", "after 0"], _steps);
    }

    [Fact]
    public void A_nested_iterator_runs_to_its_end_before_the_outer_one_goes_on()
    {
        IEnumerator Inner()
        {
            _steps.Add("inner");
            yield return null;
            _steps.Add("inner done");
        }
        IEnumerator Outer()
        {
            _steps.Add("outer");
            yield return Inner();
            _steps.Add("outer done");
        }

        _coroutines.Start(Outer());
        Assert.Equal(["outer", "inner"], _steps);
        Frame();
        Assert.Equal(["outer", "inner", "inner done", "outer done"], _steps);
    }

    [Fact]
    public void A_coroutine_can_wait_for_another_and_for_a_task()
    {
        var task = new TaskCompletionSource<bool>();
        var first = _coroutines.Start(Steps(null, null));
        _coroutines.Start(Steps(first));
        _coroutines.Start(Steps(task.Task));

        Frame();
        Assert.Equal(1, _steps.Count(s => s == "after 0")); // the first coroutine's step only
        Frame(); // the first ends, and the one waiting for it goes on
        Assert.Equal(2, _steps.Count(s => s == "after 0"));
        task.SetResult(true);
        Frame();
        Assert.Equal(3, _steps.Count(s => s == "after 0"));
    }

    [Fact]
    public void An_exception_stops_only_that_coroutine_and_is_reported_with_its_name()
    {
        IEnumerator Broken()
        {
            yield return null;
            throw new InvalidOperationException("boom");
        }

        _coroutines.Start(Broken());
        var other = _coroutines.Start(Steps(null, null));
        Frame();
        Frame();

        var (name, error) = Assert.Single(_errors);
        Assert.Equal("Broken", name);
        Assert.Equal("boom", error.Message);
        Assert.False(other.IsRunning);
        Assert.Contains("after 1", _steps);
    }

    [Fact]
    public void A_failing_condition_stops_the_coroutine()
    {
        _coroutines.Start(Steps(Wait.Until(() => throw new InvalidOperationException("bad condition"))));

        Assert.Equal("bad condition", Assert.Single(_errors).Error.Message);
        Assert.Equal(0, _coroutines.Count);
    }

    [Fact]
    public void Stop_and_StopAll_end_coroutines_before_their_next_step()
    {
        var first = _coroutines.Start(Steps(null, null));
        _coroutines.Start(Steps(null, null));
        Assert.Equal(2, _coroutines.Count);

        _coroutines.Stop(first);
        Assert.False(first.IsRunning);
        Frame();
        Assert.Equal(["start", "start", "after 0"], _steps);

        _coroutines.StopAll();
        Frame();
        Assert.Equal(3, _steps.Count);
        Assert.Equal(0, _coroutines.Count);
    }

    [Fact]
    public void Unity_yield_instructions_are_understood()
    {
        var waiting = true;
        _coroutines.Start(Steps(new UnityEngine.WaitForSeconds(2)));
        _coroutines.Start(Steps(new UnityEngine.WaitForFixedUpdate()));
        _coroutines.Start(Steps(new UnityEngine.WaitUntil(() => !waiting)));

        _scheduler.Tick(Phase.FixedUpdate);
        Assert.Single(_steps, "after 0");
        _gameTime = 2;
        waiting = false;
        _scheduler.Tick(Phase.Update);
        Assert.Equal(3, _steps.Count(s => s == "after 0"));
    }

    [Fact]
    public async Task A_coroutine_started_on_another_thread_begins_on_the_next_frame()
    {
        await Task.Run(() => _coroutines.Start(Steps(Wait.NextFrame)), TestContext.Current.CancellationToken);

        Assert.Empty(_steps);
        Assert.Equal(1, _coroutines.Count);
        _scheduler.Tick(Phase.Update);
        Assert.Equal(["start"], _steps);
        _scheduler.Tick(Phase.Update);
        Assert.Equal(["start", "after 0"], _steps);
    }

    [Fact]
    public void Waits_reject_bad_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Wait.Seconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Wait.Frames(-1));
        Assert.Throws<ArgumentNullException>(() => Wait.Until(null!));
        Assert.Throws<ArgumentNullException>(() => _coroutines.Start(null!));
    }
}
