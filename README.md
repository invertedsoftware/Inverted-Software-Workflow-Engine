# Inverted Software Workflow Engine

A cross-platform **.NET 10 workflow execution engine** built on the
producer–consumer pattern. Define jobs as ordered steps in `Workflow.xml`,
publish work messages from anywhere, and let one or more consumer processes
pick them up, run the steps, retry on failure, and surface results — all
backed by the message broker of your choice.

- **Pluggable transport.** RabbitMQ, Apache Kafka, Azure Service Bus, or an
  in-memory broker for tests. Swap with one line of config.
- **Multi-tier queue failover.** Declare multiple `<Queue>` entries per job
  and the engine handles the resilience pattern automatically: producers fall
  forward (primary, then backups) on outage; consumers iterate in reverse to
  drain stale backlogs while the primary stays responsive.
- **Production-grade out of the box.** OpenTelemetry tracing, OpenMetrics-compatible
  metrics, source-generated structured logging, health checks, graceful shutdown,
  optional idempotency store for at-least-once safety.
- **Cross-platform.** Linux containers, Windows services, macOS dev boxes —
  same engine, same code.
- **Library, not a framework.** Drop it into your existing .NET host. No
  daemon, no orchestrator service, no required database.

> **A note on history.** The engine started in 2010 as a .NET Framework 4.5 +
> MSMQ project (code-named *Gazelle*). The current code is a complete .NET 10
> rewrite — MSMQ has been replaced with a provider abstraction, the API is
> async-first, and the observability and idempotency hooks are new. See
> [CHANGELOG.md](CHANGELOG.md) for the full migration story.

---

## When to use it

The engine fits well when you have:

- **Background work that takes seconds to hours**, not milliseconds — too slow
  for the request thread, but you don't need a full orchestration platform.
- **Multi-step processes** where step B should only run after step A
  succeeds, and a failure in step C should retry or route to a dead-letter queue.
- **A natural unit of work** that maps to one queue message — file batches,
  reports to generate, emails to send, orders to fulfil, ETL records to ingest.
- **A team that already operates RabbitMQ / Kafka / Azure Service Bus** and
  wants to use it for application-level workflows without a Workflow-as-a-Service
  vendor on top.
- **Distributed consumers**: many worker processes reading from the same queue,
  scaling horizontally, surviving rolling deploys.

It is **not** a replacement for:

- **Temporal / Cadence / Conductor** — these handle long-running workflows with
  durable history, signals, child workflows, and SDKs in multiple languages.
  Use them if you need stateful sagas spanning days or weeks.
- **Hangfire / Quartz.NET** — these are great for scheduled-job execution
  (cron-style). This engine is event-driven; you push work in, it processes.
- **Step Functions / Azure Logic Apps** — managed cloud orchestrators.

It sits in the niche between "raw `IConnection.Publish`" and a full workflow
platform: more structure than a bare broker, less ceremony than a workflow
engine.

### Concrete use cases

- **File-processing pipelines.** Watch a folder, drop a message per batch, run
  multi-step processing (copy → validate → transform → archive).
- **Outbound notifications.** Email / SMS / push pipelines where each message is
  one notification, with retry-on-failure and dead-letter routing.
- **ETL / data ingestion.** One queue message per record or batch; steps for
  fetch, validate, enrich, load.
- **Order fulfilment.** Reserve inventory → charge payment → notify warehouse →
  email customer, with per-step retry and idempotency.
- **Periodic batch jobs.** Cron triggers a single message; one worker picks it
  up and runs the multi-step batch.
- **Webhook fan-out.** One incoming webhook produces N downstream messages, each
  processed independently.

---

## Architecture

