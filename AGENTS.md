# Real-time primitives showcase

Run `dotnet test tests/RealtimePrimitives.Tests/RealtimePrimitives.Tests.csproj -c Release` after changes.
Preserve the original namespaces and provenance. Time-sensitive resilience code accepts TimeProvider; tests use a manual clock.
Do not add live providers, devices, credentials or side effects to this offline suite. Keep telemetry non-blocking and best-effort.
