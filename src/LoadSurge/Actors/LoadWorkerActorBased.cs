using System;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Akka.Routing;
using LoadSurge.Messages;
using LoadSurge.Models;

namespace LoadSurge.Actors
{
    /// <summary>
    /// Pure Akka.NET actor-based load worker using router pools, scheduler, supervision, and Become/Unbecome.
    /// Targets ~10-50k RPS with strong fault tolerance via supervised child actors.
    /// </summary>
    public class LoadWorkerActorBased : ReceiveActor
    {
        private readonly LoadExecutionPlan _executionPlan;
        private readonly IActorRef _resultCollector;
        private readonly ILoggingAdapter _logger = Context.GetLogger();

        // State tracking
        private IActorRef _replyTo = ActorRefs.Nobody;
        private IActorRef _router = ActorRefs.Nobody;
        private ICancelable _tickSchedule = new Cancelable(Context.System.Scheduler);
        private ICancelable _durationSchedule = new Cancelable(Context.System.Scheduler);
        private int _batchNumber;
        private int _totalScheduled;
        private int _inFlightCount;
        private int _workerCount;
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

        private class CollectResultMessage
        {
            public static readonly CollectResultMessage Instance = new CollectResultMessage();
        }

        #endregion

        /// <inheritdoc cref="LoadWorkerActorHybrid(LoadExecutionPlan, IActorRef)"/>
        public LoadWorkerActorBased(LoadExecutionPlan executionPlan, IActorRef resultCollector)
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
                _batchNumber = 0;
                _totalScheduled = 0;
                _inFlightCount = 0;

                _resultCollector.Tell(new StartLoadMessage());

                _workerCount = CalculateWorkerCount(_executionPlan.Settings.Concurrency);
                _resultCollector.Tell(new WorkerThreadCountMessage { ThreadCount = _workerCount });

                _router = Context.ActorOf(
                    Props.Create(() => new RequestExecutorActor())
                        .WithRouter(new SmallestMailboxPool(_workerCount)),
                    "request-router"
                );

                _logger.Info("LoadWorkerActorBased starting with {0} router workers, duration={1}s",
                    _workerCount, _executionPlan.Settings.Duration.TotalSeconds);

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
        /// Running state — processes batch ticks and tracks completions.
        /// </summary>
        private void Running()
        {
            Receive<ScheduleTickMessage>(msg => HandleTick(msg));
            Receive<RequestExecutorActor.RequestCompletedMessage>(_ => HandleRequestCompleted());
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
                _logger.Debug("LoadWorkerActorBased terminating scheduling: mode={0}, elapsed={1:F2}ms, batch={2}",
                    _executionPlan.Settings.TerminationMode, elapsedTime.TotalMilliseconds, msg.BatchNumber + 1);
                HandleDurationExpired();
                return;
            }

            var itemsThisBatch = _executionPlan.Settings.Concurrency;
            if (_executionPlan.Settings.MaxIterations.HasValue)
            {
                var remaining = _executionPlan.Settings.MaxIterations.Value - _totalScheduled;
                itemsThisBatch = Math.Min(itemsThisBatch, remaining);
            }

            var now = DateTime.UtcNow;
            for (int i = 0; i < itemsThisBatch; i++)
            {
                _router.Tell(new RequestExecutorActor.ExecuteRequestMessage(
                    _executionPlan.Action!,
                    _resultCollector,
                    Self,
                    now
                ));
                _inFlightCount++;
                _totalScheduled++;
            }

            _resultCollector.Tell(new BatchCompletedMessage(_batchNumber, itemsThisBatch, now));
            _batchNumber = msg.BatchNumber + 1;

            // Schedule next tick using Akka Scheduler
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

        private void HandleRequestCompleted()
        {
            _inFlightCount--;
        }

