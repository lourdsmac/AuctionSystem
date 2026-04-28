# API headers — requests, responses, and security posture

Headers are contracts between clients, proxies, browsers, and your API surface. This document explains **meaningful headers** listed in our doc plan, **why each exists**, risks if mis-set, plus **AuctionSystem specifics** where this codebase sets real headers (`text/event-stream` SSE, JSON, CORS response headers implicitly via framework).

---

## Request headers clients send

### `Authorization`

**What:** Carries credentials — almost always Bearer tokens:

```http
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6...
```

**Why it matters:** Your API distinguishes anonymous vs authenticated callers. Without validating, anyone can spoof subject claims locally — verification is server-side cryptographic work.

**This repo:** **Not required** — no `[Authorize]` on auction routes.

---

### `Content-Type`

**What:** MIME type describing request body format.

Examples:

```http
Content-Type: application/json
Content-Type: application/x-www-form-urlencoded
```

**Why:** Server picks deserializer safely; prevents parser confusion bugs.

---

### `Accept`

```http
Accept: application/json
```

**What:** Client preference for representation (JSON vs HAL vs XML historically).

**Why:** Helps content negotiation (`406 Not Acceptable` if unsupported).

---

### `Idempotency-Key`

```http
Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000
```

**What:** Client-chosen unique key for **safe retries** of non-idempotent operations (especially `POST` payments).

**Why:** See `IDEMPOTENCY_DEEP_DIVE.md`.

**This repo:** **Not implemented** on HTTP — WebSocket bids are not payment-grade idempotent.

---

### `Origin`

```http
Origin: http://localhost:5173
```

**What:** Browser sends origin for **cross-origin** requests.

**Why:** Server CORS policy checks this value (not `Host` alone) to decide `Access-Control-Allow-Origin`.

**REAL WORLD EXAMPLE:** Your API returns `Access-Control-Allow-Origin: https://app.yourcompany.com` — not `*` if cookies/credentials matter.

---

### `User-Agent`

**What:** Client software string (browser, mobile app).

**Why:** Abuse detection, analytics, sometimes vendor workarounds — **never** trust for security alone (spoofable).

---

### `X-Correlation-ID` (or `traceparent` in OpenTelemetry)

```http
X-Correlation-ID: 4bf92f3577b34da6a3ce929d0e0e4736
```

**What:** End-to-end identifier threading logs across services.

**Why:** Support answers “show me all logs for user’s checkout at 14:02” without grep chaos.

**This repo:** **Not auto-minted** — Serilog request logging uses framework defaults; you could add middleware (see `DEBUGGING_AND_OBSERVABILITY.md`).

---

## Response headers servers return

### `X-Idempotent-Replay` (convention)

```http
X-Idempotent-Replay: true
```

**What:** Custom header signaling “this response body was **replayed** from original idempotency storage.”

**Why:** Clients / SDKs can log analytics differently; debugging duplicate paths.

**Not standardized** — many teams invent similar names — document yours.

---

### `X-Correlation-ID` (echo)

```http
X-Correlation-ID: 4bf92f3577b34da6a3ce929d0e0e4736
```

**What:** Echo accepted or generated correlation id so clients include in support tickets.

---

### `X-Content-Type-Options: nosniff`

**What:** Tells browsers not to MIME-sniff into executable types.

**Why:** Prevents obscure XSS vectors when hosting user-controlled files.

**ASP.NET:** Often defaulted or added via security headers middleware in hardened apps — **explicitly audit** yours.

---

### `X-Frame-Options` or CSP `frame-ancestors`

**What:**

```http
X-Frame-Options: DENY
```

**Why:** Reduce **clickjacking** — attackers embedding your checkout in invisible iframe.

Modern alternative: CSP `frame-ancestors 'none'`.

---

### `Referrer-Policy`

Examples:

```http
Referrer-Policy: strict-origin-when-cross-origin
```

**What:** Controls leakage of referring URL across origins.

