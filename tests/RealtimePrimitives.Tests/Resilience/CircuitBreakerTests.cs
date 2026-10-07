using ShadowPrompter.Core.Resilience;
using ShadowPrompter.Tests.TestDoubles;
using Xunit;

namespace ShadowPrompter.Tests.Resilience;

/// <summary>
/// The one breaker of the application, as a MECHANISM: Closed -> Open -> HalfOpen (exactly one probe) -> Closed; typed causes with a domain
/// filter; a rest that is the longest of every candidate; permanent trips that survive a restart. Routing policy (who may override a
/// rest) is NOT here: see the pool tests (strict pass vs fallback pass).
/// </summary>
public sealed class CircuitBreakerTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly CircuitBreakerOptions Default = new()
    {
        Accepts = [BreakerCause.Transient, BreakerCause.Network, BreakerCause.RateLimited, BreakerCause.QuotaExhausted, BreakerCause.Authentication],
        OpenSteps = [S(10), S(30), S(60)]
    };

    private static (CircuitBreaker Breaker, ManualPoolClock Clock, List<string> Log) Make(CircuitBreakerOptions? options = null)
    {
        var clock = new ManualPoolClock(T0);
        var log = new List<string>();
        var breaker = new CircuitBreaker("test", options ?? Default, clock, new ListLogger(log));
        return (breaker, clock, log);
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // ---- the state machine --------------------------------------------------------------------------------------

    [Fact]
    public void A_New_Breaker_Is_Closed_And_Admits_Everything()
    {
        var (b, _, _) = Make();

        Assert.Equal(BreakerState.Closed, b.State);
        Assert.True(b.Admits());
        Assert.Equal(TimeSpan.Zero, b.Remaining);
        Assert.Equal(OpenMode.None, b.Snapshot().Mode);
    }

    [Fact]
    public void By_Default_One_Failure_Opens_It_For_The_First_Step_And_Names_The_Cause()
    {
        var (b, _, log) = Make();

        Assert.True(b.RecordFailure(BreakerCause.Transient, detail: "boom"));

        Assert.Equal(BreakerState.Open, b.State);
        Assert.False(b.Admits());
        Assert.Equal(S(10), b.Remaining);
        Assert.Equal("boom", b.Detail);
        var s = b.Snapshot();
        Assert.Equal((BreakerCause.Transient, OpenMode.Timed), (s.Cause, s.Mode));
        Assert.Contains(log, l => l.Contains("OPEN 10s [Timed] (Transient: boom)", StringComparison.Ordinal));
    }

    [Fact]
    public void Consecutive_Failures_Are_Counted_And_A_Success_Between_Them_Resets_The_Count()
    {
        var (b, _, _) = Make(Default with { ConsecutiveFailures = 3 });

        b.RecordFailure(BreakerCause.Transient);
        b.RecordFailure(BreakerCause.Transient);
        b.RecordSuccess();
        b.RecordFailure(BreakerCause.Transient);
        b.RecordFailure(BreakerCause.Transient);
        Assert.Equal(BreakerState.Closed, b.State); // never three in a row

        b.RecordFailure(BreakerCause.Transient);
        Assert.Equal(BreakerState.Open, b.State);
    }

    [Fact]
    public void When_The_Rest_Ends_It_Is_HalfOpen_And_Admits_One_Probe()
    {
        var (b, clock, _) = Make();
        b.RecordFailure(BreakerCause.Transient);

        clock.Advance(S(9));
        Assert.False(b.Admits());
        Assert.False(b.TryBeginProbe("late"));
        clock.Advance(S(2));

        Assert.Equal(BreakerState.HalfOpen, b.State);
        Assert.True(b.Admits());
        Assert.Equal(TimeSpan.Zero, b.Remaining);
        Assert.Equal(OpenMode.None, b.Snapshot().Mode);
    }

    [Fact]
    public void A_Successful_Probe_Closes_It_And_Restarts_The_Escalation()
    {
        var (b, clock, log) = Make();
        b.RecordFailure(BreakerCause.Transient);
        clock.Advance(S(11));
        Assert.True(b.TryBeginProbe("p"));
        b.RecordSuccess();

        Assert.Equal(BreakerState.Closed, b.State);
        Assert.Contains(log, l => l.Contains("CLOSED", StringComparison.Ordinal));

        b.RecordFailure(BreakerCause.Transient);
        Assert.Equal(S(10), b.Remaining); // the first step again, not the second
    }

    [Fact]
    public void A_Failed_Probe_Reopens_It_At_Once_With_The_Next_Longer_Rest()
    {
        var (b, clock, _) = Make();
        b.RecordFailure(BreakerCause.Transient);        // step 1: 10 s
        clock.Advance(S(11));
        Assert.True(b.TryBeginProbe("p"));

        b.RecordFailure(BreakerCause.Transient);        // the probe failed: step 2: 30 s

        Assert.Equal(BreakerState.Open, b.State);
        Assert.Equal(S(30), b.Remaining);
        Assert.False(b.TryBeginProbe("q"));
    }

    [Fact]
    public void Escalation_Caps_At_The_Last_Step()
    {
        var (b, clock, _) = Make();

        for (var i = 0; i < 10; i++)
        {
            b.RecordFailure(BreakerCause.Transient);
            clock.Advance(S(61));
        }

        b.RecordFailure(BreakerCause.Transient);
        Assert.Equal(S(60), b.Remaining);
    }

    [Fact]
    public void Stragglers_While_Open_Extend_The_Rest_But_Do_Not_Climb_The_Escalation_Ladder()
    {
        var (b, clock, _) = Make();
        b.RecordFailure(BreakerCause.Transient);        // open 10 s (step 1)
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(S(1));
            b.RecordFailure(BreakerCause.Transient);    // five requests that were already in flight fail one by one
        }

        Assert.Equal(S(10), b.Remaining);               // fresh evidence rests at least one local step from now, nothing more

        clock.Advance(S(11));
        b.TryBeginProbe("p");
        b.RecordFailure(BreakerCause.Transient);        // the real probe fails: step 2 (30 s), not step 7

        Assert.Equal(S(30), b.Remaining);
    }

    // ---- exactly one probe ------------------------------------------------------------------------------------------

    [Fact]
    public void Closed_Admits_Every_Probe_Open_Admits_None()
    {
        var (b, _, _) = Make();
        Assert.True(b.TryBeginProbe("a"));
        Assert.True(b.TryBeginProbe("b"));

        b.RecordFailure(BreakerCause.Transient);
        Assert.False(b.TryBeginProbe("c"));
    }

    [Fact]
    public void HalfOpen_Gives_The_Probe_To_The_First_Caller_Only_And_It_Is_Idempotent_For_Its_Owner()
    {
        var (b, clock, _) = Make();
        b.RecordFailure(BreakerCause.Transient);
        clock.Advance(S(11));

        Assert.True(b.TryBeginProbe("first"));
        Assert.True(b.TryBeginProbe("first"));
        Assert.False(b.TryBeginProbe("second"));
        Assert.False(b.Admits());                        // a pure query agrees: the slot is taken
        Assert.True(b.Snapshot().ProbeInFlight);
    }

    [Fact]
    public void Twelve_Concurrent_Callers_Get_Exactly_One_Probe()
    {
        for (var round = 0; round < 200; round++)
        {
            var (b, clock, _) = Make();
            b.RecordFailure(BreakerCause.Transient);
            clock.Advance(S(11));                        // the rest is over: HalfOpen

            using var barrier = new Barrier(12);
            var winners = 0;
            var threads = Enumerable.Range(0, 12).Select(i => new Thread(() =>
            {
                barrier.SignalAndWait();
                if (b.TryBeginProbe("caller-" + i)) Interlocked.Increment(ref winners);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Equal(1, winners);                    // 1 == true, 11 == false
        }
    }

    [Fact]
    public void A_Cancelled_Probe_Frees_The_Slot_Only_For_Its_Owner_To_Free()
    {
        var (b, clock, _) = Make();
        b.RecordFailure(BreakerCause.Transient);
        clock.Advance(S(11));
        Assert.True(b.TryBeginProbe("owner"));

        b.CancelProbe("somebody-else");
        Assert.False(b.TryBeginProbe("second"));         // not theirs to free

        b.CancelProbe("owner");
        Assert.True(b.TryBeginProbe("second"));          // freed, and counted as neither success nor failure
        Assert.Equal(BreakerState.HalfOpen, b.State);
    }

    [Fact]
    public void A_Probe_That_Never_Reports_Frees_Its_Slot_When_The_Lease_Runs_Out()
    {
        var (b, clock, _) = Make(Default with { ProbeLease = S(30) });
        b.RecordFailure(BreakerCause.Transient);
        clock.Advance(S(11));
        Assert.True(b.TryBeginProbe("lost"));

        clock.Advance(S(29));
        Assert.False(b.TryBeginProbe("next"));
        clock.Advance(S(2));
        Assert.True(b.TryBeginProbe("next"));            // recovery cannot deadlock behind a request that vanished
    }

    [Fact]
    public void A_Passing_Probe_Opens_The_Gate_For_Everybody_Else()
    {
        var (b, clock, _) = Make();
        b.RecordFailure(BreakerCause.Transient);
        clock.Advance(S(11));
        Assert.True(b.TryBeginProbe("p"));
        Assert.False(b.Admits());

        b.RecordSuccess();

        Assert.True(b.Admits());
        Assert.True(b.TryBeginProbe("x"));
        Assert.True(b.TryBeginProbe("y"));
    }

    // ---- the rest is the longest of every candidate ----------------------------------------------------------------------

    [Fact]
    public void A_Short_Retry_After_Cannot_Shorten_A_Rest_Already_In_Force()
    {
        var (b, _, _) = Make(Default with { OpenSteps = [S(16)] });
        b.RecordFailure(BreakerCause.RateLimited);       // 16 s

        b.RecordFailure(BreakerCause.RateLimited, retryAfter: S(1));

        Assert.True(b.Remaining >= S(16));
        Assert.Equal(OpenMode.Timed, b.Snapshot().Mode);
    }

    [Fact]
    public void A_Long_Retry_After_Beats_Our_Local_Step()
    {
        var (b, _, _) = Make(Default with { OpenSteps = [S(8)] });

        b.RecordFailure(BreakerCause.RateLimited, retryAfter: S(43));

        Assert.Equal(S(43), b.Remaining);
        Assert.Equal(OpenMode.RetryAfter, b.Snapshot().Mode);
    }

    [Fact]
    public void Retry_After_Extends_A_Longer_Local_Backoff_Only_When_It_Is_Longer()
    {
        var (b, _, _) = Make(Default with { OpenSteps = [S(4)] });
        b.RecordFailure(BreakerCause.RateLimited, retryAfter: S(30));
        Assert.Equal(S(30), b.Remaining);

        b.RecordFailure(BreakerCause.RateLimited, retryAfter: S(2));
        Assert.Equal(S(30), b.Remaining);                // max(30, 4, 2)
    }

    [Fact]
    public void A_Reset_Instant_Beats_A_Shorter_Retry_After_And_The_Local_Step()
    {
        var (b, clock, _) = Make();

        b.RecordFailure(BreakerCause.QuotaExhausted, retryAfter: S(45), resetAtUtc: T0.AddHours(19));

        Assert.Equal(TimeSpan.FromHours(19), b.Remaining);
        Assert.Equal(OpenMode.UntilReset, b.Snapshot().Mode);
        clock.Advance(TimeSpan.FromHours(18.9));
        Assert.False(b.Admits());
        clock.Advance(TimeSpan.FromHours(0.2));
        Assert.Equal(BreakerState.HalfOpen, b.State);
    }

    [Fact]
    public void A_Dictated_Wait_Trips_Immediately_Even_When_More_Failures_Are_Normally_Needed()
    {
        var (b, _, _) = Make(Default with { ConsecutiveFailures = 5 });

        b.RecordFailure(BreakerCause.RateLimited, retryAfter: S(20));

        Assert.Equal(BreakerState.Open, b.State);
    }

    [Fact]
    public void A_Source_Can_Lengthen_A_Rest_But_Never_Shorten_It()
    {
        var (b, _, _) = Make();
        b.RecordFailure(BreakerCause.QuotaExhausted, resetAtUtc: T0.AddSeconds(120));

        b.RecordFailure(BreakerCause.RateLimited, retryAfter: S(5));

        Assert.Equal(S(120), b.Remaining);
    }

    // ---- distinct sources ------------------------------------------------------------------------------------------

    [Fact]
    public void Distinct_Sources_Trip_The_Breaker_But_One_Source_Failing_Repeatedly_Does_Not()
    {
        var (b, _, _) = Make(Default with { ConsecutiveFailures = int.MaxValue, DistinctSources = 2, OpenSteps = [S(30)] });

        b.RecordFailure(BreakerCause.Transient, source: "model-a");
        b.RecordFailure(BreakerCause.Transient, source: "model-a");
        b.RecordFailure(BreakerCause.Transient, source: "model-a");
        Assert.Equal(BreakerState.Closed, b.State);

        b.RecordFailure(BreakerCause.Transient, source: "model-b");
        Assert.Equal(BreakerState.Open, b.State);
    }

    [Fact]
    public void Failures_Outside_The_Window_Do_Not_Add_Up()
    {
        var (b, clock, _) = Make(Default with { ConsecutiveFailures = int.MaxValue, DistinctSources = 2, Window = S(60), OpenSteps = [S(30)] });

        b.RecordFailure(BreakerCause.Transient, source: "a");
        clock.Advance(S(61));
        b.RecordFailure(BreakerCause.Transient, source: "b");

        Assert.Equal(BreakerState.Closed, b.State); // "a" is history
    }

    [Fact]
    public void An_Immediate_Failure_Trips_Regardless_Of_Any_Threshold()
    {
        var (b, _, _) = Make(Default with { ConsecutiveFailures = int.MaxValue, DistinctSources = 5, OpenSteps = [S(30)] });

        b.RecordFailure(BreakerCause.Network, source: "a", detail: "DNS", immediate: true);

        Assert.Equal(BreakerState.Open, b.State);
    }

    // ---- causes: a breaker guards one failure domain ----------------------------------------------------------------------

    [Fact]
    public void A_Cause_Outside_The_Domain_Is_Refused_Logged_And_Opens_Nothing()
    {
        var (b, _, log) = Make(new CircuitBreakerOptions { Accepts = [BreakerCause.Transient, BreakerCause.Network] });

        Assert.False(b.RecordFailure(BreakerCause.RateLimited, retryAfter: S(60)));  // a 429 cannot open a provider breaker
        Assert.False(b.RecordFailure(BreakerCause.Authentication, immediate: true)); // nor can a rejected key
        b.TripPermanently(BreakerCause.Authentication);

        Assert.Equal(BreakerState.Closed, b.State);
        Assert.True(b.Admits());
        Assert.Equal(0, b.TotalTrips);
        Assert.Equal(3, log.Count(l => l.Contains("ignored", StringComparison.Ordinal)));
    }

    // ---- permanent and persistence -----------------------------------------------------------------------------------

    [Fact]
    public void A_Permanent_Trip_Stays_Open_Ignores_Successes_And_Only_Reset_Closes_It()
    {
        var (b, clock, log) = Make();

        b.TripPermanently(BreakerCause.Authentication, "auth failed");
        clock.Advance(TimeSpan.FromDays(30));

        Assert.False(b.Admits());
        Assert.False(b.TryBeginProbe("p"));
        b.RecordSuccess();
        Assert.True(b.IsOpen);
        Assert.False(b.RecordFailure(BreakerCause.Transient));
        Assert.Equal(DateTime.MaxValue, b.OpenUntilUtc);
        Assert.Equal(TimeSpan.MaxValue, b.Remaining);
        Assert.Equal((OpenMode.Permanent, BreakerCause.Authentication), (b.Snapshot().Mode, b.Snapshot().Cause));
        Assert.Contains(log, l => l.Contains("permanently (Authentication: auth failed)", StringComparison.Ordinal));

        b.Reset();
        Assert.Equal(BreakerState.Closed, b.State);
    }

    [Fact]
    public void A_Permanent_Trip_Survives_A_Restart_Through_Restore()
    {
        var (before, _, _) = Make();
        before.TripPermanently(BreakerCause.Authentication, "HTTP 401");
        var saved = before.Snapshot();

        var (after, _, _) = Make();
        after.Restore(saved.OpenUntilUtc, saved.Cause!.Value, saved.Detail);

        Assert.True(after.IsOpen);
        Assert.False(after.Admits());
        Assert.Equal(OpenMode.Permanent, after.Snapshot().Mode);
        Assert.Equal("HTTP 401", after.Detail);
        after.RecordSuccess();
        Assert.True(after.IsOpen);
    }

    [Fact]
    public void A_Timed_Rest_Survives_A_Restart_And_A_Past_One_Restores_Nothing()
    {
        var (b, clock, _) = Make();

        b.Restore(T0.AddMinutes(30), BreakerCause.Authentication, "HTTP 401");
        Assert.False(b.Admits());
        Assert.Equal("HTTP 401", b.Detail);
        Assert.Equal(BreakerCause.Authentication, b.Snapshot().Cause);
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(BreakerState.HalfOpen, b.State);   // the restored rest ends in ONE probe, like any other
        Assert.True(b.TryBeginProbe("p"));
        Assert.False(b.TryBeginProbe("q"));

        var (fresh, _, _) = Make();
        fresh.Restore(T0.AddMinutes(-5), BreakerCause.Transient, "stale");
        Assert.Equal(BreakerState.Closed, fresh.State);
    }

    [Fact]
    public void An_External_Cooldown_Can_Extend_A_Rest_But_Never_Shorten_It()
    {
        var (b, clock, _) = Make();
        b.RecordFailure(BreakerCause.RateLimited, detail: "429"); // local step: 10 s (Default.OpenSteps[0])

        // Shorter than what is already in force: a no-op, exactly like a Retry-After that undercuts our own escalation step.
        b.ExtendFrom(clock.GetUtcNow().UtcDateTime.AddSeconds(5), BreakerCause.RateLimited, "cross-process, shorter");
        var afterShort = b.OpenUntilUtc;
        Assert.Equal(T0.AddSeconds(10), afterShort);

        // Longer: extends it, exactly like local evidence would.
        var longer = clock.GetUtcNow().UtcDateTime.AddMinutes(5);
        b.ExtendFrom(longer, BreakerCause.RateLimited, "cross-process, longer");
        Assert.Equal(longer, b.OpenUntilUtc);
        Assert.Equal("cross-process, longer", b.Detail);
        Assert.False(b.Admits());

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(b.Admits()); // still resting: the extension actually took hold, not just the return value
    }

    [Fact]
    public void An_External_Cooldown_Never_Reopens_A_Permanent_Breaker_Or_Overrides_Its_Cause()
    {
        var (b, clock, _) = Make();
        b.TripPermanently(BreakerCause.Authentication, "key rejected");

        b.ExtendFrom(clock.GetUtcNow().UtcDateTime.AddDays(1), BreakerCause.RateLimited, "cross-process");

        Assert.Equal(OpenMode.Permanent, b.Snapshot().Mode);
        Assert.Equal(BreakerCause.Authentication, b.Snapshot().Cause);
        Assert.Equal("key rejected", b.Detail);
    }

    [Fact]
    public void The_Snapshot_Describes_The_Breaker_From_Outside()
    {
        var (b, _, _) = Make();
        b.RecordFailure(BreakerCause.Transient, detail: "overload");

        var s = b.Snapshot();

        Assert.Equal((BreakerState.Open, "overload", false, BreakerCause.Transient), (s.State, s.Detail, s.Permanent, s.Cause));
        Assert.Equal(S(10), s.Remaining);
        Assert.Equal(1, b.TotalTrips);
    }

    // ---- named policies ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_Device_Policy_Retries_Soon_Then_Backs_Off_And_Never_Hammers()
    {
        var clock = new ManualPoolClock(T0);
        var b = Breakers.DeviceReopen("dev", clock);
        var rests = new List<double>();

        for (var i = 0; i < 9; i++)
        {
            b.RecordFailure(BreakerCause.Transient);
            rests.Add(b.Remaining.TotalSeconds);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal([0.5, 1, 2, 4, 8, 16, 30, 30, 30], rests);
    }

    [Fact]
    public void The_Stream_Policy_Is_Two_Five_Fifteen_And_A_Rejected_Key_Is_Permanent()
    {
        var clock = new ManualPoolClock(T0);
        var b = Breakers.StreamReconnect("stt", clock);
        var rests = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            b.RecordFailure(BreakerCause.Transient);
            rests.Add(b.Remaining.TotalSeconds);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal([2, 5, 15, 15], rests);
        b.RecordSuccess();
        b.RecordFailure(BreakerCause.Network);
        Assert.Equal(2, b.Remaining.TotalSeconds); // a healthy run starts the escalation over

        Assert.False(b.RecordFailure(BreakerCause.RateLimited)); // a quota is not a stream's business
        b.TripPermanently(BreakerCause.Authentication, "auth");
        b.RecordSuccess();
        Assert.True(b.IsOpen);
    }

    [Fact]
    public void The_Background_Policy_Forgives_One_Blip_Then_Skips_The_Job_For_A_Growing_While()
    {
        var clock = new ManualPoolClock(T0);
        var b = Breakers.BackgroundJob("warm", clock);

        b.RecordFailure(BreakerCause.Network);
        Assert.True(b.Admits());     // one failed ping is noise
        b.RecordFailure(BreakerCause.Network);
        Assert.False(b.Admits());
        Assert.Equal(60, b.Remaining.TotalSeconds);
    }

    [Fact]
    public void The_Tool_Policy_Takes_A_Quota_Wall_Or_A_Rejection_And_Ignores_Outages()
    {
        var b = Breakers.ToolModel("kb", new ManualPoolClock(T0));

        Assert.False(b.RecordFailure(BreakerCause.Transient));   // an outage is retried by the tool's own loop
        Assert.True(b.RecordFailure(BreakerCause.RateLimited));
        Assert.True(b.IsOpen);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1500, 1500)]
    [InlineData(999_999_999, 3_600_000)]
    public void WaitMs_Is_Safe_For_WaitOne_And_Delay(double ms, int expected)
    {
        Assert.Equal(expected, Breakers.WaitMs(TimeSpan.FromMilliseconds(ms)));
    }

    // ---- concurrency --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_Failures_Successes_And_Probes_Never_Corrupt_The_Breaker()
    {
        var b = new CircuitBreaker(
            "race", Default with { ConsecutiveFailures = 2, DistinctSources = 3, OpenSteps = [S(0.001), S(0.002)] });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                if ((i + t) % 3 == 0) b.RecordSuccess();
                else b.RecordFailure(BreakerCause.Transient, source: "s" + (i % 5));
                if (b.TryBeginProbe("t" + t)) b.CancelProbe("t" + t);
                _ = b.Admits();
                _ = b.Snapshot();
            }
        }, TestContext.Current.CancellationToken)));

        Assert.True(b.TotalTrips >= 0); // reached the end without an exception or a deadlock
    }
}
