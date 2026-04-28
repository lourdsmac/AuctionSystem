# Frontend ↔ backend flow — AuctionSystem specifics + REST patterns

This document ties **browser behavior**, **SSE (`EventSource`)**, **WebSockets**, optional **axios/fetch** patterns carrying auth & idempotency headers, mapping every layer to observable reality in **`frontend/`** vs theoretical payment flows.

---

## Architecture slice (implemented)

```
┌─────────────────────────────┐
│ React (Vite SPA) src/App.tsx│
│ ├─ SsePanel ─ EventSource()  │
│ └─ WebSocketPanel ─ WS send  │
└──────────────┬──────────────┘
               │ HTTPS / WSS paths
┌──────────────▼─────────────────────────────┐
│ ASP.NET Api `Program.cs`                   │
│  Serilog ▸ CORS ▸ WebSockets ▸ Controllers │
│  MapAuctionWebSocket()                     │
└────────────────────────────────────────────┘
```

---

## Scenario 1 — live price feed (`EventSource`)

### Client code essence (`frontend/src/App.tsx`)

```tsx
const url = httpUrl('/api/auction/sse');
const es = new EventSource(url);
```

`httpUrl` (`apiConfig.ts`):

- If `VITE_API_BASE` **absent** → `''` → **relative** URLs like `/api/auction/sse` → Vite proxy forwards to the API.  
- If **set** → prepend `http://localhost:5088` (or your staging URL).

### Network behavior

The browser opens a **long-lived HTTP GET** and incrementally reads `text/event-stream` body chunks.

Pseudo wire:

```
GET /api/auction/sse HTTP/1.1
Host: localhost:5173
Accept: text/event-stream
...
```

Server writes comment + repeated `data:` lines (see `AuctionController.GetSse`).

### Why not axios for SSE

`axios` is poor at streaming line-based SSE; `EventSource` is the standard API.

**Edge case:** Some teams need custom headers on SSE (e.g. `Authorization`). `EventSource` historically did not support arbitrary headers in all browsers — patterns include `fetch()` + `ReadableStream` parsing, or cookies for auth.

**WHY THIS MATTERS:** When you add JWT to SSE, validate browser constraints early.

---

## Scenario 2 — bids via WebSockets

Essence:

```ts
const url = webSocketUrl('/ws/auction');
const ws = new WebSocket(url);
ws.send(JSON.stringify({ bidAmount: 105 }));
```

`webSocketUrl`:

- **No** `VITE_API_BASE` → build `ws(s)://<browser-host>/ws/auction` (Vite proxies `/ws` in dev).  
- **With** base → convert `http`→`ws`, `https`→`wss`, append path.

### Message shapes (server → client)

**After connect:**

```json
{
  "type": "connected",
  "auction": {
    "id": 1,
    "name": "Vintage Watch",
    "currentPrice": 100.0,
    "lastUpdated": "2026-04-28T12:34:56.789Z"
  }
}
```

**Broadcast after successful bid:**

```json
{
  "type": "auction_update",
  "payload": {
    "id": 1,
    "name": "Vintage Watch",
    "currentPrice": 105.0,
    "lastUpdated": "2026-04-28T12:35:01.000Z"
  }
}
```

**Rejected bid (server → single connection for error detail):**

```json
{
  "type": "bid_rejected",
  "error": "Bid must be higher than current price (105).",
  "auction": { "...": "..." }
}
```

---

## Hypothetical axios + JWT + idempotency (pattern for future)

Not in this repo; typical production SPA:

```typescript
import axios from 'axios';

const api = axios.create({
  baseURL: import.meta.env.VITE_API_BASE ?? '',
});

api.interceptors.request.use((config) => {
  const token = getAccessJwtFromMemoryOrSecureStore();
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

await api.post(
  '/v1/payments',
  { amount: { currency: 'USD', value: '1999' } },
  {
    headers: {
      'Idempotency-Key': crypto.randomUUID(),
      'Content-Type': 'application/json',
    },
  },
);
```

### Reading custom response headers

```typescript
const replay = response.headers['x-idempotent-replay']; // string | undefined
```

Browser note: CORS may **hide** some headers unless server exposes them via `Access-Control-Expose-Headers`.

---

## How `Authorization` attaches (pattern)

**Mechanically:** request interceptor or wrapper around `fetch` adds `Authorization: Bearer …` just before the network call.

**Security hygiene:** never log bearer tokens; rotate on leak; prefer short-lived access tokens.

---

## CORS + dev proxy interplay

| Setup | Browser sees |
|-------|----------------|
| Vite proxy, relative URLs | Same origin to dev server → fewer CORS preflights |
| Direct `VITE_API_BASE=http://localhost:5088` | Cross-origin → must match `Cors:FrontendOrigins` |

This repo’s API allows `http://localhost:5173` in configuration.

---

## React UI state flows (implemented)

### SSE panel

| Event | UI effect |
|-------|-----------|
| `onopen` | status “Connected” |
| `onmessage` | parse JSON → update price + log |
| `onerror` | unless user clicked Disconnect → backoff reconnect |

Backoff prevents tight loops hammering dead servers.

### WebSocket panel

| Event | UI effect |
|-------|-----------|
| `onopen` | status Connected |
| `onmessage` | route by `type` |
| Bid rejected | show error tone + echoed auction snapshot |

---

## Manual test matrix

| Step | Expected |
|------|-----------|
| Open two tabs, WS Connect both | broadcasts visible on both |
| Bid below active price | `bid_rejected` |
| Backend stopped | SSE retries until manual disconnect |

---

## COMMON MISTAKES

| Mistake | Result |
|---------|--------|
| Forgetting `-N` on `curl` for SSE debugging | buffered output confusion |
| Using `localhost` inside Docker wrongly | websocket refused — use compose service DNS |
| Stale JWT silently on axios retry | unexplained `401` chain |

---

## Summary

Implementations here **prove** duplex vs simplex channels in the browser stack. Extend with JWT + axios when you evolve beyond classroom demos—the layering stays: **transport** (`App.tsx`), **adaptation** (Api), **use cases** (Application), **rules** (Domain).
