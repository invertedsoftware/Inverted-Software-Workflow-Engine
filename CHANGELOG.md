# Changelog

## 2.2.0 — Restore v1 multi-tier queue failover

Brings back the resilience feature from the original 2010 engine: when a job
declares multiple `<Queue>` entries in `Workflow.xml`, producers iterate them
forward (primary, then fallbacks) and consumers iterate in reverse (last
tier first, preferring whichever has pending work).

### Added

* **`LogicalQueue.Tier`** field with mapping-key suffix convention. Tier 0
  uses the bare `"JobName:Kind"` form so single-queue configs require no
  changes; tier > 0 uses `"JobName#N:Kind"`.
* **`IQueueProvider.ConsumeAsync(jobName, options, tier, ct)`** and
  **`CheckHealthAsync(jobName, tier, ct)`** accept the tier parameter (default 0).
* **`FrameworkManager.PublishAsync`** iterates declared tiers forward on
  `QueueUnavailableException`, falling over to the next tier. Non-transient
  exceptions (e.g. missing mapping) still surface immediately so config
  problems aren't masked.
* **`Processor` tier selection**: at startup and on each rebalance tick, picks
  the highest-tier queue with pending messages; falls back to tier 0 (primary)
  if no tier reports work. The currently-bound tier is logged at Information
  (event ID 5000); secondary publishes (Error/Poison/Completed) route to the
  same tier so a message's lifecycle stays colocated.
* **`EngineOptions.TierRebalanceIntervalSeconds`** (default 30, minimum 5)
  controls how often a multi-tier consumer re-evaluates tiers so it can switch
  back to the primary when it recovers.
* **`MultiTierFailoverTests`** (×3) — mapping-key format, producer forward
  failover under primary outage, consumer binding to the highest-tier queue
  with pending work.

### Why

This was a v1 feature I dropped in the .NET 10 rewrite, on the (wrong) theory
that broker-connection-level failover replaced it. It doesn't: broker
failover handles a downed RabbitMQ host; multi-queue failover handles a
downed *logical destination* (e.g. a quota-exceeded queue, a region failure,
a planned drain). They compose — a multi-tier deployment can use both.

### Migration

No code changes required for single-queue jobs (the common case). To use
the feature, declare multiple `<Queue>` entries in `Workflow.xml` and add
provider mappings keyed `"JobName#1:Main"`, `"JobName#1:Error"`, etc. for
each non-primary tier.

## 2.1.1 — Bug sweep and architectural cleanup

Targeted bug fixes and a few architectural improvements found during a deep
audit. No breaking public-API changes.

### Fixed

* **Kafka offset watermark never advanced**: `OffsetWatermark.PartitionState.NextExpected`
  was initialised to `Offset.Beginning` (-2), so the contiguous-walk loop never
  matched any real acked offset. `StoreOffset` was effectively pinned to "begin
  from start" forever — consumer rebalance / restart would re-read every message
  on the partition. Fix: `RecordDelivered(tpo)` is called from the consume loop
  on first delivery per partition, seeding the baseline to the real first offset.
  Subsequent acks now correctly advance and commit a strictly-increasing watermark.
* **Azure Service Bus message lock expired on long-running steps.** The previous
  implementation used `ServiceBusReceiver`, which does not auto-renew message
  locks. Steps running longer than the queue's lock duration (default 30s, max
  5 min) lost their lock; the broker redelivered while the original consumer was
  still processing, causing duplicate work. Fix: migrated the consume path to
  `ServiceBusProcessor` with `MaxAutoLockRenewalDuration` automatically sized to
  `ConsumeOptions.AckTimeout × 1.2`. The callback-based processor is bridged to
  `IAsyncEnumerable<IReceivedMessage>` via a bounded channel so the engine-side
  API is unchanged.

### Performance

* **O(N) per-message reflection scan removed.** Every provider's `DeserializeBody`
  and `TypeNameStepFactory.GetStep` previously iterated `AppDomain.GetAssemblies()`
  on every call when a type wasn't already resolved. With 100+ loaded assemblies
  (typical ASP.NET Core app), this added hundreds of reflection lookups per
  message on the hot path. Extracted to a shared `TypeNameResolver` in
  `Queue.Abstractions` that caches every lookup in a `ConcurrentDictionary`.
  Each distinct type name pays the scan once per process lifetime.

