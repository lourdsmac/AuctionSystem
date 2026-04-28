# Architecture overview — Clean Architecture and this repository

This document explains **Clean Architecture** as used in real backends, maps it to **this** AuctionSystem repo, and walks through the **request/data flow** from HTTP to persistence.

---

## SSE vs WebSocket — which file is which?

| Mechanism | Endpoint | Primary code file(s) |
|-----------|----------|----------------------|
| **REST snapshot** | `GET /api/auction` | `src/Api/Controllers/AuctionController.cs` (GET without `sse`) |
| **SSE stream** | `GET /api/auction/sse` | Same controller → **`GetSse`** (`text/event-stream`, `BodyWriter`, `PeriodicTimer`) |
| **WebSocket** | `WS /ws/auction` | **`src/Api/AuctionWebSocketEndpoint.cs`** (handshake, receive loop, JSON bids) — registered in **`src/Api/Program.cs`** via **`MapAuctionWebSocket()`** |

**Shared logic:** `AuctionService` + `AuctionItem` domain rules. **WebSocket-only infrastructure:** `WebSocketConnectionManager`, `AuctionWebSocketNotifier` (`IAuctionRealtimeNotifier`). SSE does **not** use those classes — it only **reads** current state and writes bytes to the HTTP response.

**Frontend split:** `frontend/src/App.tsx` — **`SsePanel`** vs **`WebSocketPanel`** (separate components, separate transport).

**Why WebSockets involve more moving parts than SSE here** (registry, receive loop, `IServiceScopeFactory`, broadcast notifier): see **`SSE_VS_WEBSOCKET_COMPLEXITY.md`**.

---

## Why structure code at all?

Small scripts can live in one file. Production systems last years, get new requirements, and move between teams. Structure exists so that:

- **Business rules** do not get lost inside HTTP or SQL string concatenation  
- **Infrastructure** (database, email, payment SDK) can be swapped or faked in tests  
- **New engineers** can find “where does the money logic live?” in one place  

**WHY THIS MATTERS:** In interviews, “where would you put X?” is testing whether you understand **separation of concerns**, not whether you memorized a folder name.

---

## The four layers (conceptual)

Think of your system as an **onion**:

```
                    ┌─────────────────────────────┐
                    │   Outer world (HTTP, MQ)    │
                    └──────────────┬──────────────┘
                                   │
                    ┌──────────────▼──────────────┐
                    │   Api / presentation        │  ← Translates HTTP/WebSocket to application calls
                    └──────────────┬──────────────┘
                                   │
                    ┌──────────────▼──────────────┐
                    │   Application               │  ← Use cases, orchestration, ports (interfaces)
                    └──────────────┬──────────────┘
                                   │
         ┌─────────────────────────┴─────────────────────────┐
         │           Domain (entities + rules)               │  ← Pure business logic
         └─────────────────────────┬─────────────────────────┘
                                   │
                    ┌──────────────▼──────────────┐
                    │   Infrastructure           │  ← EF Core, HTTP clients, queues
                    └────────────────────────────┘
```

**Dependency rule (the important part):** Dependencies point **inward**. The **Domain** knows nothing about ASP.NET, SQL dialects, or React. The **Api** knows about HTTP, but it should not embed pricing rules.

### Visual: dependency direction (compile-time references)

In a Clean Architecture solution, **outer** projects reference **inner** abstractions — not the reverse.

```
                    ┌─────────────┐
                    │     Api     │──── references ────┐
                    └──────┬──────┘                    │
                           │                           ▼
                    ┌──────▼──────┐            ┌───────────────┐
                    │Infrastructure│───────────►│  Application  │
                    └──────┬──────┘            │  (+ Domain)   │
                           │                   └───────┬───────┘
                           │                           │
                           └──────────────────────────►│   Domain      │
                                                       │  (no deps on   │
                                                       │   outer layers)│
                                                       └───────────────┘
```

**Rules in one glance:**

