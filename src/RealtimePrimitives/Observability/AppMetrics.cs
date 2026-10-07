using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ShadowPrompter.Core.Observability;

/// <summary>
/// The application's one Meter (every counter/histogram/gauge any layer creates lives off this) and ActivitySource. Lives in Core, not
/// App, so lower layers (CircuitBreaker, the pool) can create instruments without a reference to the composition root; the OTel SDK and
/// its exporters are wired only at the composition root (see ShadowPrompter.App.Observability.TelemetryServices). A Meter with nothing
/// listening is a cheap no-op, so every call site below is safe whether or not OTel is enabled or even referenced by the running process.
/// </summary>
public static class AppMetrics
{
    public const string MeterName = "ShadowPrompter";
    public const string ActivitySourceName = "ShadowPrompter";

    public static readonly Meter Meter = new(MeterName, "1.0.0");
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
}