```
                  ┌─────────────────────────────────────────────┐
                  │             Your Application                │
                  │                                             │
┌──────────┐      │   ┌──────────────────────────────────────┐  │
│ Producer │──────┼──>│  FrameworkManager.AddFrameworkJobAsync│  │
│  (any    │      │   └──────────────────────────────────────┘  │
│  caller) │      │                    │                        │
└──────────┘      │                    │ publish                │
                  └────────────────────┼────────────────────────┘
                                       │
                                       ▼
                  ┌────────────────────────────────────────────────────┐
                  │             Message Broker                         │
                  │   (RabbitMQ / Kafka / Azure Service Bus / Memory)  │
                  │                                                    │
                  │  ┌────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐ │
                  │  │  Main  │  │ Error   │  │ Poison  │  │Completed│ │
                  │  │ queue  │  │  queue  │  │  queue  │  │  queue  │ │
                  │  └────────┘  └─────────┘  └─────────┘  └─────────┘ │
                  └──────┬────────────────────────────────────────▲────┘
                         │                                        │
                         │ consume                                │ ack / publish
                         │                                        │
              ┌──────────┼────────────────────────────────────────┼───────┐
              │          ▼                                        │       │
              │   ┌─────────────────────────────────────────────┐ │       │
              │   │            Processor (per job)              │ │       │
              │   │   await foreach IReceivedMessage            │ │       │
              │   │                  │                          │ │       │
              │   │            [Semaphore: FrameworkMaxThreads] │ │       │
              │   │                  │                          │ │       │
              │   │           ┌──────┴──────┬────────┐          │ │       │
              │   │           ▼             ▼        ▼          │ │       │
              │   │       [Job CTS]    [Job CTS] [Job CTS] …    │ │       │
              │   │           │             │        │          │ │       │
              │   │           ▼             ▼        ▼          │ │       │
              │   │       Executor      Executor  Executor      │ │       │
              │   │      (Sequential or Pipelined)              │ │       │
              │   │           │             │        │          │ │       │
              │   │      [Step 1]      [Step 1]  [Step 1]       │ │       │
              │   │      [Step 2]      [Step 2]  [Step 2]       │ │       │
              │   │      [Step n]      [Step n]  [Step n]       │ │       │
              │   │           │             │        │          │ │       │
              │   │           └─────────────┴────────┴──────────┼─┘       │
              │   │                ack / nack / poison          │         │
              │   └─────────────────────────────────────────────┘         │
              │                  Consumer Process                         │
              └───────────────────────────────────────────────────────────┘
```

### How it works, step by step

**1. Define a job.** A job is a named workflow consisting of an ordered list
of steps. Job definitions live in `Workflow.xml`. Each job declares its
message type, its four queues, a maximum runtime, and its steps with their
error-handling policies.

**2. Implement steps.** A step is a class that implements `IStep`. It receives
the workflow message and a `CancellationToken` and does its work.

**3. Pick a queue provider.** RabbitMQ, Kafka, ASB, or in-memory. The provider
maps each job's *logical* queue names (`MyJob.Main`, `MyJob.Error`, …) to
broker-native resources (exchanges/queues, topics, ASB queues) via a config
dictionary.

**4. Produce work.** From anywhere — a web API, a scheduled task, another
service — call `FrameworkManager.AddFrameworkJobAsync(jobName, message)`.
The message is durably written to the Main queue and the call returns.

**5. Consume work.** One or more consumer processes host a `Processor` per
job they want to handle. The Processor reads the Main queue via
`await foreach`, dispatches each message to the configured executor
(Sequential by default; Pipelined on multi-core for higher throughput),
runs the steps, and acks the message on success.

**6. Handle failure.** Per-step `OnError` policies — `RetryStep`, `RetryJob`,
`Skip`, or `Exit` — drive what happens on exceptions. Unrecoverable failures
publish to the Error queue (summary) and the Poison queue (original body).
Successful jobs optionally publish to the Completed queue.

**7. Observe.** Every job emits OpenTelemetry spans, metrics, and structured
log events with stable event IDs. Wire to your APM and dashboards.

---

## Installation

The engine ships as a set of NuGet packages. Install **the engine package** plus
**exactly one queue-provider package**:

