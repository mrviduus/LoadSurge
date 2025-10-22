<div align="center">

# ⚡ LoadSurge

**High-performance, actor-based load testing framework for .NET**

[![NuGet](https://img.shields.io/nuget/v/LoadSurge.svg?style=flat-square)](https://www.nuget.org/packages/LoadSurge)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LoadSurge.svg?style=flat-square)](https://www.nuget.org/packages/LoadSurge)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg?style=flat-square)](LICENSE)
[![GitHub Stars](https://img.shields.io/github/stars/mrviduus/LoadSurge?style=flat-square)](https://github.com/mrviduus/LoadSurge/stargazers)

*Unleash the power of distributed load testing with battle-tested Akka.NET actors*

[Features](#-features) • [Quick Start](#-quick-start) • [Documentation](#-architecture) • [Examples](#-advanced-examples)

</div>

---

## 📖 Overview

LoadSurge is a **framework-agnostic load testing engine** built on Akka.NET actors for distributed, fault-tolerant load testing. Born from [xUnitV3LoadFramework](https://github.com/mrviduus/xUnitV3LoadFramework), LoadSurge provides the core load testing capabilities that can be integrated with **any testing framework** or used standalone.

Perfect for testing APIs, microservices, databases, and distributed systems under extreme load.

## ✨ Features

- 🎭 **Actor-Based Architecture** - Built on battle-tested Akka.NET for distributed, fault-tolerant execution
- 🚀 **High Performance** - Hybrid mode supports **100,000+ concurrent operations** using channels and fixed thread pools
- 🔧 **Framework Agnostic** - Use with xUnit, NUnit, MSTest, or standalone in console applications
- 🎯 **Precise Control** - Three termination modes for exact request count control
- 💫 **Graceful Shutdown** - Configurable grace periods for in-flight request completion
- 📊 **Comprehensive Metrics** - Detailed latency percentiles, throughput, and resource utilization

## 🚀 Quick Start

### Installation

```bash
dotnet add package LoadSurge
```

### Basic Usage

Get started with a simple load test in just a few lines:

```csharp
using LoadSurge.Models;
using LoadSurge.Runner;

var plan = new LoadExecutionPlan
{
    Name = "API_Load_Test",
    Settings = new LoadSettings
    {
        Concurrency = 50,
        Duration = TimeSpan.FromMinutes(2),
        Interval = TimeSpan.FromMilliseconds(100)
    },
    Action = async () =>
    {
        var response = await httpClient.GetAsync("https://api.example.com/endpoint");
        return response.IsSuccessStatusCode;
    }
};

var result = await LoadRunner.Run(plan);

Console.WriteLine($"✅ Total Requests: {result.TotalRequests}");
Console.WriteLine($"📈 Success Rate: {result.Success}/{result.TotalRequests} ({result.Success * 100.0 / result.TotalRequests:F1}%)");
Console.WriteLine($"⚡ Throughput: {result.RequestsPerSecond:F1} req/sec");
Console.WriteLine($"⏱️  Avg Latency: {result.AverageLatency:F2}ms");
Console.WriteLine($"📊 P95 Latency: {result.Percentile95Latency:F2}ms");
```

## 📚 Architecture

### Core Components

**LoadRunner** - Entry point for executing load tests. Orchestrates actor system creation and manages test lifecycle.

**LoadWorkerActorHybrid** (default) - High-performance implementation using fixed thread pools with channels. Optimized for 100k+ concurrent operations.

**LoadWorkerActor** - Task-based implementation for moderate load scenarios. Good for functional testing.

**ResultCollectorActor** - Aggregates performance metrics including latency percentiles, throughput, and success rates.

### Execution Modes

```csharp
var config = new LoadWorkerConfiguration
{
    Mode = LoadWorkerMode.Hybrid  // or TaskBased
};

var result = await LoadRunner.Run(plan, config);
```

- **Hybrid** (default) - Channel-based with fixed worker pools. Best for high-throughput scenarios (10k+ RPS)
- **TaskBased** - Uses .NET Task.Run. Best for moderate load (< 10k RPS)

### Termination Modes

Control exactly when the test stops:

```csharp
Settings = new LoadSettings
{
    Concurrency = 10,
    Duration = TimeSpan.FromSeconds(30),
    Interval = TimeSpan.FromMilliseconds(500),
    TerminationMode = TerminationMode.CompleteCurrentInterval
}
```

- **Duration** - Stop immediately when duration expires (fastest)
- **CompleteCurrentInterval** - Let current interval finish (recommended for accurate request counts)
- **StrictDuration** - Stop at exact duration, cancel in-flight requests

### Graceful Shutdown

```csharp
Settings = new LoadSettings
{
    Duration = TimeSpan.FromMinutes(5),
    GracefulStopTimeout = TimeSpan.FromSeconds(10)  // Wait up to 10s for in-flight requests
}
```

If not specified, automatically calculated as 30% of duration (min: 5s, max: 60s).

## 💡 Advanced Examples

### Database Load Testing

```csharp
var plan = new LoadExecutionPlan
{
    Name = "Database_Connection_Pool_Test",
    Settings = new LoadSettings
    {
        Concurrency = 100,
        Duration = TimeSpan.FromMinutes(5),
        Interval = TimeSpan.FromMilliseconds(50)
    },
    Action = async () =>
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP 1 * FROM Users WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", Random.Shared.Next(1, 10000));

        var result = await command.ExecuteScalarAsync();
        return result != null;
    }
};

var result = await LoadRunner.Run(plan);
```

### HTTP API Load Testing

```csharp
using var httpClient = new HttpClient();

var plan = new LoadExecutionPlan
{
    Name = "API_Stress_Test",
    Settings = new LoadSettings
    {
        Concurrency = 200,
        Duration = TimeSpan.FromMinutes(10),
        Interval = TimeSpan.FromMilliseconds(25),
        TerminationMode = TerminationMode.CompleteCurrentInterval
    },
    Action = async () =>
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync(
                "https://api.example.com/orders",
                new { customerId = Random.Shared.Next(1, 1000), amount = 99.99 }
            );

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
};

var result = await LoadRunner.Run(plan);

// Analyze results
Console.WriteLine($"Total Requests: {result.TotalRequests}");
Console.WriteLine($"Successful: {result.Success} ({result.Success * 100.0 / result.TotalRequests:F1}%)");
Console.WriteLine($"Failed: {result.Failed}");
Console.WriteLine($"Throughput: {result.RequestsPerSecond:F1} req/sec");
Console.WriteLine($"Latency - Min: {result.MinLatency:F2}ms, Avg: {result.AverageLatency:F2}ms, Max: {result.MaxLatency:F2}ms");
Console.WriteLine($"Latency - P50: {result.MedianLatency:F2}ms, P95: {result.Percentile95Latency:F2}ms, P99: {result.Percentile99Latency:F2}ms");
```

## 📊 Performance Metrics

LoadSurge provides comprehensive performance data:

```csharp
public class LoadResult
{
    // Request Counts
    public int TotalRequests { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }

    // Throughput
    public double RequestsPerSecond { get; set; }

    // Latency Statistics (milliseconds)
    public double AverageLatency { get; set; }
    public double MinLatency { get; set; }
    public double MaxLatency { get; set; }
    public double MedianLatency { get; set; }
    public double Percentile95Latency { get; set; }
    public double Percentile99Latency { get; set; }

    // Resource Utilization
    public int WorkerThreadsUsed { get; set; }
    public double WorkerUtilization { get; set; }
    public long PeakMemoryUsage { get; set; }
}
```

## 🔗 Integration with Test Frameworks

### xUnit v3

Use [xUnitV3LoadFramework](https://github.com/mrviduus/xUnitV3LoadFramework) for seamless xUnit integration with attributes and fluent API.

### NUnit / MSTest

Use LoadSurge directly in your test methods:

```csharp
[Test]  // NUnit
public async Task Load_Test_API_Endpoint()
{
    var plan = new LoadExecutionPlan { /* ... */ };
    var result = await LoadRunner.Run(plan);

    Assert.That(result.Success, Is.GreaterThan(result.TotalRequests * 0.95)); // 95% success rate
    Assert.That(result.Percentile95Latency, Is.LessThan(500)); // P95 < 500ms
}
```

## 🤔 Why LoadSurge?

### vs NBomber
- ✅ Proven Akka.NET actors for distribution and fault tolerance
- ✅ Simpler API focused on common load testing scenarios
- ✅ Tighter integration with .NET testing frameworks

### vs k6/Gatling
- ✅ Native .NET - reuse your existing C# code and libraries
- ✅ Framework-agnostic design for flexibility
- ✅ Full control over test logic with C# async/await

### vs Custom Solutions
- ✅ Production-ready with comprehensive error handling
- ✅ Proven actor model for scalability
- ✅ Detailed metrics out of the box

## ⚙️ Requirements

- .NET 8.0 or later
- Akka.NET 1.5.54

## 🤝 Contributing

Contributions are welcome! Please submit issues and pull requests on [GitHub](https://github.com/mrviduus/LoadSurge).

## 📄 License

MIT License - see [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

LoadSurge is extracted from [xUnitV3LoadFramework](https://github.com/mrviduus/xUnitV3LoadFramework) to provide a framework-agnostic core that can be used across different testing frameworks and scenarios.

---

<div align="center">

**Built with ❤️ by [Vasyl Vdovychenko](https://github.com/mrviduus)**

⭐ Star this repo if you find it useful!

</div>
