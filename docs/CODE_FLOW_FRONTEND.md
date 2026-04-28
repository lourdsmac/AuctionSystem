# Code flow — frontend (React) → backend → UI

This document is for readers who are **new to React**. It walks **entry point → user actions → network → server → back to the screen**, using the **exact files and functions** in this repository.

---

## React in one minute (just enough context)

- **React** is a library for building UIs with **components** (functions that return **JSX** — HTML-like markup in JavaScript/TypeScript).
- When **state** (`useState`) changes, React **re-renders** the component so the DOM matches the new data.
- **`onClick={connect}`** means: when the user clicks, run the function named `connect` — that is how buttons trigger behavior.

You do **not** need to master React to follow the flows below — treat each numbered step as a checklist.

---

## 1. Where the frontend starts (entry → first paint)

### 1.1 The HTML shell — `frontend/index.html`

Browsers load **HTML first**. This file:

- Loads fonts and metadata.  
- Declares **`<div id="root"></div>`** — an **empty placeholder** where the app will be injected.  
- Loads the JavaScript bundle via **`<script type="module" src="/src/main.tsx"></script>`** (Vite resolves this in development and build).

Nothing “React-specific” renders yet until that script runs.

```15:16:frontend/index.html
    <div id="root"></div>
    <script type="module" src="/src/main.tsx"></script>
```

---

### 1.2 Bootstrap — `frontend/src/main.tsx`

This is the **JavaScript entry point**.

1. Imports **React** and **`react-dom/client`**.  
2. Imports **`App`** from `./App` (your real UI lives there).  
3. Imports **`index.css`** (global styles).  
4. Finds **`document.getElementById('root')`** and calls **`ReactDOM.createRoot(...).render(...)`** — that **mounts** React onto the empty `root` div.  
5. Wraps **`App`** in **`React.StrictMode`** (development checks — optional to understand).

```1:10:frontend/src/main.tsx
import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
```

**Note:** Many tutorials use **`main.jsx`** or **`index.js`**. **This repo uses TypeScript:** **`main.tsx`** (TS + JSX).

---

### 1.3 Top-level UI — `App` in `frontend/src/App.tsx`

**`App`** is a **component function** exported as **`export default function App()`**. It renders:

- A title and short description  
- **`SsePanel`** (left column — SSE)  
- **`WebSocketPanel`** (right column — WebSockets)  
- A footer note about `VITE_API_BASE`

Both panels are **sibling components** inside a **`<div className="grid">`**.

```20:38:frontend/src/App.tsx
export default function App() {
  return (
    <>
      <h1>Auction demo: SSE vs WebSocket</h1>
      ...
      <div className="grid">
        <SsePanel />
        <WebSocketPanel />
      </div>
      ...
    </>
  );
}
```

**Visualization — bootstrap chain:**

```
index.html
    │ loads
    ▼
main.tsx  ─── render(<App />)
    │
    ▼
App.tsx  ─── renders SsePanel + WebSocketPanel
    │
    ├─────────────────────┬───────────────────────┐
    ▼                     ▼                       ▼
 SsePanel            WebSocketPanel          (static text / footer)
```

---

## 2. Supporting files — URLs for API and WebSocket (`apiConfig.ts`)

**`frontend/src/apiConfig.ts`** does **not** render UI. It builds URLs:

| Function | Role |
|----------|------|
| **`getHttpBase()`** | Reads **`VITE_API_BASE`** env var; empty string means “same origin as the page” (Vite proxy in dev). |
| **`httpUrl('/api/auction/sse')`** | Builds `http(s)://.../api/auction/sse` or a **relative** `/api/auction/sse`. |
| **`webSocketUrl('/ws/auction')`** | Builds `ws(s)://host/ws/auction` (or ties to **`VITE_API_BASE`** scheme). |

`SsePanel` and **`WebSocketPanel`** import these helpers — **no axios** in this demo for these transports.

---

## 3. File / responsibility map

