// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

/// <summary>Materialise a minimal Workflow.xml in a temp file for tests.</summary>
internal sealed class TestWorkflowXml : IDisposable
{
    public string Path { get; }

    public TestWorkflowXml(string jobName, params (string Name, string InvokeClassFullName)[] steps)
    {
        Path = System.IO.Path.GetTempFileName();
        var stepsXml = string.Join(Environment.NewLine, steps.Select(s =>
            $"""            <Step Name="{s.Name}" Group="g" InvokeClass="{s.InvokeClassFullName}" OnError="Skip" RetryTimes="0" RunMode="Synchronous" />"""));

        File.WriteAllText(Path,
            $"""
            <Workflow>
                <Job Name="{jobName}" MessageClass="ExampleMessage" NotifyComplete="true" MaxRunTimeMilliseconds="60000" MessageQueueType="Transactional">
                    <Queues>
                        <Queue MessageQueue="{jobName}.Main" ErrorQueue="{jobName}.Error" PoisonQueue="{jobName}.Poison" CompletedQueue="{jobName}.Completed" MessageQueueType="Transactional" />
                    </Queues>
                    <Steps>
            {stepsXml}
                    </Steps>
                </Job>
            </Workflow>
            """);
    }

    public void Dispose() { try { File.Delete(Path); } catch { } }
}
