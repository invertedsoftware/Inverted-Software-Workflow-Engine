---
title: "Bringing the Inverted Software Workflow Engine to .NET 10"
subtitle: "From MSMQ to RabbitMQ, Kafka, and Azure Service Bus — a pluggable producer/consumer pattern for modern .NET"
date: 2026-05-19
tags: [dotnet, workflow, producer-consumer, rabbitmq, kafka, azure-service-bus, opentelemetry]
---

Back in 2010 I wrote a little .NET workflow engine, code-named *Gazelle*, and
posted it on my blog. It targeted .NET Framework 4.5 and used MSMQ — Microsoft
Message Queuing — as its transport. For its time it was decent: jobs were
ordered sequences of steps, each step could retry independently, failures
routed to a poison queue, multiple machines could co-consume the same queue.
I used it on several projects. Then the world changed.

MSMQ never made the jump to .NET Core. `System.Messaging` is Windows-only and
hasn't seen meaningful work in over a decade. By 2020 the message-queue
landscape had crystallised around three real options: **RabbitMQ** for
self-hosted, **Apache Kafka** for high-throughput event streams, **Azure
Service Bus** for cloud-managed. My old engine became a museum piece.

I just rewrote it. Top to bottom. The result is **v2.1.1** on .NET 10, with a
pluggable queue provider that lets you pick your broker at startup — and a
proper production-readiness story (OpenTelemetry, metrics, structured logs,
health checks, idempotency hooks).

This post walks through why I rewrote it, how it works, and how to put it to
use in your own .NET 10 app.

**Where to grab it:** <https://github.com/invertedsoftware/Inverted-Software-Workflow-Engine>

---

## Why a workflow engine, and why this one?

Most .NET back-ends I've worked on have at least one place where the right
shape is **producer/consumer**:

- The web request needs to return in 200 ms, but the work it kicks off takes
  30 seconds.
- A nightly batch job has to copy files, validate them, transform, and load
  them, with retries.
- An order comes in: reserve inventory, charge payment, notify warehouse,
  email the customer — each step can fail independently, and you don't want
  the customer's HTTP request hanging while you do all that.

The "obvious" answer is to publish a message to a queue and let a background
worker process it. So far so good. The problems creep in when:

- You need **multiple steps** per message, with **retry policies per step**.
- You need to retry the whole job differently than individual steps.
- You need **idempotency** because the broker is at-least-once.
- You need **observability** — distributed traces, metrics, logs.
- You need to **scale horizontally** across machines.
- And eventually, you need to **swap brokers** because the team that runs
  Kafka isn't the team that wants to host RabbitMQ.

You can build that on top of `IConnection.Publish` and `BasicConsume` yourself.
A lot of teams do, and end up with a bespoke half-engine that nobody fully
understands. Or you can reach for something heavier — Temporal, Conductor,
Hangfire — and find that they solve different problems than the one in front
of you.

The Inverted Software Workflow Engine sits in that middle ground: it
**structures** the producer/consumer pattern around jobs and steps, gives you
retries and dead-lettering for free, and stays out of your way for everything
else. It's a library you reference from your own host — no daemon, no
external orchestrator service, no required database.

---

## The rewrite, in one breath

Everything you'd expect from a modern .NET library:

- **Targets .NET 10** across every project. SDK-style csproj. No
  `packages.config`, no `AssemblyInfo.cs`.
- **MSMQ is gone.** All queue I/O goes through an `IQueueProvider`
  abstraction. Four providers ship in the box: **RabbitMQ**, **Apache Kafka**,
  **Azure Service Bus**, and an **in-memory** provider for tests.
- **Async-first.** `StartFrameworkAsync`, `AddFrameworkJobAsync`, and the
  consumer uses `IAsyncEnumerable<IReceivedMessage>`.
- **Cooperative cancellation.** `Thread.Abort()` is dead in .NET 10. Per-job
  `MaxRunTimeMilliseconds` is enforced via a linked `CancellationTokenSource`
  that propagates all the way into your step code.
- **OpenTelemetry built in.** W3C TraceContext propagates from producer
  through the broker to the consumer; metrics are emitted on a `Meter`;
  structured logs use source-generated `LoggerMessage` definitions with
  stable event IDs.
