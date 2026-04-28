# Idempotency — deep dive

Idempotency is one of the **most interview-tested** ideas in backend and payments engineering. This document explains **what** it is, **why** payments break without it, **how** servers implement it (keys, hashing, stored responses), **edge cases**, and how that relates to **this** repository (which does **not** yet implement payment idempotency — so you can honestly discuss both theory and gaps).

---

## Decision flow (first request vs duplicate)

Use this as a mental **state machine** when designing or reading payment APIs.

```
                    POST /payments
                    Idempotency-Key: K
                    Body: B
                          │
                          ▼
                 ┌─────────────────┐
                 │ Hash body → H   │
                 └────────┬────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │ Row (user, K) exists? │
              └───────────┬───────────┘
                    │                    │
                   NO                   YES
                    │                    │
                    ▼                    ▼
         ┌──────────────────┐   ┌────────────────────┐
         │ INSERT InProgress│   │ Stored hash == H? │
         │ (unique success) │   └─────────┬──────────┘
         └────────┬─────────┘             │        │
                  │                      YES      NO
                  │                       │        │
                  ▼                       ▼        ▼
         ┌──────────────────┐    ┌────────────┐  ┌─────────────┐
         │ Call provider    │    │ Return     │  │ 409 Conflict│
         │ Commit payment   │    │ CACHED     │  │ (bad client)│
         │ Store response   │    │ response   │  └─────────────┘
         └────────┬─────────┘    └────────────┘
                  │
                  ▼
         ┌──────────────────┐
         │ 200 + body (first)│
         └──────────────────┘
```

**WHY THIS MATTERS:** The **first** path touches money; the **duplicate** path should be a **database read** of the stored HTTP snapshot.

---

## What “idempotent” means

An operation is **idempotent** if doing it **once** or **many times** produces the **same observable outcome** (for that operation’s contract).

**HTTP perspective (informal but useful):**

- `PUT /users/7` with full replacement body → repeating should converge to the same resource state.  
- `POST /payments` to **charge** money → repeating without care can **charge twice** → **not idempotent** unless you add **explicit machinery**.

**WHY THIS MATTERS:** Payment networks and accountants care about **exactly-once effect** (or defensible **at-most-once** semantics). HTTP `POST` defaults are **not** your friend.

---

## Why idempotency matters in payments

Money-moving APIs sit on unreliable networks:

```
User clicks "Pay"
       │
       ▼
┌─────────────┐     slow / lossy      ┌──────────────┐
│   Mobile    │ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ │   Your API   │
│   Browser   │ ◄──── retry logic ─── │              │
└─────────────┘                       └──────────────┘
```

The client **cannot know** whether “my request reached the server” after a timeout.

**Three outcomes without idempotency:**

| Case | What happened server-side | What user thinks | Risk |
|------|---------------------------|------------------|------|
| A | Charge succeeded, response lost | Retry | **Second charge** |
| B | Charge failed | Retry | Maybe OK, maybe duplicate provider calls |
| C | Never reached server | Retry | Maybe OK |

**REAL WORLD EXAMPLE:** Stripe documents idempotency extensively. Adyen, PayPal, and internal payment switches all expect **retries** — because TCP, mobile radios, and humans **will** retry.

---

## Real-world retry sources

### 1. Double-click / impatient user

Two HTTP requests fire milliseconds apart. Without idempotency windows, you may process both.

### 2. Network retry libraries

HTTP clients with “retry on 5xx” duplicate **POST** unless the server de-dupes.

### 3. Mobile background retry

OS wakes your app; it resends the **same** pending mutation.

### 4. API Gateway / service mesh

Ingress proxies sometimes replay on uncertain failures.

### 5. Exactly-once illusion

There is **no magic exactly-once over unreliable networks** — you implement **at-most-once processing** with **deduplication** using durable records.

---

## Core pattern: `Idempotency-Key` + persisted outcome

### Request shape (client)

```http
POST /v1/payments HTTP/1.1
Host: api.example.com
Authorization: Bearer eyJhbGciOi...
Content-Type: application/json
Idempotency-Key: 7f2a9c21-4e8b-41d0-9f3c-6b1e2a9d0c11

{
  "amount": { "currency": "USD", "value": "1999" },
  "paymentMethodId": "pm_abc123"
}
```

**Rule:** The client generates a **unique key per intent** (often UUID) **once** and **reuses** it on retries for the **same logical payment attempt**.

---

### First-time processing flow

```
Client
  │
  │ POST /payments + Idempotency-Key + body
  ▼
┌─────────────────────┐
│     API boundary    │
│  Idempotency layer  │
└──────────┬──────────┘
           │
           │ 1) Normalize + hash request canonical body
           │ 2) BEGIN TRANSACTION
           │    INSERT idempotency_record(key, request_hash, status=InProgress)
           │      — if unique constraint passes, we "own" this key for now
           │ 3) Call payment provider (authorize/capture)
           │ 4) Persist Payment row + final status
           │    UPDATE idempotency_record SET status=Completed, response_json=..., response_hash=...
           │ 5) COMMIT
           ▼
┌─────────────────────┐
│  HTTP 200 + body    │  (+ header like X-Idempotent-Replay: false)
└─────────────────────┘
```

ASCII pipeline:

