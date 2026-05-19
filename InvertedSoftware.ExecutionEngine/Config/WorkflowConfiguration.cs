// Copyright (c) Inverted Software. All rights reserved.

using System.Xml.Linq;
using InvertedSoftware.WorkflowEngine.Common;
using InvertedSoftware.WorkflowEngine.Common.Exceptions;
using InvertedSoftware.WorkflowEngine.DataObjects;

namespace InvertedSoftware.WorkflowEngine.Config;

/// <summary>
/// Loads job and step definitions from <c>Workflow.xml</c>. Replaces the v1
/// <c>XmlDocument</c>+XPath implementation with LINQ-to-XML.
/// </summary>
public sealed class WorkflowConfiguration
{
    private readonly EngineOptions _options;

    public WorkflowConfiguration(EngineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Populate <paramref name="processorJob"/> from the configured XML file.
    /// </summary>
    /// <exception cref="FrameworkFatalException">
    /// Thrown when the file is missing, malformed, or contains no <c>Job</c> with the requested name.
    /// </exception>
    public void LoadFrameworkConfig(ProcessorJob processorJob)
    {
        ArgumentNullException.ThrowIfNull(processorJob);

        var path = ResolvePath(_options.FrameworkConfigLocation);
        if (!File.Exists(path))
            throw new FrameworkFatalException(
                $"Workflow file not found at '{path}' (configured value: '{_options.FrameworkConfigLocation}').");

        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception e)
        {
            throw new FrameworkFatalException(
                $"Failed to parse workflow file '{path}': {e.Message}", e);
        }

        var jobNode = doc.Descendants("Job")
            .FirstOrDefault(j => (string?)j.Attribute("Name") == processorJob.JobName)
            ?? throw new FrameworkFatalException(
                $"Job '{processorJob.JobName}' not found in workflow file.");

        processorJob.MessageClass = (string?)jobNode.Attribute("MessageClass") ?? string.Empty;
        processorJob.MessageQueue = (string?)jobNode.Attribute("MessageQueue") ?? string.Empty;
        processorJob.ErrorQueue = (string?)jobNode.Attribute("ErrorQueue") ?? string.Empty;
        processorJob.PoisonQueue = (string?)jobNode.Attribute("PoisonQueue") ?? string.Empty;
        processorJob.CompletedQueue = (string?)jobNode.Attribute("CompletedQueue") ?? string.Empty;

        if (bool.TryParse((string?)jobNode.Attribute("NotifyComplete"), out var notifyComplete))
            processorJob.NotifyComplete = notifyComplete;

        if (int.TryParse((string?)jobNode.Attribute("MaxRunTimeMilliseconds"), out var maxMs))
            processorJob.MaxRunTimeMilliseconds = maxMs;

        if (Enum.TryParse<MessageQueueType>((string?)jobNode.Attribute("MessageQueueType"), ignoreCase: true, out var mqt))
            processorJob.MessageQueueType = mqt;

        var queuesNode = jobNode.Element("Queues");
        if (queuesNode is not null)
            LoadJobQueues(processorJob, queuesNode);

        var stepsNode = jobNode.Element("Steps");
        // Backward compat: pre-2.0 layouts could put <Step> directly under <Job>.
        LoadJobSteps(processorJob, stepsNode ?? jobNode);
    }

    private static void LoadJobQueues(ProcessorJob processorJob, XElement queuesNode)
    {
        foreach (var queueNode in queuesNode.Elements("Queue"))
        {
            var queue = new ProcessorQueue
            {
                MessageQueue = (string?)queueNode.Attribute("MessageQueue") ?? string.Empty,
                ErrorQueue = (string?)queueNode.Attribute("ErrorQueue") ?? string.Empty,
                PoisonQueue = (string?)queueNode.Attribute("PoisonQueue") ?? string.Empty,
                CompletedQueue = (string?)queueNode.Attribute("CompletedQueue") ?? string.Empty,
            };

            if (Enum.TryParse<MessageQueueType>((string?)queueNode.Attribute("MessageQueueType"), ignoreCase: true, out var mqt))
                queue.MessageQueueType = mqt;

            processorJob.ProcessorQueues.Add(queue);
        }
    }

    private static void LoadJobSteps(ProcessorJob processorJob, XElement stepsNode)
    {
        foreach (var stepNode in stepsNode.Elements("Step"))
        {
            var step = new ProcessorStep
            {
                StepName = (string?)stepNode.Attribute("Name") ?? string.Empty,
                Group = (string?)stepNode.Attribute("Group") ?? string.Empty,
                InvokeClass = (string?)stepNode.Attribute("InvokeClass") ?? string.Empty,
                DependsOn = (string?)stepNode.Attribute("DependsOn") ?? string.Empty,
                DependsOnGroup = (string?)stepNode.Attribute("DependsOnGroup") ?? string.Empty,
            };

            if (Enum.TryParse<OnFrameworkStepError>((string?)stepNode.Attribute("OnError"), ignoreCase: true, out var onErr))
                step.OnError = onErr;

            if (int.TryParse((string?)stepNode.Attribute("RetryTimes"), out var retry))
                step.RetryTimes = retry;

            if (int.TryParse((string?)stepNode.Attribute("WaitBetweenRetriesMilliseconds"), out var waitRetry))
                step.WaitBetweenRetriesMilliseconds = waitRetry;

            if (int.TryParse((string?)stepNode.Attribute("WaitForDependsOnMilliseconds"), out var waitDep))
                step.WaitForDependsOnMilliseconds = waitDep;

            step.RunMode = ParseRunMode((string?)stepNode.Attribute("RunMode"));

            // Encrypted attributes (legacy RijndaelEnhanced format preserved).
            var rad = (string?)stepNode.Attribute("RunAsDomain");
            if (!string.IsNullOrEmpty(rad)) step.RunAsDomain = Utils.GetDecryptedString(rad);
            var rau = (string?)stepNode.Attribute("RunAsUser");
            if (!string.IsNullOrEmpty(rau)) step.RunAsUser = Utils.GetDecryptedString(rau);
            var rap = (string?)stepNode.Attribute("RunAsPassword");
            if (!string.IsNullOrEmpty(rap)) step.RunAsPassword = Utils.GetDecryptedString(rap);

            processorJob.WorkFlowSteps.Add(step);
        }
    }

    /// <summary>
    /// Resolve a relative <see cref="EngineOptions.FrameworkConfigLocation"/> against the app
    /// base directory so the path works regardless of the consumer's current working directory.
    /// Absolute paths are returned unchanged.
    /// </summary>
    private static string ResolvePath(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;
        var fromBase = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(fromBase)) return fromBase;
        // Fall back to current working directory so callers that explicitly chdir still work.
        return configured;
    }

    /// <summary>
    /// Accept new names (<c>Synchronous</c>/<c>FireAndForget</c>) AND the legacy
    /// <c>STA</c>/<c>MTA</c> values still present in many v1 Workflow.xml files.
    /// </summary>
    private static StepExecutionMode ParseRunMode(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return StepExecutionMode.Synchronous;
        return raw.Trim().ToUpperInvariant() switch
        {
            "STA" => StepExecutionMode.Synchronous,
            "MTA" => StepExecutionMode.FireAndForget,
            "SYNCHRONOUS" => StepExecutionMode.Synchronous,
            "FIREANDFORGET" => StepExecutionMode.FireAndForget,
            _ => StepExecutionMode.Synchronous,
        };
    }
}
