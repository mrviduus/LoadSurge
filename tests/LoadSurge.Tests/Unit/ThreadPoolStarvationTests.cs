using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LoadSurge.Configuration;
using LoadSurge.Engine;
using LoadSurge.Models;

namespace LoadSurge.Tests.Unit
{
    /// <summary>
    /// Runs alone: these tests block thread-pool threads on purpose (Thread.Sleep), which would
    /// starve parallel timing-sensitive tests and make them flaky.
    /// </summary>
    [CollectionDefinition(nameof(ThreadPoolStarvationCollection), DisableParallelization = true)]
    public sealed class ThreadPoolStarvationCollection
    {
    }

    /// <summary>
    /// Engine tests whose actions block thread-pool threads synchronously.
    /// </summary>
    [Collection(nameof(ThreadPoolStarvationCollection))]
    public class ThreadPoolStarvationTests
    {
        [Fact]
        public async Task RequestTimeout_Fires_For_Legacy_Action_That_Blocks_Synchronously()
        {
            // Arrange - blocks before any await; the timeout race used to start only after it (issue #37).
            var settings = new LoadSettings
            {
                Concurrency = 1,
                Interval = TimeSpan.FromMilliseconds(100),
                Duration = TimeSpan.FromSeconds(5),
                MaxIterations = 1,
                RequestTimeout = TimeSpan.FromMilliseconds(100)
            };
            var plan = LoadEngineTests.CreatePlan("blocking-legacy-timeout", settings, () =>
            {
                Thread.Sleep(2000);
                return Task.FromResult(true);
            });
            var wallClock = Stopwatch.StartNew();

            // Act
            var result = await LoadEngine.RunAsync(plan, new LoadWorkerConfiguration(), TestContext.Current.CancellationToken);
            wallClock.Stop();

            // Assert - timed out as a failure without waiting for the 2s block.
            Assert.Equal(1, result.Failure);
            Assert.True(wallClock.Elapsed < TimeSpan.FromMilliseconds(1500),
                $"Timeout must not wait for the blocking call, took {wallClock.Elapsed}");
            Assert.True(result.MaxFailureLatency >= 90, $"Failure latency must be recorded, got {result.MaxFailureLatency}");
        }

        [Fact]
        public async Task IncludeQueueTimeInLatency_Adds_Queue_Delay_To_Latency()
        {
            // Arrange - sync-blocking actions outnumber pool threads, so later ones queue (issue #39).
            var iterations = Environment.ProcessorCount * 3;
            var settings = new LoadSettings
            {
                Concurrency = iterations,
                Interval = TimeSpan.FromMilliseconds(100),
                Duration = TimeSpan.FromSeconds(5),
                MaxIterations = iterations,
                IncludeQueueTimeInLatency = true
            };
            var plan = LoadEngineTests.CreatePlan("queue-in-latency", settings, () =>
            {
                Thread.Sleep(100);
                return Task.FromResult(true);
            });

            // Act
            var result = await LoadEngine.RunAsync(plan, new LoadWorkerConfiguration(), TestContext.Current.CancellationToken);

            // Assert - the most-queued iteration's latency covers its queue wait plus ~100ms work.
            Assert.Equal(iterations, result.Success);
            Assert.True(result.MaxLatency >= result.MaxQueueTime + 90,
                $"Latency {result.MaxLatency}ms must include queue time {result.MaxQueueTime}ms");
        }
    }
}
