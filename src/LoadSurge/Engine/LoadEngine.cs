using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LoadSurge.Configuration;
using LoadSurge.Models;

namespace LoadSurge.Engine
{
    /// <summary>
    /// Open-workload-model load engine (constant arrival rate, NBomber Inject / k6 arrival-rate style).
    /// A scheduler loop injects Concurrency iterations every Interval at absolute-time boundaries
    /// (no drift), regardless of whether previous responses have returned - in-flight requests
    /// accumulate under a slow system, which is exactly what an open model must measure.
    /// Each iteration is a task-per-arrival on the thread pool; there is no worker pool that
    /// would silently convert the open model into a closed one.
    /// The optional MaxInFlight cap drops excess iterations and counts them (k6 dropped_iterations).
    /// </summary>
    internal static class LoadEngine
    {
        private static readonly double TimestampToMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Executes the plan and returns aggregated results.
        /// Cancellation stops scheduling, cancels in-flight cancellation-aware actions,
        /// and returns the partial results collected so far.
        /// </summary>
        public static async Task<LoadResult> RunAsync(
            LoadExecutionPlan plan,
            LoadWorkerConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var settings = plan.Settings;
            var collector = new MetricsCollector(plan.Name);
            collector.MarkStarted();

            // Live progress: an independent low-frequency loop; stopped (with a final
            // report) just before the result is built so consumers see the closing state.
            using var progressCts = configuration.Progress != null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            var progressTask = configuration.Progress != null
                ? ReportProgressAsync(configuration.Progress, configuration.ProgressInterval, collector, progressCts!.Token)
                : Task.CompletedTask;

            var runStart = Stopwatch.GetTimestamp();
            var executed = 0;   // iterations actually spawned (dropped ones do not consume MaxIterations budget)
            var batchNumber = 0;
            var maxInFlight = configuration.MaxInFlight;
            var durationMs = settings.Duration.TotalMilliseconds;
            var intervalMs = settings.Interval.TotalMilliseconds;
            var stoppedByMaxIterations = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - runStart) * TimestampToMs;
                var expectedBatchStartMs = batchNumber * intervalMs;

                if (settings.MaxIterations.HasValue && executed >= settings.MaxIterations.Value)
                {
                    stoppedByMaxIterations = true;
                    break;
                }

                // CompleteCurrentInterval schedules every batch whose slot begins within Duration;
                // Duration and StrictDuration stop as soon as elapsed time runs out.
                var shouldStop = settings.TerminationMode == TerminationMode.CompleteCurrentInterval
                    ? expectedBatchStartMs >= durationMs
                    : elapsedMs >= durationMs;
                if (shouldStop)
                    break;

                var itemsThisBatch = settings.Concurrency;
                if (settings.MaxIterations.HasValue)
                    itemsThisBatch = Math.Min(itemsThisBatch, settings.MaxIterations.Value - executed);

                // Queue time is measured from the planned slot, not the spawn moment, so scheduler
                // lag (late timer wake-up, slow batch spawn) shows up in queue time and latency.
                var slotTimestamp = runStart + (long)(expectedBatchStartMs / TimestampToMs);

                for (var i = 0; i < itemsThisBatch; i++)
                {
                    if (maxInFlight.HasValue && collector.InFlight >= maxInFlight.Value)
                    {
                        collector.RecordDropped();
                        continue;
                    }

                    // Counted before Task.Run so queued-but-not-started iterations hit the cap.
                    // Only this loop increments in-flight, so check-then-increment cannot overshoot.
                    collector.RequestScheduled();
                    // CancellationToken.None is deliberate: a spawned iteration must always run
                    // and record its outcome; run cancellation reaches the action via the token
                    // passed into ExecuteOneAsync instead.
                    _ = Task.Run(() => ExecuteOneAsync(plan, collector, slotTimestamp, cancellationToken), CancellationToken.None);
                    executed++;
                }

                collector.BatchCompleted();
                batchNumber++;

                // Absolute-time pacing: sleep until the next batch slot, immune to scheduling drift.
                var nextBatchMs = batchNumber * intervalMs;
                var delayMs = nextBatchMs - (Stopwatch.GetTimestamp() - runStart) * TimestampToMs;
                if (delayMs > 0 && !await DelayAsync(delayMs, cancellationToken).ConfigureAwait(false))
                    break;
            }

            // The run spans the full Duration window so Time/RPS are schedule-normalized
            // (matches pre-3.0 behavior). MaxIterations and cancellation complete early.
            // Loop because Task.Delay may wake marginally early relative to Stopwatch.
            if (!stoppedByMaxIterations
                && settings.TerminationMode != TerminationMode.StrictDuration
                && !cancellationToken.IsCancellationRequested)
            {
                while (true)
                {
                    var remainingMs = durationMs - (Stopwatch.GetTimestamp() - runStart) * TimestampToMs;
                    if (remainingMs <= 0 || !await DelayAsync(remainingMs, cancellationToken).ConfigureAwait(false))
                        break;
                }
            }

            // Graceful drain: give in-flight requests up to the grace period to finish.
            // StrictDuration and cancellation skip the drain and report whatever is still in flight
            // (cancellation-aware actions have been signalled and unwind on their own).
            if (settings.TerminationMode != TerminationMode.StrictDuration
                && !cancellationToken.IsCancellationRequested)
            {
                var graceMs = settings.EffectiveGracefulStopTimeout.TotalMilliseconds;
                var drainStart = Stopwatch.GetTimestamp();
                while (collector.InFlight > 0
                    && (Stopwatch.GetTimestamp() - drainStart) * TimestampToMs < graceMs)
                {
                    if (!await DelayAsync(10, cancellationToken).ConfigureAwait(false))
                        break;
                }
            }