```
HTTP POST
   │
   ▼
┌──────────────────┐      ┌─────────────────┐      ┌──────────────────┐
│ Hash canonical   │────► │ Lookup / insert │────► │ Provider charge  │
│ body (SHA-256)    │      │ idempotency row │      │ (Stripe, etc.)    │
└──────────────────┘      └─────────────────┘      └────────┬─────────┘
                                                            │
                         ┌──────────────────────────────────┘
                         ▼
               ┌─────────────────────┐
               │ Store HTTP response │
               │ snapshot (+ status)  │
               └─────────────────────┘
```

---

### Duplicate request flow (safe replay)

```
Client (retry, SAME key, SAME body)
   │
   ▼
┌──────────────────────┐
│ Compute request hash │
└──────────┬───────────┘
           │
           ▼
┌──────────────────────────────────────────────────┐
│ Row exists for key + Completed + same body hash  │
│                                                  │
│ YES → return stored HTTP status + body immediately
│       (Optionally: X-Idempotent-Replay: true)     │
│                                                   │
│ NO  → if key exists with DIFFERENT body hash     │
│       → 409 Conflict (or 422) — client bug       │
└──────────────────────────────────────────────────┘
```

**WHY THIS MATTERS:** On duplicate, you **never** call Stripe again. You return the **remembered** answer — the user still gets a 200 with the same `paymentId`.

---

## Example HTTP responses

### First creation (synthetic)

```http
HTTP/1.1 200 OK
Content-Type: application/json
X-Idempotent-Replay: false
X-Correlation-ID: req-9f3b2a1

{
  "paymentId": "pay_01HZXQ9R",
  "status": "succeeded",
  "amount": { "currency": "USD", "value": "1999" }
}
```

### Retry (same key + same body)

```http
HTTP/1.1 200 OK
Content-Type: application/json
X-Idempotent-Replay: true
X-Correlation-ID: req-z9y8x7

{
  "paymentId": "pay_01HZXQ9R",
  "status": "succeeded",
  "amount": { "currency": "USD", "value": "1999" }
}
```

**Notice:** `paymentId` unchanged — that is the entire point.

---

## Why hash the request body?

The idempotency **key** identifies the **attempt window**. The **hash** proves the **semantic operation** is unchanged.

**Edge case:** attacker or buggy client reuses key but swaps amount from `$19.99` to `$1999.00`.

Without hashing:

- Server might return cached success for the **wrong** amounts — catastrophic.

With hashing:

```
stored_hash = SHA256(canonical_JSON(original_body))
incoming_hash = SHA256(canonical_JSON(new_body))

if incoming_hash != stored_hash:
    return 409 Conflict
```

**COMMON MISTAKE:** Hashing pretty-printed JSON with unstable key order — use **canonical JSON** (sorted keys) or hash a normalized structure.

---

## Why store the entire response

After a successful charge, clients need **stable JSON** even if your internal code path changed version later.

**Also:** support teams can answer “what did we tell the app at 14:32 UTC?” by reading the row — not by grepping dead application instances.

---

## Why a unique database constraint is critical

You'll have:

- `PRIMARY KEY (user_id, idempotency_key)` **or** globally unique `idempotency_key` if keys are UUIDs from a good generator.

**Concurrent requests** with the **same** key:

Two app instances race:

```
Transaction A                          Transaction B
-----------                            -----------
INSERT idempotency … (key K)           INSERT idempotency … (key K)
SUCCESS                                FAILS unique constraint →
                                       must re-read row and return stored response
```

Without a **unique index**, you can get **two payments** under one key.

**REAL WORLD EXAMPLE:** Postgres `ON CONFLICT DO NOTHING` / `ON CONFLICT DO UPDATE` (“upsert”) patterns are used heavily for this.

---

## In-progress and partial failure handling

### State machine (typical)

```
Received → InProgress → Completed
                    └→ Failed (maybe allow retry with new key)
```

If the server **crashed** after provider charge but **before** DB commit:

- Next retry — provider may reject duplicate invoice idempotency key **or** recognize duplicate — your integration must reconcile (provider dashboards, reconciliation jobs).

**WHY THIS MATTERS:** Idempotency at your API boundary **does not** remove reconciliation — it **reduces** duplicate user-visible charges.

---

## Concurrency edge cases summary

| Scenario | Desired behavior |
|----------|------------------|
| Same key, same body, parallel | One charge, others get stored response |
| Same key, different body | **Reject** loudly (4xx) |
| Same key, first still InProgress | Return **409** or **202** + poll (document your policy) |

---

## Idempotency vs this AuctionSystem repo

This project’s **`POST`** equivalent is **`WebSocket` JSON bid**. It does **not** include:

- `Idempotency-Key` headers  
- `IdempotencyRecords` table  
- Stored HTTP response replay  

WebSocket bids are still **dangerous under duplicate delivery** if you ever bridged them to card charges.

**Interview soundbite:** “Our demo auction uses domain rules (must beat price) but **real money** needs idempotent request handling at the HTTP API layer and reconciliation with the provider.”

---

## COMMON MISTAKES (checklist)

1. Treating `GET` as always safe — if it triggers side effects (rare anti-pattern), still protect.  
2. Idempotency keys that are **per session** instead of **per user** — can leak collisions at scale (see `DATABASE_DESIGN.md`).  
3. Hashing raw bytes without normalization.  
4. Returning **200** with a **different** body shape on replay — breaks mobile parsers.  

---

## Summary

**Idempotency** is how honest systems say: **“Retries are expected; duplicates are not.”** You get that with **client keys**, **canonical hashing**, **durable rows**, **unique constraints**, and **stored responses** — not with hope.