```bash
# Engine (always required)
dotnet add package InvertedSoftware.WorkflowEngine
dotnet add package InvertedSoftware.WorkflowEngine.Queue.Serialization

# Choose one queue provider
dotnet add package InvertedSoftware.WorkflowEngine.Queue.RabbitMq
# or
dotnet add package InvertedSoftware.WorkflowEngine.Queue.Kafka
# or
dotnet add package InvertedSoftware.WorkflowEngine.Queue.AzureServiceBus
# or (for tests / local dev)
dotnet add package InvertedSoftware.WorkflowEngine.Queue.InMemory
```

**Requirements:**

- **.NET 10 SDK** for building.
- **.NET 10 runtime** for running.
- An accessible instance of your chosen broker (or use `InMemory` for local dev).

> **Until first NuGet publish:** if the packages aren't yet on nuget.org for
> your environment, clone this repo, run `dotnet pack`, and reference the
> `.nupkg` files from a local feed (`dotnet nuget add source ./artifacts`).

---

## Quick start (complete, runnable)

This is everything you need to send an "email" through the engine using the
in-memory provider — no broker required.

### 1. Create a console project

```bash
mkdir MyApp && cd MyApp
dotnet new console -f net10.0
dotnet add package InvertedSoftware.WorkflowEngine
dotnet add package InvertedSoftware.WorkflowEngine.Queue.Serialization
dotnet add package InvertedSoftware.WorkflowEngine.Queue.InMemory
```

### 2. Define a message and a step

```csharp
// SendEmailMessage.cs
using InvertedSoftware.WorkflowEngine.Messages;

public class SendEmailMessage : IWorkflowMessage
{
    public int JobID { get; set; }
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
}

// SendEmailStep.cs
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Steps;

public class SendEmailStep : IStep
{
    public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken)
    {
        var email = (SendEmailMessage)message;
        Console.WriteLine($"[Send] To={email.To}  Subject={email.Subject}");
        // ... real send logic, honour cancellationToken on long ops ...
    }

    public void Dispose() { }
}
```

### 3. Create `Config/Workflow.xml`

```xml
<Workflow>
  <Job Name="SendEmail"
       MessageClass="SendEmailMessage"
       NotifyComplete="true"
       MaxRunTimeMilliseconds="30000"
       MessageQueueType="Transactional">
    <Queues>
      <Queue MessageQueue="SendEmail.Main"
             ErrorQueue="SendEmail.Error"
             PoisonQueue="SendEmail.Poison"
             CompletedQueue="SendEmail.Completed"
             MessageQueueType="Transactional"/>
    </Queues>
    <Steps>
      <Step Name="Send"
            Group="g1"
            InvokeClass="MyApp.SendEmailStep, MyApp"
            OnError="RetryStep"
            RetryTimes="3"
            WaitBetweenRetriesMilliseconds="5000"
            RunMode="Synchronous"/>
    </Steps>
  </Job>
</Workflow>
```

Add it to your `.csproj` so it's copied next to the executable:

```xml
<ItemGroup>
  <None Update="Config\Workflow.xml" CopyToOutputDirectory="Always" />
</ItemGroup>
```

### 4. Wire and run

```csharp
// Program.cs
using InvertedSoftware.WorkflowEngine;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;

// Register every step class this consumer can run.
var stepFactory = new TypeNameStepFactory()
    .Register<SendEmailStep>(typeof(SendEmailStep).FullName!, () => new SendEmailStep());

// Pick a queue provider. InMemory needs no broker.
await using var queue = new InMemoryQueueProvider();

// Compose the engine host.
var host = new WorkflowEngineHost(
    queueProvider: queue,
    serializer:    new JsonMessageSerializer(),
    stepFactory:   stepFactory,
    options:       new EngineOptions { FrameworkConfigLocation = "Config/Workflow.xml" });

// Start a consumer for the job.
using var processor = host.CreateProcessor();
var consumerTask = Task.Run(() => processor.StartFrameworkAsync("SendEmail"));

// Publish work.
await FrameworkManager.AddFrameworkJobAsync("SendEmail", new SendEmailMessage
{
    JobID = 1,
    To = "alice@example.com",
    Subject = "Welcome",
    Body = "Hello, Alice!",
});

// In a real app you'd wait for SIGTERM; here we wait a moment then shut down cleanly.
await Task.Delay(1000);
await processor.StopFrameworkAsync(isSoftExit: true);
await consumerTask;
```