| Responsibility | File | Key symbols |
|----------------|------|--------------|
| HTML shell | `frontend/index.html` | `#root`, script tag |
| React mount | `frontend/src/main.tsx` | `createRoot`, `<App />` |
| Layout + two panels | `frontend/src/App.tsx` | `App`, `<SsePanel />`, `<WebSocketPanel />` |
| SSE UI + logic | **Same file** (`App.tsx`) | **`function SsePanel()`**, `connect`, `disconnect`, `EventSource` |
| WebSocket UI + logic | **Same file** (`App.tsx`) | **`function WebSocketPanel()`**, `connect`, `sendBid`, `WebSocket` |
| URL helpers | `frontend/src/apiConfig.ts` | `httpUrl`, `webSocketUrl` |

---

## 4. User clicks “Connect” — SSE (`SsePanel`)

### What the button does

The **Connect** button is:

```tsx
<button type="button" className="primary-sse" onClick={connect}>
  Connect
</button>
```

**`onClick={connect}`** binds the click to the **`connect`** function defined **inside `SsePanel`** (`useCallback`).

---

### Step-by-step sequence (SSE)

| Step | What happens |
|------|----------------|
| **1** | User clicks **Connect**. |
| **2** | React runs **`SsePanel`’s `connect`** (`App.tsx`). |
| **3** | `stoppedByUser.current = false`; closes any existing `EventSource`. |
| **4** | **`url = httpUrl('/api/auction/sse')`** — computes URL string (proxy or absolute API). |
| **5** | **`const es = new EventSource(url)`** — browser starts **HTTP GET** to that URL and keeps the connection open for **Server-Sent Events**. |
| **6** | Server responds with **`Content-Type: text/event-stream`** and streams bytes (see backend). |
| **7** | **`es.onopen`** fires → **`setStatus('Connected')`** etc. → React re-renders status line. |
| **8** | Each time the server pushes an event line **`data: {...}`**, **`es.onmessage`** runs → **`JSON.parse(ev.data)`** → **`setAuction(data)`** → React re-shows price. |
| **9** | On error (and if user did **not** click Disconnect), **`onerror`** schedules **`setTimeout(() => connect(), delay)`** to retry. |

**Disconnect:**

| Step | What happens |
|------|----------------|
| **1** | User clicks **Disconnect**. |
| **2** | **`disconnect`** sets **`stoppedByUser.current = true`**, **`esRef.current?.close()`** — browser tears down the SSE connection. |
| **3** | Status returns to disconnected; **`onerror`** will **not** auto-reconnect because **`stoppedByUser`** is true. |

---

### ASCII sequence diagram — SSE Connect → streamed updates

```
User          SsePanel (React)        Browser APIs          Network               Backend
  │                 │                       │                     │                       │
  │ click Connect    │                       │                     │                       │
  ├────────────────>│ httpUrl(...)          │                     │                       │
  │                 │ new EventSource(url) ─────────────────────► GET /api/auction/sse   │
  │                 │                       │                     ├──────────────────────>│ AuctionController.GetSse
  │                 │                       │◄── text/event-stream                       │
  │                 │ onopen ──────────────►│                     │ chunks : + data:    │
  │                 │ setStatus(Connected)  │                     │                       │
  │◄────────────────┤ render                │                     │                       │
  │                 │ onmessage ───────────►│ parse JSON           │ periodic data:{...}  │
  │                 │ setAuction(dto)       │                     │◄──────────────────────│
  │ sees new price ─┤                       │                     │                       │
```

---

## 5. User clicks “Connect” — WebSocket (`WebSocketPanel`)

### What the button does

```tsx
<button type="button" className="primary-ws" onClick={connect}>Connect</button>
```

Same React pattern — **`WebSocketPanel`’s `connect`** runs.

---

### Step-by-step sequence (WebSocket)

