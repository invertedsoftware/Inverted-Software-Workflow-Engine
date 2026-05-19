# Operations Guide

How to run InvertedSoftware.WorkflowEngine in production. Pair this with the
[README](README.md) (which covers what the library does) and the
[CHANGELOG](CHANGELOG.md) (which lists tradeoffs and known issues).

---

## Mental model

* **Producers** call `FrameworkManager.AddFrameworkJobAsync(jobName, message)`.
  They publish to the Main queue and return as soon as the broker accepts.
* **Consumers** host one `Processor` per job they want to consume. Each
  `Processor` opens an `await foreach` on the broker, dispatches messages to
  `IExecutor` (sequential or pipelined), and acks/nacks per the outcome.
* **Brokers** carry four logical queues per job: Main (work), Error
  (`WorkflowErrorMessage`), Poison (failed message bodies), Completed
  (success notifications, optional).

The engine itself is stateless — every consumer instance can be killed and
restarted at any time. Anything you need to survive a crash must live in
durable state outside the engine (the broker, your database, your blob store).

---

## Wiring up a consumer (recommended pattern)

```csharp
using InvertedSoftware.WorkflowEngine;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Hosting;
using InvertedSoftware.WorkflowEngine.Queue.RabbitMq;        // or Kafka / AzureServiceBus
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;

var builder = Host.CreateApplicationBuilder(args);

// 1. Engine options from configuration.
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("WorkflowEngine"));

// 2. Step registry. Register every step type this consumer can run.
builder.Services.AddSingleton<IStepFactory>(_ => new TypeNameStepFactory()
    .Register<CopyFiles>(typeof(CopyFiles).FullName!, () => new CopyFiles())
    .Register<RenameFiles>(typeof(RenameFiles).FullName!, () => new RenameFiles()));

// 3. Queue provider — choose ONE.
builder.Services.AddSingleton<IQueueProvider>(_ => new RabbitMqQueueProvider(new RabbitMqOptions
{
    ConnectionStrings = { builder.Configuration["WorkflowEngine:RabbitMq:Uri"]! },
    Mappings = { /* … */ },
}));

builder.Services.AddSingleton<IMessageSerializer>(_ => new JsonMessageSerializer());

// 4. Host bundles everything; constructor wires the static FrameworkManager facade.
builder.Services.AddSingleton<WorkflowEngineHost>(sp => new WorkflowEngineHost(
    queueProvider: sp.GetRequiredService<IQueueProvider>(),
    serializer: sp.GetRequiredService<IMessageSerializer>(),
    stepFactory: sp.GetRequiredService<IStepFactory>(),
    options: sp.GetRequiredService<IOptions<EngineOptions>>().Value,
    loggerFactory: sp.GetRequiredService<ILoggerFactory>()));

// 5. Run the consumer as a hosted service so it shuts down gracefully with the host.
builder.Services.AddHostedService(sp =>
    new WorkflowConsumerHostedService(sp.GetRequiredService<WorkflowEngineHost>(), "ExampleJob"));

// 6. Health endpoints for Kubernetes readiness/liveness probes.
builder.Services.AddHealthChecks()
    .AddCheck("workflow-queue", new WorkflowQueueHealthCheck(
        builder.Services.BuildServiceProvider().GetRequiredService<WorkflowEngineHost>(), "ExampleJob"),
        tags: new[] { "ready" });

var app = builder.Build();
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });
app.MapHealthChecks("/health/live");
await app.RunAsync();
```

---

## Observability

### Distributed tracing (OpenTelemetry)

