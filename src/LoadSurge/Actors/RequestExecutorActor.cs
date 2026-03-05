using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using LoadSurge.Messages;

namespace LoadSurge.Actors
{
    /// <summary>
    /// Lightweight child actor that executes a single load test request.
    /// Managed by a router pool inside LoadWorkerActorBased for fault-tolerant, supervised execution.
    /// </summary>
    public class RequestExecutorActor : ReceiveActor
    {
        private readonly ILoggingAdapter _logger = Context.GetLogger();

        /// <summary>
        /// Message sent from coordinator to request executor via router pool.
        /// </summary>
        public class ExecuteRequestMessage
        {
            /// <summary>The test action to execute.</summary>
            public Func<Task<bool>> Action { get; }
            /// <summary>Actor to receive result metrics.</summary>
            public IActorRef ResultCollector { get; }
            /// <summary>Coordinator actor to notify on completion.</summary>
            public IActorRef Coordinator { get; }
            /// <summary>When this request was scheduled, for queue time calculation.</summary>
            public DateTime ScheduledTime { get; }

            /// <summary>Creates a new ExecuteRequestMessage.</summary>
            public ExecuteRequestMessage(Func<Task<bool>> action, IActorRef resultCollector, IActorRef coordinator, DateTime scheduledTime)
            {
                Action = action;
                ResultCollector = resultCollector;
                Coordinator = coordinator;
                ScheduledTime = scheduledTime;
            }
        }

        /// <summary>
        /// Sent back to coordinator to track in-flight request completion.
        /// </summary>
        public class RequestCompletedMessage
        {
            /// <summary>Singleton instance.</summary>
            public static readonly RequestCompletedMessage Instance = new RequestCompletedMessage();
        }

        /// <summary>
        /// Internal message used by PipeTo to deliver execution results back to self.
        /// </summary>
        private class ExecutionResult
        {
            public bool Success { get; }
            public double LatencyMs { get; }
            public double QueueTimeMs { get; }
            public IActorRef ResultCollector { get; }
            public IActorRef Coordinator { get; }

            public ExecutionResult(bool success, double latencyMs, double queueTimeMs, IActorRef resultCollector, IActorRef coordinator)
            {
                Success = success;
                LatencyMs = latencyMs;
                QueueTimeMs = queueTimeMs;
                ResultCollector = resultCollector;
                Coordinator = coordinator;
            }
        }

        /// <summary>Initializes request executor with message handlers.</summary>
        public RequestExecutorActor()
        {
            Receive<ExecuteRequestMessage>(HandleExecuteRequest);
            Receive<ExecutionResult>(HandleExecutionResult);
        }

        private void HandleExecuteRequest(ExecuteRequestMessage msg)
        {
            msg.ResultCollector.Tell(new RequestStartedMessage());

            var stopwatch = Stopwatch.StartNew();
            var scheduledTime = msg.ScheduledTime;
            var resultCollector = msg.ResultCollector;
            var coordinator = msg.Coordinator;

            msg.Action().ContinueWith(task =>
            {
                stopwatch.Stop();
                var queueTime = (DateTime.UtcNow - scheduledTime).TotalMilliseconds;

                if (task.IsFaulted || task.IsCanceled)
                {
                    return new ExecutionResult(false, 0, queueTime, resultCollector, coordinator);
                }

                return new ExecutionResult(task.Result, stopwatch.Elapsed.TotalMilliseconds, queueTime, resultCollector, coordinator);
            }).PipeTo(Self);
        }

        private void HandleExecutionResult(ExecutionResult result)
        {
            result.ResultCollector.Tell(new StepResultMessage(result.Success, result.LatencyMs, result.QueueTimeMs));
            result.Coordinator.Tell(RequestCompletedMessage.Instance);
        }
    }
}
