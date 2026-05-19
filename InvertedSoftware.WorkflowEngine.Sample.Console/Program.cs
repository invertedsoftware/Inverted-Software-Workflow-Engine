// Copyright (c) Inverted Software. All rights reserved.
//
// Cross-platform console demo. Runs end-to-end against the in-memory queue
// provider — no broker required. Swap the provider construction below to
// exercise RabbitMQ, Kafka, or Azure Service Bus.

using InvertedSoftware.WorkflowEngine;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;
using Microsoft.Extensions.Configuration;

// ----- 1. Load options ------------------------------------------------------
var configRoot = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();
var options = configRoot.GetSection("WorkflowEngine").Get<EngineOptions>() ?? new EngineOptions();

// ----- 2. Wire the engine ---------------------------------------------------
var stepFactory = new TypeNameStepFactory()
    .Register<CopyFiles>("InvertedSoftware.WorkflowEngine.Steps.CopyFiles", () => new CopyFiles())
    .Register<RenameFiles>("InvertedSoftware.WorkflowEngine.Steps.RenameFiles", () => new RenameFiles());

await using var queueProvider = new InMemoryQueueProvider();
var host = new WorkflowEngineHost(
    queueProvider: queueProvider,
    serializer: new JsonMessageSerializer(),
    stepFactory: stepFactory,
    options: options);

// ----- 3. Start the consumer ------------------------------------------------
using var shutdownCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdownCts.Cancel(); };

using var processor = host.CreateProcessor();
var processorTask = Task.Run(() => processor.StartFrameworkAsync("ExampleJob", shutdownCts.Token));

// ----- 4. Publish a few example messages ------------------------------------
Console.WriteLine("Publishing 3 example messages...");
var tempDir = Path.Combine(Path.GetTempPath(), "wf-engine-sample");
Directory.CreateDirectory(Path.Combine(tempDir, "source"));
Directory.CreateDirectory(Path.Combine(tempDir, "dest"));

for (var i = 1; i <= 3; i++)
{
    await FrameworkManager.AddFrameworkJobAsync("ExampleJob", new ExampleMessage
    {
        JobID = i,
        CopyFilesFrom = Path.Combine(tempDir, "source"),
        CopyFilesTo = Path.Combine(tempDir, "dest"),
    });
    Console.WriteLine($"  -> message {i} enqueued");
}

// ----- 5. Wait briefly for processing, then stop ----------------------------
await Task.Delay(TimeSpan.FromSeconds(3), shutdownCts.Token).ContinueWith(_ => { });
Console.WriteLine($"Jobs running: {processor.JobsRunning}");

Console.WriteLine("Stopping framework (soft)...");
await processor.StopFrameworkAsync(isSoftExit: true);

try { await processorTask; }
catch (OperationCanceledException) { }

Console.WriteLine("Done.");
