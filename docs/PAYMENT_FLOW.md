# Payment flow — lifecycle and duplicate protection

This document describes a **typical production payment lifecycle** through an API — idempotency service, payment service, **fake** or real provider, database — and maps concepts to **AuctionSystem** (which only simulates bidding, not card networks).

---

## Big picture

```
┌──────────┐     HTTPS      ┌─────────────┐    ┌──────────────────┐    ┌─────────────────┐
│ Frontend │ ─────────────► │  API layer  │───►│ Idempotency gate │───►│ PaymentService  │
└──────────┘                └──────┬──────┘    └────────┬─────────┘    └────────┬────────┘
                                   │                   │                        │
                                   │                   │                        ▼
                                   │                   │                 ┌──────────────┐
                                   │                   │                 │FakeProvider │
                                   │                   │                 │ or Stripe    │
                                   │                   │                 └──────┬───────┘
                                   │                   │                        │
                                   │                   ▼                        ▼
                                   │            ┌────────────────────────────────────┐
                                   │            │ Database: payments, idempotency     │
                                   │            └────────────────────────────────────┘
                                   ▼
                            HTTP response to client
```

**WHY THIS MATTERS:** Money leaves the building **once**; your database and provider ledger must **agree** eventually.

---

## Sequence diagram — happy path (single charge, eventual response)

Vertical time flows **down**. Dotted lines indicate **optional** provider-internal steps.

```
  Client SPA          API Ingress        Idempotency       PaymentSvc        Provider        Postgres
     │                    │                  │                  │                 │                │
     │ POST /payments     │                  │                  │                 │                │
     │ Idempotency-Key K  │                  │                  │                 │                │
     │──────────────────>│                  │                  │                 │                │
     │                    │ insert row K     │                  │                 │                │
     │                    │ InProgress       │                  │                 │                │
     │                    │──────────────────────────────────────────────────────────────────────>│
     │                    │                  │                  │ stripe charge   │                │
     │                    │                  │                  │───────────────>│                │
     │                    │                  │                  │<─ pi_xxx ok ───│                │
     │                    │                  │                  │ update row +   │                │
     │                    │                  │                  │ idempotency OK │                │
     │                    │                  │                  │───────────────>│                │
     │ 200 + JSON body    │                  │                  │                 │                │
     │<───────────────────│                  │                  │                 │                │
```

If the **network drops** after charge but before the client sees `200`, the **same** `Idempotency-Key` retry must land in the **replay** branch (no second charge) — see `IDEMPOTENCY_DEEP_DIVE.md`.

---

## Actors

| Actor | Role |
|-------|------|
| **Client** | Collects user consent, obtains a **payment method token** from provider SDK (PCI scope minimized). |
| **Your API** | Orchestrates, enforces authz, idempotency, audit trail. |
| **IdempotencyService** | Ensures duplicate HTTP posts do not create duplicate charges. |
| **PaymentService** | Domain + application rules: limits, currency, state transitions. |
| **PaymentProvider** | Stripe/Adyen — network that moves money. |
| **Database** | Source of truth for your view of the world. |

---

## Happy path (chronological)

### Step 0 — User intent in the browser

The client creates a **client-side idempotency key** when the user commits to pay (not on every keystroke).

```json
{
  "idempotencyKey": "7f2a9c21-4e8b-41d0-9f3c-6b1e2a9d0c11",
  "amount": { "currency": "USD", "value": "1999" },
  "paymentMethodId": "pm_abc"
}
```

### Step 1 — Request hits API

```
POST /v1/payments
Authorization: Bearer <access_token>
Idempotency-Key: 7f2a9c21-4e8b-41d0-9f3c-6b1e2a9d0c11
Content-Type: application/json
```

### Step 2 — IdempotencyService (before touching money)

Pseudo-flow:

