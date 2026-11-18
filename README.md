# Rockets Service with UI

It started innocently: I wanted to build a small synchronous service with every rocket in memory and one lock per rocket, keyed by its channel.
Then I asked myself "what if?" too many times, and ended up with this slightly over-engineered solution (considering the 6-hour limit) :D.
I let my curiosity lead and had fun with it,

The service is bundled with:

- A .NET 10 backend service
- A React UI with a live view of the rockets
- PostgreSQL for the event store and rocket state
- Apache Pulsar for message queueing
- Aspire Dashboard for metrics and logs
- pgweb, a read-only PostgreSQL UI
- Sweat, tears, confusion and a lot of coffee 🥲 (not included in the docker compose)

It works on my machine, so it should work on yours right ? :D So we have a docker compose file to help you spin up everything at once on any platform (Linux, MacOS, Windows).
You only need Docker with Docker Compose installed on your machine.

## Run it

```bash
docker compose up -d --build
```

This starts six containers: the service, PostgreSQL, Pulsar (standalone mode), the Rockets UI, the Aspire Dashboard and
pgweb. The service waits until PostgreSQL and Pulsar are healthy (Pulsar takes about 10–60 s on first start).

| What | URL |
|---|---|
| Service (Swagger UI as home page) | http://localhost:8088 |
| Rockets UI (live view) | http://localhost:3088 |
| Aspire Dashboard (metrics and logs) | http://localhost:18888 |
| pgweb (read-only view of the tables) | http://localhost:8089 |

Then send messages with the test program and watch the rockets in the UI:

```bash
./rockets launch "http://localhost:8088/messages"
```

- The service runs as a non-root user and has a health check (`GET /health`).
- If a port is busy, change it with `ROCKETS_PORT`, `UI_PORT`, `DASHBOARD_PORT`, `PGWEB_PORT`, `POSTGRES_PORT`
  (default 55432) or `PULSAR_PORT` (default 6650).
- The UI, the dashboard and pgweb are extras: the service doesn't depend on them.

For a fresh run, remove the volumes (PostgreSQL data and Pulsar state). Local use only:

```bash
docker compose down -v
docker compose up -d --build
```

## API

| Endpoint | Description |
|---|---|
| `POST /messages` | Receives a rocket message. `202` with `X-Ingest-Result`, or `503` + `Retry-After` if it can't be stored |
| `GET /rockets/{channel}` | One rocket, or `404` |
| `GET /rockets?sortBy=channel\|type\|speed\|mission\|status\|launchedAt&order=asc\|desc` | All launched rockets |
| `GET /unprocessable-messages` | Messages that could not be processed |

## Tests

```bash
dotnet test
```

18 tests for concurrency, ordering, the outbox and ingest, against a real PostgreSQL started by Testcontainers
(Docker must be running).

## Architecture brief

The full reasoning is in [ADRs.md](docs/ADRs.md).

1. **Event-driven.** `POST /messages` validates the message, publishes it to Pulsar (key = channel) and returns `202`.
   A background worker in each instance consumes the messages and applies them in PostgreSQL. The service can run as
   several instances.
2. **Concurrency.** Each rocket has one row in PostgreSQL. Before processing an event, the worker locks that row
   (`SELECT … FOR UPDATE`). Until it commits, no other thread, worker or instance can change that rocket; other rockets
   are not blocked. Pulsar's Key_Shared subscription also sends each rocket to one consumer, but the lock is what
   guarantees it.
3. **Ordering.** Messages are applied strictly by message number. If a message arrives early, it is stored as waiting.
   When the missing message arrives, both are applied in order.
4. **Idempotency.** If a message's number is already applied or already waiting, it is a duplicate. It is acknowledged
   but not stored or applied again.
5. **Crash safety.** The worker acknowledges a Pulsar message only after its database commit. A crash before the commit
   rolls back and Pulsar redelivers the message; a crash after the commit makes the redelivery a duplicate.
6. **Outages.** If PostgreSQL is down, messages wait in Pulsar until it is back. If Pulsar is down, messages are stored
   in an outbox table in PostgreSQL and published when Pulsar is back. Pulsar and PostgreSQL have a bro-code 😂 each covers for the other, but not both at once. 
   If both are down, the service returns `503` and
   the sender resends. 
7. **Back-pressure.** At most 1,000 messages can wait per rocket. Beyond that, the message is redelivered a second
   later.
8. **Eventual consistency.** A `202` means the message is stored, not yet applied. `GET /rockets` shows the state after
   the worker commits, usually within milliseconds.
9. **Avoiding a race condition when creating the Pulsar producer.** All requests share one Pulsar producer, which is
   created when the first message arrives. If several requests arrive at that moment, each could see that no producer
   exists yet and create its own. To prevent this, a `SemaphoreSlim(1, 1)` lets only one request create the producer.
   The others wait, then use the one that was created. Once the producer exists, requests use it directly without
   waiting. If creating it fails, for example because Pulsar is still starting, the next request tries again.
10. **Data structure.** Each rocket's waiting messages are held in a SortedDictionary keyed by message number. 
    It finds duplicates and the next message to apply in O(log n), since its a tree based ds and keeps them in number order. 
    
## Domain rules (not specified in the brief)

- **Speed** = `launchSpeed + Σ increases − Σ decreases`, not clamped at 0: clamping would hide bad data as a limitation.
- **The launch starts the sequence.** Until a rocket has launched, only its `RocketLaunched` can be applied. Its
  number N starts the sequence; the test program uses 1, but any N works, and the next message is N+1. Messages
  numbered below N are dropped.
- **`RocketExploded` is final.** Later messages still advance the sequence but don't change the state.

## Known limitations

- **Single nodes.** Pulsar standalone and one PostgreSQL are for local use. Production would replicate both
  (several BookKeeper nodes, a PostgreSQL standby).
- **Reads depend on PostgreSQL.** While it is down, `GET /rockets` fails.
- **Unprocessable messages** are kept in memory only (max 10,000). Pulsar's dead-letter topic would be the production
  version.
- **A permanently missing message stalls that rocket** (visible in `pendingMessages`). A gap policy would be a product
  decision.
- **No authentication, rate limiting or pagination.** API docs are enabled in every environment for easy review.