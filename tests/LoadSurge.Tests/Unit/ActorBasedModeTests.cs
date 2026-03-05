using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LoadSurge.Configuration;
using LoadSurge.Models;
using LoadSurge.Runner;

namespace LoadSurge.Tests.Unit
{
    public class ActorBasedModeTests : IDisposable
    {
        private static readonly LoadWorkerConfiguration ActorBasedConfig = new LoadWorkerConfiguration
        {
            Mode = LoadWorkerMode.ActorBased
        };

        [Fact]
        public async Task Should_Execute_Requests_With_ActorBased_Mode()
        {
            var requestCount = 0;
            var plan = new LoadExecutionPlan
            {
                Name = "ActorBased_Basic",
                Settings = new LoadSettings
                {
                    Duration = TimeSpan.FromSeconds(3),
                    Interval = TimeSpan.FromSeconds(1),
                    Concurrency = 5,
                    TerminationMode = TerminationMode.CompleteCurrentInterval,
                    GracefulStopTimeout = TimeSpan.FromSeconds(5)
                },
                Action = async () =>
                {
                    Interlocked.Increment(ref requestCount);
                    await Task.Delay(10);
                    return true;
                }
            };

            var result = await LoadRunner.Run(plan, ActorBasedConfig);

            Assert.True(result.Total >= 15);
            Assert.True(result.Total <= 20); // Allow some variance for actor messaging
            Assert.True(result.Success > 0);
            Assert.Equal(0, result.Failure);
        }

        [Fact]
        public async Task Should_Handle_Mixed_Success_And_Failure()
        {
            var requestCount = 0;
            var plan = new LoadExecutionPlan
            {
                Name = "ActorBased_Mixed",
                Settings = new LoadSettings
                {
                    Duration = TimeSpan.FromSeconds(3),
                    Interval = TimeSpan.FromSeconds(1),
                    Concurrency = 5,
                    TerminationMode = TerminationMode.CompleteCurrentInterval,
                    GracefulStopTimeout = TimeSpan.FromSeconds(5)
                },
                Action = async () =>
                {
                    var count = Interlocked.Increment(ref requestCount);
                    await Task.Delay(10);
                    return count % 3 != 0; // Every 3rd fails
                }
            };

            var result = await LoadRunner.Run(plan, ActorBasedConfig);

            Assert.True(result.Total >= 15);
            Assert.True(result.Success > 0);
            Assert.True(result.Failure > 0);
            Assert.Equal(result.Success + result.Failure, result.Total);
        }

        [Fact]
        public async Task Should_Handle_Failures_With_Supervision()
        {
            var requestCount = 0;
            var plan = new LoadExecutionPlan
            {
                Name = "ActorBased_Supervision",
                Settings = new LoadSettings
                {
                    Duration = TimeSpan.FromSeconds(3),
                    Interval = TimeSpan.FromSeconds(1),
                    Concurrency = 3,
                    TerminationMode = TerminationMode.CompleteCurrentInterval,
                    GracefulStopTimeout = TimeSpan.FromSeconds(5)
                },
                Action = async () =>
                {
                    var count = Interlocked.Increment(ref requestCount);
                    await Task.Delay(10);
                    if (count % 2 == 0)
                        throw new InvalidOperationException("Test exception");
                    return true;
                }
            };

            var result = await LoadRunner.Run(plan, ActorBasedConfig);

            // Actor supervision should handle exceptions, test should complete
            Assert.True(result.Total >= 9); // At least 3 batches * 3 requests
            Assert.True(result.Success > 0);
            Assert.True(result.Failure > 0);
            Assert.Equal(result.Success + result.Failure, result.Total);
        }

        [Fact]
        public async Task Should_Respect_Duration_TerminationMode()
        {
            var requestCount = 0;
            var plan = new LoadExecutionPlan
            {
                Name = "ActorBased_Duration",
                Settings = new LoadSettings
                {
                    Duration = TimeSpan.FromSeconds(3),
                    Interval = TimeSpan.FromSeconds(1),
                    Concurrency = 5,
                    TerminationMode = TerminationMode.Duration,
                    GracefulStopTimeout = TimeSpan.FromSeconds(5)
                },
                Action = async () =>
                {
                    Interlocked.Increment(ref requestCount);
                    await Task.Delay(10);
                    return true;
                }
            };

            var startTime = DateTime.UtcNow;
            var result = await LoadRunner.Run(plan, ActorBasedConfig);
            var elapsed = DateTime.UtcNow - startTime;

            Assert.True(result.Total > 0);
            Assert.True(elapsed.TotalSeconds <= 15.0); // Duration + grace + buffer
        }

        [Fact]
        public async Task Should_Respect_StrictDuration_TerminationMode()
        {
            var requestCount = 0;
            var plan = new LoadExecutionPlan
            {
                Name = "ActorBased_StrictDuration",
                Settings = new LoadSettings
                {
                    Duration = TimeSpan.FromSeconds(3),
                    Interval = TimeSpan.FromSeconds(1),
                    Concurrency = 10,
                    TerminationMode = TerminationMode.StrictDuration,
                    GracefulStopTimeout = TimeSpan.FromSeconds(1)
                },
                Action = async () =>
                {
                    Interlocked.Increment(ref requestCount);
                    await Task.Delay(100);
                    return true;
                }
            };

            var startTime = DateTime.UtcNow;
            var result = await LoadRunner.Run(plan, ActorBasedConfig);
            var elapsed = DateTime.UtcNow - startTime;

            // StrictDuration should stop quickly
            Assert.True(elapsed.TotalSeconds <= 8.0); // 3s + 1s grace + 4s buffer for CI
            Assert.True(result.Total >= 10); // At least some requests
        }

        [Fact]
        public async Task Should_Respect_CompleteCurrentInterval_TerminationMode()
        {
            var requestCount = 0;
            var plan = new LoadExecutionPlan
            {
                Name = "ActorBased_CompleteInterval",
                Settings = new LoadSettings
                {
                    Duration = TimeSpan.FromSeconds(5),
                    Interval = TimeSpan.FromSeconds(1),
                    Concurrency = 10,
                    TerminationMode = TerminationMode.CompleteCurrentInterval,
                    GracefulStopTimeout = TimeSpan.FromSeconds(5)
                },
                Action = async () =>
                {
                    Interlocked.Increment(ref requestCount);
                    await Task.Delay(10);
                    return true;
                }
            };

            var result = await LoadRunner.Run(plan, ActorBasedConfig);

            Assert.True(result.Total >= 50); // 5 batches * 10
            Assert.True(result.Total <= 60); // Allow some variance
            Assert.Equal(0, result.Failure);
        }

        public void Dispose()
        {
        }
    }
}