Run it:

```bash
dotnet run
```

You'll see:

```
[Send] To=alice@example.com  Subject=Welcome
```

To switch to a real broker, replace `new InMemoryQueueProvider()` with the
RabbitMQ / Kafka / ASB version (see below). Everything else stays the same.

---

## Concepts

### Jobs

A **job** is a named workflow. It binds a message type to an ordered list of
steps. One queue per logical destination (Main / Error / Poison / Completed)
per job.

### Steps

A **step** is a unit of work that implements `IStep`:

```csharp
public interface IStep : IDisposable
{
    void RunStep(IWorkflowMessage message, CancellationToken cancellationToken);
}
```

Steps run in declaration order. Each step has:

- **`OnError`** — one of `RetryStep`, `RetryJob`, `Skip`, `Exit`.
- **`RetryTimes`** — how many retries before giving up.
- **`WaitBetweenRetriesMilliseconds`** — backoff delay.
- **`DependsOn`** / **`DependsOnGroup`** — wait for named steps/groups before running.
- **`RunMode`** — `Synchronous` (inline, errors bubble) or `FireAndForget`
  (background task, errors routed to error queue).
- **`RunAsDomain`**/`RunAsUser`/`RunAsPassword`** — Windows-only impersonation.

Use the `StepBase` abstract class if you want automatic
`cancellationToken.ThrowIfCancellationRequested()` at the top of every step.

### Messages

A **message** carries the input data for one invocation of a job. It implements
`IWorkflowMessage` (just a `JobID` property; everything else is yours). The
concrete CLR type travels in the `x-wf-message-type` header so producer and
consumer can be different processes / different deployments.

### Queues

Each job has four logical queues:

| Queue | Direction | Contains |
|---|---|---|
| **Main** | producer → consumer | New work to process. |
| **Error** | consumer → ops | `WorkflowErrorMessage` summaries (one per failed step). |
| **Poison** | consumer → ops | Original message bodies that couldn't be processed. |
| **Completed** | consumer → producer | (Optional) success notifications. |

The provider maps these *logical* names to *physical* broker resources.
The engine never sees the broker; it just says "publish to MyJob:Poison".

### Multi-tier failover (resilience)

A job can declare **multiple `<Queue>` entries** in `Workflow.xml`. Each
entry becomes a *tier*. Tier 0 is the primary (the first declared); higher
tiers are fallbacks.

The engine handles the asymmetric resilience pattern automatically:

- **Producers iterate FORWARD.** Try to publish to tier 0; on
  `QueueUnavailableException`, fall over to tier 1; etc. The first tier
  that accepts wins. When the primary recovers, the next publish goes
  there again — no manual cutover.
- **Consumers iterate REVERSE.** On startup (and every
  `TierRebalanceIntervalSeconds`, default 30s), each consumer probes
  tiers from highest to lowest via `CheckHealthAsync` and binds to the
  first tier with pending messages. If no tier has work, it sits on
  tier 0 waiting for new arrivals.
- **Secondary publishes follow the consumer.** Error / Poison / Completed
  messages route to the same tier the work was consumed from, so a
  message's whole lifecycle stays colocated.

Net effect: backup queues drain stale backlogs while the primary stays
responsive to fresh writes. Originally a 2010 feature of the MSMQ-era
engine; preserved verbatim through the .NET 10 rewrite.

To use it, declare extra queue entries in `Workflow.xml`:

```xml
<Queues>
  <Queue MessageQueue="MyJob.Main"
         ErrorQueue="MyJob.Error"
         PoisonQueue="MyJob.Poison"
         CompletedQueue="MyJob.Completed"
         MessageQueueType="Transactional"/>
  <Queue MessageQueue="MyJob.Backup.Main"
         ErrorQueue="MyJob.Backup.Error"
         PoisonQueue="MyJob.Backup.Poison"
         CompletedQueue="MyJob.Backup.Completed"
         MessageQueueType="Transactional"/>
</Queues>
```

