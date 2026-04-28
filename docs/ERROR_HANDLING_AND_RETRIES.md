# Error handling and retries — networks lie, clocks skew, humans double-tap

Systems fail openly—TCP timeouts restart flows, VMs vanish mid-request, GPUs overheat. This handbook-style doc classifies failures, explains naive retries creating duplicate side effects, and contrasts **payments + idempotent APIs** versus **this auction demo`.

---

## Master diagram — retry safety (mental model)

```
                    ┌───────────────────────────────────────┐
                    │ Is the HTTP method + path "safe"       │
                    │ to blindly retry?                     │
                    └───────────────────┬───────────────────┘
                                        │
                        ┌───────────────┴───────────────┐
                        │                               │
                       YES                              NO
                        │                               │
                        ▼                               ▼
              ┌──────────────────┐           ┌──────────────────────┐
              │ Typical: GET     │           │ POST /payments etc. │
              │ Retry w/ backoff  │           │ Danger: duplicate $.  │
              │ is often OK       │           └──────────┬────────────┘
              └──────────────────┘                      │
                                                         ▼
                                            ┌────────────────────────┐
                                            │ Needs Idempotency-Key │
                                            │ + stored response DB   │
                                            │ (or reconcile w/ PSP) │
                                            └────────────────────────┘
```

---

## Taxonomy of failures

### 1. Transport / network timeouts

Symptoms:

```
Client: request sent… silence… hangs… finally error
Unknown: maybe server processed; maybe provider charged; maybe nothing
```

**WHY THIS MATTERS:** The **only** safe stance is deterministic **server-side bookkeeping** keyed by caller intent (**idempotency**) plus **query / reconcile**.

---

### 2. Server crashes mid-flight

Classic lost commit:

```
PaymentsService charge OK at Stripe
Crash before COMMIT persistence in your PostgreSQL
Retry from client ──► without idempotency → double charge danger
```

Mitigations:

- Persist **intent** BEFORE external effect OR  
- Align provider-side idempotent keys strictly + reconciliation cron  

---

### 3. Partial success (distributed sagas)

Charge succeeded, fulfillment email failed → user unsure.

Pattern:

```
Saga state machine:

Charged ──► EmailPending ─► EmailSent ─► Done
                         └► retry/backoff ─► alerting if stuck
```

**REAL WORLD EXAMPLE:** Outbox pattern writes domain event + transactional row together — separate worker relays.

---

## Retry behavior illustrations

### Without idempotency (dangerous loops)

```
User clicks PAY
────────────► Request A ──► server charges $100 (success)
Lost response
User clicks PAY again ("nothing happened!")
────────────► Request B ──► server charges AGAIN $100
```

Debt: **Double charge.**

---

### With idempotency (safe retry illusion)

```
Request A POST + Idempotency-Key K + body hash H
Server stores Completed + replay payload

Retry Request A' identical (K+H)
────────────────────────────► instant replay SAME JSON
```

User sees duplicate button clicks—but **financial effect once**.

Diagram:

```
Retries ───► same Idempotency-Key ───► SAME stored HTTP body
                                      (no duplicate provider POST)
```

---

## HTTP status cues (client libraries)

Typical naive rules (simplified):

| Status | Typical retry safe? |
|--------|---------------------|
| 429 Too Many Requests | YES with backoff respecting `Retry-After` |
| 500 Internal Server Error | MAYBE exponential backoff capped |
| 400 Bad Request | NO — payload invalid |
| 409 Conflict idempotency | NO — programmer error key/body mismatch |

**COMMON MISTAKE:** Automated retry loops on **`400`** — infinite stupidity amplification.

---

## Domain-specific: WebSocket auctions (this repo)

Failure modes distinct from REST:

| Issue | Manifestation |
|-------|---------------|
| Send bid while disconnected | UI should queue + warn user |
| Server processed bid, broadcast delayed | Still OK — authoritative state persisted |
| SSE stream drops | Client reconnect/backoff |

**SSE panel** in frontend implements backoff reconnect after transient EventSource failures (controlled—skips retries after intentional disconnect).

**WHY DIFFER:** WebSocket duplication rarely maps 1:1 with HTTP idempotency story unless you unify command handling.

---

## Human factors

**Double-submit:** Prevent via disabled button UI + spinner + idempotent server.

**Slow networks:** Prefer showing **processing** state—not silent failure.

---

## Observability interplay

Failures without trace ids → impossible incident response.

Pair with:

```
Structured logs containing correlation/trace id
Dashboards distinguishing 5xx vs provider decline vs client bug
```

(See `DEBUGGING_AND_OBSERVABILITY.md`.)

---

## Summary matrix

| Layer | Technique |
|-------|-----------|
| Ingress | Retry budgets + backoff + jitter |
| Application | Domain validation + transactional boundaries |
| Financial | Idempotency keys + hashed bodies |
| Messaging | Dedup consumer keys (`message-id`) |
| Client UX | Disabled actions + truthful status text |

Failures are normal; **duplicated monetary effects are not.**