- **Cross-platform.** Linux containers, macOS dev boxes, Windows services —
  same code. The only Windows-only bit is the optional WPF demo.

If you used v1 and want to upgrade, see [CHANGELOG.md][changelog] for the
breaking changes. The biggest one: `IStep.RunStep` gained a `CancellationToken`
parameter.

[changelog]: https://github.com/invertedsoftware/Inverted-Software-Workflow-Engine/blob/master/CHANGELOG.md

---

## The mental model

Three concepts and you can read any code that follows.

**Job.** A named workflow. It maps a message type to an ordered list of
steps. You define jobs in `Workflow.xml`.

**Step.** A unit of work. Implements `IStep`. The engine invokes it once per
incoming message. Per-step you configure retry policy (`OnError` = `RetryStep`
| `RetryJob` | `Skip` | `Exit`), dependencies, and Windows impersonation if
you need it.

**Message.** One invocation of a job. Implements `IWorkflowMessage` — the
only requirement is a `JobID` property. Everything else is your data.

Each job has four logical queues:

| Queue | Direction | Contains |
|---|---|---|
| **Main** | producer → consumer | New work. |
| **Error** | consumer → ops | `WorkflowErrorMessage` summaries for failed jobs. |
| **Poison** | consumer → ops | The original messages that couldn't be processed. |
| **Completed** | consumer → producer | (Optional) success notifications. |

Those four names are *logical*. The provider maps them to whatever your
broker uses — RabbitMQ exchanges, Kafka topics, Azure Service Bus queues.
The engine never sees the broker; it just says "publish to MyJob:Poison" and
the provider figures out where.

---

## The flow, in pictures

```
┌──────────┐      ┌────────────────────────────────────────────────────┐
│ Producer │─────>│             Message Broker                         │
└──────────┘      │   (RabbitMQ / Kafka / Azure Service Bus / Memory)  │
                  │                                                    │
                  │  ┌────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐ │
                  │  │  Main  │  │ Error   │  │ Poison  │  │Completed│ │
                  │  │ queue  │  │  queue  │  │  queue  │  │  queue  │ │
                  │  └────┬───┘  └────▲────┘  └────▲────┘  └────▲────┘ │
                  └───────┼───────────┼────────────┼────────────┼──────┘
                          │           │            │            │
                          ▼           │            │            │
                  ┌───────────────────┴────────────┴────────────┴──────┐
                  │            Consumer (Processor per job)            │
                  │   ┌─────────────────────────────────────────────┐  │
                  │   │   await foreach IReceivedMessage            │  │
                  │   │            │                                │  │
                  │   │     [SemaphoreSlim] ← FrameworkMaxThreads   │  │
                  │   │            │                                │  │
                  │   │     ┌──────┴──────┬────────┐                │  │
                  │   │     ▼             ▼        ▼                │  │
                  │   │  [Job CTS]   [Job CTS]  [Job CTS] …         │  │
                  │   │     │             │        │                │  │
                  │   │  Executor    Executor   Executor            │  │
                  │   │     │             │        │                │  │
                  │   │  [Step 1]    [Step 1]   [Step 1]            │  │
                  │   │  [Step 2]    [Step 2]   [Step 2]            │  │
                  │   │     ack          ack       ack              │  │
                  │   └─────────────────────────────────────────────┘  │
                  └────────────────────────────────────────────────────┘
```

The producer side is dead simple: serialize a message, write it to the Main
queue, return. The producer doesn't know — or care — who reads it.

The consumer side runs a `Processor` per job. The Processor consumes the Main
queue via `await foreach`, dispatches each message under a semaphore that
caps total concurrency, gives each job its own `CancellationTokenSource` for
the per-job timeout, and acks the message only after the executor returns
successfully. Failures route to Error + Poison. Successes optionally route
to Completed.

Multiple Processor instances can run on different machines against the same
queue and the broker round-robins messages between them. That's your
horizontal scaling — no coordination protocol needed.

---

## Quick start: hello, workflow

Let's build a tiny but complete consumer. We'll use the in-memory provider so
no broker is needed.

