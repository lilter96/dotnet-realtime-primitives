using System.Diagnostics.Metrics;
using ShadowPrompter.Core.Observability.Flight;
using Microsoft.Extensions.Logging.Abstractions;
using ShadowPrompter.Core.Observability;

namespace ShadowPrompter.Core.Resilience;

/// <summary>Closed = traffic flows. Open = rejected until the rest period ends. HalfOpen = the rest ended: ONE probe decides.</summary>
public enum BreakerState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>
/// WHY a failure is being counted. A breaker guards exactly one failure domain and accepts only the causes of that domain
/// (<see cref="CircuitBreakerOptions.Accepts"/>): a rate limit cannot open a provider breaker, a 503 cannot open a credential breaker.
/// Without this a breaker degrades into a `catch (Exception)` with a nicer name.
/// A prompt that is too large is deliberately NOT a cause: it never rests anything, it teaches a limit.
/// </summary>
public enum BreakerCause
{
    /// <summary>Overload (5xx), a timeout, a stream that ended or faulted, a device read/open error: it may pass.</summary>
    Transient,

    /// <summary>No HTTP status at all (DNS, connect, reset): every user of the same host fails alike.</summary>
    Network,

    /// <summary>429 / concurrency limit: the quota owner says "slow down".</summary>
    RateLimited,

    /// <summary>A quota wall with a known reset (a daily cap).</summary>
    QuotaExhausted,

    /// <summary>401 / 403: the credential is wrong.</summary>
    Authentication,

    /// <summary>404 / 400 for THIS model or request shape.</summary>
    Rejected
}

/// <summary>How the current rest was decided (diagnostics; the mechanism always takes the longest of the candidates).</summary>
public enum OpenMode
{
    None,

    /// <summary>The local escalation schedule.</summary>
    Timed,

    /// <summary>The provider's Retry-After / retry delay was longer than our own schedule.</summary>
    RetryAfter,

    /// <summary>Until a known instant (a daily quota that resets at midnight).</summary>
    UntilReset,

    /// <summary>Never (a rejected key stopping a reconnect loop); only <see cref="CircuitBreaker.Reset"/> closes it.</summary>
    Permanent
}

/// <summary>How a breaker trips, what it accepts and how long it stays open. The mechanism only: WHO may override a rest is the caller's policy.</summary>
public sealed record CircuitBreakerOptions
{
    /// <summary>The failure causes of this breaker's domain. Anything else is a bug in the caller.</summary>
    public required IReadOnlyCollection<BreakerCause> Accepts { get; init; }

    /// <summary>Failures in a row (with no success between them) that trip the breaker. 1 = every failure trips. int.MaxValue = never by count.</summary>
    public int ConsecutiveFailures { get; init; } = 1;

    /// <summary>
    /// Trip when failures come from this many DIFFERENT sources within <see cref="Window"/> (a vendor is down when several of its
    /// models fail, however few times each). 0 = off.
    /// </summary>
    public int DistinctSources { get; init; }

    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Local rest of the 1st, 2nd, ... trip; the last step repeats. A success starts the sequence again.</summary>
    public IReadOnlyList<TimeSpan> OpenSteps { get; init; } = [TimeSpan.FromSeconds(30)];

