# LoadSurge

High-performance, actor-based load testing framework for .NET.

[![NuGet](https://img.shields.io/nuget/v/LoadSurge.svg)](https://www.nuget.org/packages/LoadSurge)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LoadSurge.svg)](https://www.nuget.org/packages/LoadSurge)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

## Installation

```bash
dotnet add package LoadSurge
```

## Quick Start

```csharp
using LoadSurge.Models;
using LoadSurge.Runner;

var plan = new LoadExecutionPlan
{
    Name = "API_Load_Test",
    Settings = new LoadSettings
    {
        Concurrency = 50,
        Duration = TimeSpan.FromSeconds(30),
        Interval = TimeSpan.FromMilliseconds(100)
    },
    Action = async () =>
    {
        var response = await httpClient.GetAsync("https://api.example.com/health");
        return response.IsSuccessStatusCode;
    }
};

var result = await LoadRunner.Run(plan);

Console.WriteLine($"Total: {result.Total}, Success: {result.Success}, Failed: {result.Failure}");
Console.WriteLine($"RPS: {result.RequestsPerSecond:F1}, Avg: {result.AverageLatency:F1}ms, P95: {result.Percentile95Latency:F1}ms");
```

## Examples

### Fixed Iteration Count

```csharp
var plan = new LoadExecutionPlan
{
    Name = "Fixed_100_Requests",
    Settings = new LoadSettings
    {
        Concurrency = 10,
        Duration = TimeSpan.FromMinutes(5),
        Interval = TimeSpan.FromMilliseconds(100),
        MaxIterations = 100  // Stop after exactly 100 requests
    },
    Action = async () => { /* your test */ return true; }
};
```

### Database Testing

```csharp
var plan = new LoadExecutionPlan
{
    Name = "DB_Pool_Test",
    Settings = new LoadSettings
    {
        Concurrency = 100,
        Duration = TimeSpan.FromMinutes(2),
        Interval = TimeSpan.FromMilliseconds(50)
    },
    Action = async () =>
    {
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        return await cmd.ExecuteScalarAsync() != null;
    }
};
```

## Configuration

### Settings

| Property | Description |
|----------|-------------|
| `Concurrency` | Number of parallel operations per interval |
| `Duration` | Total test duration |
| `Interval` | Time between batches |
| `MaxIterations` | Optional max request count |
| `TerminationMode` | How test stops (Duration, CompleteCurrentInterval, StrictDuration) |
| `GracefulStopTimeout` | Time to wait for in-flight requests (default: 30% of duration) |
| `RequestTimeout` | Optional per-request timeout; hung requests counted as failures |

### Workload Model

LoadSurge uses an **open workload model** (constant arrival rate, like NBomber `Inject` / k6 `constant-arrival-rate`): iterations are injected on schedule regardless of response times. If the system under test slows down, in-flight requests accumulate — which is exactly what a load test must measure.

```csharp
var config = new LoadWorkerConfiguration
{
    MaxInFlight = 10_000  // optional safety cap; excess iterations are dropped and counted
};
var result = await LoadRunner.Run(plan, config);
```

Per-request timeout and cancellation-aware actions:

```csharp
var plan = new LoadExecutionPlan
{
    Name = "API_Test",
    Settings = new LoadSettings
    {
        // ...
        RequestTimeout = TimeSpan.FromSeconds(5), // hung requests counted as failures
        IncludeQueueTimeInLatency = true          // opt-in: latency from scheduled slot (coordinated-omission correction)
    },
    // Preferred over Action: the token fires on RequestTimeout and run cancellation,
    // so timed-out work is truly aborted instead of leaking in the background.
    ActionWithCancellation = async token =>
    {
        var response = await httpClient.GetAsync(url, token);
        return response.IsSuccessStatusCode;
    }
};

// Cancelling returns partial results collected so far (no exception).
var result = await LoadRunner.Run(plan, config, cancellationToken);
```

Live progress (long runs are not a black box):

```csharp
var config = new LoadWorkerConfiguration
{
    Progress = new Progress<LoadProgress>(p =>
        Console.WriteLine($"[{p.ElapsedSeconds:F0}s] started={p.RequestsStarted} ok={p.Success} " +
                          $"fail={p.Failure} inflight={p.InFlight} dropped={p.Dropped} rps={p.RequestsPerSecond:F0}")),
    ProgressInterval = TimeSpan.FromSeconds(1) // default
};
```

## Results

```csharp
result.Total              // Total completed requests
result.Success            // Successful requests
result.Failure            // Failed requests (incl. timeouts)
result.Dropped            // Iterations dropped by MaxInFlight cap
result.RequestsInFlight   // Scheduled but unfinished at test end (queued or executing)
result.RequestsPerSecond  // Throughput
result.AverageLatency     // Mean latency (ms)
result.Percentile95Latency // P95 latency (ms)
result.Percentile99Latency // P99 latency (ms)
result.AverageFailureLatency // Mean latency of failures (ms); failures are excluded from the stats above
result.MaxFailureLatency  // Max latency of failures (ms)
```

## Benchmarks

BenchmarkDotNet v0.15.8, Apple M4 (10 cores), .NET 8.0.22. Reproduce: `dotnet run -c Release --project benchmarks/LoadSurge.Benchmarks`.

Engine overhead, 1 s run with a no-op action (100 ms interval):

| Concurrency per interval | Iterations | Wall clock | Allocated | Per iteration |
|---|---|---|---|---|
| 1,000 | 10,000 | 1.001 s | 2.6 MB | ~260 B |
| 10,000 | 100,000 | 1.005 s | 26 MB | ~260 B |

Metrics hot path (per call, 0 B allocated):

| Operation | Mean |
|---|---|
| `RequestStarted` + `RecordResult` | 6.7 ns |
| `RecordResult` (failure) | 5.7 ns |
| `InFlight` read | 0.11 ns |

Overhead is ~1 ms (1k) and ~5 ms (10k) over the target duration, so the generator stays out of the way of what you measure.

## Samples

Runnable offline examples: `dotnet run --project samples/LoadSurge.Samples`

## Requirements

- .NET Standard 2.0+ (.NET Framework 4.7.2+, .NET 6/8/9+)
- The `net8.0` build is Native-AOT-compatible and trimmable
- Zero external dependencies

## Related

- [xUnitV3LoadFramework](https://github.com/mrviduus/xUnitV3LoadFramework) - xUnit v3 integration with `[Load]` attribute

## License

MIT