| Step | What happens |
|------|----------------|
| **1** | User clicks **Connect**. |
| **2** | React runs **`connect`** (`App.tsx`, inside **`WebSocketPanel`**). |
| **3** | If a socket is already **`OPEN`**, log “already connected” and return. |
| **4** | **`url = webSocketUrl('/ws/auction')`** — **`ws:`** or **`wss:`** URL. |
| **5** | **`const ws = new WebSocket(url)`** — browser performs **HTTP Upgrade** handshake to **`/ws/auction`**. |
| **6** | Backend **`AcceptWebSocketAsync`** (`AuctionWebSocketEndpoint`) — see backend section. |
| **7** | **`ws.onopen`** → status **Connected**. |
| **8** | Server sends initial JSON **`{ type: "connected", auction: {...} }`** → **`ws.onmessage`** → **`applyMessage`** → **`setAuction`** — price appears. |
| **9** | Later frames (e.g. after someone bids elsewhere): **`auction_update`** — same **`applyMessage`** path. |

---

### ASCII sequence diagram — WebSocket Connect

```
User       WebSocketPanel (React)     Browser APIs              Backend (summary)
 │                │                         │                             │
 │ click Connect │ webSocketUrl('/ws/auction')│                            │
 │──────────────>│ new WebSocket(url) ───────► Upgrade GET /ws/auction ────► MapAuctionWebSocket
 │                │                           │ ◄──── 101 + WebSocket ◄────  AcceptWebSocketAsync
 │                │ onopen                    │ SendCurrentState ─────────►  JSON greeting
 │                │ onmessage (connected) ───►│                             │
 │ sees price ────┤ applyMessage ─ setAuction │                             │
```

### WebSocket server “listen loop” vs one-shot HTTP request/response

After **Connect**, each bid is **not** a new `POST /api/...` URL. The server **`ListenLoop`** in `AuctionWebSocketEndpoint` uses **`ReceiveAsync`** in a **`while`** — each wake-up is one **iteration** (like a “tick”) when a **text frame** arrives. Replies are **WebSocket frames** on the same long-lived connection. See **`ARCHITECTURE_OVERVIEW.md`** for the full ASCII contrast with REST and SSE.

```
  HTTP GET snapshot:   client ----request----> server ----200 JSON----> client (done)

  WebSocket:           client ----Upgrade----> server ----101 + frames---
                       same connection:  client --text bid--> server --text update--> client (repeat)
```

---

## 6. User clicks “Send bid” — WebSocket only

The **Send bid** button:

```tsx
<button type="button" onClick={sendBid}>Send bid</button>
```

Bid amount comes from **controlled input**: **`value={bid}`**, **`onChange={(e) => setBid(e.target.value)}`** — typing updates React state **`bid`** (string).

---

### Step-by-step — send bid

| Step | What happens |
|------|----------------|
| **1** | User enters amount (optional) and clicks **Send bid**. |
| **2** | **`sendBid`** runs. If socket not **`OPEN`**, logs “not connected” and returns. |
| **3** | **`Number(bid)`** validated; must be finite and **> 0**. |
| **4** | **`ws.send(JSON.stringify({ bidAmount: n }))`** — browser sends **one text frame** to the server (not HTTP POST). |
| **5** | Backend **`ListenLoop`** → **`ReceiveAsync`** → **`JsonSerializer.Deserialize<BidMessage>`** → **`AuctionService.PlaceBidAsync`**. |
| **6a** | **If bid rejected:** server sends **`{ type: "bid_rejected", ... }`** → **`applyMessage`** → status line + **`setAuction`** if included. |
| **6b** | **If bid accepted:** **`IAuctionRealtimeNotifier`** broadcasts **`auction_update`** to **all** WebSocket clients → every open tab **`onmessage`** updates price. |

---

### ASCII sequence — successful bid + broadcast

