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

### Execution Modes

```csharp
var config = new LoadWorkerConfiguration { Mode = LoadWorkerMode.Hybrid };
var result = await LoadRunner.Run(plan, config);
```

- **Hybrid** (default) - Channel-based, optimized for high throughput (10k+ RPS)
- **TaskBased** - Task.Run based, suitable for moderate load

## Results

```csharp
result.Total              // Total requests
result.Success            // Successful requests
result.Failure            // Failed requests
result.RequestsPerSecond  // Throughput
result.AverageLatency     // Mean latency (ms)
result.Percentile95Latency // P95 latency (ms)
result.Percentile99Latency // P99 latency (ms)
```

## Requirements

- .NET Standard 2.0+ (.NET Framework 4.7.2+, .NET 6/8/9+)

## Related

- [xUnitV3LoadFramework](https://github.com/mrviduus/xUnitV3LoadFramework) - xUnit v3 integration with `[Load]` attribute

## License

MIT
