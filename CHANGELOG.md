# Changelog

All notable changes to the LoadSurge project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2025-10-21

### Added
- Initial release of LoadSurge as a standalone, framework-agnostic load testing engine
- Extracted core functionality from xUnitV3LoadFramework v2.0.0
- Actor-based architecture using Akka.NET 1.5.54
- LoadRunner for orchestrating load test execution
- LoadWorkerActorHybrid for high-performance channel-based execution (100k+ RPS)
- LoadWorkerActor for task-based execution (moderate load scenarios)
- ResultCollectorActor for comprehensive metrics aggregation
- Three termination modes: Duration, CompleteCurrentInterval, StrictDuration
- Configurable graceful shutdown with automatic timeout calculation
- Comprehensive performance metrics including:
  - Request counts (total, success, failed)
  - Throughput (requests per second)
  - Latency statistics (min, max, average, median, P95, P99)
  - Resource utilization (worker threads, memory usage)
- Support for .NET 8.0
- MIT License
- Comprehensive test suite with 5 core unit tests
- Full XML documentation for all public APIs

### Changed
- Namespace migration from `xUnitV3LoadFramework.LoadRunnerCore.*` to `LoadSurge.*`
- Updated all internal references to use new LoadSurge namespaces

### Technical Details
- Target Framework: .NET 8.0
- Language Version: C# 12
- Key Dependencies:
  - Akka.NET 1.5.54
  - Microsoft.SourceLink.GitHub 8.0.0 (build-time)
- Package Structure:
  - LoadSurge (core package)
  - LoadSurge.Tests (test project)

### Migration Notes
For users migrating from xUnitV3LoadFramework v2.x:
- The core load testing engine is now available as the `LoadSurge` package
- xUnit-specific features (LoadAttribute, LoadTestRunner) remain in the xUnitV3LoadFramework package
- Direct LoadRunner users: Update `using xUnitV3LoadFramework.LoadRunnerCore.*` to `using LoadSurge.*`
- See PROGRESS.md for detailed migration information

## Future Releases

### [Planned for 1.1.0]
- Additional performance optimizations
- Enhanced metrics and reporting capabilities
- Support for custom result collectors
- Performance profiling tools

### [Planned for 2.0.0]
- Support for distributed load testing across multiple nodes
- Built-in result persistence
- Real-time metrics streaming
- Dashboard integration

---

For integration with xUnit v3, see [xUnitV3LoadFramework](https://github.com/mrviduus/xUnitV3LoadFramework)