### 1. New console project

```bash
mkdir SendEmailWorker && cd SendEmailWorker
dotnet new console -f net10.0
dotnet add package InvertedSoftware.WorkflowEngine
dotnet add package InvertedSoftware.WorkflowEngine.Queue.Serialization
dotnet add package InvertedSoftware.WorkflowEngine.Queue.InMemory
```

> Until the NuGet packages land on nuget.org for your environment, clone the
> repo, run `dotnet pack`, and reference the local `.nupkg` files. Everything
> below works either way.

### 2. Define the message and step

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
        // ... real SMTP / Mailgun / Postmark code here, honouring cancellationToken ...
    }

    public void Dispose() { }
}
```

That's the user-visible surface: a POCO message and a class that implements
`IStep`. No reflection magic, no attribute soup.

### 3. Describe the workflow

`Config/Workflow.xml` (copy it next to your executable via `<CopyToOutputDirectory>`):

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
            InvokeClass="SendEmailWorker.SendEmailStep, SendEmailWorker"
            OnError="RetryStep"
            RetryTimes="3"
            WaitBetweenRetriesMilliseconds="5000"
            RunMode="Synchronous"/>
    </Steps>
  </Job>
</Workflow>
```

A few notes:

- `MaxRunTimeMilliseconds` is the per-message deadline. After this, the
  engine cancels the `CancellationToken` your step holds.
- `OnError="RetryStep"` means: on exception, wait `WaitBetweenRetriesMilliseconds`
  and call the step again, up to `RetryTimes` attempts.
- The four queue strings are **logical names**. The provider resolves them.
- `MessageQueueType="Transactional"` means "ack on successful job completion"
  (the default and recommended setting). The alternative is
  `"NonTransactional"`, which auto-acks on receive — fire-and-forget.

### 4. Wire it up

```csharp
// Program.cs
using InvertedSoftware.WorkflowEngine;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;

// Tell the engine where to find your step classes.
var stepFactory = new TypeNameStepFactory()
    .Register<SendEmailStep>(typeof(SendEmailStep).FullName!, () => new SendEmailStep());

// Pick a queue provider. InMemory needs no broker; we'll swap this later.
await using var queue = new InMemoryQueueProvider();

// Bundle everything in a host.
var host = new WorkflowEngineHost(
    queueProvider: queue,
    serializer:    new JsonMessageSerializer(),
    stepFactory:   stepFactory,
    options:       new EngineOptions { FrameworkConfigLocation = "Config/Workflow.xml" });

// Start consuming.
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

// In production you'd wait for SIGTERM. For the demo, give it a second
// then shut down cleanly.
await Task.Delay(1000);
await processor.StopFrameworkAsync(isSoftExit: true);
await consumerTask;
```

`dotnet run` and you'll see:

```
[Send] To=alice@example.com  Subject=Welcome
```

That's an end-to-end workflow on .NET 10. To switch to a real broker, you
change exactly one line: the `new InMemoryQueueProvider()` call.

---

## Switching to a real broker

Each provider has its own NuGet package and its own typed options class. The
mapping from logical queue names to broker resources is explicit — you don't
end up with magic naming conventions you can't trace.

### RabbitMQ

```bash
dotnet add package InvertedSoftware.WorkflowEngine.Queue.RabbitMq
```

```csharp
using InvertedSoftware.WorkflowEngine.Queue.RabbitMq;

await using var queue = new RabbitMqQueueProvider(new RabbitMqOptions
{
    ConnectionStrings =
    {
        "amqp://user:pwd@primary-host:5672/wf",
        "amqp://user:pwd@secondary-host:5672/wf",   // automatic failover
    },
    Mappings =
    {
        ["SendEmail:Main"]      = new RabbitMqDestination("wf.email", "main",      "wf.email.main"),
        ["SendEmail:Error"]     = new RabbitMqDestination("wf.email", "error",     "wf.email.error"),
        ["SendEmail:Poison"]    = new RabbitMqDestination("wf.email", "poison",    "wf.email.poison"),
        ["SendEmail:Completed"] = new RabbitMqDestination("wf.email", "completed", "wf.email.completed"),
    },
    PublisherConfirms = true,
    Prefetch = 16,
    DeclareTopologyOnStartup = true,
});
```

