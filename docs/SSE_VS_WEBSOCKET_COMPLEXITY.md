# SSE vs WebSocket — complexity in *this* codebase

This doc answers: **why does WebSocket “feel heavier” than SSE here?** It is grounded in **actual types and flows** in AuctionSystem—not abstract theory.

---

## TL;DR

| Aspect | **SSE** (`GET /api/auction/sse`) | **WebSocket** (`/ws/auction`) |
|--------|----------------------------------|-------------------------------|
| **Transport** | Stays plain **HTTP**: one response body streamed forever | **Protocol upgrade** (HTTP → WebSocket framing) |
| **Server code surface** | Mostly **one method**: `AuctionController.GetSse` | **Endpoint class** + **connection manager** + **notifier** + **DI quirks** |
| **Connection bookkeeping** | **None** — each SSE client is its own HTTP request lifecycle | **`WebSocketConnectionManager`** — registry, concurrent sends, cleanup |
| **Inbound data** | **None** over the SSE connection (browser cannot POST on that stream) | **Receive loop**, buffer, JSON parsing, validation, error replies |
| **Outbound paths** | **One**: write `data:` lines to **this** response | **Many**: greeting per socket + **fan-out broadcast** after bids |
| **Scoped services** | Normal controller scope — `AuctionService` injected once | **Manual `CreateAsyncScope()`** whenever the handler touches `AuctionService` (WebSocket lifetime ≠ one HTTP request scope) |

**Takeaway:** In this repo, SSE is “**format bytes and loop**.” WebSocket is “**own a long-lived socket, read frames, deserialize, apply domain rules, then coordinate N other sockets**.”

---

## SSE — what the code actually does

Entry point: **`AuctionController.GetSse`** in `src/Api/Controllers/AuctionController.cs`.

Rough structure:

1. Disable buffering, set **SSE headers** (`text/event-stream`, cache control, etc.).  
2. Optional comment line (`: sse-connected`) for the stream.  
3. **`PeriodicTimer`** every 2 seconds.  
4. Each tick: **`GetCurrentAsync`** → serialize JSON → prefix with `data:` and `\n\n` → **`BodyWriter` write + flush**.  
5. Stop when **`CancellationToken`** fires (client gone) → catch `OperationCanceledException`, log.

```45:88:src/Api/Controllers/AuctionController.cs
    [HttpGet("sse")]
    public async Task GetSse(CancellationToken cancellationToken)
    {
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        // ... headers ...
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var dto = await _auctionService.GetCurrentAsync(cancellationToken);
                // ...
                await writer.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE client disconnected.");
        }
    }
```

**Why this is simpler (here):**

- **No** “who is connected?” map — Kestrel’s request is the only thing you write to.  
- **No** incoming application messages on that connection — you only **read** domain state and **echo** it.  
- **No** WebSocket-specific exceptions, message types, or multi-socket fan-out.

**WHY THIS MATTERS:** Complexity is **O(your loop + serialization)** per client, not **O(connections × fan-out)** on the write path (though you still pay **one** DB read per tick per subscriber — scaling SSE is its own ops topic).

---

## WebSocket — what makes it more complex (here)

WebSocket spans **multiple files** and **two layers** (Api + Infrastructure).

### 1) Upgrade handshake and non-controller endpoint

SSE reuses MVC: **`[HttpGet("sse")]`** on `AuctionController`.

WebSockets are mapped with **`app.Map("/ws/auction", …)`** in **`Program.cs`** (extension **`MapAuctionWebSocket`**). The handler must:

- Reject plain HTTP callers: **`IsWebSocketRequest`** else **400**.  
- Call **`AcceptWebSocketAsync()`** — you are now in **WebSocket framing** land, not a normal MVC action body.

See `AuctionWebSocketEndpoint.cs` startup of the delegate.

---

### 2) Connection registry + lifecycle (SSE has no equivalent)

**`WebSocketConnectionManager`** keeps a **`ConcurrentDictionary<Guid, WebSocket>`**. Every accepted socket is **registered**; on exit, **removed** — including cleanup when broadcast fails (`WebSocketException`).

```10:76:src/Infrastructure/WebSockets/WebSocketConnectionManager.cs
public sealed class WebSocketConnectionManager
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _connections = new();
    // ...
    public async Task BroadcastTextAsync(string message, CancellationToken cancellationToken)
    {
        foreach (var (id, socket) in _connections)
        {
            // Skip dead, catch WebSocketException, Remove dead IDs...
        }
    }
}
```

**SSE:** no shared structure — **each subscriber is isolated** behind its own GET.

---

### 3) Two different “send” stories

| Path | Purpose |
|------|---------|
| **After connect** | **`SendCurrentState`** — one JSON message **only to this socket** (`type: connected`). |
| **After a successful bid** | **`AuctionWebSocketNotifier`** — **`BroadcastTextAsync`** to **every** open socket (`type: auction_update`). |
| **After a rejected bid** | **`SendJson`** in the handler — message **only to the bidder** (`type: bid_rejected`). |

So the server maintains **three** outbound semantics; SSE maintains **one** (stream lines to whoever opened that GET).

---

### 4) Scoped DI vs long-lived WebSocket

Controllers get **`AuctionService`** in the normal **request scope**. A WebSocket can live **much longer than** one invocation, so **`AuctionWebSocketEndpoint`** resolves **`AuctionService`** via **`IServiceScopeFactory.CreateAsyncScope()`** inside **`SendCurrentState`** and **`ListenLoop`** (`PlaceBid`). That pattern is mandatory for correctness but adds **mental overhead** versus “inject in constructor.”

---

### 5) Receive loop complexity

The listener:

- **`ReceiveAsync`** into a fixed buffer (**4096 bytes** — large messages must be aggregated in production code; this demo assumes single-frame JSON).  
- Branches **`WebSocketMessageType.Close`**, ignores non-text.  
- **Deserializes** JSON, validates **`bidAmount`**, invokes **`AuctionService.PlaceBidAsync`**, optionally sends **`bid_rejected`** to **this** socket.

That is strictly **more branches** than the SSE timer loop.

---

### 6) Infrastructure adapter for broadcasts

Successful bids trigger **`IAuctionRealtimeNotifier`** — implemented by **`AuctionWebSocketNotifier`**, which serializes envelopes and calls **`BroadcastTextAsync`**. SSE **never** calls this notifier: it **only polls** **`GetCurrentAsync`**.

So WebSocket introduces an **extra port + implementation** that exists **solely** to push to many sockets.

---

## Frontend complexity (brief)

In **`frontend/src/App.tsx`**:

- **`SsePanel`**: **`EventSource`**, **`onmessage`** parses one kind of payload (the streamed JSON body).  
- **`WebSocketPanel`**: **`WebSocket`**, **`onmessage`** dispatches **`type`** (`connected` | `auction_update` | `bid_rejected`), **`send`** for bids, connect/disconnect state.

More **message types** and **user-triggered sends** ⇒ more UI logic — aligned with backend complexity.

---

## Summary (interview soundbite)

> “In our demo, **SSE** is a **thin HTTP stream**: timer, snapshot read, SSE framing. **WebSockets** add **upgrade + connection registry**, **scoped service factories per operation**, **receive/decode/validate loops**, **per-client vs broadcast** sends, and a **fan-out notifier** — that’s why the WebSocket path touches more layers and lines of code even though **both** read the same `AuctionItem` state.”

For file locations, see **`docs/README.md`** → section *Where SSE vs WebSocket is implemented*.
