# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LoadSurge is a high-performance, actor-based load testing framework for .NET built on Akka.NET. It provides framework-agnostic load testing capabilities that can be integrated with any testing framework or used standalone.

**Key Technologies:**
- .NET 8.0
- Akka.NET 1.5.54 (Actor model)
- xUnit v3 (Testing)
- NuGet package published as `LoadSurge`

## Build & Development Commands

### Build
```bash
# Restore dependencies
dotnet restore

# Build solution
dotnet build

# Build in Release mode
dotnet build --configuration Release
```

### Testing
```bash
# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity detailed

# Run tests with code coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory TestResults

# Run a specific test by class name
dotnet test --filter "FullyQualifiedName~HybridModeTests"

# Run a specific test method
dotnet test --filter "FullyQualifiedName~HybridModeTests.ShouldHandleHighConcurrency"

# Run tests excluding CI-flaky tests
dotnet test --filter "Category!=CI-Flaky"
```

### Package
```bash
# Create NuGet package
dotnet pack src/LoadSurge/LoadSurge.csproj --configuration Release

# Pack with specific version
dotnet pack src/LoadSurge/LoadSurge.csproj -p:Version=1.0.1

# Pack with symbols for debugging
dotnet pack src/LoadSurge/LoadSurge.csproj --configuration Release --include-symbols --include-source
```

## Architecture Overview

### Actor-Based Design

LoadSurge uses the actor model for distributed, fault-tolerant load testing:

```
LoadRunner (Entry Point)
    ↓
    Creates ActorSystem
    ↓
    ┌─────────────────────────────────────┐
    │  ResultCollectorActor               │  ← Aggregates metrics
    │  - Tracks requests                  │
    │  - Calculates latency percentiles   │
    │  - Reports final LoadResult         │
    └─────────────────────────────────────┘
    ↓
    ┌─────────────────────────────────────┐
    │  LoadWorkerActor / Hybrid           │  ← Executes load test
    │  - Schedules batches at intervals   │
    │  - Executes user's Action           │
    │  - Reports results via messages     │
    └─────────────────────────────────────┘
```

**Key Components:**

1. **LoadRunner** (`src/LoadSurge/Runner/LoadRunner.cs`)
   - Static entry point for executing load tests
   - Orchestrates actor system lifecycle
   - Uses Akka Ask pattern with adaptive timeouts

2. **LoadWorkerActorHybrid** (`src/LoadSurge/Actors/LoadWorkerActorHybrid.cs`) - **DEFAULT**
   - Channel-based producer-consumer pattern
   - Fixed thread pool prevents thread pool exhaustion
   - Optimized for 100k+ concurrent operations
   - Tracks queue time metrics

3. **LoadWorkerActor** (`src/LoadSurge/Actors/LoadWorkerActor.cs`)
   - Task-based implementation using Task.Run
   - Good for moderate load scenarios (<10k RPS)
   - Simpler implementation, higher overhead

4. **ResultCollectorActor** (`src/LoadSurge/Actors/ResultCollectorActor.cs`)
   - Aggregates all performance metrics
   - Calculates latency percentiles (P50, P95, P99)
   - Tracks resource utilization and memory usage

### Execution Modes

**LoadWorkerMode:**
- `Hybrid` (default) - Channel-based with fixed worker pool, best for high throughput (100k+ RPS)
- `TaskBased` - Task.Run based, best for moderate load (<10k RPS)
- `ActorBased` - **NOT IMPLEMENTED** - Throws exception if selected (reserved for future actor-based isolation)

**TerminationMode:**
- `Duration` - Stop immediately when duration expires (fastest)
- `CompleteCurrentInterval` - Finish current batch before stopping (recommended for accurate counts)
- `StrictDuration` - Hard stop at duration, cancel in-flight requests

### Message Protocol

Actors communicate via immutable message objects (`src/LoadSurge/Messages/`):

- `StartLoadMessage` - Initiates test execution
- `RequestStartedMessage` - Tracks request lifecycle
- `StepResultMessage` - Reports individual result (success/failure, latency, queue time)
- `BatchCompletedMessage` - Marks batch completion
- `WorkerThreadCountMessage` - Reports worker pool size
- `GetLoadResultMessage` - Requests final aggregated results

### LoadWorkerConfiguration Options

Fine-tune load test execution with these configuration settings:

