# Architecture Decision Records — Rockets service

Each record states the context, the decision I made, the pattern it follows, the alternatives I considered, and the
consequences I accepted. The reasoning is AI-assisted, but the decisions are mine. The service is small enough that I could keep all the reasoning in my head, 
but I wrote it down to show my thought process and to help me remember it later.

| # | Decision | Pattern |
|---|---|---|
| [001](#adr-001-architecture-overview) | Architecture overview | Event-driven architecture, layered architecture |
| [002](#adr-002-event-driven-processing-with-pulsar) | Event-driven processing with Pulsar | Asynchronous messaging, competing consumers with key affinity |
| [003](#adr-003-ingest-endpoint-and-response-codes) | Ingest endpoint and response codes | Accept-and-acknowledge (202) |
| [004](#adr-004-message-ordering) | Message ordering | Resequencer |
| [005](#adr-005-duplicate-messages) | Duplicate messages | Idempotent consumer |
| [006](#adr-006-concurrency-control) | Concurrency control | Pessimistic locking (row lock) |
| [007](#adr-007-persistence) | Persistence | Event store with snapshots |
| [008](#adr-008-outbox-when-pulsar-is-unavailable) | Outbox when Pulsar is unavailable | Store-and-forward (outbox), circuit breaker |
| [009](#adr-009-back-pressure) | Back-pressure | Bounded buffer, negative acknowledgement |
| [010](#adr-010-unprocessable-messages) | Unprocessable messages | Invalid-message store |
| [011](#adr-011-domain-rules) | Domain rules | — |
| [012](#adr-012-read-api) | Read API | Read model (snapshot table) |
| [013](#adr-013-observability) | Observability | OpenTelemetry metrics and logs |
| [014](#adr-014-verification) | Verification | — |

## Requirements from the brief

| Requirement | Addressed in |
|---|---|
| Messages arrive out of order | ADR-004 |
| Messages can arrive more than once (at-least-once delivery) | ADR-005 |
| Any non-2xx response makes the sender resend the message | ADR-003, ADR-008 |
| The default sender runs 3 requests in parallel | ADR-006 |
| Return the current state of one rocket, and of all rockets with sorting | ADR-012 |
| Assessment: data structures, concurrency, persistence, error handling, scale | ADR-002 to ADR-009 |
| Assessment: verification | ADR-014 |

---

## ADR-001: Architecture overview

**Context.** The service receives rocket messages over HTTP from a sender that delivers them out of order, at least once,
and resends on any non-2xx response. It must expose each rocket's current state over a REST API and be able to scale.

**Decision.** An event-driven .NET 10 service with two roles in one process:
- an **ingest API** (`POST /messages`) that validates each message and publishes it to **Apache Pulsar**;
- a **background worker** that consumes the messages from Pulsar and applies them to each rocket in **PostgreSQL**.

The code uses classic layers; namespaces match folders and dependencies point one way:

| Folder | Responsibility |
|---|---|
| `Controllers/` | HTTP routes and status codes |
| `Services/` | `RocketService` (ingest and queries), `RocketEventProcessor` (applies one event), `RocketMessageMapper` (JSON to event), `RocketMetrics` |
| `Messaging/` | Pulsar publisher, Pulsar consumer (the worker), outbox relay |
| `Repositories/` | `RocketRepository` (events and state), `OutboxRepository`, `UnprocessableMessageStore`, each behind an interface |
| `Events/` | One class per event type; the class name is the `messageType` value |
| `Domain/` | `Rocket` (ordering, duplicates, launch and explosion rules) and `RocketSnapshot` |
| `Requests/`, `Enums/`, `Configuration/` | API classes, shared enums, options validated at startup |

`docker compose up` starts six containers: the service (`:8088`), PostgreSQL, Pulsar (standalone mode), the Rockets UI
(`:3088`), the Aspire Dashboard (`:18888`) and pgweb, a read-only PostgreSQL viewer (`:8089`). The service waits until
PostgreSQL and Pulsar are healthy; it does not depend on the other three.

**Pattern.** Event-driven architecture; layered architecture; repository pattern; background worker.

**Alternatives considered.**
- *Synchronous processing in the HTTP request* (in memory with SQLite, my earlier version). Simpler, with immediate
  reads, but the sender waited for every disk write, storage failures reached the sender, and the state lived in one
  process, so scaling required routing each rocket to the same instance.
- *Feature folders.* Better with many features; with one feature, layers are easier to navigate.

**Consequences.**
- Ingest and processing are decoupled: ingest keeps working while processing is slow or stopped.
- The service can run as several instances (ADR-002, ADR-006).
- Six containers instead of one, and reads are eventually consistent (ADR-003).

## ADR-002: Event-driven processing with Pulsar

**Context.** I wanted ingest to be independent of processing, processing to scale across instances, and each rocket's
events to stay with one consumer.

**Decision.**
- Ingest publishes the raw JSON to the topic `rocket-events` with the **channel as the message key**.
- The worker consumes with a **Key_Shared** subscription, four consumers per instance. Pulsar delivers all messages with
  the same key to the same consumer, so different rockets are processed in parallel.
- A message is **acknowledged only after** its database transaction has committed. On failure it is negatively
  acknowledged and Pulsar redelivers it after one second.
- Producer batching is off: a batch with several keys would be delivered to a single consumer.


**Pattern.** Asynchronous messaging; competing consumers with key affinity (Key_Shared); at-least-once delivery with
acknowledgement after commit.

**Alternatives considered.**
- *Kafka* with the channel as the partition key: equivalent ordering, but adding consumers requires more partitions.
- *RabbitMQ* with a consistent-hash exchange by channel: equivalent; it is the broker describes using.
- *A Shared subscription:* rejected, because it spreads one rocket's messages over several consumers.

**Consequences.**
- Measured: the worker processes about 2,300 events per second on one instance; while the worker was broken by a bug, all
  100,000 events waited in Pulsar and were processed in 43 seconds after the fix.
- Pulsar preserves the order in which messages are published, but the sender posts in parallel, so messages are already
  out of order before publishing. Resequencing is still required (ADR-004).
- Pulsar redeliveries are a second source of duplicates (ADR-005).

## ADR-003: Ingest endpoint and response codes

**Context.** The sender resends any message that does not get a 2xx response. A wrong status code either loses a message
(2xx when it was not stored) or causes endless resends (non-2xx for a message that can never succeed).

**Decision.** `POST /messages` reads the raw body, validates it, and stores it (in Pulsar, or in the outbox when Pulsar is
unavailable). It does not apply it.

| Outcome | Response | `X-Ingest-Result` |
|---|---|---|
| Valid, stored in Pulsar | `202` | `Accepted` |
| Valid, Pulsar unavailable, stored in the outbox (ADR-008) | `202` | `AcceptedToOutbox` |
| Invalid (malformed JSON, unknown type, invalid values) | `202` | `Unprocessable` (ADR-010) |
| Neither Pulsar nor the outbox could store it | `503` + `Retry-After: 2` | — |

A `202` has no body; the outcome is in the `X-Ingest-Result` header.

**Pattern.** Accept-and-acknowledge: `202 Accepted` means the message is stored durably and will be processed.

**Alternatives considered.**
- *`400` for invalid messages:* rejected, because the sender would resend them indefinitely, a 400 would cause the sender to resend the malfunctioned message.
- *`500` when storage fails:* rejected in favour of `503` + `Retry-After`, which states that the failure is temporary.
- *A JSON body on `202`:* Provide connection errors and resends from the test program; an empty body caused none.

**Consequences.**
- `202` means "stored", not "applied": a `GET` immediately after a `POST` can still show the previous state.
- The response cannot report `Applied`, `Buffered` or `Duplicate`; those are decided later by the worker.
- Each `202` waits for Pulsar to store the message.

## ADR-004: Message ordering

**Context.** The brief states that the message number is the order within a channel and that messages arrive out of
order. Mission changes and the explosion depend on order, so messages cannot be applied in arrival order.

**Decision.**
- Each rocket's messages are applied strictly by message number.
- The launch starts the sequence: until a rocket has launched, only its `RocketLaunched` message can be applied, and its
  number N (normally 1) is the start. After that, only `lastApplied + 1` can be applied; earlier-arriving messages wait.
  Waiting messages numbered below N are dropped.
- Waiting messages are stored in the `events` table. For each new event, the worker rebuilds the rocket from its saved
  state plus its waiting events, applies the next ones in order, and saves the result.

**Pattern.** Resequencer (a sorted buffer per rocket: `SortedDictionary` keyed by message number).

**Alternatives considered.**
- *Apply in arrival order:* incorrect for mission changes and explosions.
- *"Highest number wins" per field, without a buffer:* no stall on a missing message, but needs a growing set of seen
  numbers for duplicates.
- *Start every rocket at #1:* a stream that starts later would never be applied; the launch number is used instead.


## ADR-005: Duplicate messages

**Context.** Duplicates come from the sender (at-least-once delivery, resends after timeouts or failures), from Pulsar
(redelivery of a message processed but not yet acknowledged), and from rerunning the test program on the same data.

**Decision.**
- A message is a duplicate if its number is at or below the rocket's `lastApplied`, or the same number is already waiting.
  It is acknowledged but not stored and not applied.
- The check runs under the rocket's row lock (ADR-006), so two copies processed at the same time cannot both pass it.
- The `events` primary key `(channel, message_number)` is a second safeguard.

**Pattern.** Idempotent consumer.

**Alternatives considered.**
- *A set of seen message IDs per rocket:* unnecessary, because strict ordering makes one number (`lastApplied`) enough.
- *Storing every delivery:* it would make the event store contain the same event twice and corrupt any replay. A separate
  delivery log would be the right place if delivery auditing were required.

**Consequences.**
- Each distinct event is stored exactly once
- Duplicates are visible as the `Duplicate` result in metrics and logs.
- Assumption: one sender per channel, so the same number always has the same content. A different message with a used
  number is treated as a duplicate (the first one wins).

## ADR-006: Concurrency control

**Context.** The sender runs requests in parallel, the worker runs several consumers, and the service can run as several
instances. Two events for the same rocket must never be applied at the same time.

**Decision.** Processing an event starts a PostgreSQL transaction. Until the transaction ends, 
no other thread or instance can process that rocket; other rockets are not
affected. Readers are not blocked: they see the last committed state.

**Pattern.** Pessimistic locking with a database row lock.

**Alternatives considered.**
- *An in-memory lock per rocket* (my earlier version): only works inside one process.
- *A PostgreSQL advisory lock per channel:* equivalent, but not tied to the row being changed.

**Consequences.**
- Ordering and exactly-once application hold across instances. Key_Shared normally gives each rocket one consumer; the
  row lock guarantees it, including while consumers join, leave or restart.
- A crash releases the lock through the transaction rollback.
- A rocket that receives most of the traffic is still processed by one consumer at a time, ordering requires it. 
- A celebrity rocket: if the consumer processing it is paused by the OS (for example a long GC pause), its transaction stays open and the row lock stays held. 
  - With Key_Shared, that rocket's next messages are routed to the same consumer, so they queue in Pulsar until it resumes. Other rockets are unaffected. 
  - Pulsar doesn't redeliver them on its own: there's no acknowledgement timeout, only a 1-second redelivery after a negative acknowledgement. 
  - The rocket's throughput is limited by that one consumer. In production, idle_in_transaction_session_timeout in PostgreSQL and an acknowledgement timeout in Pulsar would limit how long one stuck consumer can hold a rocket.

## ADR-007: Persistence

**Context.** Messages must not be lost or applied twice across crashes, and queries and restarts should not need to
replay the history.

**Decision.** Two PostgreSQL tables (`RocketRepository`):
- `events (channel, message_number, body, stored_at)`, primary key `(channel, message_number)`: every accepted event,
  kept as history.
- `rocket_state`: one row per rocket with its current state and `last_applied` (the snapshot). Its row is also the lock.

For each event, in one transaction: lock the rocket's row, rebuild from the snapshot and the waiting events, apply,
insert the event, update the snapshot, commit. The Pulsar message is acknowledged after the commit. Tables are created at
startup with `CREATE TABLE IF NOT EXISTS`.

**Pattern.** Event store with snapshots; unit of work (one transaction per event).

**Alternatives considered.**
- *Replaying all events on startup* (an earlier version): correct, but startup time and storage grew with history.
- *Deleting processed events* (an earlier version): kept the database small, but lost the history.

**Consequences.**

| Failure | Result |
|---|---|
| Crash before the commit | Rolled back; Pulsar redelivers; processed normally |
| Crash after the commit, before the acknowledgement | Redelivered; recognised as a duplicate |

- The `events` table grows with history; production would partition it by time and archive old partitions.
- No schema migrations: `IF NOT EXISTS` cannot change an existing table. Production would use versioned migrations.

## ADR-008: Outbox when Pulsar is unavailable

**Context.** Without a fallback, ingest returned `503` for as long as Pulsar was down, relying on the sender to resend.

**Decision.**
- If publishing fails (no answer within 3 seconds), ingest stores the message in an `outbox` table in PostgreSQL and
  answers `202 AcceptedToOutbox`.
- After a failure, a circuit breaker sends messages straight to the outbox for 5 seconds, instead of each waiting for the
  timeout.
- An outbox relay runs in every instance. Every second it claims the 100 oldest rows with `SELECT … FOR UPDATE SKIP
  LOCKED`, publishes them to Pulsar, deletes them and commits. If publishing fails, the transaction rolls back and the
  rows stay. Locked rows are skipped by other instances, so each row is published by one instance.

**Pattern.** Store-and-forward (outbox with a relay); circuit breaker.

**Alternatives considered.**
- *Keep `503`:* no code, but ingest is unavailable during the outage.
- *Process the message directly in the request during the outage:* less code, but two processing paths.
- *Always write to the outbox first (classic transactional outbox pattern):* every message would depend on PostgreSQL and take
  an extra step.

**Consequences.**
- Tested: three relays running at once published 300 rows exactly once; without the row lock they published 900.
- After an outage, relayed (older) and new messages reach the worker mixed; the worker resequences them (ADR-004). The
  state is behind until the relay has caught up (about 400 messages per second).
- With both Pulsar and PostgreSQL down, the API answers `503` and the sender's resends are the last buffer.

## ADR-009: Back-pressure

**Context.** A message that never arrives makes later messages for that rocket wait. Without a limit, the number of
waiting messages, and the cost of rebuilding the rocket for each event, would grow without bound.

**Decision.** At most `MaxPendingMessagesPerRocket` (default 1,000, configurable, validated at startup) messages can wait
per rocket. Beyond that, an early message is not stored; the worker negatively acknowledges it and Pulsar redelivers it
after one second. The next expected message and a rocket's launch are always accepted, so the gap can always be filled.

**Pattern.** Bounded buffer with back-pressure through negative acknowledgement.

**Alternatives considered.**
- *No limit:* unbounded storage and processing cost per rocket.
- *Move overflow to the unprocessable store* (an earlier version): would discard valid messages.

**Consequences.** Nothing is lost; under a long gap, early messages are redelivered repeatedly until the gap is filled.
In normal runs the limit was never reached.

## ADR-010: Unprocessable messages

**Context.** Some messages can never be processed: malformed JSON, missing metadata, an unknown `messageType`, invalid
values such as a negative `by`. Rejecting them with an error would cause endless resends.

**Decision.** Ingest keeps them in memory with the raw body, the reason and a timestamp (at most 10,000; the oldest are
dropped first), logs a warning with the raw body, and answers `202 Unprocessable`. They are listed at
`GET /unprocessable-messages` and are never published.

**Pattern.** Invalid-message store.

**Alternatives considered.**
- *A dead-letter topic:* durable and replayable; Pulsar supports it and it would be the production choice.
- *Dropping them silently:* rejected, because problems would go unnoticed.
- In production scenario, for unprocessed message from the sender we should have notification to the sender team to fix the sender service, and for unprocessed message from the worker we should have notification to the platform team to fix the service.

**Consequences.** Invalid messages are visible but lost on restart. No message from the test program ended up here.

## ADR-011: Domain rules

**Context.** The brief describes the message types but not how to combine them.

**Decision.**
- `RocketLaunched` sets type, speed, mission and launch time, and the status `Launched`. Its number starts the sequence
  (ADR-004). A rocket is not visible in the API until it has launched.
- `RocketSpeedIncreased` and `RocketSpeedDecreased` change speed by `by`. Speed is not clamped at zero.
- `RocketMissionChanged` sets the mission.
- `RocketExploded` sets the status `Exploded` and the reason. It is final: later messages still advance the message
  number but do not change the state.
- All rules are in `Rocket.Apply`.

**Alternatives considered.** *Clamping speed at zero:* would hide invalid data and make the result depend on order.

**Consequences.** A negative speed is possible and visible.

## ADR-012: Read API

**Context.** A dashboard needs each rocket's current state and a sortable list.

**Decision.**
- `GET /rockets/{channel}`: one rocket, or `404` if it is unknown or has not launched.
- `GET /rockets?sortBy=channel|type|speed|mission|status|launchedAt&order=asc|desc`: all launched rockets; an invalid
  value returns `400`.
- Each rocket includes `channel, type, speed, mission, status, explosionReason, launchedAt, lastUpdatedAt,
  lastMessageNumber, pendingMessages`. Times are in UTC.
- Reads come from `rocket_state` only. Also available: `GET /unprocessable-messages`, `GET /health`, Swagger UI and the
  OpenAPI document.

**Pattern.** Read model: a snapshot table kept up to date by the worker.

**Alternatives considered.** 
- *An endpoint listing each rocket's events:* not needed for the dashboard; the history is
available in the `events` table.
- *A signalR endpoint so UI clients can subscribe to updates*

**Consequences.** Reads are fast and do not block on processing, but are eventually consistent. No pagination.

## ADR-013: Observability

**Context.** At thousands of messages per second, individual log lines are not a practical way to watch the service.

**Decision.**
- A counter, `rockets.messages`, tagged by result: `Accepted`, `AcceptedToOutbox`, `Unprocessable`, `Unavailable` at
  ingest; `Applied`, `Buffered`, `Duplicate`, `BufferFull` in the worker.
- OpenTelemetry metrics (this counter plus ASP.NET Core, Kestrel and .NET runtime metrics) and logs are exported over
  OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. `docker compose` sets it to the Aspire Dashboard.
- One Debug log line per processed event goes to the dashboard only; the console shows Information and above.
  Unprocessable messages are logged with their raw body.

**Pattern.** OpenTelemetry metrics and structured logs.

**Alternatives considered.** *Logging every request to the console:* would slow ingest at full load. *Tracing every
request:* would need sampling at this volume; not added.

**Consequences.** The service does not depend on the dashboard. Next steps would be consumer lag as a metric and
sampled traces across ingest, Pulsar and the worker.

## ADR-014: Verification

**Decision.** A small set of automated tests, one per concern, against a real PostgreSQL started by Testcontainers
(`dotnet test`, 18 tests):
- **Concurrency:** simultaneous copies of one event are applied once; three instances processing the same stream apply
  each event once; the lock is per rocket; readers see only committed state; 200 events for one rocket on 8 threads.
- **Ordering and processing:** early events wait for the gap; any of 20 arrival orders with duplicates and an explosion
  gives the in-order result; waiting events survive a restart; the launch starts the sequence; a full buffer rejects and
  the redelivery succeeds; a redelivery after a crash is a duplicate; a crash before the commit rolls back.
- **Outbox:** oldest first and then removed; rows stay when publishing fails; three relays publish each row once.
- **Ingest API:** valid messages are published with the channel as key; invalid ones are kept; Pulsar down leads to the
  outbox; Pulsar and the outbox down lead to `503`.