…and add provider mappings keyed `JobName#N:Kind` for each non-primary tier
(tier 0 uses the bare `JobName:Kind` form so existing single-queue configs
need no changes):

```csharp
new RabbitMqOptions
{
    Mappings =
    {
        // Tier 0 (primary)
        ["MyJob:Main"]      = new RabbitMqDestination("wf.myjob", "main",      "wf.myjob.main"),
        ["MyJob:Error"]     = new RabbitMqDestination("wf.myjob", "error",     "wf.myjob.error"),
        ["MyJob:Poison"]    = new RabbitMqDestination("wf.myjob", "poison",    "wf.myjob.poison"),
        ["MyJob:Completed"] = new RabbitMqDestination("wf.myjob", "completed", "wf.myjob.completed"),

        // Tier 1 (backup) — same shape, different broker resources
        ["MyJob#1:Main"]      = new RabbitMqDestination("wf.myjob.backup", "main",      "wf.myjob.backup.main"),
        ["MyJob#1:Error"]     = new RabbitMqDestination("wf.myjob.backup", "error",     "wf.myjob.backup.error"),
        ["MyJob#1:Poison"]    = new RabbitMqDestination("wf.myjob.backup", "poison",    "wf.myjob.backup.poison"),
        ["MyJob#1:Completed"] = new RabbitMqDestination("wf.myjob.backup", "completed", "wf.myjob.backup.completed"),
    },
}
```

Single-queue jobs (one or zero `<Queue>` entries) skip all of this and use
the bare `LogicalQueue` mapping keys exactly as before. You only opt in by
declaring more than one queue entry.

Multi-tier queue failover **composes with broker-level connection failover**
— a RabbitMQ provider can have both multiple `ConnectionStrings` (handles
a downed broker host) and multiple tiers (handles a downed *destination*
like a quota-exceeded queue or a planned drain).

### `MessageQueueType`

- **`Transactional`** — ack on successful job completion. The default.
  At-least-once delivery; a consumer crash mid-job causes redelivery.
- **`NonTransactional`** — provider auto-acks on receive (fire-and-forget).
  A consumer crash loses the message. Use only when at-most-once is acceptable.

### Executors

- **`SequentialExecutor`** (default on single-core, or when
  `EngineOptions.UsePipelinedOnMulticore = false`) — runs each step strictly
  in order, in the same task.
- **`PipelinedExecutor`** (default on multi-core) — TPL Dataflow pipeline with
  `MaxDegreeOfParallelism = FrameworkMaxThreads`. Each step is a
  `TransformBlock`; multiple jobs flow through the pipeline concurrently.

You don't pick one explicitly — the engine selects based on
`Environment.ProcessorCount` and `EngineOptions.UsePipelinedOnMulticore`.

### Idempotency

At-least-once delivery means **steps can run more than once for the same
message**. Strategies:

1. **Naturally idempotent steps** — UPSERTs, set-not-add, read-only operations.
   Nothing to configure.
2. **External dedup via `IIdempotencyStore`** — the engine consults the store
   before each step. A claim that's already completed means "skip this step,
   it's done". Default implementation is no-op; ship a Redis / SQL / Cosmos
   version for non-idempotent side effects (emails, payments, external APIs).

See [OPERATIONS.md](OPERATIONS.md) for patterns.

---

## Building your own step

A real-world step looks like this:

```csharp
public class FetchAndStoreStep : IStep
{
    private readonly HttpClient _http;
    private readonly IRepository _repo;

    public FetchAndStoreStep(HttpClient http, IRepository repo)
    {
        _http = http;
        _repo = repo;
    }

    public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken)
    {
        var work = (FetchWorkMessage)message;

        // Honour cancellation on long ops.
        cancellationToken.ThrowIfCancellationRequested();

        // Step code is sync; bridge to async APIs with .GetAwaiter().GetResult()
        // or expose your step as Task-returning and bridge inside.
        var data = _http.GetStringAsync(work.Url, cancellationToken).GetAwaiter().GetResult();

        _repo.Save(work.JobID, data);
    }

    public void Dispose() { /* clean up if needed */ }
}
```