- `Domain` → **zero** project references to Api/Infrastructure.  
- `Application` → references `Domain` only (plus generic abstractions).  
- `Infrastructure` → implements interfaces **defined in Application** (and uses `Domain` entities).  
- `Api` → wires DI, hosts HTTP + WebSockets, references `Application` + `Infrastructure` for composition root.

### Visual: runtime call path vs compile-time dependency

Compile-time: **Infrastructure** depends on **Application** interfaces.  
Runtime: **Api** creates `EfAuctionRepository : IAuctionRepository` and injects it — **dependency inversion**.

```
  At runtime (simplified):

      Api  ──creates──►  EfAuctionRepository  ──implements──►  IAuctionRepository
        │                                                        ▲
        └──── injects IAuctionRepository into AuctionService ─────┘
```

---

## Layer responsibilities

### 1. Domain

**What lives here**

- Entities (`AuctionItem`)  
- Value objects and invariants  
- Pure operations that enforce rules (`TryApplyBid`)

**What must NOT live here**

- `DbContext`, `HttpClient`, `WebSocket`, `JsonSerializer` *unless* you are deliberately modeling a portable abstraction  

**WHY THIS MATTERS:** Domain rules are the **truth** of your business. If “bid must exceed current price” sits only inside a controller, someone will duplicate it in a batch job six months later and bugs diverge.

**This repo:** `src/Domain/AuctionItem.cs` — bid validation and timestamp updates live here.

---

### 2. Application

**What lives here**

- **Use cases / application services** — `AuctionService` coordinates “load → apply rules → persist → notify”  
- **DTOs** for stable API-facing shapes (`AuctionStateDto`)  
- **Ports (interfaces)** that Application defines — `IAuctionRepository`, `IAuctionRealtimeNotifier`

**What must NOT live here**

- Direct WebSocket APIs or raw SQL strings (Infrastructure implements ports)

**WHY THIS MATTERS:** Application is where **orchestration** lives: the story of “what happens when a user bids” without leaking transport details.

**This repo:** `src/Application/Services/AuctionService.cs`, `src/Application/Abstractions/*`, `src/Application/Dtos/*`.

---

### 3. Infrastructure

**What lives here**

- EF Core `AppDbContext`, repositories (`EfAuctionRepository`)  
- Adapters — `AuctionWebSocketNotifier` implements `IAuctionRealtimeNotifier`  
- Anything that touches **specific technologies**

**WHY THIS MATTERS:** You can swap in-memory EF for Postgres in acceptance tests **without** rewriting bids rules.

**This repo:**

- `src/Infrastructure/Persistence/AppDbContext.cs`  
- `src/Infrastructure/Repositories/EfAuctionRepository.cs`  
- `src/Infrastructure/WebSockets/WebSocketConnectionManager.cs`  

---

### 4. Api (presentation)

**What lives here**

- Controllers (`AuctionController`) — map HTTP verbs to application calls  
- WebSocket routing (`AuctionWebSocketEndpoint`) — handshake, framing, scoped service resolution  
- Cross-cutting middleware registration in `Program.cs` (Serilog, CORS, WebSockets)

**Thin controller rule:** If a controller grows business “if bid else …” forks, extract that into Application or Domain.

**This repo:**

- `src/Api/Controllers/AuctionController.cs` — SSE stream + REST snapshot  
- `src/Api/AuctionWebSocketEndpoint.cs` — parses JSON bids, invokes `AuctionService`  
- `src/Api/Program.cs` — middleware pipeline composition  

---

## End-to-end flow diagram (REST snapshot)

ASCII flow for **`GET /api/auction`**:

```
Browser                     Api layer                Application           Domain           Infrastructure
   |                           |                         |                   |                     |
   |  HTTP GET /api/auction    |                         |                   |                     |
   |-------------------------->|                         |                   |                     |
   |                           |  AuctionController      |                   |                     |
   |                           |  GetCurrentAsync() ------>| AuctionService    |                     |
   |                           |                         |------ read ------>| IAuctionRepository  |
   |                           |                         |                   |                     |--- EF Core
   |                           |                         |<----- AuctionItem-| (snapshot/no track)|
   |                           |                         | AuctionStateDto   |                     |
   |                           |<-------- 200 OK --------|                   |                     |
   |<--------------------------|  JSON                   |                   |                     |
```