Publisher confirms make `PublishAsync` wait for the broker's durable ack before
returning. `tx.select` is used for the atomic Error+Poison batch send. Multiple
connection strings give automatic failover on connect failure.

### Apache Kafka

```bash
dotnet add package InvertedSoftware.WorkflowEngine.Queue.Kafka
```

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
        ["SendEmail:Main"]      = new KafkaDestination("wf.email.main", ConsumerGroup: "wf.email.workers"),
        ["SendEmail:Error"]     = new KafkaDestination("wf.email.error"),
        ["SendEmail:Poison"]    = new KafkaDestination("wf.email.poison"),
        ["SendEmail:Completed"] = new KafkaDestination("wf.email.completed"),
    },
    TransactionalId = "wf-engine-prod-1",
    EnableIdempotence = true,
    Acks = "All",
});
```

`enable.idempotence=true` + `acks=All` gives at-least-once with no
producer-side duplicates within a session. The `TransactionalId` enables the
atomic multi-publish (Kafka transactional producer) used by the error path.

One Kafka-specific quirk worth knowing: per-job message ordering depends on
partitioning. The engine sets the Kafka message key to your message's
`CorrelationId` (which equals `JobID`), so all messages for the same job
land on the same partition. If you care about strict ordering for the same
business entity, encode it into the JobID.

### Azure Service Bus

```bash
dotnet add package InvertedSoftware.WorkflowEngine.Queue.AzureServiceBus
```

```csharp
using InvertedSoftware.WorkflowEngine.Queue.AzureServiceBus;

await using var queue = new AzureServiceBusQueueProvider(new AzureServiceBusOptions
{
    FullyQualifiedNamespace = "wf-engine.servicebus.windows.net",
    UseManagedIdentity = true,
    Mappings =
    {
        ["SendEmail:Main"]      = new AsbDestination("wf-email-main"),
        ["SendEmail:Error"]     = new AsbDestination("wf-email-error"),
        ["SendEmail:Poison"]    = new AsbDestination("wf-email-poison"),
        ["SendEmail:Completed"] = new AsbDestination("wf-email-completed"),
    },
    MaxConcurrentCalls = 15,
    PrefetchCount = 16,
});
```

Managed Identity by default (works on App Service, AKS, VMs with system-
assigned identity). `ConnectionString` is also supported for local dev. The
consume path uses `ServiceBusProcessor` so long-running steps don't lose their
peek-lock mid-execution — `MaxAutoLockRenewalDuration` is auto-sized from
your `MaxRunTimeMilliseconds`.

---

## A more realistic step

The "hello world" step above is fine for a demo. Here's what a real step
looks like with dependency-injected services, cancellation honoured, and
proper exception handling:

```csharp
public class ChargePaymentStep : IStep
{
    private readonly IPaymentGateway _gateway;
    private readonly IOrderRepository _orders;

    public ChargePaymentStep(IPaymentGateway gateway, IOrderRepository orders)
    {
        _gateway = gateway;
        _orders = orders;
    }

    public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken)
    {
        var fulfilOrder = (FulfilOrderMessage)message;

        // Honour cancellation on long external calls.
        cancellationToken.ThrowIfCancellationRequested();

        // Idempotency key — the gateway dedupes by this so retries don't
        // double-charge. The engine guarantees at-least-once delivery; YOU
        // make that effectively-once via idempotency keys at the boundary.
        var idempotencyKey = $"order-{fulfilOrder.JobID}-charge";

        var result = _gateway.ChargeAsync(
            amount: fulfilOrder.AmountCents,
            customerId: fulfilOrder.CustomerId,
            idempotencyKey: idempotencyKey,
            cancellationToken: cancellationToken).GetAwaiter().GetResult();

        if (!result.Success)
            throw new InvalidOperationException($"Charge declined: {result.Reason}");

        _orders.MarkCharged(fulfilOrder.JobID, result.TransactionId);
    }

    public void Dispose() { }
}
```

Register it with the step factory using a factory delegate that pulls
services from your DI container, and you're good. The engine doesn't insist
you use any particular DI container — it just wants something callable that
returns an `IStep` instance.

---

## Production: the bits you'll thank me for at 3 AM

The library ships with the operational primitives a distributed system
needs. None of it is bolt-on: it's wired through the engine and emits with
zero configuration.

### Distributed tracing

```csharp
using OpenTelemetry;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource("InvertedSoftware.WorkflowEngine")
    .AddAspNetCoreInstrumentation()
    .AddOtlpExporter());