    /// <summary>
    /// A HalfOpen probe that never reports back (a cancelled request) frees its slot after this long, so recovery cannot deadlock.
    /// </summary>
    public TimeSpan ProbeLease { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>What a breaker looks like from outside (diagnostics, persistence).</summary>
public readonly record struct BreakerSnapshot(
    BreakerState State, OpenMode Mode, BreakerCause? Cause, DateTime OpenUntilUtc, TimeSpan Remaining, string Detail, int Step,
    bool Permanent, bool ProbeInFlight);

/// <summary>
/// The one circuit breaker of the application, a resilience MECHANISM (never routing policy):
/// <code>
///   Closed --failure(s)--> Open --rest ends--> HalfOpen --ONE probe succeeds--> Closed
///                            ^                     |
///                            +----probe fails------+   (re-opens with the next, longer rest)
/// </code>
/// <list type="bullet">
/// <item><b>One way to record a failure</b> (<see cref="RecordFailure"/>) that carries a <see cref="BreakerCause"/> and, when the source
/// knows them, a retry delay and/or a reset instant. The rest is the LONGEST of: the rest already in force, our own escalation step,
/// the source's Retry-After, the reset instant. A server that says "2 s" cannot shorten a 16 s back-off we earned, and one that says
/// "43 s" beats our 8 s.</item>
/// <item><b>Exactly one probe</b> in HalfOpen (<see cref="TryBeginProbe"/>): when the rest ends, one caller tests the water; the other
/// callers of the same breaker stay out until it reports (<see cref="RecordSuccess"/> / <see cref="RecordFailure"/> /
/// <see cref="CancelProbe"/>) - recovery is never a burst.</item>
/// <item><b>Permanent</b> for conditions that cannot fix themselves (<see cref="TripPermanently"/>).</item>
/// </list>
/// Time comes from the caller (a test clock); the breaker never blocks or sleeps: callers ask <see cref="Admits"/> / <see cref="Remaining"/>
/// and decide, including whether a rest is overridable when nothing else is left. Thread-safe.
/// </summary>
public sealed class CircuitBreaker
{
    // One pair of instruments for every breaker in the application (tagged by breaker name): this is the single mechanism, so
    // instrumenting it here covers the provider/credential/budget/target-rejection/target-health breakers at once, with no per-caller work.
    private static readonly Counter<long> Trips = AppMetrics.Meter.CreateCounter<long>(
        "shadowprompter.breaker.trips", description: "A breaker opened or re-opened (tags: breaker, cause).");
    private static readonly Counter<long> Recoveries = AppMetrics.Meter.CreateCounter<long>(
        "shadowprompter.breaker.recoveries", description: "A breaker closed after having been open (tags: breaker).");

    private readonly Lock _lock = new();
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Queue<(DateTime At, string Source)> _window = new();

    private DateTime _openUntil = DateTime.MinValue;
    private bool _tripped;          // has been open since the last success (so an elapsed rest is HalfOpen, not Closed)
    private bool _permanent;
    private int _consecutive;
    private int _step;
    private OpenMode _mode;
    private BreakerCause? _cause;
    private string _detail = string.Empty;
    private string? _probeOwner;
    private DateTime _probeUntil = DateTime.MinValue;

    public CircuitBreaker(string name, CircuitBreakerOptions options, TimeProvider? time = null, ILogger? log = null)
    {
        Name = name;
        _options = options;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    public string Name { get; }

    /// <summary>Total number of times this breaker has opened (diagnostics).</summary>
    public int TotalTrips { get; private set; }

    public BreakerState State
    {
        get
        {
            lock (_lock)
            {
                return StateAt(UtcNow());
            }
        }
    }

    public bool IsOpen => State == BreakerState.Open;

    /// <summary>Human-readable detail of the last trip.</summary>
    public string Detail
    {
        get
        {
            lock (_lock)
            {
                return _detail;
            }
        }
    }

    public DateTime OpenUntilUtc
    {
        get
        {
            lock (_lock)
            {
                return _permanent ? DateTime.MaxValue : _openUntil;
            }
        }
    }

    /// <summary>Time left in the rest period (zero when not open; TimeSpan.MaxValue when permanent).</summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
            {
                var now = UtcNow();
                if (_permanent) return TimeSpan.MaxValue;
                return now < _openUntil ? _openUntil - now : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// May a call go through right now? Closed: yes. Open: no. HalfOpen: yes for ONE probe - true only while no probe is in flight (or its
    /// lease ran out). A pure query: taking the probe slot is <see cref="TryBeginProbe"/>.
    /// </summary>
    public bool Admits()
    {
        lock (_lock)
        {
            var now = UtcNow();
            return StateAt(now) switch
            {
                BreakerState.Closed => true,
                BreakerState.Open => false,
                _ => !ProbeHeld(now, null)
            };
        }
    }

    /// <summary>
    /// Takes the HalfOpen probe slot for <paramref name="owner"/> (a request id). Closed: true (nothing to take). Open: false. HalfOpen:
    /// true for the first owner, false for everybody else until the probe reports or its lease expires. Idempotent for the same owner.
    /// </summary>
    public bool TryBeginProbe(string owner)
    {
        lock (_lock)
        {
            var now = UtcNow();
            switch (StateAt(now))
            {
                case BreakerState.Closed:
                    return true;
                case BreakerState.Open:
                    return false;
                default:
                    if (ProbeHeld(now, owner)) return false;
                    _probeOwner = owner;
                    _probeUntil = now + _options.ProbeLease;
                    return true;
            }
        }
    }

    /// <summary>The probe ended without a verdict (cancelled, superseded): free the slot, count nothing.</summary>
    public void CancelProbe(string owner)
    {
        lock (_lock)
        {
            if (_probeOwner == owner) ClearProbe();
        }
    }

    public BreakerSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = UtcNow();
            var state = StateAt(now);
            return new BreakerSnapshot(
                state, state == BreakerState.Open ? _mode : OpenMode.None, _cause, _permanent ? DateTime.MaxValue : _openUntil,
                _permanent ? TimeSpan.MaxValue : now < _openUntil ? _openUntil - now : TimeSpan.Zero, _detail, _step, _permanent,
                ProbeHeld(now, null));
        }
    }

    // ---- recording ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A failure of the given <paramref name="cause"/>. While Closed it counts (consecutive, and distinct <paramref name="source"/>s within
    /// the window) and trips when a threshold is met, when <paramref name="immediate"/>, or when the source dictated a wait
    /// (<paramref name="retryAfter"/> / <paramref name="resetAtUtc"/>). While Open or HalfOpen it re-trips at once with the next, longer
    /// rest. The rest is the longest of: the one in force, the next local step, <paramref name="retryAfter"/>, <paramref name="resetAtUtc"/>.
    /// Returns true when this call opened or extended the breaker.
    /// </summary>
    public bool RecordFailure(
        BreakerCause cause, string? source = null, string? detail = null, TimeSpan? retryAfter = null, DateTime? resetAtUtc = null,
        bool immediate = false)
    {
        lock (_lock)
        {
            if (!_options.Accepts.Contains(cause))
            {
                // A caller feeding a breaker the wrong kind of failure is a bug: it must be visible (log, false) but must never take the
                // process down (Debug.Fail would FailFast a live interview) nor open a breaker of another domain.
                _log.Info($"[breaker] {Name}: ignored a {cause} failure (not this breaker's domain)");
                return false;
            }

            var now = UtcNow();
            if (_permanent) return false;
            ClearProbe();
            if (StateAt(now) != BreakerState.Closed) return TripStep(now, cause, detail, retryAfter, resetAtUtc);

            _consecutive++;
            var trip = immediate || retryAfter is not null || resetAtUtc is not null || _consecutive >= _options.ConsecutiveFailures;
            if (_options.DistinctSources > 0)
            {
                _window.Enqueue((now, source ?? string.Empty));
                while (_window.Count > 0 && now - _window.Peek().At > _options.Window) _window.Dequeue();
                if (_window.Select(w => w.Source).Distinct(StringComparer.Ordinal).Count() >= _options.DistinctSources) trip = true;
            }

            return trip && TripStep(now, cause, detail, retryAfter, resetAtUtc);
        }
    }

    /// <summary>A success (the probe worked, or ordinary traffic did): Closed again, escalation starts over. A permanent trip is not cleared (only <see cref="Reset"/>).</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_permanent) return;
            var wasOpen = _tripped;
            _consecutive = 0;
            _step = 0;
            _tripped = false;
            _openUntil = DateTime.MinValue;
            _mode = OpenMode.None;
            _cause = null;
            _detail = string.Empty;
            _window.Clear();
            ClearProbe();
            if (wasOpen)
            {
                _log.Info($"[breaker] {Name}: CLOSED (recovered)");
                Recoveries.Add(1, new KeyValuePair<string, object?>("breaker", Name));
                Flight.Event("breaker.recover").Str("breaker", Name).Str("how", "success").Emit();
            }
        }
    }

