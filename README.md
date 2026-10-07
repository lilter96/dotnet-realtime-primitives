# .NET Real-time Primitives

A focused extraction of the concurrency, resilience and observability code from my private experimental audio/AI application, ShadowPrompter. This repository exposes the engineering mechanisms with their original tests and source namespaces.

**.NET 10 · 89 passing tests · no credentials or external services required**

## What it demonstrates

| Mechanism | Engineering detail | Source / tests |
| --- | --- | --- |
| Typed circuit breaker | Separate failure domains, longest applicable backoff, one half-open probe, expiring probe leases, permanent rejection and recovery | [CircuitBreaker](src/RealtimePrimitives/Resilience/CircuitBreaker.cs), [tests](tests/RealtimePrimitives.Tests/Resilience/CircuitBreakerTests.cs) |
| HTTP failure classification | Authentication rejection, Retry-After milliseconds/seconds/date handling, nested transport failures | [Classifier](src/RealtimePrimitives/Resilience/HttpFailureClassifier.cs), [tests](tests/RealtimePrimitives.Tests/Resilience/HttpFailureClassifierTests.cs) |
| Retained audio stream | Bounded retention, reconnect replay, acquisition time mapping and explicit loss markers | [Reader](src/RealtimePrimitives/Domain/RetainingAudioReader.cs), [tests](tests/RealtimePrimitives.Tests/Audio/RetainingAudioReaderTests.cs) |
| Non-blocking flight recorder | Bounded channel, drop accounting, faulty sink isolation, async context and ordered shutdown | [Recorder](src/RealtimePrimitives/Observability/Flight/FlightRecorder.cs), [tests](tests/RealtimePrimitives.Tests/Observability/Flight/FlightRecorderTests.cs) |

## Review in 10 minutes

1. Read the breaker state-machine contract and the concurrent half-open probe tests.
2. Inspect retention/replay: retained bytes must map to acquisition time, including gaps.
3. Read the recorder's queue overflow and failing-sink tests. Observability must not break the observed work.
4. Run the complete extracted suite:

```bash
dotnet test tests/RealtimePrimitives.Tests/RealtimePrimitives.Tests.csproj -c Release
```

Requires .NET 10 SDK. No live model provider, audio device, browser login, Docker service or paid API is used by this test suite.

## Provenance and limits

The implementation and tests were extracted from private source commit `9895c41`. Namespaces remain `ShadowPrompter.*` to make the origin explicit. The extracted project rebuilt successfully with warnings treated as errors; **89 tests passed on 2026-10-07**.

This is a reviewable source extraction, not a polished general-purpose NuGet package. It has not been benchmarked as a standalone library. It does not include the parent application's UI, operating-system capture adapters, model routing pool or personal prompts; no claim about those omitted features is established by these 89 tests.

AI assisted parts of implementation. Architecture, constraints, verification and final review remain my responsibility. Public source is provided for review; no open-source reuse license has been added.
