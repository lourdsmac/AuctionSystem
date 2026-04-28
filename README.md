# AuctionSystem — SSE vs WebSocket demo

End-to-end sample that contrasts **Server-Sent Events (SSE)** with **WebSockets** using a small **auction** simulation: shared in-memory state (EF Core), Clean Architecture on the backend, and a React UI with two side-by-side panels.

**Extended handbook (engineering + system-design deep dives):** see **[docs/README.md](docs/README.md)** — includes onboarding, interview-style explanations (idempotency, JWT, payments, CORS), and an honest matrix of **implemented vs conceptual** topics.

### Where the code lives (SSE vs WebSocket)

| | **SSE** | **WebSocket** |
|---|--------|---------------|
| **Route** | `GET /api/auction/sse` | `/ws/auction` (`ws://…` or proxied) |
| **Server** | `src/Api/Controllers/AuctionController.cs` → `GetSse` | `src/Api/AuctionWebSocketEndpoint.cs` + `Program.cs` → `MapAuctionWebSocket()` |
| **Broadcast** | _(per-connection stream only; no shared “WS manager”)_ | `src/Infrastructure/WebSockets/WebSocketConnectionManager.cs` + `AuctionWebSocketNotifier.cs` |
| **UI** | `frontend/src/App.tsx` → `SsePanel` (`EventSource`) | `WebSocketPanel` (`WebSocket`) |

---

## Architecture (text diagram)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              Browser (React)                                 │
├───────────────────────────────┬─────────────────────────────────────────────┤
│  LEFT: EventSource           │  RIGHT: WebSocket                            │
│  GET /api/auction/sse        │  WS /ws/auction                             │
│  (read-only snapshots)       │  JSON bids + broadcast payloads            │
└───────────────┬───────────────┴─────────────────────┬───────────────────────┘
                │ text/event-stream                    │ bid JSON + broadcasts
                ▼                                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         AuctionSystem.Api (ASP.NET Core 8)                  │
│  Controllers (thin)  │  AuctionWebSocketEndpoint (upgrade + receive loop)  │
└───────────────┬─────────────────────────────────────┬───────────────────────┘
                │                                     │
                ▼                                     ▼
┌───────────────────────────────┐    ┌────────────────────────────────────────┐
│      Application              │    │         Infrastructure                 │
│  AuctionService               │    │  AppDbContext (InMemory)             │
│  (orchestration + logging)    │    │  EfAuctionRepository                 │
│                               │    │  WebSocketConnectionManager          │
│                               │    │  AuctionWebSocketNotifier (broadcast)│
└───────────────┬───────────────┘    └──────────────────┬─────────────────────┘
                │                                        │
                ▼                                        ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                              Domain                                          │