    /// <summary>Open for good: the condition cannot fix itself (a rejected key). Only <see cref="Reset"/> closes it.</summary>
    public void TripPermanently(BreakerCause cause, string? detail = null)
    {
        lock (_lock)
        {
            if (!_options.Accepts.Contains(cause))
            {
                _log.Info($"[breaker] {Name}: ignored a permanent {cause} trip (not this breaker's domain)");
                return;
            }

            if (_permanent) return;
            _permanent = true;
            _tripped = true;
            _mode = OpenMode.Permanent;
            _cause = cause;
            _detail = detail ?? cause.ToString();
            ClearProbe();
            TotalTrips++;
            Trips.Add(1, new KeyValuePair<string, object?>("breaker", Name), new KeyValuePair<string, object?>("cause", cause.ToString()));
            _log.Info($"[breaker] {Name}: OPEN permanently ({cause}: {_detail})");
            Flight.Event("breaker.trip").Str("breaker", Name).Str("cause", cause.ToString()).Str("mode", "permanent").Str("detail", _detail).Emit();
        }
    }

    /// <summary>Back to Closed regardless of history (an operator or a test).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _permanent = false;
            _tripped = false;
            _consecutive = 0;
            _step = 0;
            _openUntil = DateTime.MinValue;
            _mode = OpenMode.None;
            _cause = null;
            _detail = string.Empty;
            _window.Clear();
            ClearProbe();
        }
    }

    /// <summary>
    /// Closes the breaker when the rest in force (or the one just served) was earned by one of <paramref name="causes"/>, and forgets the
    /// failures counted so far: they were evidence about a world that no longer exists (a Wi-Fi that came back, a restarted tunnel). A
    /// rest earned by anything else (a quota wall, a rejected key) stays. Returns true when it closed a breaker that was tripped.
    /// </summary>
    public bool ForgiveCauses(params BreakerCause[] causes)
    {
        lock (_lock)
        {
            _consecutive = 0;
            _window.Clear();
            if (_permanent || !_tripped || _cause is not { } cause || !causes.Contains(cause)) return false;

            _tripped = false;
            _step = 0;
            _openUntil = DateTime.MinValue;
            _mode = OpenMode.None;
            _cause = null;
            _detail = string.Empty;
            ClearProbe();
            _log.Info($"[breaker] {Name}: CLOSED (the rest was earned by {cause}, which no longer holds)");
            Recoveries.Add(1, new KeyValuePair<string, object?>("breaker", Name));
            Flight.Event("breaker.recover").Str("breaker", Name).Str("how", "forgiven").Str("cause", cause.ToString()).Emit();
            return true;
        }
    }

    /// <summary>
    /// Re-opens from persisted state (a restart must not forget that a key was rejected). <see cref="DateTime.MaxValue"/> restores a
    /// permanent trip. A past instant restores nothing.
    /// </summary>
    public void Restore(DateTime openUntilUtc, BreakerCause cause, string detail)
    {
        lock (_lock)
        {
            if (openUntilUtc == DateTime.MaxValue)
            {
                _permanent = true;
                _tripped = true;
                _mode = OpenMode.Permanent;
                _cause = cause;
                _detail = detail;
                return;
            }

            if (openUntilUtc <= UtcNow()) return;
            _openUntil = openUntilUtc;
            _tripped = true;
            _mode = OpenMode.UntilReset;
            _cause = cause;
            _detail = detail;
        }
    }

    /// <summary>
    /// External evidence (another process, a Redis-synced peer) says the rest should last until at least this instant: extends the
    /// local rest only if the candidate is later, exactly like a Retry-After extending the local escalation step - never shortens it.
    /// Stays a "dumb mechanism": state + time, nothing about Redis or processes. A no-op when not later, or the breaker is permanent.
    /// </summary>
    public void ExtendFrom(DateTime candidateUntilUtc, BreakerCause cause, string detail)
    {
        lock (_lock)
        {
            if (_permanent || candidateUntilUtc <= _openUntil) return;
            _openUntil = candidateUntilUtc;
            _tripped = true;
            _mode = OpenMode.UntilReset;
            _cause = cause;
            _detail = detail;
        }
    }

    // ---- internals ----------------------------------------------------------------------------------------------------

    private BreakerState StateAt(DateTime now)
    {
        if (_permanent) return BreakerState.Open;
        if (now < _openUntil) return BreakerState.Open;
        return _tripped ? BreakerState.HalfOpen : BreakerState.Closed;
    }

    // Is the HalfOpen slot taken by somebody other than `asker` (null = anybody) and still within its lease?
    private bool ProbeHeld(DateTime now, string? asker) =>
        _probeOwner is not null && now < _probeUntil && !string.Equals(_probeOwner, asker, StringComparison.Ordinal);

    private void ClearProbe()
    {
        _probeOwner = null;
        _probeUntil = DateTime.MinValue;
    }

    private bool TripStep(DateTime now, BreakerCause cause, string? detail, TimeSpan? retryAfter, DateTime? resetAtUtc)
    {
        var steps = _options.OpenSteps;
        var wasOpen = StateAt(now) == BreakerState.Open;

        // A failure that arrives while the rest is still in force (a request that was already in flight) is more evidence, not a new trip:
        // it must not climb the escalation ladder once per straggler. Only Closed -> Open and a failed HalfOpen probe take the next step.
        var stepIndex = wasOpen ? Math.Max(0, _step - 1) : _step;
        var local = steps.Count == 0 ? TimeSpan.FromSeconds(30) : steps[Math.Min(stepIndex, steps.Count - 1)];
        if (!wasOpen) _step++;
        _consecutive = 0;
        _window.Clear();

        // effectiveOpenUntil = max(currentOpenUntil, now + local step, now + Retry-After, reset instant): a source can lengthen a rest, never shorten it.
        var candidate = now + local;
        var mode = OpenMode.Timed;
        if (retryAfter is { } ra && now + ra > candidate)
        {
            candidate = now + ra;
            mode = OpenMode.RetryAfter;
        }

        if (resetAtUtc is { } reset && reset > candidate)
        {
            candidate = reset;
            mode = OpenMode.UntilReset;
        }

        if (candidate > _openUntil || !wasOpen)
        {
            _openUntil = candidate;
            _mode = mode;
        }

        _tripped = true;
        _cause = cause;
        _detail = detail ?? cause.ToString();
        TotalTrips++;
        Trips.Add(1, new KeyValuePair<string, object?>("breaker", Name), new KeyValuePair<string, object?>("cause", cause.ToString()));
        _log.Info(
            $"[breaker] {Name}: {(wasOpen ? "still OPEN" : "OPEN")} {Format(_openUntil - now)} [{_mode}] ({cause}: {_detail}){(_step > 1 ? $", escalation step {_step}" : string.Empty)}");
        Flight.Event("breaker.trip").Str("breaker", Name).Str("cause", cause.ToString()).Str("mode", _mode.ToString()).Flag("was_open", wasOpen)
            .Num("rest_ms", (_openUntil - now).TotalMilliseconds, 0).Num("step", _step).Str("detail", _detail).Emit();
        return true;
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:F1}h" : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:F0}m" : $"{Math.Max(0, t.TotalSeconds):F0}s";
}