```csharp
var config = new LoadWorkerConfiguration
{
    Mode = LoadWorkerMode.Hybrid,              // Execution mode (Hybrid/TaskBased)
    MaxWorkerThreads = null,                   // null = auto-calculate based on CPU and concurrency
    ChannelCapacity = null,                    // null = unbounded (higher memory, better throughput)
    EnableDetailedMetrics = false,             // Enable comprehensive monitoring (impacts performance)
    WorkerUtilizationWarningThreshold = 0.8,   // Warn if worker efficiency < 80%
    QueueTimeWarningThreshold = 1000           // Warn if queue time > 1000ms
};
```

**Configuration Guidelines:**
- `MaxWorkerThreads`: Leave null for auto-sizing or set for predictable resource usage
- `ChannelCapacity`: Use bounded (e.g., 10000) for memory-constrained environments
- `EnableDetailedMetrics`: Turn on for debugging, off for benchmarks
- Worker utilization < 80% indicates insufficient parallelism or I/O bottlenecks
- Queue time > 1000ms suggests worker pool saturation

## Key Design Patterns

### Graceful Shutdown
- In-flight requests are allowed to complete after duration expires
- Grace period auto-calculated as 30% of test duration (min: 5s, max: 60s)
- Can be overridden via `LoadSettings.GracefulStopTimeout`
- Both worker implementations handle graceful shutdown differently:
  - TaskBased: Waits for tasks with timeout
  - Hybrid: Completes channel, waits for worker pool

### Worker Pool Sizing (Hybrid Mode)
```csharp
baseWorkers = ProcessorCount * 2
scaledWorkers = Max(baseWorkers, concurrency / 10)
maxWorkers = Min(1000, ProcessorCount * 50)
optimalWorkers = Min(scaledWorkers, maxWorkers)
```

### Percentile Calculation
- Uses ceiling method for conservative estimates
- Percentiles calculated from sorted latency list
- P95 = latencies[ceiling(0.95 * count)]

## Code Patterns & Conventions

### Test Actions Must Be:
1. **Thread-safe** - Will be called concurrently
2. **Idempotent** - Safe to retry
3. **Return Task<bool>** - true = success, false = failure

Example:
```csharp
Action = async () =>
{
    try
    {
        var response = await httpClient.GetAsync(url);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false; // Mark as failure
    }
}
```

### Actor Pattern Usage
- Use `Tell` for fire-and-forget messages
- Use `Ask<T>` for request-response with timeout
- Use `PipeTo` for async continuation with sender context
- Always dispose ActorSystem after use

### Timing & Scheduling
- Use `DateTime.UtcNow` for all timing
- Maintain interval precision with calculated next execution times
- Monitor schedule drift and log warnings

## Important Implementation Details

### LoadResult Metrics

The `LoadResult` object provides comprehensive performance metrics:

**Core Metrics:**
- `ScenarioName` / `Name` - Test scenario identifier
- `Total` - Sum of successful and failed operations
- `Success` - Operations completed successfully
- `Failure` - Operations that failed
- `Time` - Total execution time in seconds
- `RequestsPerSecond` - Calculated throughput

**Latency Metrics (milliseconds):**
- `MinLatency` - Best-case response time
- `AverageLatency` - Mean response time across all operations
- `MedianLatency` - 50th percentile (P50)
- `Percentile95Latency` - 95% of requests complete within this time (P95)
- `Percentile99Latency` - 99% of requests complete within this time (P99)
- `MaxLatency` - Worst-case response time

**Request Tracking:**
- `RequestsStarted` - Total initiated requests (may exceed Total if test stops mid-flight)
- `RequestsInFlight` - Currently executing requests at test end
- `BatchesCompleted` - Number of execution batches processed

**Queue Metrics (Hybrid Mode Only):**
- `AvgQueueTime` - Average time requests wait before execution (ms)
- `MaxQueueTime` - Maximum queue wait time observed (ms)
- High queue times indicate worker pool saturation

**Resource Metrics:**
- `WorkerThreadsUsed` - Number of worker threads in hybrid mode
- `WorkerUtilization` - Efficiency percentage (0.0-1.0)
- `PeakMemoryUsage` - Maximum memory consumption in bytes

### Request Counting
- `RequestsStarted` tracks all initiated requests
- `RequestsInFlight` tracks currently executing requests
- `Total` tracks completed requests (Success + Failed)
- In CI environments, allow variance (±10%) for timing-sensitive tests

### Queue Time Tracking (Hybrid Mode Only)
- Measures time from work item creation to execution start
- Helps identify scheduling bottlenecks vs. execution bottlenecks
- Not available in TaskBased mode
- High queue times (>1000ms) indicate worker pool saturation

### Memory Monitoring
- Peak memory captured during `RequestStartedMessage` processing
- Uses `GC.GetTotalMemory(false)` for snapshot
- Reported in `LoadResult.PeakMemoryUsage` (bytes)

