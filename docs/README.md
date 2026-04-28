# AuctionSystem — Documentation Hub

Welcome. This folder is deliberately **thick**: it mixes an **engineering onboarding guide**, a **system-design deep dive**, and a **backend learning handbook**.

---

## ⚠️ Read this before you skim

### What this Git repository actually is

**AuctionSystem** is a focused **.NET 8 + React** sample that contrasts:

- **Server-Sent Events (SSE)** — server → browser, one-way price snapshots every 2 seconds  
- **WebSockets** — bidirectional: clients send **`{ "bidAmount": number }`**; the server broadcasts price updates  

It uses **Clean Architecture** (Domain, Application, Infrastructure, Api), **EF Core in-memory**, **Serilog**, and **explicit CORS** for the Vite dev server.

### What this repository does **not** implement (today)

Many topics in these docs (**idempotency keys**, **payments**, **JWT**, **refresh tokens**, **persistent user sessions**, **rate limiting middleware**, **`X-Correlation-ID` plumbing**) describe **production-grade patterns you would add** to a real-money or multi-tenant API — or that you will see in interviews and job codebases.

They are explained here so you can:

- Learn the ideas **in depth**  
- Relate them to **this** repo where possible (CORS, logging, layered code)  
- Explain and build a **full** system in an interview or greenfield project  

See **Feature matrix** below so you never confuse “documented pattern” with “code in this folder.”

---

## Where SSE vs WebSocket is implemented (navigation)

Use this table when you’re lost in the tree: **SSE** and **WebSocket** use **different routes, different files, different browser APIs**. They only share **business state** through `AuctionService` and the EF `AuctionItem` row.

| | **SSE (one-way: server → browser)** | **WebSocket (two-way)** |
|---|-------------------------------------|-------------------------|
| **HTTP / URL** | Long-lived `GET` **`/api/auction/sse`** | Upgrade on **`/ws/auction`** (scheme `ws:` / `wss:`) |
| **Backend entry** | `src/Api/Controllers/AuctionController.cs` → **`GetSse`** | `src/Api/AuctionWebSocketEndpoint.cs` → **`MapAuctionWebSocket`** (invoked from **`Program.cs`**) |
| **What it does** | Streams `text/event-stream` chunks every ~2s; calls **`AuctionService.GetCurrentAsync`** only | Accepts socket, sends initial `connected` JSON, reads **`{ "bidAmount": number }`**, calls **`AuctionService.PlaceBidAsync`**, then **broadcasts** to all sockets |
| **“Push to all clients”** | N/A — **each** SSE connection is its **own** GET; server writes that response only | **`WebSocketConnectionManager`** + **`AuctionWebSocketNotifier`** fan out JSON to every open WebSocket |
| **Application** | `AuctionService` (read path) | `AuctionService` (write path + triggers notifier) |
| **Infrastructure unique to channel** | _(none — streaming is in the controller)_ | `WebSocketConnectionManager.cs`, `AuctionWebSocketNotifier.cs` |
| **Frontend** | `frontend/src/App.tsx` → **`SsePanel`**, `new EventSource(httpUrl('/api/auction/sse'))` | **`WebSocketPanel`**, `new WebSocket(webSocketUrl('/ws/auction'))` |

**What is shared (both channels):**

- **Domain:** `src/Domain/AuctionItem.cs` (bid rules live here).  
- **Application:** `src/Application/Services/AuctionService.cs` — REST + SSE read snapshots; WebSocket bids mutate and call **`IAuctionRealtimeNotifier`**.  
- **Persistence:** `src/Infrastructure/Repositories/EfAuctionRepository.cs`, `AppDbContext`.

**Wire-up (see once, remember):** `src/Api/Program.cs` registers CORS, **`UseWebSockets`**, **`MapControllers()`** (SSE lives under the controller), then **`MapAuctionWebSocket()`** (WS route). SSE is **not** registered in `Map` — it’s **`[HttpGet("sse")]`** on `AuctionController`.