```

`AddSource("InvertedSoftware.WorkflowEngine")` and that's it. The producer
side opens a `workflow.publish` span, injects W3C `traceparent` into the
message headers, and the consumer side opens a `workflow.consume` span
linked to the producer's context. Each step gets a `workflow.step` child
span. Open your APM and you can follow a job through every hop.

### Metrics

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m
    .AddMeter("InvertedSoftware.WorkflowEngine")
    .AddPrometheusExporter());
```

The instruments:

| Name | Type | Tags | Meaning |
|---|---|---|---|
| `wf.jobs.processed` | Counter | job, outcome | Total terminal events. |
| `wf.jobs.in_flight` | UpDownCounter | job | Jobs running right now. |
| `wf.job.duration` | Histogram | job, outcome | End-to-end job duration. |
| `wf.step.duration` | Histogram | job, step, outcome | Per-step. |
| `wf.errors` | Counter | job, step, kind | Engine-level errors. |

Throw these into a Grafana dashboard, alert on `wf.errors{kind="timeout"}`
or growing `wf.jobs.in_flight`, sleep at night.

### Idempotency

At-least-once delivery means **steps can run more than once for the same
message**. Strategies the engine supports:

1. **Naturally idempotent steps.** UPSERTs, set-not-add, read-only ops.
   Nothing to configure.
2. **External idempotency store**. Implement `IIdempotencyStore`, pass it to
   the host. The engine consults it before each step. Steps that the store
   reports as already-completed are skipped on redelivery.

A minimal Redis implementation:

```csharp
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IDatabase _redis;

    public RedisIdempotencyStore(IDatabase redis) => _redis = redis;

    public async ValueTask<bool> TryClaimAsync(IdempotencyClaim claim, CancellationToken ct)
    {
        // SET key value NX EX 86400 — returns true if we won the race
        return await _redis.StringSetAsync(claim.Key, "claimed",
            expiry: TimeSpan.FromDays(1), When.NotExists);
    }

    public async ValueTask MarkCompletedAsync(IdempotencyClaim claim, CancellationToken ct)
    {
        await _redis.StringSetAsync(claim.Key, "completed", expiry: TimeSpan.FromDays(7));
    }
}

// Wire it:
var host = new WorkflowEngineHost(
    queueProvider, serializer, stepFactory, options,
    idempotencyStore: new RedisIdempotencyStore(redis));
```

For a payment step, this is what stands between "we charged the customer
once" and "we charged the customer four times because of a broker hiccup."

### Hosted service

Don't roll your own `BackgroundService`:

```csharp
services.AddSingleton(host);
services.AddHostedService(sp =>
    new WorkflowConsumerHostedService(
        sp.GetRequiredService<WorkflowEngineHost>(),
        jobName: "SendEmail"));
```

Soft-shutdown semantics: in-flight jobs run to natural completion, ack
normally, and the host shuts down cleanly. Hard shutdown (cancel the
process token) cancels in-flight jobs and they nack-requeue.

### Health checks

```csharp
services.AddHealthChecks()
    .AddCheck(
        "workflow-queue",
        new WorkflowQueueHealthCheck(host, "SendEmail"),
        tags: new[] { "ready" });

app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });
```

Points your Kubernetes readiness probe at `/health/ready` and you'll only
take traffic when the broker is reachable.

---

## Honest trade-offs

I'd rather you know the limits up front than discover them in production.

**At-least-once, not exactly-once.** Your steps will sometimes run twice for
the same message. The engine cannot prevent this — no producer/consumer
system on a real broker can. Build idempotency into the boundary (idempotency
keys, UPSERTs, the `IIdempotencyStore` hook).