### Adaptive Timeouts
Worker timeout calculation:
```csharp
var testDuration = settings.Duration.TotalSeconds;
var workerTimeout = TimeSpan.FromSeconds(Math.Max(60, testDuration + 60));
```
This ensures resilience in CI environments where execution may be slower.

## Testing Guidelines

### Test File Organization
All tests in `tests/LoadSurge.Tests/Unit/`:
- `HybridModeTests.cs` - High-concurrency scenarios
- `RequestCountAccuracyTests.cs` - Request counting validation
- `GracefulStopConfigurationTests.cs` - Shutdown behavior
- `LoadRunnerTimeoutTests.cs` - Timeout and error handling
- `BackwardCompatibilityTests.cs` - Legacy support

### Test Patterns
```csharp
[Fact]
public async Task Descriptive_Test_Name()
{
    // Arrange - Create plan with settings
    var plan = new LoadExecutionPlan { /* ... */ };

    // Act - Execute load test
    var result = await LoadRunner.Run(plan);

    // Assert - Validate results
    Assert.True(result.Total >= expectedMin);
    Assert.True(result.Total <= expectedMax); // Allow timing variance
}
```

### Timing Variance in Tests
- CI environments may introduce timing variance
- Allow ±10% variance for request count assertions
- Use `Assert.True(result.Total >= X && result.Total <= Y)` pattern
- For StrictDuration tests, variance should be minimal

## CI/CD

### GitHub Actions Workflow (`.github/workflows/ci-cd.yml`)
- **Build Job:** Restore → Build → Test → Upload Coverage
- **Package Job:** Triggers on main branch or tags → Publishes to NuGet
- **Security Job:** Scans for vulnerabilities

### NuGet Publishing
- Automatic on main branch commits (version: 1.0.0.{GITHUB_RUN_NUMBER})
- Automatic on version tags (e.g., `v1.0.1` uses exact tag version)
- Requires `NUGET_API_KEY` secret in repository
- Package includes symbols and source link for debugging

## Common Scenarios

### Adding New Termination Mode
1. Add enum value to `Models/TerminationMode.cs`
2. Implement logic in both worker actors (TaskBased and Hybrid)
3. Add tests in `RequestCountAccuracyTests.cs`
4. Update README examples

### Adding New Metrics
1. Add field to `ResultCollectorActor` state
2. Create message type in `Messages/` if needed
3. Update `LoadResult` model
4. Handle message in `ResultCollectorActor`
5. Add tests to validate new metric

### Performance Optimization
- Profile with high-concurrency scenarios (20k+ RPS)
- Monitor queue time in hybrid mode for scheduling bottlenecks
- Check worker utilization (should be <80%)
- Review `PeakMemoryUsage` for memory leaks
- Use `EnableDetailedMetrics` for deeper analysis

## Project Structure

```
LoadSurge/
├── src/LoadSurge/           # Main library
│   ├── Actors/              # Worker and collector actors
│   ├── Configuration/       # LoadWorkerConfiguration, modes
│   ├── Messages/            # Actor message contracts
│   ├── Models/              # LoadExecutionPlan, LoadResult, LoadSettings
│   └── Runner/              # LoadRunner entry point
├── tests/LoadSurge.Tests/   # xUnit test suite
│   └── Unit/                # Unit and integration tests
├── .github/workflows/       # CI/CD configuration
├── Directory.Packages.props # Central package management
└── LoadSurge.sln           # Solution file

Key files:
- src/LoadSurge/LoadSurge.csproj - Package configuration
- global.json - .NET SDK version (8.0)
```

### Build Configuration

**Quality Settings:**
- `TreatWarningsAsErrors: true` (Release mode) - Enforces clean builds
- `Nullable: enable` - C# nullable reference types for safety
- `GenerateDocumentationFile: true` - XML documentation required
- `Deterministic: true` - Reproducible builds
- `IncludeSymbols/IncludeSource: true` - Full debugging support
- `LangVersion: 12` - C# 12 language features

## Backward Compatibility

LoadSurge maintains backward compatibility with previous versions:
- Default behavior unchanged (now using Hybrid mode)
- Legacy configurations supported
- API surface remains stable
- Tests in `BackwardCompatibilityTests.cs` ensure compatibility

## References

- **Repository:** https://github.com/mrviduus/LoadSurge
- **NuGet Package:** https://www.nuget.org/packages/LoadSurge
- **Parent Project:** https://github.com/mrviduus/xUnitV3LoadFramework
- **Akka.NET Docs:** https://getakka.net/articles/intro/what-is-akka.html