**REAL WORLD EXAMPLE:** Same shape for **`POST /orders`**: Controller validates shape → Application service → Domain invariants → Repository persists → optional outbox for events.

---

## End-to-end flow (WebSocket bid)

```
Browser (JSON bid)           Api                        Application              Domain           Infrastructure
       |                       |                               |                     |                     |
       | WS text frame         | Map("/ws/auction")           |                     |                     |
       | {"bidAmount":105} --->| Deserialize                  |                     |                     |
       |                       | CreateScope → AuctionService-> PlaceBidAsync()---->| TryApplyBid         |
       |                       |                               |                     |----------+        |
       |                       |                               | SaveChanges <-------| persisted |        |
       |                       |                               | IAuctionRealtimeNotifier.broadcast      |
       |                       |                               |                     |                     |--> all WS clients
```

**WHY THIS MATTERS:** WebSocket handlers are often **Fat** in tutorials. Here, **JSON parsing stays at the edge**; **money rules stay in Domain**.

---

## Additional diagram: SSE tick loop vs request/response

`GET /api/auction` returns **once** and the TCP connection closes (typical). `GET /api/auction/sse` keeps the **same** connection open:

```
REST (GET /api/auction):

  Client                         Server
    |                               |
    |-------- request ------------->|
    |<------- 200 JSON ------------|      connection usually ends here
    |                               |

SSE (GET /api/auction/sse):

  Client                         Server
    |                               |
    |-------- request ------------->|
    |       (connection stays OPEN) |
    |<------- chunk: comment ------|
    |<------- chunk: data json ----|
    |               ...wait 2s...   |
    |<------- chunk: data json ----|
    |               ...             |        until CancellationToken fires
```

---

## Where business logic lives (answer plainly)

| Concern | Layer |
|---------|-------|
| “Bid must be higher than current price” | **Domain** (`AuctionItem.TryApplyBid`) |
| “After success, tell everyone subscribed” | **Application** invokes **Infrastructure** notifier port |
| “How to deserialize `{ bidAmount: 105 }`” | **Api** WebSocket adapter |
| “How rows are tracked in EF for updates” | **Infrastructure** repository |

---

## Why production teams adopt this structure

1. **Testability:** Domain logic tests run without ASP.NET startup.  
2. **Replaceability:** Swap EF + Postgres for DynamoDB behind the same repository interface later.  
3. **Safety reviews:** Compliance can read Domain + Application paths without drowning in MVC boilerplate.  
4. **Onboarding:** “Start at Domain README” beats “grep the codebase for regex.”  

**COMMON MISTAKE:** Putting **everything** behind interfaces prematurely. Interfaces where you have **multiple implementations** or need **boundary tests**. Do not bury one-line getters behind `IAuctionItemCurrentPriceReaderFactory`.

---

## Mapping to “this repository” vs “payments + JWT” (future)

This repo **stops** at a small auction. A production payment service would add:

- **Domain:** `Payment`, `Money`, idempotency rules  
- **Application:** `CreatePaymentCommand`, `IdempotencyService`  
- **Infrastructure:** Payment provider client, idempotency table, Postgres  
- **Api:** Middleware for `Authorization`, `Idempotency-Key`, rate limits  

Those are **patterns**, not fantasies — see sibling docs (`IDEMPOTENCY_DEEP_DIVE.md`, `PAYMENT_FLOW.md`, etc.).

---

## Summary

Clean Architecture here is **not dogma for dogma’s sake**. It physically separates:

- **HTTP/WebSocket quirks**  
- **Use-case choreography**  
- **Business truths**  
- **Messy I/O**  

Start reading code at **`src/Domain`**, then **`Application`**, then **`Infrastructure`**, then **`Api`**. That order matches how **importance** flows inward.