**Why:** Privacy — avoid leaking sensitive query params to third-party analytics unintentionally.

---

### `Content-Security-Policy` (CSP)

Example fragment:

```http
Content-Security-Policy: default-src 'self'; script-src 'self'; object-src 'none'
```

**What:** Declares where scripts/styles/images may load — primary **XSS containment** lever for browsers that honor CSP.

**Why:** XSS is still rampant — CSP shrinks blast radius though config is fiddly.

**COMMON MISTAKE:** Overly lax `script-src 'unsafe-inline'` negates benefits.

---

## AuctionSystem — headers you can observe today

### `GET /api/auction`

Typical skeleton:

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Server: Kestrel
```

(Serilog request logging prints structured lines separately.)

### `GET /api/auction/sse` (**Server-Sent Events**)

Important ones set in **`AuctionController.GetSse`**:

```http
HTTP/1.1 200 OK
Content-Type: text/event-stream; charset=utf-8
Cache-Control: no-cache, no-transform
Pragma: no-cache
Connection: keep-alive
X-Accel-Buffering: no
```

**WHY EACH:**

| Header | Meaning |
|--------|---------|
| `text/event-stream` | Identifies an **event stream** per HTML5 SSE spec semantics. |
| `no-cache, no-transform` | Discourage intermediary caching / compression transforms mangling chunked stream. |
| `Connection: keep-alive` | Hints reuse on HTTP/1.1 pipelines (semantics nuanced behind proxies). |
| `X-Accel-Buffering: nginx` | Disables proxy buffering that would delay live events. |

**Edge case:** HTTP/2 multiplexing changes some HTTP/1.1 header nuances — proxies still inspect `Cache-Control`.

---

### WebSocket upgrade (101 Switching Protocols)

First response:

```http
HTTP/1.1 101 Switching Protocols
Connection: Upgrade
Upgrade: websocket
Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=
```

**Why:** Not JSON — establishes raw framed channel.

---

### CORS preflight responses (automatic when needed)

OPTIONS response might include:

```http
Access-Control-Allow-Origin: http://localhost:5173
Access-Control-Allow-Methods: GET, POST, OPTIONS
Access-Control-Allow-Headers: Authorization, Content-Type
```

Configured in **`Program.cs`** policy `Frontend` + `appsettings.json` origins.

---

## “WHY THIS MATTERS” — layering security headers

Defense in depth sketch:

```
┌───────────────────────────────────────────────┐
│ TLS (encryption in transit)                 │
└─────────────────┬─────────────────────────────┘
                  ▼
┌───────────────────────────────────────────────┐
│ JWT validation / CSRF defenses for cookies  │
└─────────────────┬─────────────────────────────┘
                  ▼
┌───────────────────────────────────────────────┐
│ Security headers influencing browser handling │
└─────────────────┬─────────────────────────────┘
                  ▼
┌───────────────────────────────────────────────┐
│ App authorization + input validation           │
└─────────────────────────────────────────────────┘
```

---

## COMMON MISTAKES

1. Believing **`Authorization`** implies **authorization** (permission checks) — it’s merely **credentials** naming collision.  
2. Adding heavy CSP blindly — breaks SPA script chunks until tuned.  
3. Logging **Authorization** verbatim in ELK stacks — leakage of bearer tokens across teams.  

---

## Summary cheat sheet

| Header | Category | Punchline |
|--------|----------|-----------|
| `Authorization` | Request proof | Bearer JWT / scheme-specific |
| `Idempotency-Key` | Request safety | dedupe retries |
| `Origin` | Browser CORS | cross-site policy input |
| `Content-Type` / `Accept` | Serialization | parsers + negotiation |
| `text/event-stream` | SSE semantics | chunked live feed |
| `101 Switching Protocols` | WebSocket handshake | duplex channel replaces HTTP body |
| CSP / XFO / nosniff | Browser guardrails | reduce XSS / clickjack |

Tune headers deliberately — they are silent compliance + security contracts.