            if (progressCts != null)
            {
                progressCts.Cancel();
                await progressTask.ConfigureAwait(false);
                try
                {
                    configuration.Progress!.Report(collector.Snapshot()); // closing snapshot
                }
                catch
                {
                    // A throwing progress consumer must not take down the run.
                }
            }

            return collector.BuildResult();
        }

        /// <summary>Reports collector snapshots at a fixed cadence until cancelled. Never throws.</summary>
        private static async Task ReportProgressAsync(
            IProgress<LoadProgress> progress,
            TimeSpan interval,
            MetricsCollector collector,
            CancellationToken cancellationToken)
        {
            while (await DelayAsync(interval.TotalMilliseconds, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    progress.Report(collector.Snapshot());
                }
                catch
                {
                    // A throwing progress consumer must not take down the run.
                }
            }
        }

        /// <summary>Delays without throwing; returns false when cancelled so callers can break out.</summary>
        private static async Task<bool> DelayAsync(double milliseconds, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Runs a single iteration: measures queue lag (schedule to start) and execution latency,
        /// applies the optional per-request timeout, and records the outcome. Never throws.
        /// </summary>
        private static async Task ExecuteOneAsync(
            LoadExecutionPlan plan,
            MetricsCollector collector,
            long scheduledAt,
            CancellationToken cancellationToken)
        {
            collector.RequestStarted();
            // Mutable: the legacy timeout path moves it to when the action really began,
            // so its extra thread-pool hop counts as queue time, not latency.
            var startedAt = Stopwatch.GetTimestamp();

            try
            {
                var timeout = plan.Settings.RequestTimeout;
                bool isSuccess;

                if (plan.ActionWithCancellation != null)
                {
                    // Cancellation-aware path: the action observes both run cancellation and
                    // the per-request timeout, so no work leaks past either signal.
                    using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        if (timeout.HasValue)
                            requestCts.CancelAfter(timeout.Value);
                        isSuccess = await plan.ActionWithCancellation(requestCts.Token).ConfigureAwait(false);
                    }
                }
                else if (timeout.HasValue)
                {
                    (isSuccess, startedAt) = await ExecuteWithTimeoutAsync(plan.Action!, timeout.Value).ConfigureAwait(false);
                }
                else
                {
                    isSuccess = await plan.Action!().ConfigureAwait(false);
                }

                collector.RecordResult(isSuccess, LatencyMs(plan, scheduledAt, startedAt), QueueTimeMs(scheduledAt, startedAt));
            }
            catch
            {
                // A throwing action (including OperationCanceledException on timeout) is a failure;
                // its latency feeds the failure-latency stats only. Run cancellation is the harness
                // stopping, not SUT behavior, so it is counted without latency.
                if (cancellationToken.IsCancellationRequested)
                    collector.RecordCancelled(QueueTimeMs(scheduledAt, startedAt));
                else
                    collector.RecordResult(false, LatencyMs(plan, scheduledAt, startedAt), QueueTimeMs(scheduledAt, startedAt));
            }
        }

        // Task.Delay can wake marginally before the slot, so an iteration may start before
        // scheduledAt: clamp instead of reporting negative queue time.
        private static double QueueTimeMs(long scheduledAt, long startedAt) =>
            Math.Max(0, startedAt - scheduledAt) * TimestampToMs;

        /// <summary>
        /// Execution latency up to now; with IncludeQueueTimeInLatency (opt-in coordinated-omission
        /// correction) it is measured from the scheduled slot instead of the execution start
        /// (or from the start, if it was marginally early, so latency never undercounts execution).
        /// </summary>
        private static double LatencyMs(LoadExecutionPlan plan, long scheduledAt, long startedAt) =>
            (Stopwatch.GetTimestamp() - (plan.Settings.IncludeQueueTimeInLatency ? Math.Min(scheduledAt, startedAt) : startedAt)) * TimestampToMs;

        /// <summary>
        /// Races the legacy (token-less) action against a timeout. On timeout the iteration is
        /// recorded as a failure; the underlying task keeps running unobserved, so its eventual
        /// fault is observed to avoid UnobservedTaskException. Prefer ActionWithCancellation,
        /// which aborts the work itself instead of leaking it.
        /// Returns the outcome and the timestamp at which the action actually began. Never throws:
        /// a faulted action is a failure.
        /// </summary>
        private static async Task<(bool IsSuccess, long StartedAt)> ExecuteWithTimeoutAsync(Func<Task<bool>> action, TimeSpan timeout)
        {
            // Task.Run, not action(): an action that blocks before its first await would
            // otherwise run to completion before the timeout race even starts.
            // The timeout clock starts only once the action really begins, so thread-pool
            // queueing never turns into a timeout for work that has not run yet.
            var started = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var actionTask = Task.Run(() =>
            {
                started.TrySetResult(Stopwatch.GetTimestamp());
                return action();
            });
            var startedAt = await started.Task.ConfigureAwait(false);

            using (var timeoutCts = new CancellationTokenSource())
            {
                var delayTask = Task.Delay(timeout, timeoutCts.Token);
                var winner = await Task.WhenAny(actionTask, delayTask).ConfigureAwait(false);
                if (winner == actionTask)
                {
                    timeoutCts.Cancel(); // release the timer
                    try
                    {
                        return (await actionTask.ConfigureAwait(false), startedAt);
                    }
                    catch
                    {
                        // Swallowed here, not in ExecuteOneAsync, so the real start time survives
                        // and the inner hop wait stays queue time, not latency.
                        return (false, startedAt);
                    }
                }

                _ = actionTask.ContinueWith(
                    t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return (false, startedAt);
            }
        }
    }
}