Register it in DI or in the step factory:

```csharp
var stepFactory = new TypeNameStepFactory()
    .Register<FetchAndStoreStep>(
        typeof(FetchAndStoreStep).FullName!,
        () => new FetchAndStoreStep(httpClient, repository));
```

The string registered must match the `InvokeClass` attribute in your
`Workflow.xml`. Fully qualified class names are recommended.

---

## Choosing a queue provider

### RabbitMQ

```csharp
using InvertedSoftware.WorkflowEngine.Queue.RabbitMq;

await using var queue = new RabbitMqQueueProvider(new RabbitMqOptions
{
    ConnectionStrings =
    {
        "amqp://user:pwd@primary:5672/wf",
        "amqp://user:pwd@secondary:5672/wf",   // failover
    },
    Mappings =
    {
        ["MyJob:Main"]      = new RabbitMqDestination("wf.myjob", "main",      "wf.myjob.main"),
        ["MyJob:Error"]     = new RabbitMqDestination("wf.myjob", "error",     "wf.myjob.error"),
        ["MyJob:Poison"]    = new RabbitMqDestination("wf.myjob", "poison",    "wf.myjob.poison"),
        ["MyJob:Completed"] = new RabbitMqDestination("wf.myjob", "completed", "wf.myjob.completed"),
    },
    PublisherConfirms = true,
    Prefetch = 16,
    DeclareTopologyOnStartup = true,
});
```

- Publisher confirms wait for the broker's durable ack before returning.
- `tx.select` is used for atomic multi-publish (`HandleErrorAsync` writes
  Error+Poison in one transaction).
- Multiple connection strings give automatic failover on connect failure.

### Apache Kafka

```csharp
using InvertedSoftware.WorkflowEngine.Queue.Kafka;

await using var queue = new KafkaQueueProvider(new KafkaOptions
{
    BootstrapServers = "kafka-1:9092,kafka-2:9092,kafka-3:9092",
    SecurityProtocol = "SaslSsl",
    SaslMechanism = "ScramSha512",
    SaslUsername = "wf",
    SaslPassword = Environment.GetEnvironmentVariable("KAFKA_PASSWORD"),
    Mappings =
    {
        ["MyJob:Main"]      = new KafkaDestination("wf.myjob.main", ConsumerGroup: "wf.myjob.workers"),
        ["MyJob:Error"]     = new KafkaDestination("wf.myjob.error"),
        ["MyJob:Poison"]    = new KafkaDestination("wf.myjob.poison"),
        ["MyJob:Completed"] = new KafkaDestination("wf.myjob.completed"),
    },
    TransactionalId = "wf-engine-prod-1",
    EnableIdempotence = true,
    Acks = "All",
});
```

- `enable.idempotence=true` + `acks=All` for at-least-once with no
  producer-side duplicates.
- `TransactionalId` enables atomic multi-publish via Kafka transactions.
- Per-job ordering: the engine uses `MessageHeaders.CorrelationId` (= JobID)
  as the Kafka message key, so all messages for one job land on the same
  partition.

### Azure Service Bus

```csharp
using InvertedSoftware.WorkflowEngine.Queue.AzureServiceBus;

await using var queue = new AzureServiceBusQueueProvider(new AzureServiceBusOptions
{
    FullyQualifiedNamespace = "wf-engine.servicebus.windows.net",
    UseManagedIdentity = true,
    Mappings =
    {
        ["MyJob:Main"]      = new AsbDestination("wf-myjob-main"),
        ["MyJob:Error"]     = new AsbDestination("wf-myjob-error"),
        ["MyJob:Poison"]    = new AsbDestination("wf-myjob-poison"),
        ["MyJob:Completed"] = new AsbDestination("wf-myjob-completed"),
    },
    MaxConcurrentCalls = 15,
    PrefetchCount = 16,
});
```

- Managed Identity by default (recommended for AKS / VMs / App Service).
- `ConnectionString` is also supported for local dev.
- Peek-lock mode; the engine calls `CompleteMessageAsync` on success,
  `AbandonMessageAsync` for nack-requeue, `DeadLetterMessageAsync` for poison.

