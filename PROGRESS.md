# Surge - Extraction Progress Documentation

This document tracks the progress of extracting the core load testing engine from xUnitV3LoadFramework into the standalone Surge package.

## Project Overview

**Goal:** Extract the framework-agnostic LoadRunnerCore from xUnitV3LoadFramework into a standalone NuGet package named "Surge", enabling use with any testing framework or standalone applications.

**Source:** [xUnitV3LoadFramework](https://github.com/mrviduus/xUnitV3LoadFramework) v2.0.0
**Target:** Surge v1.0.0 (new standalone package)
**Date:** October 21, 2025

---

## Completed Tasks ✅

### Phase 1: Repository Setup & Structure
- [x] Created Surge repository directory structure
  - `src/Surge/` - Core package source
  - `tests/Surge.Tests/` - Test project
  - `.github/workflows/` - CI/CD pipelines
  - `docs/` - Documentation
  - `examples/` - Example projects (placeholder)

- [x] Created project configuration files
  - `Surge.csproj` - Package project with metadata
  - `Surge.sln` - Solution file
  - `Directory.Packages.props` - Central package management
  - `global.json` - .NET SDK version (8.0.100)
  - `.gitignore` - Standard .NET exclusions

### Phase 2: Code Migration
- [x] Extracted LoadRunnerCore source code (15 files)
  - **Actors/** (3 files)
    - `LoadWorkerActor.cs` - Task-based worker implementation
    - `LoadWorkerActorHybrid.cs` - Channel-based high-performance worker
    - `ResultCollectorActor.cs` - Metrics aggregation actor
  - **Configuration/** (1 file)
    - `LoadWorkerConfiguration.cs` - Worker mode configuration
  - **Messages/** (6 files)
    - `BatchCompletedMessage.cs`
    - `GetLoadResultMessage.cs`
    - `RequestStartedMessage.cs`
    - `StartLoadMessage.cs`
    - `StepResultMessage.cs`
    - `WorkerThreadCountMessage.cs`
  - **Models/** (4 files)
    - `LoadExecutionPlan.cs` - Test configuration model
    - `LoadResult.cs` - Result aggregation model
    - `LoadSettings.cs` - Test timing and behavior settings
    - `TerminationMode.cs` - Enumeration for test termination strategies
  - **Runner/** (1 file)
    - `LoadRunner.cs` - Main orchestration entry point

- [x] Performed namespace migration
  - From: `xUnitV3LoadFramework.LoadRunnerCore.*`
  - To: `Surge.*`
  - Updated all `using` statements
  - Updated all `namespace` declarations
  - Verified no remaining old namespace references

### Phase 3: Testing
- [x] Created Surge.Tests project
  - Configured xUnit v3.0.0
  - Referenced Surge project
  - Targeted .NET 8.0

- [x] Migrated core unit tests (5 test files)
  - `HybridModeTests.cs` - Tests for hybrid worker mode
  - `RequestCountAccuracyTests.cs` - Termination mode accuracy tests
  - `LoadRunnerTimeoutTests.cs` - Timeout handling tests
  - `GracefulStopConfigurationTests.cs` - Graceful shutdown tests
  - `BackwardCompatibilityTests.cs` - Compatibility tests

- [x] Updated test namespaces
  - From: `xUnitV3LoadFrameworkTests.*`
  - To: `Surge.Tests.*`

- [x] Verified build success
  - **Status:** ✅ Build successful (0 errors, 2 warnings)
  - Warnings are expected (git repository not initialized yet, SourceLink)

### Phase 4: Documentation & CI/CD
- [x] Created comprehensive README.md
  - Project overview and features
  - Quick start guide
  - Architecture documentation
  - Usage examples (basic, database, HTTP API)
  - Performance metrics reference
  - Framework integration guide

- [x] Created LICENSE (MIT)
  - Copyright 2025 Vasyl Vdovychenko

- [x] Created CHANGELOG.md
  - v1.0.0 release notes
  - Migration notes from xUnitV3LoadFramework
  - Future roadmap

- [x] Created .gitignore
  - Standard .NET exclusions
  - Visual Studio, Rider, VS Code support

- [x] Created CI/CD pipeline (.github/workflows/ci-cd.yml)
  - Multi-OS testing (Ubuntu, Windows, macOS)
  - Automated testing with code coverage
  - NuGet package creation
  - Automated publishing on release
  - Security scanning

- [x] Created this PROGRESS.md documentation

---

## Pending Tasks 📋

### Phase 5: Git & GitHub
- [ ] Initialize git repository
- [ ] Create initial commit
- [ ] Create GitHub repository
- [ ] Push to GitHub
- [ ] Configure repository settings
- [ ] Add NuGet API key to GitHub secrets

### Phase 6: xUnit Integration Migration (Separate Project)
- [ ] Rename xUnitV3LoadFramework → Surge.xUnit
- [ ] Update Surge.xUnit to depend on Surge package
- [ ] Migrate xUnit-specific code:
  - `Attributes/LoadAttribute.cs`
  - `Extensions/LoadTestRunner.cs`
- [ ] Update namespaces in xUnit integration
- [ ] Remove LoadRunnerCore from xUnit project
- [ ] Create MIGRATION.md guide for users
- [ ] Test Surge.xUnit with Surge package
- [ ] Update xUnitV3LoadFramework README

### Phase 7: Publishing
- [ ] Publish Surge 1.0.0 to NuGet
- [ ] Verify package availability
- [ ] Publish Surge.xUnit 3.0.0 to NuGet
- [ ] Deprecate xUnitV3LoadFramework 2.x
- [ ] Announce release

---

## Technical Details

### Package Configuration

**Surge.csproj:**
- **Package ID:** Surge
- **Version:** 1.0.0
- **Target Framework:** net8.0
- **Language Version:** C# 12
- **Dependencies:**
  - Akka.NET 1.5.54
  - Microsoft.SourceLink.GitHub 8.0.0 (build-time)

**Features:**
- ImplicitUsings: enabled
- Nullable: enabled
- GenerateDocumentationFile: true
- IncludeSymbols: true (snupkg)
- Deterministic builds
- Source Link integration

### Build Results

```
Build succeeded.
  Surge -> /Users/vasylvdovychenko/projects/xUnitLoad/surge/src/Surge/bin/Release/net8.0/Surge.dll
  Surge.Tests -> /Users/vasylvdovychenko/projects/xUnitLoad/surge/tests/Surge.Tests/bin/Release/net8.0/Surge.Tests.dll

Warnings: 2 (git repository, SourceLink - expected)
Errors: 0
```

### Tests Included in Surge

**Unit Tests (Core functionality, no xUnit dependencies):**
1. HybridModeTests - 6 tests
2. RequestCountAccuracyTests - Multiple termination mode tests
3. LoadRunnerTimeoutTests - Timeout behavior validation
4. GracefulStopConfigurationTests - Shutdown behavior tests
5. BackwardCompatibilityTests - Compatibility validation

**Excluded (xUnit-specific, staying in Surge.xUnit):**
- LoadAttributeTests
- LoadTestRunnerTests
- MixedTestingScenarios
- HighLoadPerformanceTests (uses LoadAttribute)

---

## Key Decisions & Rationale

### 1. Target Framework: .NET 8.0 Only
**Decision:** Support only .NET 8.0 (removed net6.0, net7.0)
**Reason:** C# 11 `required` keyword used in models; maintaining compatibility would require removing modern language features
**Impact:** Cleaner codebase, modern features, smaller surface area

### 2. Package Name: "Surge"
**Decision:** Name the package "Surge" instead of "LoadRunner.Core" or similar
**Reason:**
- Short, memorable, impactful
- Implies: traffic surges, load spikes
- Good SEO for load testing
- Differentiates from HP LoadRunner
- Sets up branding for ecosystem (Surge.xUnit, Surge.NUnit, etc.)

### 3. Namespace: `Surge.*`
**Decision:** Use `Surge.*` instead of `Surge.Core.*` or `Surge.LoadRunner.*`
**Reason:**
- Simpler, cleaner
- Package name matches root namespace
- Less verbose using statements
- Standard .NET convention

### 4. Test Migration Strategy
**Decision:** Only migrate tests that don't depend on xUnit-specific features
**Reason:**
- Keeps Surge truly framework-agnostic
- Tests validate core functionality
- xUnit-specific tests stay with Surge.xUnit

### 5. Documentation Structure
**Decision:** Comprehensive README with examples, separate CHANGELOG and PROGRESS
**Reason:**
- README serves as primary documentation
- CHANGELOG follows keepachangelog.com standard
- PROGRESS tracks extraction process for reference

---

## Namespace Migration Map

| Old Namespace | New Namespace |
|---------------|---------------|
| `xUnitV3LoadFramework.LoadRunnerCore.Actors` | `Surge.Actors` |
| `xUnitV3LoadFramework.LoadRunnerCore.Configuration` | `Surge.Configuration` |
| `xUnitV3LoadFramework.LoadRunnerCore.Messages` | `Surge.Messages` |
| `xUnitV3LoadFramework.LoadRunnerCore.Models` | `Surge.Models` |
| `xUnitV3LoadFramework.LoadRunnerCore.Runner` | `Surge.Runner` |

---

## Success Criteria

- [x] Surge package builds without errors
- [x] All migrated tests pass
- [x] Zero dependencies except Akka.NET
- [x] Comprehensive documentation
- [x] CI/CD pipeline configured
- [ ] Published to NuGet
- [ ] GitHub repository live
- [ ] Surge.xUnit successfully depends on Surge

---

## Notes & Observations

### What Went Well
- Clean separation between core and xUnit-specific code
- Namespace migration was straightforward with sed
- Actor architecture is truly framework-agnostic
- Tests migrated cleanly without modification

### Challenges Encountered
1. **C# Version Compatibility:** Had to drop .NET 6.0/7.0 support due to `required` keyword
   - Resolution: Accepted .NET 8.0 only for v1.0.0
   - Future: Could add multi-targeting if needed

2. **XML Comment Parsing:** `<` and `>` characters in XML comments caused build errors
   - Resolution: Replaced with "less than" and "greater than" text

3. **Test Framework Dependency:** HighLoadPerformanceTests uses LoadAttribute
   - Resolution: Excluded from core tests, stays with Surge.xUnit

### Improvements for Future
- Consider multi-targeting (net6.0+) if community needs it
- Add more examples to examples/ directory
- Create Docker-based examples for distributed testing
- Performance benchmarking suite

---

## Timeline

- **Start:** October 21, 2025
- **Phase 1-2 (Setup & Migration):** ~2 hours
- **Phase 3 (Testing):** ~30 minutes
- **Phase 4 (Documentation):** ~1 hour
- **Total Time:** ~3.5 hours

---

## Contact & Resources

- **Author:** Vasyl Vdovychenko
- **GitHub:** [@mrviduus](https://github.com/mrviduus)
- **Source Project:** [xUnitV3LoadFramework](https://github.com/mrviduus/xUnitV3LoadFramework)
- **Email:** mrviduus@gmail.com

---

*This document will be updated as the project progresses.*
