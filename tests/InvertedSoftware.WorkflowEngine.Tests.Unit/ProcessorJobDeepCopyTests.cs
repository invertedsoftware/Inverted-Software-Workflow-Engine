// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.DataObjects;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

/// <summary>
/// Pins the v2.0.0 hand-written DeepCopy() that replaced the v1
/// BinaryFormatter-based <c>ICloneable.Clone()</c>.
/// </summary>
public class ProcessorJobDeepCopyTests
{
    [Fact]
    public void DeepCopy_Mutating_Clone_Does_Not_Affect_Original()
    {
        var template = new ProcessorJob
        {
            JobName = "OriginalJob",
            MaxRunTimeMilliseconds = 60_000,
            NotifyComplete = true,
            ProcessorQueues = { new ProcessorQueue { MessageQueue = "main" } },
            WorkFlowSteps = { new ProcessorStep { StepName = "step1" } },
        };

        var copy = template.DeepCopy();
        copy.JobName = "MutatedJob";
        copy.MaxRunTimeMilliseconds = 1;
        copy.NotifyComplete = false;
        copy.ProcessorQueues[0].MessageQueue = "mutated";
        copy.WorkFlowSteps[0].StepName = "mutated-step";
        copy.WorkFlowSteps.Add(new ProcessorStep { StepName = "extra" });

        Assert.Equal("OriginalJob", template.JobName);
        Assert.Equal(60_000, template.MaxRunTimeMilliseconds);
        Assert.True(template.NotifyComplete);
        Assert.Equal("main", template.ProcessorQueues[0].MessageQueue);
        Assert.Equal("step1", template.WorkFlowSteps[0].StepName);
        Assert.Single(template.WorkFlowSteps);
    }

    [Fact]
    public void DeepCopy_Does_Not_Copy_Event_Subscribers()
    {
        var queue = new ProcessorQueue { MessageQueue = "initial" };
        var fired = 0;
        queue.ProcessorQueueChanged += (_, _) => fired++;

        var copy = queue.DeepCopy();
        copy.MessageQueue = "changed";   // would fire the event if subscribers were copied

        Assert.Equal(0, fired);
    }
}