│  AuctionItem.TryApplyBid — bid must exceed CurrentPrice; updates timestamp  │
└─────────────────────────────────────────────────────────────────────────────┘
```

**Layer roles**

| Layer | Responsibility |
|--------|----------------|
| **Domain** | `AuctionItem` and bid validation rules (`TryApplyBid`). |
| **Application** | `AuctionService`, DTOs, ports (`IAuctionRepository`, `IAuctionRealtimeNotifier`). |
| **Infrastructure** | EF Core in-memory DB, repository, WebSocket connection registry + broadcast. |
| **Api** | HTTP + SSE + WebSocket upgrade; no business rules. |

---

## Request flows

### SSE — `GET /api/auction/sse`

1. Browser opens a long-lived HTTP response with `Content-Type: text/event-stream`.
2. Server disables response buffering, sets cache/no-transform headers, and may send comment lines (`: sse-connected`) for compatibility.
3. Every **2 seconds** the server writes a line of the form `data: {json}\n\n` and flushes via `HttpResponse.BodyWriter` (Kestrel `PipeWriter`).
4. The client **cannot** send a body on the same channel; new input would require a **separate** HTTP request (not used here). That is why this demo’s UI is **read-only**.

### WebSocket — `GET /ws/auction` (upgrade)

1. Client sends an HTTP request with `Connection: Upgrade`, `Upgrade: websocket`, and the WebSocket handshake headers (`Sec-WebSocket-Key`, `Sec-WebSocket-Version`, …).
2. ASP.NET Core completes the **101 Switching Protocols** response; the socket becomes a full-duplex binary/text channel.
3. Client may send text frames, e.g. `{ "bidAmount": 120 }`.
4. Server resolves a scoped `AuctionService`, validates the bid in the domain, persists, then `IAuctionRealtimeNotifier` pushes JSON to **every** open socket (including the bidder).

---

## Why SSE is one-way

- SSE is a **single long-lived HTTP response** streamed from server to client. The browser `EventSource` API is **receive-only**; there is no standard way to attach an upload stream to the same connection.
- To send data to the server you use **separate** requests (POST/PUT) or another channel (WebSocket, fetch).

## Why WebSocket is two-way

- After the upgrade, the connection is **not** request/response bound: both sides can send **frames** at any time (text or binary), enabling chat, collaborative apps, and bidirectional commands like **placing a bid**.

## When to use which

| Technology | Good for | Less ideal for |
|------------|----------|----------------|
| **SSE** | Live tickers, notifications, read-only dashboards, fan-out from server | Client commands on the same channel, binary payloads, very high message rates |
| **WebSocket** | Chat, auctions, games, collaborative editing, bidirectional RPC | Simple “server notify me” where HTTP polling or SSE suffices |

---

## HTTP details implemented

### SSE response headers (see `AuctionController.GetSse`)

- `Content-Type: text/event-stream; charset=utf-8` — identifies an event stream.
- `Cache-Control: no-cache, no-transform` — discourage intermediaries from coalescing or caching.
- `Connection: keep-alive` — hint for HTTP/1.1 connection reuse (Kestrel still streams the body).
- `X-Accel-Buffering: no` — common with nginx to disable buffering of streams.
- `IHttpResponseBodyFeature.DisableBuffering()` — avoid buffering the stream in the host.

### WebSocket

- `app.UseWebSockets(...)` enables the upgrade pipeline; `AcceptWebSocketAsync()` completes the handshake.
- Subprotocols are optional; this demo uses default.

### CORS

- Configured in `Program.cs` under policy `Frontend` with origins from `Cors:FrontendOrigins` (defaults include `http://localhost:5173`).
- Required when the SPA is served from a **different origin** than the API (e.g. Vite on 5173, API on 5088).

---

## Logging (Serilog)

- Request logging via `UseSerilogRequestLogging`.
- Application logs accepted/rejected bids; infrastructure logs WebSocket register/remove and broadcasts; API logs SSE connects.

---

## How to run

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org/) (for the frontend)

### Backend

```bash
cd /path/to/AuctionSystem
dotnet run --project src/Api/AuctionSystem.Api.csproj
```

- Default URL: **http://localhost:5088** (see `src/Api/Properties/launchSettings.json`).
- REST: **GET** `http://localhost:5088/api/auction`
- SSE: **GET** `http://localhost:5088/api/auction/sse`
- WebSocket: **ws://localhost:5088/ws/auction`

### Frontend

**Option A — Vite dev server (recommended for development)**

Uses the proxy in `frontend/vite.config.ts` so relative `/api` and `/ws` calls go to `http://localhost:5088` (override with `VITE_DEV_PROXY_TARGET`).

```bash
cd frontend
npm install
npm run dev
```

- UI: **http://localhost:5173**

**Option B — Direct API URL**

Create `frontend/.env`:

```env
VITE_API_BASE=http://localhost:5088
```

Then `npm run dev` — the app will call the API origin directly (CORS must allow the dev origin; already configured for 5173).

### Production build (static files)

```bash
cd frontend
npm run build
```

Serve `frontend/dist` with any static host; set `VITE_API_BASE` at build time to your API’s public URL.

---

## Definition of done (checklist)

- [x] SSE streams JSON snapshots every 2 seconds from shared auction state.
- [x] WebSocket accepts `{ "bidAmount": number }`, validates in domain, persists, broadcasts to all clients.
- [x] Multiple browser tabs/clients see WebSocket broadcasts; SSE reflects updated price on the next tick.
- [x] Clean Architecture layout under `src/` + `frontend/`.
- [x] Serilog for connections, bids, broadcasts.
- [x] README documents architecture, flows, and trade-offs.

---

## Optional extensions

- **Reconnect**: SSE panel retries with exponential backoff (skips retry after manual disconnect). You can add similar logic for WebSockets.
- **Multiple items**: Add `AuctionItem` rows and parameterize routes/SSE topics.
- **Styling**: The React UI already includes a compact dark theme for clarity in demos.

---

## License

Sample code for learning and demonstration; use freely in your own projects.
