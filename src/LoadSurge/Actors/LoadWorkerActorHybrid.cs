using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using LoadSurge.Messages;
using LoadSurge.Models;

namespace LoadSurge.Actors
{
    /// <summary>
    /// Hybrid implementation of LoadWorkerActor optimized for high-throughput scenarios (100k+ requests).
    /// Uses Become/Unbecome state machine with Akka Scheduler for batch timing.
    /// Keeps Channel-based producer-consumer pattern with fixed worker pool for maximum performance.
    /// </summary>
    public class LoadWorkerActorHybrid : ReceiveActor
    {
        private readonly LoadExecutionPlan _executionPlan;
        private readonly IActorRef _resultCollector;
        private readonly ILoggingAdapter _logger = Context.GetLogger();

        // Channel-based work distribution (performance critical - kept as-is)
        private readonly Channel<WorkItem> _workChannel;
        private readonly List<Task> _workerTasks;
        private readonly CancellationTokenSource _workerCts = new CancellationTokenSource();
        private readonly int _workerCount;

        // Akka state
        private IActorRef _replyTo = ActorRefs.Nobody;
        private ICancelable _tickSchedule = new Cancelable(Context.System.Scheduler);
        private ICancelable _durationSchedule = new Cancelable(Context.System.Scheduler);
        private int _batchNumber;
        private int _totalScheduled;
        private DateTime _startTime;

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

        private class WorkersCompletedMessage
        {
            public static readonly WorkersCompletedMessage Instance = new WorkersCompletedMessage();
        }

        private class GracePeriodExpiredMessage
        {
            public static readonly GracePeriodExpiredMessage Instance = new GracePeriodExpiredMessage();
        }

        #endregion

        /// <summary>
        /// Constructor to initialize the LoadWorkerActorHybrid with execution plan and result collector.
        /// Sets up the channel-based work distribution system and calculates optimal worker count.
        /// </summary>
        /// <param name="executionPlan">The configuration defining test duration, concurrency, and action</param>
        /// <param name="resultCollector">Actor reference for sending test results and performance metrics</param>
        public LoadWorkerActorHybrid(LoadExecutionPlan executionPlan, IActorRef resultCollector)
        {
            _executionPlan = executionPlan;
            _resultCollector = resultCollector;

            _workChannel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });

            _workerCount = CalculateOptimalWorkerCount(executionPlan.Settings.Concurrency);
            _workerTasks = new List<Task>(_workerCount);

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
                _batchNumber = 0;
                _totalScheduled = 0;

                _resultCollector.Tell(new StartLoadMessage());
                _resultCollector.Tell(new WorkerThreadCountMessage { ThreadCount = _workerCount });

                // Start the fixed worker pool (Channel consumers)
                for (int i = 0; i < _workerCount; i++)
                {
                    var workerId = i;
                    _workerTasks.Add(ProcessWorkItems(workerId, _workerCts.Token));
                }

                // Schedule duration timeout via Akka Scheduler
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
            var elapsedTime = DateTime.UtcNow - _startTime;
            var expectedBatchStartTime = TimeSpan.FromMilliseconds(msg.BatchNumber * _executionPlan.Settings.Interval.TotalMilliseconds);