Deeper walkthrough: **`FRONTEND_BACKEND_FLOW.md`** and diagrams below in this file.

---

## Feature matrix: this repo vs. handbook chapters

| Capability | In this repo’s code? | Where to learn |
|------------|----------------------|----------------|
| Clean Architecture layers | ✅ Yes | `ARCHITECTURE_OVERVIEW.md` |
| SSE streaming | ✅ Yes | `FRONTEND_BACKEND_FLOW.md`, root `README.md` |
| WebSocket bidding + broadcast | ✅ Yes | Same |
| EF Core **in-memory** + `AuctionItem` | ✅ Yes | `DATABASE_DESIGN.md` |
| Serilog request + app logging | ✅ Yes | `DEBUGGING_AND_OBSERVABILITY.md` |
| CORS for dev origins | ✅ Yes | `CORS_DEEP_DIVE.md` |
| Docker / docker compose | ❌ Not shipped | Example in this file (copy-paste) |
| Idempotency (`Idempotency-Key`) | ❌ Not implemented | `IDEMPOTENCY_DEEP_DIVE.md` |
| Payment provider integration | ❌ Not implemented | `PAYMENT_FLOW.md` |
| JWT access + refresh | ❌ Not implemented | `AUTHENTICATION_JWT_DEEP_DIVE.md`, `USER_SESSION_FLOW.md` |
| Rate limiting | ❌ Not implemented | `RATE_LIMITING_DEEP_DIVE.md` |
| Security headers middleware | ❌ Partial (framework defaults) | `API_HEADERS_AND_SECURITY.md` |
| Correlation ID middleware | ❌ Not implemented | `DEBUGGING_AND_OBSERVABILITY.md` |

**WHY THIS MATTERS:** Interviewers and senior engineers expect you to separate “I read about it” from “our service does it.” This table keeps you honest while still letting you learn the full story.

---

## What problem this project solves

**Problem:** It is hard to *feel* the difference between **SSE** (one HTTP response, endless stream) and **WebSockets** (upgraded TCP-like channel, both directions) from blog posts alone.

**Solution:** One small **auction** domain, one shared price in the database, two panes in the UI:

- Left: **EventSource** — you only watch; the server pushes.  
- Right: **WebSocket** — you send bids; everyone sees updates.

**REAL WORLD EXAMPLE:** News tickers and admin dashboards often use SSE. Live auctions, chat, and games usually use WebSockets (or SignalR, which can use WebSockets under the hood).

---

## High-level architecture (this repository)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Browser (Vite + React)                               │
│   Panel A: new EventSource('/api/auction/sse')                               │
│   Panel B: new WebSocket('ws://.../ws/auction')  +  send JSON bids           │
└───────────────┬───────────────────────────────┬─────────────────────────────┘
                │ HTTP (SSE)                     │ WS
                ▼                                 ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                    ASP.NET Core (Kestrel) — AuctionSystem.Api                 │
│   AuctionController          │   Map("/ws/auction") → WebSocket loop         │
│   GET /api/auction           │   AcceptWebSocketAsync + scoped services        │
│   GET /api/auction/sse       │                                                │
└───────────────┬───────────────────────────────┬─────────────────────────────┘
                │                                 │
                ▼                                 ▼
┌───────────────────────────────┐     ┌───────────────────────────────────────┐
│ Application: AuctionService   │     │ Infrastructure:                        │
│ Domain: AuctionItem rules     │     │ AppDbContext (InMemory), repo,         │
│                               │     │ WebSocketConnectionManager + notifier  │
└───────────────┬───────────────┘     └───────────────────┬───────────────────┘
                │                                         │
                └────────────────────┬────────────────────┘
                                     ▼
                          ┌──────────────────────┐
                          │ In-memory EF store   │
                          │ (single auction row) │
                          └──────────────────────┘