```
1. Authenticate JWT → resolved user id = uid-42
2. Canonicalize JSON body bytes
3. request_hash = SHA256(canonical_body)
4. BEGIN TRANSACTION
5. TRY INSERT IdempotencyRecord(uid-42, key, hash, status=InProgress)
   - duplicate key violation → SELECT existing row → handle Completed vs InProgress vs conflict
6. COMMIT / continue
```

**WHY EXTERNAL CALL MUST NOT FIRE BEFORE THIS:** Once money moves, rollback is **socially** unacceptable — you reconcile instead.

### Step 3 — PaymentService business checks

Examples:

- User owns the order being paid  
- Amount matches order total  
- Currency allowed  
- Fraud/risk score not blocking  

### Step 4 — Call provider (Stripe example shape)

```http
POST https://api.stripe.com/v1/payment_intents
Idempotency-Key: 7f2a9c21-4e8b-41d0-9f3c-6b1e2a9d0c11
Authorization: Bearer sk_live_...
```

**Note:** Providers **also** accept idempotency keys — defense in depth.

### Step 5 — Persist final state

```
payments row: id, user_id, amount, status=succeeded, provider_ref=pi_XXX, created_at
idempotency row: status=Completed, response_http=200, response_body_json= {...}
```

### Step 6 — Respond to caller

Same JSON every retry.

---

## Failure paths

### A) Provider timeout

Your HTTP call to Stripe hangs. You don't know outcome.

Policy options:

1. **Query** Stripe by client reference / idempotency key.  
2. Mark payment **pending**, return **202** + polling URL.  

**COMMON MISTAKE:** Blind immediate retry POST without idempotency — duplicate intents.

---

### B) Provider says “card declined”

No money moved. Persist `failed` status. Idempotent replays still return stored failure snapshot.

---

### C) Provider succeeded, DB commit failed after

Dangerous intermediate state:

- Stripe charged  
- Your DB unaware  

**Mitigation:**

- Provider idempotency key alignment  
- Reconciliation cron **pull** from Stripe for “orphaned” intents  
- Alerting  

**REAL WORLD EXAMPLE:** Every mature fintech runs **daily reconciliation** jobs comparing internal ledger ↔ provider totals.

---

## Why external payment calls must not be duplicated casually

Providers may:

- Freeze funds  
- Hit chargeback thresholds  
- Ban your MID (merchant ID)  

Your users experience **pain** immediately.

---

## Relation to AuctionSystem’s “bid flow”

In this repo, a **successful WebSocket bid** path is:

```
WebSocket JSON
   → Deserialize { bidAmount }
   → Scoped AuctionService.PlaceBidAsync
      → EF tracked AuctionItem.TryApplyBid
      → SaveChanges
      → WebSocket notifier broadcast JSON
```

**Analogies:**

| Payment world | Auction demo |
|-----------------|---------------|
| Idempotency guard | ❌ Not present — duplicate WS messages could theoretically double-submit if bridged to money |
| Provider | ❌ None — in-memory EF |
| Broadcast | ✅ All WebSocket clients see new price |

**Interview bridge:** “The auction models **domain** rules and **fan-out** similarly to broadcast after settlement — but charging cards needs **additional idempotency and reconciliation layers** described in other docs.”

---

## WHEN TO USE EACH PATTERN IN DESIGN INTERVIEWS

- **SSE feed of payment status:** great for notifying UI after server knows state (read-mostly).  
- **WebSockets:** coordinating live auction increments — analogous to cooperative locks (still pair with transactional writes).  

---

## Summary

Payments are **slow, lossy, and irreversible**. A production flow separates:

1. **Authentication / authorization**  
2. **Idempotent ingress** (`Idempotency-Key`)  
3. **Business validation**  
4. **Exactly-one provider invocation per logical intent** (plus provider-side keys)  
5. **Durable audit / replay**  

AuctionSystem deliberately implements **(4-ish)** broadcast semantics for learning — **not** card settlement.
