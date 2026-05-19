// Copyright (c) Inverted Software. All rights reserved.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace InvertedSoftware.WorkflowEngine.Hosting;

/// <summary>
/// ASP.NET Core <see cref="IHealthCheck"/> that probes the queue provider via
/// <c>IQueueProvider.CheckHealthAsync</c>. Register for Kubernetes readiness/
/// liveness probes:
/// <code>
/// services.AddHealthChecks()
///     .AddCheck("workflow-queue",
///         new WorkflowQueueHealthCheck(host, "ExampleJob"),
///         tags: new[] { "ready" });
/// </code>
/// </summary>
public sealed class WorkflowQueueHealthCheck : IHealthCheck
{
    private readonly WorkflowEngineHost _host;
    private readonly string _jobName;
    private readonly bool _requireAllQueues;

    /// <param name="host">Configured engine host.</param>
    /// <param name="jobName">The job whose queues to probe.</param>
    /// <param name="requireAllQueues">
    /// When <c>true</c>, the check is Healthy only if all four queues (Main, Error,
    /// Poison, Completed) are reachable. When <c>false</c>, only the Main queue is
    /// required (others, if missing, count as Degraded rather than Unhealthy).
    /// </param>
    public WorkflowQueueHealthCheck(WorkflowEngineHost host, string jobName, bool requireAllQueues = false)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _jobName = jobName ?? throw new ArgumentNullException(nameof(jobName));
        _requireAllQueues = requireAllQueues;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Health probe targets tier 0 (primary). Multi-tier deployments still see a
            // healthy status as long as the primary is reachable.
            var health = await _host.QueueProvider.CheckHealthAsync(_jobName, tier: 0, cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object>
            {
                ["provider"] = _host.QueueProvider.Name,
                ["job"] = _jobName,
                ["main_available"] = health.MainAvailable,
                ["error_available"] = health.ErrorAvailable,
                ["poison_available"] = health.PoisonAvailable,
                ["completed_available"] = health.CompletedAvailable,
                ["approximate_main_depth"] = health.ApproximateMainDepth ?? -1,
                ["diagnostic"] = health.Diagnostic ?? string.Empty,
            };

            if (!health.MainAvailable)
                return HealthCheckResult.Unhealthy(
                    $"Workflow queue main destination for '{_jobName}' is not reachable on {_host.QueueProvider.Name}.",
                    data: data);

            var anyMissing = !health.ErrorAvailable || !health.PoisonAvailable || !health.CompletedAvailable;
            if (anyMissing)
            {
                return _requireAllQueues
                    ? HealthCheckResult.Unhealthy(
                        $"At least one supporting queue for '{_jobName}' is unreachable.", data: data)
                    : HealthCheckResult.Degraded(
                        $"Main queue available but one or more supporting queues for '{_jobName}' are unreachable.", data: data);
            }

            return HealthCheckResult.Healthy(
                $"Workflow queues for '{_jobName}' reachable on {_host.QueueProvider.Name}.", data);
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy(
                $"Workflow queue health check threw: {e.Message}", e);
        }
    }
}
