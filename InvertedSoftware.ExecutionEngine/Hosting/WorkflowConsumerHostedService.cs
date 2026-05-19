// Copyright (c) Inverted Software. All rights reserved.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvertedSoftware.WorkflowEngine.Hosting;

/// <summary>
/// Drop-in <see cref="BackgroundService"/> that runs a <see cref="Processor"/>
/// against the configured job. Register as a hosted service in your generic-host
/// based consumer:
/// <code>
/// services.AddSingleton(host);              // your WorkflowEngineHost
/// services.AddHostedService(sp =&gt;
///     new WorkflowConsumerHostedService(sp.GetRequiredService&lt;WorkflowEngineHost&gt;(), "ExampleJob"));
/// </code>
/// </summary>
public sealed class WorkflowConsumerHostedService : BackgroundService
{
    private readonly WorkflowEngineHost _host;
    private readonly string _jobName;
    private readonly ILogger<WorkflowConsumerHostedService> _logger;
    private readonly bool _softExitOnShutdown;
    private Processor? _processor;

    /// <summary>
    /// Create a hosted consumer.
    /// </summary>
    /// <param name="host">Configured engine host.</param>
    /// <param name="jobName">Name of the job to consume.</param>
    /// <param name="softExitOnShutdown">
    /// When <c>true</c> (default), shutdown waits for in-flight jobs to finish.
    /// When <c>false</c>, in-flight jobs are cancelled and nack-requeued.
    /// </param>
    public WorkflowConsumerHostedService(WorkflowEngineHost host, string jobName, bool softExitOnShutdown = true)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _jobName = jobName ?? throw new ArgumentNullException(nameof(jobName));
        _softExitOnShutdown = softExitOnShutdown;
        _logger = host.CreateLogger<WorkflowConsumerHostedService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor = _host.CreateProcessor();
        try
        {
            await _processor.StartFrameworkAsync(_jobName, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "WorkflowConsumerHostedService for job '{JobName}' terminated unexpectedly.", _jobName);
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
            await _processor.StopFrameworkAsync(_softExitOnShutdown, cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _processor?.Dispose();
        base.Dispose();
    }
}