### Architecture

* **`TypeNameResolver`** added to `Queue.Abstractions`. Single source of truth
  for converting `MessageType` header strings to `Type` instances. Includes
  a final-fallback DLL probe of the entry assembly's directory for plugin
  scenarios where the message class lives in a sibling assembly that hasn't
  been touched yet.

### Tests

23 unit tests passing (was 15). New cases:
* `TypeNameResolverTests` (4 tests) — caching, unknown types, error messages.
* `StepFactoryTests` (4 tests) — explicit registration wins, type-name fallback,
  unknown-step error, non-`IStep`-type rejection.

## 2.1.0 — Production-readiness pass

Builds on 2.0.0 with the operational primitives a distributed deployment
actually needs. No breaking changes.

### Added

* **Distributed tracing.** `System.Diagnostics.ActivitySource` named
  `"InvertedSoftware.WorkflowEngine"`. Producer-side `workflow.publish` spans
  inject W3C `traceparent` into `MessageHeaders`; consumer-side
  `workflow.consume` spans extract it and link to the parent context.
  Per-step `workflow.step` child spans. Compatible with OpenTelemetry .NET out
  of the box.
* **Metrics.** `System.Diagnostics.Metrics.Meter` named
  `"InvertedSoftware.WorkflowEngine"`. Counters (`wf.jobs.processed`,
  `wf.errors`), histograms (`wf.job.duration`, `wf.step.duration`), up-down
  counter (`wf.jobs.in_flight`). Tagged with `job`, `step`, `outcome`, `kind`.
* **Structured logging.** Source-generated `LoggerMessage` partial methods with
  stable event IDs (1xxx framework lifecycle, 2xxx job lifecycle, 3xxx step
  lifecycle, 4xxx publish).
* **`IIdempotencyStore`** optional hook. Engine consults the store before
  invoking each step; already-completed claims cause the step to be skipped
  and logged with event ID 3003. Default `NoOpIdempotencyStore` allows all;
  `InMemoryIdempotencyStore` for tests. Implementations targeting Redis / SQL
  / Cosmos belong in consumer code.
* **`WorkflowConsumerHostedService : BackgroundService`** — drop-in hosted
  service that runs a `Processor` against a named job and gracefully
  soft-stops on host shutdown.
* **`WorkflowQueueHealthCheck : IHealthCheck`** — wraps
  `IQueueProvider.CheckHealthAsync` for ASP.NET Core / Kubernetes probes.
  Healthy / Degraded / Unhealthy semantics with detailed `Data` payload.
* **`Telemetry`** static class in `Queue.Abstractions` carrying stable string
  constants (activity names, tag keys, metric names, header names).
* **`OPERATIONS.md`** — production-deployment guide covering observability
  wiring, idempotency patterns, retry budget vs lock timeout interaction,
  per-broker scaling, rolling deployments, failure-mode triage.

### Fixed

* **`PipelinedExecutor` wrong-waiter bug** (documented as known issue in 2.0.0).
  Each job submission now carries a unique `JobInstanceId`; the terminal
  block routes results back to the submitting caller via a per-job
  `TaskCompletionSource`. Callers no longer receive other jobs' results
  under concurrency. New test `PipelinedExecutorConcurrencyTests` pins this.

### Changed

* `WorkflowEngineHost` constructor accepts optional `ILoggerFactory` and
  `IIdempotencyStore`. Defaults (`NullLoggerFactory.Instance`,
  `NoOpIdempotencyStore.Instance`) preserve existing behaviour for callers
  that don't opt in.
* Internal executor constructors take `WorkflowEngineHost` instead of
  loose dependencies — cleaner composition.
* `AzureServiceBusQueueProvider` consume path computes a lock-budget hint
  from `ConsumeOptions.AckTimeout`. (The receiver API doesn't directly expose
  lock-renewal; documented as a future hardening pass.)

### Tests

