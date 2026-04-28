# Database design — auction demo vs payment-grade schema

Two parts:

1. **What actually exists today** — EF Core **in-memory** `AuctionItems` seeded at startup (`AuctionDbInitializer.Initialize`).  
2. **Teaching schema** often used beside **payments + users + sessions + idempotency** — illustrative tables **not shipped** verbatim in repo.

Honesty first — then depth.

---

## Part A — implemented schema (`AuctionSystem`)

### Table mental model (`AuctionItems`)

| Column (conceptual) | C# property | Notes |
|---------------------|---------------|-------|
| `Id` PK | `int` | Seeded `1` |
| `Name` | `string` | Demo item name |
| `CurrentPrice` | `decimal(18,2)` | EF precision |
| `LastUpdated` | `DateTime UTC` | On successful bid |

**Sample logical row:**

| Id | Name | CurrentPrice | LastUpdated |
|----:|------|--------------|-------------|
| 1 | Vintage Watch | 100.00 | 2026-04-28T12:34:56.789Z |

**WHY ONLY ONE ROW:** Demonstration simplicity—the architecture still isolates EF behind `IAuctionRepository`.

**COMMON MISTAKE:** Using in-memory provider for production — survives process only; no durability guarantees.

ASCII:

```
┌───────────────────────┐
│      AuctionItems     │
│ PK id INTEGER         │
│ name TEXT             │
│ current_price DECIMAL │
│ last_updated TS       │
└───────────────────────┘
```

---

### Repository access patterns (`EfAuctionRepository`)

- **Read snapshots** (`AsNoTracking`) for SSE + GET JSON (cheaper concurrency semantics).  
- **Tracked reads** (`GetByIdForUpdate`) for transactional bid mutation persistence.

Missing row → **would** return NotFound—but seed ensures baseline row exists unless manually deleted via future admin path.

---

## Part B — extended “production” tables (teaching)

These align with cross-docs (`IDEMPOTENCY_DEEP_DIVE.md`, `USER_SESSION_FLOW.md`, `PAYMENT_FLOW.md`). They are canonical teaching shapes — tweak names per vendor.

---

### `users`

| Column | Meaning |
|--------|---------|
| `id` UUID PK | Surrogate |
| `email` UNIQUE NOT NULL | Login identity |
| `password_hash` | Argon2id/bcrypt — never plaintext |
| `created_at`, `deleted_at` | Soft-delete / GDPR |

Indexes: `email UNIQUE`, partial index on active users.

---

### `user_sessions`

| Column | Meaning |
|--------|---------|
| `id` UUID PK | Session row |
| `user_id` FK → users | Ownership |
| `refresh_hash` BINARY | Hash of opaque refresh cookie |
| `expires_at`, `revoked_at` | Validity lifecycle |
| `created_ip_hash` optional | Fraud heuristics |

Why hash refresh? DB leak mitigation.

Unique partial index plausible: **`(user_id) WHERE revoked_at IS NULL`** — depends product rules for concurrent device caps.

---

### `payments`

| Column | Meaning |
|--------|---------|
| `id` UUID PK | Internal payment id |
| `user_id` FK | Who paid |
| `amount_minor` BIGINT | Integer minor units (avoid float) |
| `currency` CHAR(3) | ISO-4217 |
| `status` ENUM | `pending/succeeded/failed` |
| `provider_ref` TEXT | Stripe `pi_...` |
| `created_at` | Audit |

**WHY minor units INT:** No binary float rounding horrors.

---

### `idempotency_records`

| Column | Meaning |
|--------|---------|
| `user_id` | Scope keys per principal (or global if API keys) |
| `idempotency_key` TEXT | Client-supplied uniqueness |
| `request_hash` BYTEA | SHA-256 canonical body |
| `status` | `InProgress/Completed/Failed` |
| `response_json` JSONB | Replay exact outgoing body |
| `http_status` INT | Original status |
| `created_at`, `expires_at` | GC old rows TTL |

Constraints:

```
UNIQUE (user_id, idempotency_key)
```

**WHY RESPONSE JSON:** Duplicate requests must replay byte-identical payloads for correct client parsers.

Concurrent insert conflict path returns stored row instantly.

Pseudo sample rows (`user_id=u1`):

| idempotency\_key | request\_hash prefix | status | http\_status |
|------------------|---------------------|--------|--------------|
| K1 | sha256:a3f91… | Completed | 200 |
| K2 | sha256:f82c… | Failed | 402 |

Replay `K1` identical body ⇒ same JSON out.

Replay `K1` mutated body ⇒ `409 Conflict` (design choice).

---

## Query examples (payments idempotency)

### Safe insert-first pattern (sketch)

```sql
BEGIN;
INSERT INTO idempotency_records (user_id, idempotency_key, request_hash, status)
VALUES ($uid, $key, $hash, 'InProgress')
ON CONFLICT (user_id, idempotency_key) DO NOTHING
RETURNING *;
```

If nothing inserted → existed → branch to read Completed row.

Dialect-specific nuances apply (Postgres vs SQL Server semantics).

---

### Why idempotency key unique **per user** (typical pattern)

Keeps collisions across tenants low—two different users could accidentally choose same naive string `'123'` if global uniqueness required unrealistic coordination.

Alternatively global uniqueness with strictly generated UUID keys.

---

### Cleaning old idempotency rows

TTL purge job:

```
DELETE FROM idempotency_records
WHERE expires_at < now()
```

Retain longer for audit in regulated stacks—maybe move to archive table.

---

## Relationship diagram (payments world)

```
 users 1 ─── * payments
 users 1 ─── * user_sessions
 users 1 ─── * idempotency_records
```

Auction demo:

```
AuctionItems singleton row (conceptually single global auction teaching device)
```

---

## Migration mindset

When adopting persisted PostgreSQL schema later:

```
dotnet ef migrations add InitialCreate --project Infrastructure
dotnet ef database update
```

(In-memory skips migrations today.)

---

## Summary

Today’s DB footprint is microscopic but teaches **EF tracking snapshots vs updates**. Extended tables showcase **financial + identity** durability primitives you’ll implement or critique in mature services.