```

### Detailed flow diagrams (SSE vs WebSocket vs REST)

These complement the box diagram above: they show **message order** and **who talks when**.

#### A) One-shot REST snapshot — `GET /api/auction`

```
  Browser                Kestrel / Controller        AuctionService           Repository / DB
    |                            |                        |                        |
    |  GET /api/auction          |                        |                        |
    |--------------------------->|                        |                        |
    |                            |  GetCurrentAsync()     |                        |
    |                            |----------------------->|                        |
    |                            |                        |  snapshot read         |
    |                            |                        |----------------------->|
    |                            |                        |<-----------------------|
    |                            |  200 + JSON DTO        |                        |
    |                            |<-----------------------|                        |
    |  response body (closes)    |                        |                        |
    |<---------------------------|                        |                        |
    |                            |                        |                        |
```

#### B) SSE — one long HTTP response, ticks every ~2s (`GetSse`)

`CancellationToken` fires when the tab closes or the connection drops — the loop stops cooperatively.

```
  Browser (EventSource)      AuctionController.GetSse           AuctionService           Notes
    |                                |                                |                 |
    |  GET /api/auction/sse          |                                |                 | single HTTP request
    |----------------------------->  |                                |                 | stays open
    |                                |  DisableBuffering + headers    |                 |
    |  < text/event-stream chunks    |                                |                 |
    |<----------------------------   |  ": sse-connected\n\n"         |                 |
    |                                |                                |                 |
    |         ... every 2s ...       |                                |                 |
    |  < data: {json}\n\n           |  WaitForNextTickAsync(ct)      |                 |
    |<----------------------------   |--------------------------->    | GetCurrentAsync |
    |                                |  WriteAsync + Flush (ct)       |                 |
    |                                |                                |                 |
    |  user closes tab ──────────────┼────────────────────────────────┼────────────────> RequestAborted
    |                                |  OperationCanceledException    |                 | timer + writes stop
    |                                |  (catch → log disconnect)        |                 |
```

#### C) WebSocket — connect, then many bidirectional frames

```
  Tab A (bidder)           API /ws/auction              AuctionService        All WS clients (A, B, C)
    |                               |                         |                         |
    |  HTTP Upgrade ─────────────>  |                         |                         |
    |  < 101 Switching Protocols    |                         |                         |
    |  < {type:connected,...} (text) |                         |                         |
    |                               |                         |                         |
    |  send {"bidAmount":120} ───> | PlaceBidAsync           |                         |
    |                               |------------------------>|                         |
    |                               |  persist + notifier     | broadcast JSON -------->| Tab A,B,C receive
    |  < auction_update ───────────|                         |                         | auction_update
    |                               |                         |                         |
```

#### D) Middleware / pipeline order (incoming HTTP & WS upgrade)

```
Inbound request
       │
       ▼