13 unit tests passing:
* `ProcessorJobDeepCopyTests` (×2) — deep-copy correctness, event-subscriber isolation.
* `InMemoryQueueProviderTests` (×2) — publish/consume roundtrip, atomic batch.
* `ProcessorSoftExitTests` (×2) — soft-exit lets in-flight finish; hard-exit cancels.
* `TracingTests` (×2) — producer injects `traceparent`; consumer's span links to producer's traceId.
* `IdempotencyTests` (×2) — claimed-completed steps are skipped; no-op store allows all.
* `HealthCheckTests` (×2) — healthy when provider OK; unhealthy when Main queue down.
* `PipelinedExecutorConcurrencyTests` (×1) — 20 concurrent jobs each get their own result.

## 2.0.0

Complete rewrite of the workflow engine. Targets .NET 10; introduces a pluggable
queue provider abstraction (RabbitMQ, Kafka, Azure Service Bus).

### Added

* `IQueueProvider`, `IReceivedMessage`, `LogicalQueue`, `MessageHeaders`,
  `IMessageSerializer` abstractions in a new
  `InvertedSoftware.WorkflowEngine.Queue.Abstractions` package.
* `JsonMessageSerializer` (System.Text.Json) as the default serializer.
* `InMemoryQueueProvider` — `Channel<T>`-backed, atomic batch publish, used by
  the console sample and unit tests.
* `RabbitMqQueueProvider` with publisher confirms, `tx.select` batch atomicity,
  failover across multiple connection strings.
* `KafkaQueueProvider` with `acks=all` + idempotence, transactional producer for
  atomic batches, in-order watermark offset commits, partition keys derived from
  `CorrelationId` (= `JobID`) for per-job ordering.
* `AzureServiceBusQueueProvider` using peek-lock receive,
  `CompleteMessageAsync` / `AbandonMessageAsync` / `DeadLetterMessageAsync`,
  same-namespace batch atomicity.
* `WorkflowEngineHost` as the new composition root — bundles provider,
  serializer, step factory, and engine options.
* `IStepFactory` interface; `TypeNameStepFactory` default implementation does
  registry lookup followed by assembly-qualified type-name fallback. The v1
  factory silently failed for steps defined outside the engine assembly.
* `StepBase` abstract helper that calls `ct.ThrowIfCancellationRequested()`
  before delegating to `RunStepCore`.
* `StepExecutionMode` enum (`Synchronous`, `FireAndForget`).
  `FrameworkStepRunMode` (`STA`/`MTA`) preserved as `[Obsolete]` alias; the
  Workflow.xml parser accepts both spellings.
* Cross-platform console sample (`InvertedSoftware.WorkflowEngine.Sample.Console`).

### Changed

* All projects target `net10.0` (`net10.0-windows` for the WPF sample). SDK-style
  `csproj`. `packages.config` removed.
* `IStep.RunStep` signature now takes `(IWorkflowMessage message, CancellationToken cancellationToken)`.
  **Breaking change** — user-authored step implementations must add the
  parameter.
* `FrameworkManager.AddFrameworkJob` is now async-first
  (`AddFrameworkJobAsync`). The synchronous wrapper is preserved.
* `Processor` is async-first (`StartFrameworkAsync`, `StopFrameworkAsync`),
  `IDisposable`, and constructed via `WorkflowEngineHost.CreateProcessor()`.
* `EngineConfiguration` static class replaced by `EngineOptions` POCO.
  Engine no longer depends on `Microsoft.Extensions.Configuration`.
* `WorkflowConfiguration` is non-static; takes `EngineOptions` in its constructor;
  uses `XDocument` (LINQ-to-XML) instead of `XmlDocument` + XPath. Resolves
  relative paths against `AppContext.BaseDirectory`.
* `MessageQueueType.Transactional` semantics: ack on successful step completion.
  `NonTransactional`: provider auto-acks on receive (fire-and-forget consumption).
* Queue attributes in `Workflow.xml` (`MessageQueue`, `ErrorQueue`,
  `PoisonQueue`, `CompletedQueue`) hold **logical names**; the provider's
  `Mappings` dictionary resolves them to broker resources.

### Removed

