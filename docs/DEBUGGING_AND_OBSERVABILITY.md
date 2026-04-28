# Debugging and observability — logs, traces, correlation

Observability = **knowing what's happening inside** a distributed-ish system faster than blind guessing. This doc covers **structured logging (Serilog here)**, **correlation IDs**, tracing concepts, **what’s missing in AuctionSystem** today, and how you’d scale up.

---

## Pillars (quick)

| Pillar | Question it answers | Typical tools |
|--------|---------------------|---------------|
| **Logs** | Discrete timestamped events | Serilog, ELK, Loki |
| **Metrics** | Rates, histograms, saturation | Prometheus, CloudWatch |
| **Traces** | End-to-end latency segments | OpenTelemetry, Jaeger |

**WHY THIS MATTERS:** Interviews expect you to connect user symptom → trace id → single root log line.

---

## Where Serilog fits (actual repo)

`Program.cs`:

```csharp
builder.Host.UseSerilog((ctx, services, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .ReadFrom.Services(services)
       .Enrich.FromLogContext();
});
app.UseSerilogRequestLogging();
```

Effects:

- HTTP requests log **method / path / status / duration** automatically.  
- Application code uses `ILogger<T>` for bid acceptance, broadcast fan-out, db seeding.

**Sample console line shape (illustrative):**

```
[12:34:56 INF] AuctionSystem.Application.Services.AuctionService: Accepted bid 105; new price 105
```

**REAL WORLD EXAMPLE:** Ship logs to JSON lines + ship to Splunk / Datadog with environment tags.

---

## Correlation ID flow (ideal target)

### Without correlation

```
User: "Checkout failed at 14:02"
You: grep entire cluster hopelessly
```

### With correlation

```
Incoming HTTP
  X-Correlation-ID: (client optional)
    │
    ├─ If absent → generate GUID
    │
    ▼
Middleware pushes into LogContext
    │
    ├─ Propagate to downstream HTTP calls (header forward)
    ├─ Include in WebSocket accept log scope
    └─ Return echo header in response
```

ASCII:

```
Request { corr=abc }
   └──► Log: [corr=abc] AuctionService bid…
            └──► Outbound HTTP to Stripe { corr=abc }
```

**This repo:** **Not** implemented as middleware — easy future addition:

```csharp
app.Use(async (ctx, next) =>
{
    var id = ctx.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
    using (Serilog.Context.LogContext.PushProperty("CorrelationId", id))
    {
        ctx.Response.Headers["X-Correlation-ID"] = id;
        await next();
    }
});
```

**COMMON MISTAKE:** Accepting **unsanitized** correlation strings from clients without length limits — potential log injection.

---

## OpenTelemetry traceparent (modern variant)

W3C trace context header:

```http
traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01
```

Segments: version, trace-id, parent span id, flags.

**Why adopt:** Automatic cross-service stitching when each hop emits compliant spans.

---

## Auction-specific logging touchpoints

| Area | What you’ll see |
|------|-----------------|
| Db init | Seeded auction row message |
| SSE connect | remote IP log line |
| WebSocket | register / remove / broadcast debug or info |
| Bid | accepted vs rejected reasons |

**Use these** when stepping through local demo.

---

## Debugging workflows (practical)

### Local

1. Run API with `ASPNETCORE_ENVIRONMENT=Development` (richer errors—careful exposure).  
2. Mirror requests with `curl -v` showing headers.  
3. For SSE: `curl -svN` to confirm streaming not buffered.  
4. For WS: `wscat -c ws://localhost:5088/ws/auction`.

### Staging

- Centralized log search by `path`, `status`, `duration p95`.  
- Compare deploy markers vs error spike.

### Incident

1. Identify **first failing hop** (edge vs app vs DB).  
2. Pull **trace** if available.  
3. Roll forward fix + add missing assertion log line.

---

## Metrics you’d add next

| Metric | Purpose |
|--------|---------|
| `http_server_duration_seconds` histogram | latency SLO |
| `websocket_active_connections` gauge | capacity planning |
| `payment_intent_failures_total` counter | business monitoring |

Auction demo might still add simple `/health` + open socket gauge.

---

## Privacy & compliance

- Scrub `Authorization` headers before persisting raw HTTP logs.  
- Hash user identifiers in analytics if policy demands.

---

## Summary gap list (learning → implement)

| Feature | Status in repo |
|---------|----------------|
| Serilog pipeline | ✅ |
| Request logging | ✅ |
| Correlation middleware | ❌ (pattern above) |
| OTel traces | ❌ |
| Metrics endpoint | ❌ |

Observability is iterative—**start with structured logs + unique request ids**, grow into traces when microservices multiply.