            var maxIterationsReached = _executionPlan.Settings.MaxIterations.HasValue &&
                _totalScheduled >= _executionPlan.Settings.MaxIterations.Value;

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
                _logger.Debug("LoadWorkerActorHybrid terminating: mode={0}, elapsed={1:F2}ms, duration={2:F2}ms, batch={3}",
                    _executionPlan.Settings.TerminationMode, elapsedTime.TotalMilliseconds,
                    _executionPlan.Settings.Duration.TotalMilliseconds, msg.BatchNumber + 1);
                HandleDurationExpired();
                return;
            }

            var itemsThisBatch = _executionPlan.Settings.Concurrency;
            if (_executionPlan.Settings.MaxIterations.HasValue)
            {
                var remaining = _executionPlan.Settings.MaxIterations.Value - _totalScheduled;
                itemsThisBatch = Math.Min(itemsThisBatch, remaining);
            }

            // Write work items to channel (high-performance path)
            for (int i = 0; i < itemsThisBatch; i++)
            {
                var workItem = new WorkItem
                {
                    Id = Guid.NewGuid(),
                    BatchNumber = _batchNumber,
                    ScheduledTime = DateTime.UtcNow
                };

                // TryWrite is non-blocking for unbounded channels
                _workChannel.Writer.TryWrite(workItem);
                _totalScheduled++;
            }

            _logger.Debug("Batch {0} scheduled with {1} items. Total scheduled: {2}",
                _batchNumber, itemsThisBatch, _totalScheduled);

            _resultCollector.Tell(new BatchCompletedMessage(_batchNumber, itemsThisBatch, DateTime.UtcNow));

            _batchNumber = msg.BatchNumber + 1;

            // Schedule next tick via Akka Scheduler
            var nextBatchTime = _startTime.AddMilliseconds(_batchNumber * _executionPlan.Settings.Interval.TotalMilliseconds);
            var delay = nextBatchTime - DateTime.UtcNow;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            _tickSchedule = Context.System.Scheduler.ScheduleTellOnceCancelable(
                delay,
                Self,
                new ScheduleTickMessage(_batchNumber),
                Self
            );
        }

        private void HandleDurationExpired()
        {
            _tickSchedule.Cancel();
            _durationSchedule.Cancel();

            var workerName = Self.Path.Name;
            _logger.Info("LoadWorkerActorHybrid '{0}' duration expired. Total scheduled: {1}", workerName, _totalScheduled);

            // Complete channel to signal workers to finish
            _workChannel.Writer.TryComplete();

            // Wait for workers to finish, then collect results
            var incompleteWorkers = _workerTasks.Where(t => !t.IsCompleted).ToList();
            if (!incompleteWorkers.Any())
            {
                _logger.Info("LoadWorkerActorHybrid '{0}' - all workers already completed.", workerName);
                CollectAndReplyResult();
                return;
            }

            var gracePeriod = _executionPlan.Settings.EffectiveGracefulStopTimeout;
            _logger.Info("LoadWorkerActorHybrid '{0}' waiting {1:F1}s for {2} workers to complete.",
                workerName, gracePeriod.TotalSeconds, incompleteWorkers.Count);

            // Use Task.WhenAll + PipeTo to notify when workers are done
            Task.WhenAll(incompleteWorkers)
                .ContinueWith(_ => WorkersCompletedMessage.Instance)
                .PipeTo(Self);

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
        /// Stopping state — waiting for worker pool completion or grace period.
        /// </summary>
        private void Stopping()
        {
            Receive<WorkersCompletedMessage>(_ =>
            {
                _logger.Info("LoadWorkerActorHybrid '{0}' - all workers completed successfully.", Self.Path.Name);
                CollectAndReplyResult();
            });

            Receive<GracePeriodExpiredMessage>(_ =>
            {
                var stillRunning = _workerTasks.Count(t => !t.IsCompleted);
                _logger.Warning("LoadWorkerActorHybrid '{0}' - {1} workers still running after grace period.",
                    Self.Path.Name, stillRunning);
                CollectAndReplyResult();
            });

            // Ignore stale messages
            Receive<ScheduleTickMessage>(_ => { });
            Receive<DurationExpiredMessage>(_ => { });
        }

        private void CollectAndReplyResult()
        {
            // Prevent double-reply by becoming a terminal state
            Become(() =>
            {
                // Ignore all messages in terminal state
                ReceiveAny(_ => { });
            });

            var workerName = Self.Path.Name;
            var resultTimeout = TimeSpan.FromSeconds(Math.Max(30, _executionPlan.Settings.Duration.TotalSeconds / 2));
            _resultCollector.Ask<LoadResult>(new GetLoadResultMessage(), resultTimeout)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        _logger.Error(task.Exception, "LoadWorkerActorHybrid failed to collect results");
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

                    _logger.Info("LoadWorkerActorHybrid '{0}' completed. Total: {1}, Success: {2}, Failed: {3}, In-flight: {4}",
                        workerName, task.Result.Total, task.Result.Success, task.Result.Failure, task.Result.RequestsInFlight);
                    return task.Result;
                })
                .PipeTo(_replyTo);
        }

        /// <summary>
        /// Calculates the optimal number of worker threads based on system resources and concurrency requirements.
        /// </summary>
        private int CalculateOptimalWorkerCount(int concurrency)
        {
            var coreCount = Environment.ProcessorCount;
            var baseWorkers = coreCount * 2;
            var scaledWorkers = Math.Max(baseWorkers, concurrency / 10);
            var maxWorkers = Math.Min(1000, coreCount * 50);
            var optimalWorkers = Math.Min(scaledWorkers, maxWorkers);

            _logger.Info("Calculated optimal worker count: {0} (cores: {1}, concurrency: {2})",
                optimalWorkers, coreCount, concurrency);

            return optimalWorkers;
        }

        /// <summary>
        /// Worker thread that continuously processes work items from the channel.
        /// </summary>
        private async Task ProcessWorkItems(int workerId, CancellationToken cancellationToken)
        {
            var processedCount = 0;

            try
            {
                await foreach (var workItem in _workChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    await ProcessSingleWorkItem(workItem, workerId);
                    processedCount++;

                    if (processedCount % 100 == 0)
                    {
                        await Task.Yield();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Worker {0} cancelled after processing {1} items", workerId, processedCount);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Worker {0} encountered error after processing {1} items", workerId, processedCount);
            }

            _logger.Debug("Worker {0} completed. Processed {1} items", workerId, processedCount);
        }

        /// <summary>
        /// Processes a single work item with comprehensive timing and error handling.
        /// </summary>
        private async Task ProcessSingleWorkItem(WorkItem workItem, int workerId)
        {
            try
            {
                _resultCollector.Tell(new RequestStartedMessage());

                var stopwatch = Stopwatch.StartNew();
                var result = await _executionPlan.Action!();
                stopwatch.Stop();

                var queueTime = (DateTime.UtcNow - workItem.ScheduledTime).TotalMilliseconds;
                _resultCollector.Tell(new StepResultMessage(result, stopwatch.Elapsed.TotalMilliseconds, queueTime));

                if (queueTime > 1000)
                {
                    _logger.Warning("Worker {0}: High queue time {1:F2}ms for work item from batch {2}",
                        workerId, queueTime, workItem.BatchNumber);
                }
            }
            catch (Exception ex)
            {
                _resultCollector.Tell(new StepResultMessage(false, 0));
                _logger.Error(ex, "Worker {0}: Failed to process work item from batch {1}",
                    workerId, workItem.BatchNumber);
            }
        }

        /// <summary>
        /// Actor lifecycle method called when the actor is stopping.
        /// </summary>
        protected override void PostStop()
        {
            _tickSchedule.Cancel();
            _durationSchedule.Cancel();
            _workerCts.Cancel();
            _workChannel.Writer.TryComplete();

            try
            {
                Task.WaitAll(_workerTasks.ToArray(), TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore timeout during shutdown
            }

            _workerCts.Dispose();
            base.PostStop();
        }

        private class WorkItem
        {
            /// <summary>Unique identifier for tracking.</summary>
            public Guid Id { get; set; }
            /// <summary>Batch this item belongs to.</summary>
            public int BatchNumber { get; set; }
            /// <summary>When this item was scheduled, for queue time calculation.</summary>
            public DateTime ScheduledTime { get; set; }
        }
    }
}
