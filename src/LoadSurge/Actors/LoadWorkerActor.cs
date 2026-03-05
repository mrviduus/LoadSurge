using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using LoadSurge.Messages;
using LoadSurge.Models;

namespace LoadSurge.Actors
{
    /// <summary>
    /// Actor responsible for executing load tests using Task-based concurrent execution.
    /// Uses Become/Unbecome state machine and Akka Scheduler for batch timing.
    /// Implements the Task-based execution mode for moderate concurrency scenarios.
    /// </summary>
    public class LoadWorkerActor : ReceiveActor
    {
        private readonly LoadExecutionPlan _executionPlan;
        private readonly IActorRef _resultCollector;
        private readonly ILoggingAdapter _logger = Context.GetLogger();

        // State
        private IActorRef _replyTo = ActorRefs.Nobody;
        private ICancelable _tickSchedule = new Cancelable(Context.System.Scheduler);
        private ICancelable _durationSchedule = new Cancelable(Context.System.Scheduler);
        private readonly List<Task> _runningTasks = new List<Task>();
        private DateTime _startTime;
        private int _totalIterationsSpawned;

        #region Internal Messages

        private class ScheduleTickMessage
        {
            public int BatchNumber { get; }
            public ScheduleTickMessage(int batchNumber) { BatchNumber = batchNumber; }
        }

        private class DurationExpiredMessage
        {
            public static readonly DurationExpiredMessage Instance = new DurationExpiredMessage();
        }

        private class GracePeriodExpiredMessage
        {
            public static readonly GracePeriodExpiredMessage Instance = new GracePeriodExpiredMessage();
        }

        #endregion

        /// <summary>
        /// Constructor to initialize the LoadWorkerActor with execution plan and result collector.
        /// Sets up message handling patterns and establishes communication channels.
        /// </summary>
        /// <param name="executionPlan">The configuration defining test duration, concurrency, and action</param>
        /// <param name="resultCollector">Actor reference for sending test results and metrics</param>
        public LoadWorkerActor(LoadExecutionPlan executionPlan, IActorRef resultCollector)
        {
            _executionPlan = executionPlan;
            _resultCollector = resultCollector;

            Idle();
        }

        /// <summary>
        /// Idle state — waiting for StartLoadMessage.
        /// </summary>
        private void Idle()
        {
            Receive<StartLoadMessage>(_ =>
            {
                _replyTo = Sender;
                _startTime = DateTime.UtcNow;
                _totalIterationsSpawned = 0;

                _resultCollector.Tell(new StartLoadMessage());

                var workerName = Self.Path.Name;
                var expectedBatches = (int)Math.Ceiling(_executionPlan.Settings.Duration.TotalMilliseconds / _executionPlan.Settings.Interval.TotalMilliseconds);
                var expectedTotalRequests = _executionPlan.Settings.MaxIterations ??
                    expectedBatches * _executionPlan.Settings.Concurrency;

                _logger.Info("LoadWorkerActor '{0}' starting. Expected batches: {1}, Expected total requests: {2}, MaxIterations: {3}",
                    workerName, expectedBatches, expectedTotalRequests,
                    _executionPlan.Settings.MaxIterations?.ToString() ?? "unlimited");

                // Schedule duration timeout
                _durationSchedule = Context.System.Scheduler.ScheduleTellOnceCancelable(
                    _executionPlan.Settings.Duration,
                    Self,
                    DurationExpiredMessage.Instance,
                    Self
                );

                // Schedule first batch immediately
                Self.Tell(new ScheduleTickMessage(0));

                Become(Running);
            });
        }

        /// <summary>
        /// Running state — processes batch ticks and duration expiry.
        /// </summary>
        private void Running()
        {
            Receive<ScheduleTickMessage>(msg => HandleTick(msg));
            Receive<DurationExpiredMessage>(_ => HandleDurationExpired());
        }

