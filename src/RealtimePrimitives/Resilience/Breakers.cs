
namespace ShadowPrompter.Core.Resilience;

/// <summary>
/// The named breaker POLICIES of the application: what "stop asking" means for each kind of dependency, in one place, including which
/// failure causes belong to it. Components take a policy by name instead of inventing a counter and a delay array of their own.
/// </summary>
public static class Breakers
{
    private static readonly HashSet<BreakerCause> Availability = [BreakerCause.Transient, BreakerCause.Network];

    /// <summary>A capture device that fails to open or read (hot-unplug, a restarting sound server): retry soon, then back off, never hammer.</summary>
    public static CircuitBreaker DeviceReopen(string name, TimeProvider? time = null, ILogger? log = null) =>
        new(name, new CircuitBreakerOptions
        {
            Accepts = Availability,
            ConsecutiveFailures = 1,
            OpenSteps = [Ms(500), Ms(1000), Ms(2000), Ms(4000), Ms(8000), Ms(16000), Ms(30000)]
        }, time, log);

    /// <summary>How long a reopened device must deliver audio before it counts as healthy again (and the back-off starts over).</summary>
    public static readonly TimeSpan DeviceStable = TimeSpan.FromSeconds(10);

    /// <summary>A streaming STT connection: reconnect quickly, escalate to 15 s. A rejected key is a permanent trip.</summary>
    public static CircuitBreaker StreamReconnect(string name, TimeProvider? time = null, ILogger? log = null) =>
        new(name, new CircuitBreakerOptions
        {
            Accepts = [BreakerCause.Transient, BreakerCause.Network, BreakerCause.Authentication],
            ConsecutiveFailures = 1,
            OpenSteps = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)]
        }, time, log);

    /// <summary>A best-effort periodic job (warm-up pings, quota sync): a failing target is skipped for a while, not pinged on schedule.</summary>
    public static CircuitBreaker BackgroundJob(string name, TimeProvider? time = null, ILogger? log = null) =>
        new(name, new CircuitBreakerOptions
        {
            Accepts = [BreakerCause.Transient, BreakerCause.Network, BreakerCause.Authentication],
            ConsecutiveFailures = 2,
            OpenSteps = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)]
        }, time, log);

    /// <summary>An offline tool's model: a quota wall or a hard rejection takes it out for the rest of the run.</summary>
    public static CircuitBreaker ToolModel(string name, TimeProvider? time = null, ILogger? log = null) =>
        new(name, new CircuitBreakerOptions
        {
            Accepts = [BreakerCause.RateLimited, BreakerCause.QuotaExhausted, BreakerCause.Authentication, BreakerCause.Rejected]
        }, time, log);

    // ---- the answer pool: one policy per failure domain, top-down Provider > Credential > Budget > Target -----------------------

    /// <summary>A rejected key does not fix itself mid-interview: parked for an hour.</summary>
    public static readonly TimeSpan CredentialRejectedRest = TimeSpan.FromMinutes(60);

    /// <summary>A target the provider does not know (404/410): out for an hour.</summary>
    public static readonly TimeSpan TargetGoneRest = TimeSpan.FromMinutes(60);

    /// <summary>A target that answered another 4xx (or a rejected key of the default provider): out for two minutes.</summary>
    public static readonly TimeSpan TargetRejectedRest = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A quota budget (one per quota owner: a Google project, an OpenRouter account): escalates gently when a 429 says nothing about when to
    /// come back (concurrency limits clear in seconds); a stated Retry-After or reset instant is honoured when longer.
    /// </summary>
    public static CircuitBreaker PoolBudget(string key, TimeProvider? time = null, ILogger? log = null) =>
        new($"budget:{key}", new CircuitBreakerOptions
        {
            Accepts = [BreakerCause.RateLimited, BreakerCause.QuotaExhausted],
            ConsecutiveFailures = 1,
            OpenSteps = [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)]
        }, time, log);

    /// <summary>One key and whether the provider accepts it: rejected for <see cref="CredentialRejectedRest"/>, and every target of the key waits.</summary>
    public static CircuitBreaker PoolCredential(string id, TimeProvider? time = null, ILogger? log = null) =>
        new($"credential:{id}", new CircuitBreakerOptions
        {
            Accepts = [BreakerCause.Authentication, BreakerCause.QuotaExhausted], ConsecutiveFailures = 1, OpenSteps = [CredentialRejectedRest]
        }, time, log);

    /// <summary>Rejection of ONE target (404/400 for this model, or a rejected key of the default provider); the rest is dictated by the caller.</summary>
    public static CircuitBreaker PoolTargetRejection(string id, TimeProvider? time = null, ILogger? log = null) =>
        new($"target-rejected:{id}", new CircuitBreakerOptions
        {
            Accepts = [BreakerCause.Rejected, BreakerCause.Authentication], ConsecutiveFailures = 1, OpenSteps = [TargetRejectedRest]
        }, time, log);

    /// <summary>Health of ONE target: the 2nd failure in a row trips (one blip is normal), then 30 s, 60 s, 2 m, 4 m, 5 m.</summary>
    public static CircuitBreaker PoolTargetHealth(string id, TimeProvider? time = null, ILogger? log = null) =>
        new($"target:{id}", new CircuitBreakerOptions
        {
            Accepts = [BreakerCause.Transient, BreakerCause.Network],
            ConsecutiveFailures = 2,
            OpenSteps = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5)]
        }, time, log);

    /// <summary>
    /// Health of one vendor as a whole: DIFFERENT targets failing within a minute mean the vendor is down, not the model. A network-level
    /// failure (DNS, connect, reset before any HTTP status) hits every target of the provider alike, so the caller trips it at once.
    /// </summary>
    public static CircuitBreaker PoolProvider(string name, int targets, TimeProvider? time = null, ILogger? log = null) =>
        new($"provider:{name}", new CircuitBreakerOptions
        {
            Accepts = [BreakerCause.Transient, BreakerCause.Network],
            ConsecutiveFailures = int.MaxValue,
            DistinctSources = targets >= 2 ? Math.Min(2, targets) : 0,
            Window = TimeSpan.FromSeconds(60),
            OpenSteps = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5)]
        }, time, log);

    /// <summary>A wait derived from a breaker's rest, safe for WaitOne/Task.Delay (never negative, never longer than an hour).</summary>
    public static int WaitMs(TimeSpan remaining) =>
        (int)Math.Clamp(remaining.TotalMilliseconds, 0, TimeSpan.FromHours(1).TotalMilliseconds);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);
}