┌──────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────────────────┐
│ Serilog      │──► │ UseWebSockets│──► │ UseCors     │──► │ MapControllers          │
│ request log  │    │              │    │ Frontend    │    │ GET /api/auction,...    │
└──────────────┘    └──────┬───────┘    └─────────────┘    └─────────────────────────┘
                            │
               path /ws/*   │  (WebSocket handshake only on matching branch)
                            ▼
                   Map("/ws/auction")  →  AcceptWebSocketAsync → handler loop
```

---

## How to run (what works today)

### Backend

```bash
cd /path/to/AuctionSystem
dotnet run --project src/Api/AuctionSystem.Api.csproj
```

Default base URL: **http://localhost:5088** (see `src/Api/Properties/launchSettings.json`).

### Frontend

```bash
cd frontend
npm install
npm run dev
```

Open **http://localhost:5173**. With the default setup, the Vite dev server **proxies** `/api` and `/ws` to the API (see `frontend/vite.config.ts`), so you can leave `VITE_API_BASE` unset.

Alternatively, create `frontend/.env`:

```env
VITE_API_BASE=http://localhost:5088
```

Then the React app talks to the API origin directly (CORS must allow the dev origin — already configured in `appsettings.json`).

### Docker Compose (not in repo — copy if you need it)

The repository does **not** include a checked-in `Dockerfile` / `docker-compose.yml`. Below is a **reasonable starting point** so you can package the API for Linux containers; adjust ports and URLs to match your compose network.

Example `Dockerfile` for the API (multi-stage sketch):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Api/AuctionSystem.Api.csproj
RUN dotnet publish src/Api/AuctionSystem.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AuctionSystem.Api.dll"]
```

Example `docker-compose.yml` sketch:

```yaml
services:
  api:
    build:
      context: .
      dockerfile: Dockerfile
    ports:
      - "5088:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Development
  frontend:
    image: node:20-alpine
    working_dir: /app
    volumes:
      - ./frontend:/app
    command: sh -c "npm ci && npm run dev -- --host 0.0.0.0 --port 5173"
    ports:
      - "5173:5173"
    depends_on:
      - api
```

**COMMON MISTAKE:** Pointing `VITE_API_BASE` at `http://localhost:5088` from *inside* the browser works; from *inside* a Docker container, `localhost` is the container itself — use the **service name** (e.g. `http://api:8080`) or host networking, depending on your setup.

---

## End-to-end demo (this project)

There is **no** register/login/payment wizard in code. Follow this realistic walkthrough instead.

### 1) Start backend + frontend

Two terminals:

```bash
dotnet run --project src/Api/AuctionSystem.Api.csproj
```

```bash
cd frontend && npm run dev
```

### 2) Snapshot the auction (REST)

```bash
curl -s http://localhost:5088/api/auction | jq .
```

**Expected JSON shape:**

```json
{
  "id": 1,
  "name": "Vintage Watch",
  "currentPrice": 100.00,
  "lastUpdated": "2026-04-28T12:34:56.789Z"
}
```

*(Timestamp will differ.)*

### 3) Watch SSE (streaming)

```bash
curl -sN http://localhost:5088/api/auction/sse
```

You should see `: sse-connected` followed by repeated `data: {...}` lines every ~2 seconds. Press `Ctrl+C` to stop.

**COMMON MISTAKE:** Using a tool that buffers the whole response. `curl -N` disables buffering so you see the stream live.

### 4) Exercise WebSockets (conceptual curl note)

Interactive WebSockets are awkward in plain `curl`. Use the UI **right pane** — or **`websocat`**, **`wscat`**, or a browser console:

```javascript
const ws = new WebSocket('ws://localhost:5088/ws/auction');
ws.onmessage = (e) => console.log(JSON.parse(e.data));
ws.send(JSON.stringify({ bidAmount: 105 }));
```

**Expected:**

- First message: `{ "type":"connected", "auction": { ... } }`  
- On successful bid: `{ "type":"auction_update", "payload": { ... higher currentPrice ... } }`  
- On rejected bid (too low): `{ "type":"bid_rejected", "error":"...", "auction":{...} }`  

### Multi-client test

Open two browser tabs → both WebSocket panels **Connect** → bid from one tab; the other should show the new price via **broadcast**.

---

## Example curl reference (implemented endpoints)

```bash
# Health check via auction snapshot (no separate /health endpoint in this demo)
curl -s -w "\nHTTP %{http_code}\n" http://localhost:5088/api/auction

# SSE stream (Ctrl+C to exit)
curl -sN http://localhost:5088/api/auction/sse
```

Preflight probe (OPTIONS) if you test cross-origin tooling:

```bash
curl -s -X OPTIONS http://localhost:5088/api/auction \
  -H "Origin: http://localhost:5173" \
  -H "Access-Control-Request-Method: GET" -D -
```

---

## Where “register / login / payment / retry payment” fits

Those steps describe a **payments + identity** product. This repo intentionally **does not** include them.

To study those flows in depth anyway, read:

| Topic | Doc |
|--------|-----|
| Idempotency (double submits, retries) | `IDEMPOTENCY_DEEP_DIVE.md` |
| Payments lifecycle | `PAYMENT_FLOW.md` |
| JWT | `AUTHENTICATION_JWT_DEEP_DIVE.md` |
| Sessions / refresh rotation | `USER_SESSION_FLOW.md` |
| Errors + retries | `ERROR_HANDLING_AND_RETRIES.md` |

Treat them as **what you build next** (or what your next job’s codebase contains).

---

## Documentation map

| Doc | Purpose |
|-----|---------|
| **[This README — SSE vs WebSocket map](#where-sse-vs-websocket-is-implemented-navigation)** | **Which files implement SSE vs WebSocket (quick lookup)** |
| [ARCHITECTURE_OVERVIEW.md](./ARCHITECTURE_OVERVIEW.md) | Clean Architecture in this repo and in production |
| [IDEMPOTENCY_DEEP_DIVE.md](./IDEMPOTENCY_DEEP_DIVE.md) | Idempotency-Key, hashing, duplicate requests |
| [PAYMENT_FLOW.md](./PAYMENT_FLOW.md) | Payment pipeline and why duplicates cost money |
| [AUTHENTICATION_JWT_DEEP_DIVE.md](./AUTHENTICATION_JWT_DEEP_DIVE.md) | JWT structure, claims, signing vs encryption |
| [USER_SESSION_FLOW.md](./USER_SESSION_FLOW.md) | Sessions, refresh tokens, revocation |
| [API_HEADERS_AND_SECURITY.md](./API_HEADERS_AND_SECURITY.md) | Request/response headers, security posture |
| [CORS_DEEP_DIVE.md](./CORS_DEEP_DIVE.md) | Browser same-origin policy, preflight |
| [RATE_LIMITING_DEEP_DIVE.md](./RATE_LIMITING_DEEP_DIVE.md) | Abuse protection vs idempotency |
| [ERROR_HANDLING_AND_RETRIES.md](./ERROR_HANDLING_AND_RETRIES.md) | Failures, retries, safe behavior |
| [DATABASE_DESIGN.md](./DATABASE_DESIGN.md) | This repo’s schema + extended payment schema |
| [FRONTEND_BACKEND_FLOW.md](./FRONTEND_BACKEND_FLOW.md) | How this React app talks to the API |
| [DEBUGGING_AND_OBSERVABILITY.md](./DEBUGGING_AND_OBSERVABILITY.md) | Logs, correlation IDs, tracing mindset |

---

## Suggested learning path

1. **[SSE vs WebSocket file map](#where-sse-vs-websocket-is-implemented-navigation)** (this README) — know where code lives.  
2. `ARCHITECTURE_OVERVIEW.md` — orient in the codebase.  
3. `FRONTEND_BACKEND_FLOW.md` — SSE + WS in the browser — **this repo**.  
4. `CORS_DEEP_DIVE.md` — why localhost:5173 → localhost:5088 needs config.  
5. `DATABASE_DESIGN.md` — tiny real schema vs “payments” textbook schema.  
6. `IDEMPOTENCY_DEEP_DIVE.md` → `PAYMENT_FLOW.md` → `ERROR_HANDLING_AND_RETRIES.md` — **payments interview block**.  
7. `AUTHENTICATION_JWT_DEEP_DIVE.md` → `USER_SESSION_FLOW.md` — **identity interview block**.  
8. `API_HEADERS_AND_SECURITY.md` + `RATE_LIMITING_DEEP_DIVE.md` + `DEBUGGING_AND_OBSERVABILITY.md` — **ops and hardening block**.

---

## Final note

If you implement JWT, idempotency, and payments **into** this repo later, update the **Feature matrix** at the top of this file so newcomers (and future you) never get confused about what runs in CI.