**Atomic multi-publish is broker-scoped.** When the engine writes Error +
Poison together, the atomicity holds only if both destinations are on the
same broker instance / namespace. Cross-cluster atomic publish is not a
thing on any of the three brokers.

**Kafka ordering is per-partition.** The engine sets the message key to your
`JobID` so same-job messages land on the same partition. If you need cross-
job ordering, Kafka is the wrong tool.

**Cooperative cancellation only.** A step that ignores `CancellationToken`
and runs forever cannot be killed — there's no `Thread.Abort()` replacement
in modern .NET. Document this in your team's step style guide.

**Workflow.xml lives on every consumer.** Each consumer process reads the
file at startup. Deploy in lock-step or use a central config source. The
guide in [OPERATIONS.md][operations] covers the rollout patterns.

[operations]: https://github.com/invertedsoftware/Inverted-Software-Workflow-Engine/blob/master/OPERATIONS.md

---

## Where this fits, and where it doesn't

**Use this** when you need a producer/consumer pattern with structured
multi-step workflows, per-step retry, idempotency hooks, and the ability to
swap the broker. It's right-sized for work that's measured in seconds to
hours, processed by N worker instances.

**Don't use this** if you need:

- **Long-running workflows with durable history** (days to weeks, with
  signals and child workflows) — reach for **Temporal** or **Cadence**.
- **Cron-style scheduled jobs** — **Hangfire** or **Quartz.NET** are
  purpose-built for that.
- **Visual designer / business-user authoring** — pick a BPMN platform.
- **Managed cloud orchestration** with a SaaS billing model — **Step
  Functions** or **Azure Logic Apps** fit better.

This engine is a library. You wire it into your existing host, deploy it
with your existing CI, monitor it with your existing APM. No new service
to operate.

---

## Getting it

The repo is here:

**<https://github.com/invertedsoftware/Inverted-Software-Workflow-Engine>**

Default branch is `master` (v2.1.1 .NET 10). The pre-rewrite v1 .NET 4.5 /
MSMQ code is still reachable in the git history if you want to compare.

To build from source:

```bash
git clone https://github.com/invertedsoftware/Inverted-Software-Workflow-Engine.git
cd Inverted-Software-Workflow-Engine
dotnet build
dotnet test
```

You need the **.NET 10 SDK**. The WPF demo project requires Windows; the
rest builds and tests on Linux, macOS, and Windows.

The console sample runs end-to-end against the in-memory provider with no
broker needed:

```bash
dotnet run --project InvertedSoftware.WorkflowEngine.Sample.Console
```

NuGet packages are not on nuget.org yet — the release workflow ships on tag
push (`git tag v2.1.1 && git push --tags`). Until then, `dotnet pack` to
your own feed:

```bash
dotnet pack --configuration Release --output ./artifacts
dotnet nuget add source $PWD/artifacts --name local
```

---

## What's next

A few things I want to land in the next minor releases:

- **NuGet pipeline turned on.** First publish to nuget.org.
- **Live Testcontainers integration tests** for RabbitMQ and Kafka,
  emulator-based for ASB. The contract tests exist; just need the CI infra.
- **Request/reply convenience API**: `PublishAndWaitAsync(message, ct)` that
  subscribes to the Completed queue and correlates by MessageId.
- **`ConsumeAsync` overload accepting `LogicalQueueKind`** so consumers can
  subscribe to Completed / Error / Poison directly without a custom client.
- **Schema-evolution helpers** — message-type versioning so renames don't
  break the wire format.

If you have feedback, an open PR, or want to talk about how you're using
this, the GitHub issues are open.

---

## Closing

I built the original engine because the projects I worked on kept needing
the same shape and I was tired of writing it bespoke each time. The rewrite
keeps that motivation — make the right thing easy. Distributed
producer/consumer is a pattern every back-end ends up needing somewhere.
You shouldn't have to choose between "raw broker calls" and "spin up
Temporal." There's a middle ground, and this is my version of it.

Happy to hear how it works for you. Code is on GitHub; issues and PRs
welcome.

---

*The Inverted Software Workflow Engine is MIT licensed. Repo:
<https://github.com/invertedsoftware/Inverted-Software-Workflow-Engine>*
