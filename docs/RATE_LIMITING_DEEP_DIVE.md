# Rate limiting — deep dive

**Rate limiting** controls how many requests a client (IP, user id, API key) may perform in a time window. It protects **availability** and slows **abuse** — distinct from **idempotency**, which protects **correctness** under retries.

---

## What rate limiting solves

| Problem | Without RL | With RL |
|---------|------------|---------|
| Credential stuffing on `/auth/login` | Mass password attempts | Locked / throttled account or IP |
| Token bucket spam on expensive endpoint | DB meltdown | shedding load |
| Credential stuffing / scraping | Data exfiltration | block + alert |

**WHY THIS MATTERS:** Availability is a security property — an overwhelmed API is offline for everyone.

---

## Mental model: token bucket / leaky bucket

### Token bucket (conceptual)

Think of a bucket that refills **N tokens per second** up to capacity **C**:

```
Request arrives
   │
   ▼
Enough tokens? ──No──► HTTP 429 Too Many Requests (+ Retry-After)
   │
  Yes
   ▼
Consume 1 token → process
```

**REAL WORLD EXAMPLE:** NGINX `limit_req`, Cloudflare WAF, AWS API Gateway throttles, AspNetCore RateLimiter (since .NET 7).

ASCII:

```
        refill rate r
 ─ ─ ─ ─ ►  ( tokens )
┌───────────────────────┐  burst max C
│ o o o · · · · · · · · │
└───────────────────────┘
```

---

## Different limits for different routes

Often:

| Route class | Typical limit stricter? | Reason |
|-------------|---------------------------|--------|
| `/auth/login` | **Yes** (low burst) | Brute-force surface |
| `/auth/password-reset/request` | **Very low** | Email flooding / SMTP abuse |
| `/payments/intent` | Moderate-high | Genuine traffic spikes |

**COMMON MISTAKE:** One-size global RL ignoring heavy vs cheap endpoints → either too loose security-wise or too tight UX-wise.

---

## HTTP surface when limiting

Suggested response skeleton:

```http
HTTP/1.1 429 Too Many Requests
Content-Type: application/json
Retry-After: 12
RateLimit-Limit: 60
RateLimit-Remaining: 0
RateLimit-Reset: 1714462030

{ "title": "Rate limit exceeded", "traceId": "..." }
```

**`Retry-After`** helps friendly clients back off.

**Emerging standardization:** `RateLimit-*` headers (IETF draft era) — vary by platform.

---

## Rate limiting vs idempotency (often confused)

| Axis | Rate limiting | Idempotency |
|------|----------------|--------------|
| **Goal** | Stop abuse / load | Stop duplicate side effects |
| **Typical signal** | IP / user id / API key | `Idempotency-Key` + body hash |
| **Failure mode** | `429` | `409` / replay exact body |
| **Wrong fix** | Idempotency **does not** stop brute force | Rate limit **does not** stop double charge on retry **without** idempotency |

You need **both** where money moves.

ASCII comparison:

```
Brute login attempts ───► Rate limiting (slow them)
Accidental duplicate POST payment ───► Idempotency (same effect)
```

---

## Distributed rate limiting

Single-server in-memory counters are easy — **incorrect** behind load balancers unless **sticky sessions** guarantee same node.

Better:

- Redis INCR + TTL window  
- Dedicated edge (Cloudflare, Kong) enforcing before app compute  

**WHY THIS MATTERS:** Horizontal scaling breaks naive per-process dictionaries.

---

## AuctionSystem status

This sample **does not** include `AspNetCore.RateLimiter` middleware or external stores.

You could annotate later:

```
MapPost("/auth/login").RequireRateLimiting("auth");
```

Plus policies in `Program.cs`.

**Interview soundbite:** “We’d add RL at edge + origin-based budget for `/ws/auction` message flood if abused — maybe per connection + message size caps.”

---

## COMMON MISTAKES

1. Punishing legitimate bursty clients with tiny windows — balance UX vs safety.  
2. Logging full IP indefinitely without privacy policy alignment (GDPR).  
3. Forgetting asymmetric abuse (single IP rotates thousands of IPs — need WAF + anomaly detection).  

---

## Summary checklist (production)

| Control | Addresses |
|---------|-----------|
| Rate limiting | brute force, scraping, volumetric mishaps |
| Idempotency | duplicate financial operations |
| WAF/CDN edge | volumetric DDOS amplification |
| Account lockouts / backoff | credential stuffing fallout |

AuctionSystem exposes **open bids** academically — tighten before any public-facing deployment beyond localhost demos.