The engine emits W3C TraceContext spans on `ActivitySource`
`"InvertedSoftware.WorkflowEngine"`. The producer's span propagates through
message headers; the consumer extracts the `traceparent` header and starts a
linked child span.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource(InvertedSoftware.WorkflowEngine.Queue.Telemetry.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
```

Activity names: `workflow.publish` (producer), `workflow.consume` (consumer),
`workflow.step` (per-step child).

Activity tags: `workflow.job_name`, `workflow.job_id`, `workflow.step_name`,
`workflow.step_outcome`, `messaging.message.id`, `messaging.system`,
`messaging.operation`.

### Metrics

The engine emits metrics on `Meter` `"InvertedSoftware.WorkflowEngine"`:

| Instrument | Type | Unit | Tags | Meaning |
|---|---|---|---|---|
| `wf.jobs.processed` | counter | `{job}` | job, outcome | Total jobs processed (terminal events). |
| `wf.jobs.in_flight` | up-down counter | `{job}` | job | Jobs executing now on this consumer. |
| `wf.job.duration` | histogram | `s` | job, outcome | End-to-end job duration. |
| `wf.step.duration` | histogram | `s` | job, step, outcome | Per-step duration. |
| `wf.errors` | counter | `{error}` | job, step, kind | Engine-level errors. |

`outcome` values: `complete`, `error`, `cancelled`, `deserialization_error`,
`timeout`, `skipped` (idempotency).

`kind` values: `provider`, `deserialization`, `timeout`, `step_failure`.

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter(InvertedSoftware.WorkflowEngine.Queue.Telemetry.MeterName)
        .AddPrometheusExporter());
```

### Suggested Grafana panels

* **Queue depth** — `wf.jobs.in_flight` (gauge over time).
* **Throughput** — `rate(wf.jobs.processed{outcome="complete"}[5m])`.
* **Error rate** — `sum(rate(wf.jobs.processed{outcome!="complete"}[5m])) / sum(rate(wf.jobs.processed[5m]))`.
* **P95 step duration** — `histogram_quantile(0.95, sum(rate(wf.step.duration_bucket[5m])) by (le, step))`.
* **P95 job duration** — same as above with `wf.job.duration`.
* **Backpressure** — broker-native queue depth, joined with `wf.jobs.in_flight`.

### Structured logs

The engine logs through `Microsoft.Extensions.Logging.ILogger` with
source-generated `LoggerMessage` definitions. Configure your sink to capture
the event IDs you care about; key ones:

* `1000` Framework started
* `1001` Framework stopping
* `2001` Job completed
* `2002` Job timed out
* `2003` Job failed
* `3002` Step failed
* `3003` Step skipped (idempotency)
* `4001` Publish failed

---

## At-least-once delivery and idempotency

The engine guarantees at-least-once delivery, not exactly-once. Steps WILL
re-run after a consumer crash, after a broker redelivery, or after a job-level
`OnError=RetryJob`. Plan for it.

### Pattern A: naturally idempotent steps

For read-mostly or set-not-add database operations, retries are safe. Nothing
to configure.

### Pattern B: external dedup with `IIdempotencyStore`

For non-idempotent side effects (sending emails, charging cards, calling
external HTTP APIs), supply an `IIdempotencyStore` implementation:

```csharp
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    public async ValueTask<bool> TryClaimAsync(IdempotencyClaim claim, CancellationToken ct)
    {
        // SET key value NX EX 86400 — returns true if we won the race
        return await _redis.StringSetAsync(claim.Key, "claimed", expiry: TimeSpan.FromDays(1), When.NotExists);
    }

    public async ValueTask MarkCompletedAsync(IdempotencyClaim claim, CancellationToken ct)
    {
        await _redis.StringSetAsync(claim.Key, "completed", expiry: TimeSpan.FromDays(7));
    }
}

// Wire it:
var host = new WorkflowEngineHost(..., idempotencyStore: new RedisIdempotencyStore(redis));
```

The engine consults the store BEFORE invoking each step. A step whose claim
returns "already completed" is logged with event ID `3003` and skipped; the
job continues with the next step. This makes the at-least-once delivery
effectively exactly-once for steps you mark as completed.

---

## Retry budgets vs broker lock timeouts

`OnError=RetryJob` sleeps `WaitBetweenRetriesMilliseconds` between attempts.
That sleep happens WHILE the consumer holds the message lock:

| Broker | Lock behavior | Mitigation |
|---|---|---|
| RabbitMQ | No lock timeout; message is in-flight until ack/nack or channel close. | Safe for any retry budget within the channel's lifetime. |
| Kafka | No lock; offsets only commit on `Ack`. Long retries hold up the partition. | Tune `max.poll.interval.ms` ≥ total retry budget. |
| Azure Service Bus | PeekLock has a 5-minute default; expired locks redeliver. | Set `MaxAutoLockRenewalDuration` ≥ total retry budget. |

**Rule of thumb:** `MaxRunTimeMilliseconds` ≥ Σ(step_durations) +
Σ(retries × WaitBetweenRetriesMilliseconds). The engine sets
`ConsumeOptions.AckTimeout = MaxRunTimeMilliseconds` automatically.

---

## Workflow.xml deployment

Every consumer needs an identical `Workflow.xml`. The producer needs it too
(it reads `MessageClass` to set the message-type header). Three patterns:

### Inline-with-image (recommended for small fleets)

Ship `Workflow.xml` inside your consumer container image. Roll out new
versions through your normal Docker / Kubernetes deployment process. Use a
blue/green deployment so old and new versions never publish to the same
queue simultaneously.

### Centralised config service

Pull `Workflow.xml` from Consul / Azure App Configuration / S3 on startup.
Refresh on a polling interval or via webhook. Pros: one source of truth.
Cons: now the engine has a startup dependency on the config service.

### Embedded resource (single-deploy)

If producer and consumer ship as one binary, embed `Workflow.xml` as a
resource and read it via `Assembly.GetManifestResourceStream`. Set
`EngineOptions.FrameworkConfigLocation` to a temp-extracted copy.

---

## Adding a new step type — canary rollout

Adding `MyCompany.Steps.NewStep` to an existing workflow:

1. **Deploy code first.** Roll out the new consumer image to all instances.
   Verify each is healthy and reports the new type registered (via the step
   factory; failed `StepFactory.GetStep` calls log event ID
   `WorkflowStepException`).
2. **Update Workflow.xml.** Now the consumers know how to handle the new step.
   Producers and consumers reading the new XML start producing/consuming
   messages that reference it.
3. **Never deploy code AFTER XML.** Consumers that haven't picked up the new
   code will dead-letter messages referencing the new step type.

Health check tip: extend `WorkflowQueueHealthCheck` to also instantiate every
declared step on startup, so the consumer fails liveness immediately if a
type is missing.

---

## Scaling out per broker

### RabbitMQ

* Run many consumers against the same queue (`work-stealing` model). The
  broker round-robins messages.
* Set `prefetch_count` low (16 default in the provider) so slow consumers
  don't hoard messages.
* For HA: clustered RabbitMQ with mirrored queues, or quorum queues.
* For geo-failover: list multiple connection strings in
  `RabbitMqOptions.ConnectionStrings`. The provider tries the next on connect
  failure.

### Kafka

* Number of consumer instances ≤ number of partitions for the Main topic.
  Adding instances beyond partition count means idle consumers.
* Per-job ordering: the engine keys messages by `CorrelationId` (= JobID),
  so all messages for one job hash to one partition. Use this if you care
  about in-job ordering.
* For replay: bump `group.id` to get a new consumer group with
  `auto.offset.reset=earliest`.

### Azure Service Bus

* Run many consumer instances against the same queue (competing-consumer).
* Sessions enable strict ordering per `SessionId` at the cost of throughput;
  off by default in this engine.
* Use `Premium` tier for predictable latency and geo-DR.
* Auto-lock-renewal: set `MaxAutoLockRenewalDuration` to a duration that
  comfortably exceeds `MaxRunTimeMilliseconds`.

---

## Rolling deployments

The engine's shutdown sequence is:

1. `WorkflowConsumerHostedService.StopAsync` calls
   `Processor.StopFrameworkAsync(softExitOnShutdown: true)` by default.
2. The consumer stops pulling new messages immediately.
3. In-flight jobs continue running to natural completion and ack normally.
4. The hosted service returns; the host shuts down.

For Kubernetes:

* Set `terminationGracePeriodSeconds` ≥ your longest expected job duration
  (default 30s is usually too short for batch workflows).
* `preStop` hook isn't necessary; the engine handles SIGTERM via the
  hosted-service token.
* Consider `lifecycle.preStop` with `sleep 5` if your service mesh needs
  time to drop the pod from the load balancer first.

For hard shutdowns (process kill, OOM), in-flight messages stay locked on
the broker and redeliver after the lock timeout — to another consumer that
will start the job over. Idempotency saves you here; see "At-least-once
delivery" above.

---

## Failure modes and remediation

| Symptom | Likely cause | Remediation |
|---|---|---|
| Many `wf.errors{kind="deserialization"}` | Producer changed message schema without coordination. | Roll back producer; or ship consumers that understand both old and new shapes via versioned message types; or drain the queue. |
| Growing Poison queue | Same message dead-lettering repeatedly. | Inspect the body, fix the data or the step logic. Republish from Poison once fixed. |
| `wf.jobs.in_flight` saturates and queue depth grows | Consumer thread pool exhausted. | Increase `FrameworkMaxThreads`; add more consumer instances; or check for slow steps via `wf.step.duration`. |
| `wf.errors{kind="timeout"}` spikes | `MaxRunTimeMilliseconds` too tight, or step hung on external dependency. | Increase deadline; add per-step timeouts inside step implementations; check downstream service. |
| Repeated step executions after consumer restart | At-least-once + non-idempotent step. | Implement `IIdempotencyStore`; see "Pattern B" above. |
| `wf.errors{kind="provider"}` | Broker unavailable or auth failed. | Check broker health; check `connection_strings` failover order; consult provider logs. |

---

## Operational checklist before going live

* [ ] OpenTelemetry tracing exporter configured to your APM.
* [ ] Metrics exporter scraped by Prometheus / your time-series store.
* [ ] Structured logs going to your log aggregator (Loki / Splunk / Datadog).
* [ ] Health endpoints (`/health/ready`, `/health/live`) wired to K8s probes.
* [ ] `MaxRunTimeMilliseconds` set with enough headroom for slowest expected step + retries.
* [ ] `IIdempotencyStore` implemented (Redis / SQL / DynamoDB) for any step with non-idempotent side effects.
* [ ] Workflow.xml change procedure documented and rehearsed.
* [ ] Dead-letter queue monitored (alert when message count grows).
* [ ] Broker connection-string failover list configured.
* [ ] `terminationGracePeriodSeconds` ≥ longest expected job duration.
* [ ] Load test conducted that covers expected peak + 2× margin.