* `System.Messaging` (MSMQ) and all related code paths.
  `RunRemoteTransactionalFramework` and the `PeekCompleted` async path
  collapsed into a single `IAsyncEnumerable<IReceivedMessage>` loop.
* `System.Transactions.TransactionScope` usage in `QueueOperationsHandler` and
  `Processor`. Atomic multi-publish is now `IQueueProvider.PublishBatchAsync`.
* `BinaryFormatter.Serialize` / `Deserialize` in `ProcessorJob.Clone()`.
  Replaced by hand-written `DeepCopy()` methods on `ProcessorJob`,
  `ProcessorStep`, `ProcessorQueue`. `ICloneable` interface dropped.
* `[Serializable]` attribute from `ProcessorJob`, `ProcessorStep`,
  `ProcessorQueue`, `ExampleMessage`, `WorkflowErrorMessage`.
* `Thread.Abort()`-based job-timeout enforcement. `JobTimer.cs` deleted.
  Per-job `MaxRunTimeMilliseconds` deadline is enforced via a linked
  `CancellationTokenSource` whose token rides through `IExecutor`,
  `StepExecutor`, and `IStep.RunStep`.
* `static Semaphore Processor.pool` (latent bug — shared across all
  `Processor` instances). Replaced by instance `SemaphoreSlim _pool`.
* `ProcessorQueueChanged` event-based failover swap; failover is now the
  provider's responsibility.
* `FrameworkManager.GetActiveQueue` iteration. Each provider's options take
  a list of connection strings / destinations and try them in order.
* `MsmqPoisonMessageException`.
* `WindowsIdentity.Impersonate()` (deprecated in .NET 5+). `Impersonation` now
  uses `WindowsIdentity.RunImpersonated(SafeAccessTokenHandle, Action)` via a
  source-generated `[LibraryImport]` P/Invoke and `SafeAccessTokenHandle`.
  Class annotated `[SupportedOSPlatform("windows")]`. Calling steps with
  `RunAsUser` on non-Windows now throws `PlatformNotSupportedException`.
* `RijndaelManaged`, `RNGCryptoServiceProvider`, `PasswordDeriveBytes`
  obsolete primitives. `RijndaelEnhanced` now uses `Aes.Create()`,
  `RandomNumberGenerator`, and `Rfc2898DeriveBytes.Pbkdf2`. The on-disk
  salt/IV/ciphertext format is preserved so existing encrypted Workflow.xml
  attributes still decrypt.

### Known issues carried forward from v1

* **`PipelinedExecutor` can deliver results to the wrong waiter under concurrency.**
  `RunFrameworkJobAsync` posts a `PipelineInfo` into the head `TransformBlock` and
  then awaits `ReceiveAsync` on the tail block. When more than one job is in flight,
  the tail emits results in completion order, not submission order — so the waiter
  for job A can receive job B's result. Affects the `NotifyComplete` notification
  and which job's `RunStatus` is read after the pipeline. Fix requires correlating
  responses to senders (per-job `TaskCompletionSource` keyed by a unique
  JobInstanceId), which is a future change. `SequentialExecutor` is unaffected.

### Tradeoffs documented

* **At-least-once delivery.** Steps must be idempotent. The existing
  `RetryJob`/`RetryStep` semantics already assumed this.
* **Atomic multi-publish is broker-scoped.** `IQueueProvider.PublishBatchAsync`
  is atomic only when all destinations resolve to a single broker instance.
  Cross-namespace atomicity is impossible on any provider; the engine
  documents and accepts this loss.
* **Kafka ordering depends on partition keys.** The Kafka publisher uses
  `MessageHeaders.CorrelationId` (= `JobID`) as the partition key so messages
  for the same job land on the same partition. Nack-with-requeue on Kafka
  stalls the partition until the message is resolved (Kafka cannot re-insert
  a message before its current offset).
* **Cooperative cancellation only.** A user step that ignores `CancellationToken`
  and loops forever cannot be killed; there is no `Thread.Abort()` replacement.

## 1.x

Pre-rewrite. Targeted .NET Framework 4.5, MSMQ-only, WPF demo.