### In-memory

```csharp
using InvertedSoftware.WorkflowEngine.Queue.InMemory;

await using var queue = new InMemoryQueueProvider();
```

Backed by `System.Threading.Channels`. No broker, no setup. Use for unit
tests, integration tests, and the included console sample. **Not for
production** — messages live only in this process's RAM.

---

## Running in production

For a complete production deployment guide — observability wiring, idempotency
patterns, retry budgets vs broker lock timeouts, Kubernetes graceful-shutdown
config, per-broker scaling cookbook, and an operational checklist — see
**[OPERATIONS.md](OPERATIONS.md)**.

Quick reference:

- **Distributed tracing.** Add the engine's `ActivitySource` to OpenTelemetry:
  ```csharp
  builder.Services.AddOpenTelemetry()
      .WithTracing(t => t.AddSource("InvertedSoftware.WorkflowEngine").AddOtlpExporter());
  ```
  Producer-side `workflow.publish` spans propagate the W3C `traceparent` header
  through the broker; consumer-side `workflow.consume` spans automatically
  link to the parent.

- **Metrics.** Add the engine's `Meter`:
  ```csharp
  builder.Services.AddOpenTelemetry()
      .WithMetrics(m => m.AddMeter("InvertedSoftware.WorkflowEngine").AddPrometheusExporter());
  ```
  Instruments: `wf.jobs.processed`, `wf.jobs.in_flight`, `wf.job.duration`,
  `wf.step.duration`, `wf.errors`.

- **Hosted consumer.** Use the built-in `BackgroundService`:
  ```csharp
  services.AddHostedService(sp =>
      new WorkflowConsumerHostedService(sp.GetRequiredService<WorkflowEngineHost>(), "MyJob"));
  ```

- **Health checks.** Wire `WorkflowQueueHealthCheck` to ASP.NET Core:
  ```csharp
  services.AddHealthChecks()
      .AddCheck("wf-queue", new WorkflowQueueHealthCheck(host, "MyJob"), tags: new[] { "ready" });
  ```

---

## Solution layout

| Project | NuGet package | Purpose |
| --- | --- | --- |
| `InvertedSoftware.ExecutionEngine` | `InvertedSoftware.WorkflowEngine` | Engine core: Processor, executors, FrameworkManager, observability. |
| `InvertedSoftware.WorkflowEngine.Common` | (bundled) | Shared utilities, Windows-only impersonation, AES encryption helpers. |
| `InvertedSoftware.WorkflowEngine.DataObjects` | (bundled) | `ProcessorJob`, `ProcessorStep`, `ProcessorQueue` POCOs. |
| `InvertedSoftware.WorkflowEngine.Queue.Abstractions` | `InvertedSoftware.WorkflowEngine.Queue.Abstractions` | `IQueueProvider`, `IReceivedMessage`, `LogicalQueue`, `MessageHeaders`, telemetry constants. |
| `InvertedSoftware.WorkflowEngine.Queue.Serialization` | `InvertedSoftware.WorkflowEngine.Queue.Serialization` | `JsonMessageSerializer` (System.Text.Json). |
| `InvertedSoftware.WorkflowEngine.Queue.InMemory` | `InvertedSoftware.WorkflowEngine.Queue.InMemory` | `Channel<T>`-backed provider for tests/dev. |
| `InvertedSoftware.WorkflowEngine.Queue.RabbitMq` | `InvertedSoftware.WorkflowEngine.Queue.RabbitMq` | RabbitMQ provider (RabbitMQ.Client 7.x). |
| `InvertedSoftware.WorkflowEngine.Queue.Kafka` | `InvertedSoftware.WorkflowEngine.Queue.Kafka` | Kafka provider (Confluent.Kafka 2.x). |
| `InvertedSoftware.WorkflowEngine.Queue.AzureServiceBus` | `InvertedSoftware.WorkflowEngine.Queue.AzureServiceBus` | Azure Service Bus provider (Azure.Messaging.ServiceBus 7.x). |
| `InvertedSoftware.WorkflowEngine.Sample.Console` | (sample, not packaged) | Cross-platform console demo using the in-memory provider. |
| `InvertedSoftware.WorkflowEngine.Example` | (sample, not packaged) | WPF demo (Windows-only, `net10.0-windows`). |
| `tests/InvertedSoftware.WorkflowEngine.Tests.Unit` | (tests, not packaged) | xUnit unit tests. |