```
User A tab     WebSocketPanel          Browser              Backend                    
     │               │                     │                       │
     │ Send bid ────>│ ws.send(JSON) ─────►│───────────────────────►│ PlaceBidAsync → DB
     │               │                     │                       │ Broadcast to ALL sockets
     │               │ onmessage ◄───────────│◄──────────────────────┤ auction_update
User B tab     WebSocketPanel          (same message)              │
     │               │ onmessage ◄─────────│◄──────────────────────┤
     │ sees new $ ───┤ setAuction          │                       │
```



---

## 7. End-to-end: SSE (UI → server → UI)

| Layer | Role in this project |
|-------|------------------------|
| **UI** | `SsePanel` — `EventSource`, `onmessage` → `setAuction` |
| **HTTP** | Long-lived **GET** `/api/auction/sse` |
| **Backend** | `AuctionController.GetSse` — `PeriodicTimer`, `GetCurrentAsync`, SSE `data:` lines |
| **App logic** | `AuctionService.GetCurrentAsync` → `EfAuctionRepository` snapshot |
| **Back to UI** | Each `data:` line triggers `onmessage` → new price on screen |

**SSE does not** call `WebSocketConnectionManager` or `PlaceBid` — read-only streaming.

---

## 8. End-to-end: WebSocket (UI → server → broadcast → UI)

| Layer | Role in this project |
|-------|------------------------|
| **UI** | `WebSocketPanel` — `send` / `onmessage`; **`applyMessage`** branches on **`type`** |
| **Wire** | **WebSocket** frames to **`/ws/auction`** |
| **Backend entry** | `AuctionWebSocketEndpoint.MapAuctionWebSocket` |
| **Bid logic** | `AuctionService.PlaceBidAsync` → domain → **`IAuctionRealtimeNotifier`** |
| **Fan-out** | `AuctionWebSocketNotifier` → `WebSocketConnectionManager.BroadcastTextAsync` |
| **Back to UI** | Every connected tab receives **`auction_update`** and updates **`auction` state** |

---

## 9. Backend files (paired with frontend actions)

| Frontend action | Backend files / functions |
|-----------------|---------------------------|
| SSE Connect | **`src/Api/Controllers/AuctionController.cs`** → **`GetSse`** |
| WebSocket Connect | **`src/Api/AuctionWebSocketEndpoint.cs`** → **`MapAuctionWebSocket`**, **`SendCurrentState`** |
| Send bid | Same file → **`ListenLoop`**, **`AuctionService.PlaceBidAsync`** |
| Broadcast | **`src/Infrastructure/WebSockets/AuctionWebSocketNotifier.cs`**, **`WebSocketConnectionManager.cs`** |

Server startup: **`src/Api/Program.cs`** — `UseWebSockets`, `MapControllers`, `MapAuctionWebSocket`.

---

## 10. Quick reference — “what runs when?”

```
SSE Connect     → EventSource(httpUrl(...)) → GET /api/auction/sse → GetSse stream
SSE tick        → (server pushes) → onmessage → setAuction

WS Connect      → WebSocket(webSocketUrl(...)) → Upgrade /ws/auction → SendCurrentState JSON
Send bid        → ws.send(...) → ListenLoop → PlaceBidAsync → notifier → broadcast JSON
Both UIs update → setAuction(...) → React re-renders price <div>
```

---

## 11. Glossary (for React newcomers)

| Term | Meaning here |
|------|----------------|
| **Component** | A function like **`SsePanel`** that returns JSX. |
| **`useState`** | Holds values that, when updated, trigger a re-render (`status`, `auction`, `bid`, …). |
| **`useRef`** | Holds mutable values that **do not** trigger re-render (`esRef`, `wsRef` — the live browser connection objects). |
| **`useCallback`** | Memoizes a function so it is stable between renders (`connect`, `applyMessage`). |
| **`onClick` / `onChange`** | DOM events wired to your functions. |

---

For **where** each file sits in the repo tree (navigation table), see **`README.md`** in this folder — section *Where SSE vs WebSocket is implemented*. For **why** WebSocket has more server-side moving parts, see **`SSE_VS_WEBSOCKET_COMPLEXITY.md`**.