        private void HandleTick(ScheduleTickMessage msg)
        {
            var workerName = Self.Path.Name;
            var elapsedTime = DateTime.UtcNow - _startTime;
            var expectedBatchStartTime = TimeSpan.FromMilliseconds(msg.BatchNumber * _executionPlan.Settings.Interval.TotalMilliseconds);

            var maxIterationsReached = _executionPlan.Settings.MaxIterations.HasValue &&
                _totalIterationsSpawned >= _executionPlan.Settings.MaxIterations.Value;

            var shouldTerminate = maxIterationsReached || _executionPlan.Settings.TerminationMode switch
            {
                TerminationMode.Duration => elapsedTime >= _executionPlan.Settings.Duration,
                TerminationMode.CompleteCurrentInterval =>
                    expectedBatchStartTime >= _executionPlan.Settings.Duration,
                TerminationMode.StrictDuration => elapsedTime >= _executionPlan.Settings.Duration,
                _ => elapsedTime >= _executionPlan.Settings.Duration
            };

            if (shouldTerminate)
            {
                _logger.Debug("LoadWorkerActor '{0}' terminating: mode={1}, elapsed={2:F2}ms, duration={3:F2}ms, batch={4}",
                    workerName, _executionPlan.Settings.TerminationMode, elapsedTime.TotalMilliseconds,
                    _executionPlan.Settings.Duration.TotalMilliseconds, msg.BatchNumber + 1);
                HandleDurationExpired();
                return;
            }

            var iterationsThisBatch = _executionPlan.Settings.Concurrency;
            if (_executionPlan.Settings.MaxIterations.HasValue)
            {
                var remaining = _executionPlan.Settings.MaxIterations.Value - _totalIterationsSpawned;
                iterationsThisBatch = Math.Min(iterationsThisBatch, remaining);
            }

            for (int i = 0; i < iterationsThisBatch; i++)
            {
                var task = ExecuteActionAsync(workerName);
                _runningTasks.Add(task);
                _totalIterationsSpawned++;
            }

            _logger.Debug("[{0}] Batch {1} started at {2:F2}ms (expected: {3:F2}ms). Tasks in batch: {4}",
                workerName, msg.BatchNumber + 1, elapsedTime.TotalMilliseconds,
                expectedBatchStartTime.TotalMilliseconds, iterationsThisBatch);

            // Clean up completed tasks
            _runningTasks.RemoveAll(t => t.IsCompleted);

            var nextBatchNumber = msg.BatchNumber + 1;
            var nextBatchTime = _startTime.AddMilliseconds(nextBatchNumber * _executionPlan.Settings.Interval.TotalMilliseconds);
            var delay = nextBatchTime - DateTime.UtcNow;

            if (delay <= TimeSpan.Zero)
            {
                _logger.Warning("[{0}] Running behind schedule. Next batch should have started {1:F2}ms ago",
                    workerName, -delay.TotalMilliseconds);
                delay = TimeSpan.Zero;
            }

            // Schedule next tick via Akka Scheduler
            _tickSchedule = Context.System.Scheduler.ScheduleTellOnceCancelable(
                delay,
                Self,
                new ScheduleTickMessage(nextBatchNumber),
                Self
            );
        }

        private void HandleDurationExpired()
        {
            _tickSchedule.Cancel();
            _durationSchedule.Cancel();

            var workerName = Self.Path.Name;
            var incompleteTasks = _runningTasks.Where(t => !t.IsCompleted).ToList();

            _logger.Info("LoadWorkerActor '{0}' finished spawning tasks. Waiting for {1} in-flight requests to complete.",
                workerName, incompleteTasks.Count);

            if (!incompleteTasks.Any())
            {
                _logger.Info("LoadWorkerActor '{0}' - no in-flight requests to wait for.", workerName);
                CollectAndReplyResult();
                return;
            }

            var gracePeriod = _executionPlan.Settings.EffectiveGracefulStopTimeout;
            _logger.Info("LoadWorkerActor '{0}' allowing {1:F1}s grace period for in-flight requests (mode: {2}).",
                workerName, gracePeriod.TotalSeconds, _executionPlan.Settings.TerminationMode);

            // Schedule grace period timeout
            Context.System.Scheduler.ScheduleTellOnceCancelable(
                gracePeriod,
                Self,
                GracePeriodExpiredMessage.Instance,
                Self
            );

            Become(Stopping);
        }

        /// <summary>
        /// Stopping state — waiting for in-flight tasks or grace period expiry.
        /// </summary>
        private void Stopping()
        {
            Receive<GracePeriodExpiredMessage>(_ =>
            {
                var workerName = Self.Path.Name;
                var stillRunning = _runningTasks.Count(t => !t.IsCompleted);
                if (stillRunning > 0)
                {
                    _logger.Warning("LoadWorkerActor '{0}' - {1} requests still in-flight after grace period.",
                        workerName, stillRunning);
                }
                CollectAndReplyResult();
            });

            // Ignore stale messages
            Receive<ScheduleTickMessage>(_ => { });
            Receive<DurationExpiredMessage>(_ => { });
        }

        private void CollectAndReplyResult()
        {
            var workerName = Self.Path.Name;
            _logger.Info("LoadWorkerActor '{0}' completed load testing phase.", workerName);

            var resultTimeout = TimeSpan.FromSeconds(Math.Max(30, _executionPlan.Settings.Duration.TotalSeconds / 2));
            _resultCollector.Ask<LoadResult>(new GetLoadResultMessage(), resultTimeout)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        _logger.Error(task.Exception, "LoadWorkerActor failed to collect results");
                        return new LoadResult
                        {
                            ScenarioName = _executionPlan.Name,
                            Success = 0,
                            Failure = 1,
                            Total = 1,
                            Time = 0,
                            RequestsPerSecond = 0,
                            AverageLatency = 0
                        };
                    }
                    return task.Result;
                })
                .PipeTo(_replyTo);
        }

        /// <summary>
        /// Executes a single test action with precise timing measurement and error handling.
        /// </summary>
        private async Task ExecuteActionAsync(string workerName)
        {
            try
            {
                _resultCollector.Tell(new RequestStartedMessage());

                var stopwatch = Stopwatch.StartNew();
                bool result = await _executionPlan.Action!();
                stopwatch.Stop();

                var latency = stopwatch.Elapsed.TotalMilliseconds;
                _resultCollector.Tell(new StepResultMessage(result, latency));

                _logger.Debug("[{0}] Task completed - Result: {1}, Latency: {2:F2} ms",
                    workerName, result, latency);
            }
            catch (Exception ex)
            {
                _resultCollector.Tell(new StepResultMessage(false, 0));
                _logger.Error(ex, "[{0}] Task failed with error", workerName);
            }
        }

        /// <inheritdoc/>
        protected override void PostStop()
        {
            _tickSchedule.Cancel();
            _durationSchedule.Cancel();
            base.PostStop();
        }
    }
}