---

## Building from source

```bash
git clone <repo-url>
cd <repo>
dotnet build InvertedSoftware.WorkflowEngine.sln
dotnet test
```

Building the WPF sample requires Windows; everything else builds cross-platform.

To produce NuGet packages locally:

```bash
dotnet pack InvertedSoftware.WorkflowEngine.sln --configuration Release --output ./artifacts
```

---

## Running the samples

**Console (cross-platform):**

```bash
dotnet run --project InvertedSoftware.WorkflowEngine.Sample.Console
```

Publishes three example messages through the in-memory provider and consumes
them with the default executor.

**WPF (Windows):**

```bash
dotnet build InvertedSoftware.WorkflowEngine.Example
./InvertedSoftware.WorkflowEngine.Example/bin/Debug/net10.0-windows/InvertedSoftware.WorkflowEngine.Example.exe
```

Three-button UI: Start framework, Add job, Stop framework. Uses the
in-memory provider so no broker is required.

---

## Migrating from v1 (MSMQ)

If you're upgrading from the original .NET Framework 4.5 / MSMQ-based engine,
the breaking changes are:

- **Target framework:** net4.5 → net10.0. SDK-style csproj. `packages.config` gone.
- **MSMQ is removed.** Pick a real broker (RabbitMQ / Kafka / ASB). The
  `Workflow.xml` queue strings are now *logical names* the provider maps to
  physical resources. Old `.\Private$\…` paths no longer work.
- **`IStep.RunStep`** signature gained a `CancellationToken` parameter. All
  user-authored steps need a one-line edit.
- **`EngineConfiguration` static class** is replaced by an `EngineOptions`
  POCO passed to `WorkflowEngineHost`.
- **`Processor`** is constructed via `WorkflowEngineHost.CreateProcessor()`.
  `FrameworkManager` is still static and auto-registers when you build the host.
- **`BinaryFormatter`** is gone; `ProcessorJob.Clone()` is replaced by
  `DeepCopy()`.
- **`MessageQueueType`** values reinterpreted: `Transactional` = "ack on
  success"; `NonTransactional` = "auto-ack on receive."
- **`[Serializable]`** removed from all data classes; JSON serialization
  replaces XML on the wire.
- **`Thread.Abort()`** for `MaxRunTimeMilliseconds` is replaced by cooperative
  `CancellationToken`. Steps that ignore the token cannot be forcibly killed.
- **`RunMode`** `STA`/`MTA` renamed to `Synchronous`/`FireAndForget` (old names
  still accepted by the XML parser).

See [CHANGELOG.md](CHANGELOG.md) for the full list and rationale.

---

## Project status

This codebase is the .NET 10 rewrite of a 2010-era engine, modernised in 2025
with a pluggable queue abstraction and production-grade operational hooks.

What works today:

- ✅ All four queue providers compile and pass their abstraction contract.
- ✅ 15 unit tests passing (deep-copy, queue contract, idempotency, tracing,
  health, soft-exit, pipelined concurrency, schema-drift handling).
- ✅ Console sample runs end-to-end against the in-memory provider.
- ✅ WPF sample builds on Windows.

What's deliberately out of scope for now:

- Live integration tests against real brokers (Testcontainers wiring exists
  in the design doc; implementation lives behind a future `tests/Integration` folder).
- NuGet publishing to nuget.org (build locally via `dotnet pack`).
- A schema registry integration (`MessageType` headers are CLR type names today).
- `ServiceBusProcessor`-based ASB receiver for built-in lock-renewal
  (current `ServiceBusReceiver` works for steps within the configured budget).

---

## License

MIT. See [LICENSE.txt](LICENSE.txt).