        private void HandleDurationExpired()
        {
            _tickSchedule.Cancel();
            _durationSchedule.Cancel();

            _logger.Info("LoadWorkerActorBased duration expired. Scheduled: {0}, InFlight: {1}",
                _totalScheduled, _inFlightCount);

            if (_executionPlan.Settings.TerminationMode == TerminationMode.StrictDuration)
            {
                // Hard stop — don't wait for in-flight, collect results immediately
                Self.Tell(CollectResultMessage.Instance);
                Become(Stopping);
                return;
            }

            if (_inFlightCount <= 0)
            {
                Self.Tell(CollectResultMessage.Instance);
                Become(Stopping);
                return;
            }

            // Wait for in-flight requests with grace period
            var gracePeriod = _executionPlan.Settings.EffectiveGracefulStopTimeout;
            var graceSchedule = Context.System.Scheduler.ScheduleTellOnceCancelable(
                gracePeriod,
                Self,
                CollectResultMessage.Instance,
                Self
            );

            Become(WaitingForCompletion);
        }

        /// <summary>
        /// WaitingForCompletion state — tracking in-flight requests until they finish or grace period expires.
        /// </summary>
        private void WaitingForCompletion()
        {
            Receive<RequestExecutorActor.RequestCompletedMessage>(_ =>
            {
                _inFlightCount--;
                if (_inFlightCount <= 0)
                {
                    _logger.Info("LoadWorkerActorBased all in-flight requests completed.");
                    Self.Tell(CollectResultMessage.Instance);
                    Become(Stopping);
                }
            });

            Receive<CollectResultMessage>(_ =>
            {
                if (_inFlightCount > 0)
                {
                    _logger.Warning("LoadWorkerActorBased grace period expired with {0} in-flight requests.", _inFlightCount);
                }
                Become(Stopping);
                // Re-send to handle in Stopping state
                Self.Tell(CollectResultMessage.Instance);
            });

            // Ignore stale ticks
            Receive<ScheduleTickMessage>(_ => { });
            Receive<DurationExpiredMessage>(_ => { });
        }

        /// <summary>
        /// Stopping state — collecting final results.
        /// </summary>
        private void Stopping()
        {
            Receive<CollectResultMessage>(_ =>
            {
                var resultTimeout = TimeSpan.FromSeconds(Math.Max(30, _executionPlan.Settings.Duration.TotalSeconds / 2));
                _resultCollector.Ask<LoadResult>(new GetLoadResultMessage(), resultTimeout)
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            _logger.Error(task.Exception, "Failed to collect results");
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

                _logger.Info("LoadWorkerActorBased completed. Total scheduled: {0}", _totalScheduled);
            });

            // Ignore everything else in stopping state
            Receive<RequestExecutorActor.RequestCompletedMessage>(_ => { });
            Receive<ScheduleTickMessage>(_ => { });
            Receive<DurationExpiredMessage>(_ => { });
        }

        /// <inheritdoc/>
        protected override SupervisorStrategy SupervisorStrategy()
        {
            return new OneForOneStrategy(
                maxNrOfRetries: 3,
                withinTimeRange: TimeSpan.FromSeconds(30),
                decider: Decider.From(ex =>
                {
                    if (ex is TaskCanceledException || ex is OperationCanceledException)
                        return Directive.Stop;

                    _logger.Warning("RequestExecutorActor failed, restarting: {0}", ex.Message);
                    return Directive.Restart;
                })
            );
        }

        private int CalculateWorkerCount(int concurrency)
        {
            var coreCount = Environment.ProcessorCount;
            var baseWorkers = coreCount * 2;
            var scaledWorkers = Math.Max(baseWorkers, concurrency / 10);
            var maxWorkers = Math.Min(1000, coreCount * 50);
            var optimalWorkers = Math.Min(scaledWorkers, maxWorkers);

            _logger.Info("Calculated worker count: {0} (cores: {1}, concurrency: {2})",
                optimalWorkers, coreCount, concurrency);

            return optimalWorkers;
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
