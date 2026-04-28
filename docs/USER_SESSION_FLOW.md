# User session flow — refresh, rotation, and revocation

**User sessions** model “this login is active” on the server (or tracked enough to revoke). This repo’s auction demo uses **anonymous** access — no login. This guide teaches the **patterns** production APIs apply alongside JWT access tokens.

---

## What “UserSession” means

Often a relational row similar to:

| Column | Purpose |
|--------|---------|
| `id` | Surrogate PK |
| `user_id` | FK → `users` |
| `refresh_token_hash` | bcrypt/Argon2 of opaque refresh token |
| `expires_at` | Session validity |
| `revoked_at` | Logout / theft response |
| `user_agent_hash` optional | Tie session loosely to device |
| `ip_prefix` optional | Risk / audit hints |

You **typically store refresh tokens hashed** exactly like passwords — leakage of DB dumps should not disclose usable refresh secrets.

---

## Why track sessions server-side?

Even with JWT access tokens:

- You want **Logout** (“invalidate all tokens from Thursday afternoon”).  
- You want **refresh rotation** auditing (detect theft).  
- Compliance wants **sessions** enumerated for user data deletion.

**WHY THIS MATTERS:** Pure JWT-only systems without server state struggle with **immediate** kill switches unless you introduce **denylists** or **short access lifetimes + refresh checks**.

---

## Login flow (ASCII)

```
Client                          Auth API                            Database
  |                                |                                  |
  | POST /auth/login              |                                  |
  | { email, password } ---------->| Verify password hash             |
  |                                |--------------------------------->|
  |                                | user row found                    |
  |                                |                                  |
  |                                | Create session row                |
  |                                | refresh_hash = HASH(refresh_raw)  |
  |                                |--------------------------------->|
  |                                |                                  |
  |<------------------------------| 200 + Set-Cookie HttpOnly refresh |
  |                                |     + body { accessToken: JWT }   |
```

**Response example (illustrative):**

```http
HTTP/1.1 200 OK
Set-Cookie: refresh=opaquevalue; HttpOnly; Secure; SameSite=Strict; Path=/auth/refresh; Max-Age=2592000
Content-Type: application/json

{
  "accessToken": "eyJhbGciOi...",
  "expiresIn": 900
}
```

**Browser note:** JavaScript **cannot** read HttpOnly cookies — mitigates XSS stealing refresh (not eliminate — XSS still dangerous).

---

## Refresh flow

```
POST /auth/refresh HTTP/1.1
Host: api.example.com
Cookie: refresh=opaquevalue
```

### Sequence diagram — refresh issuing new access JWT

```
  Browser (SPA)               Auth API `/auth/refresh`          user_sessions table
      │                                │                                │
      │ POST + HttpOnly cookie ───────►│                                │
      │                                │ hash(refresh) lookup            │
      │                                │───────────────────────────────►│
      │                                │◄──────── row + user_id ─────── │
      │                                │ mint new access JWT (short)     │
      │ 200 { accessToken } (JSON)     │ optional: rotate refresh ──────►│ UPDATE hash
      │◄────────────────────────────── │                                │
```

Server (same as diagram, step-by-step):

1. Hash incoming refresh  
2. Lookup row by user + hash match (or lookup by session id if you structure differently)  
3. If valid + not revoked + not expired → mint **new access JWT**  
4. Optional **refresh rotation:** issue new refresh, invalidate old (detect reuse = possible theft)  

**Rotation REAL WORLD EXAMPLE:** Auth0, Okta, Cognito support patterns like this — exact semantics vary.

---

## Logout flow

```
POST /auth/logout HTTP/1.1
Cookie: refresh=opaquevalue
Authorization: Bearer <access>
```

Server:

```
UPDATE user_sessions SET revoked_at = now() WHERE id = ...
Clear refresh cookie client-side (Set-Cookie max-age 0)
```

Access JWT may linger until expiry — combine with:

- Very short TTL, or  
- Server-side revocation list keyed by **jti** (expensive but explicit)

---

## Why refresh tokens are hashed at rest

Leak scenarios:

| Event | Plain refresh in DB risk |
|-------|---------------------------|
| DB replica snapshot stolen | attacker replays tokens |
| Insider export | catastrophic |

Hashes + per-session salts slow abuse.

---

## Why sessions aren’t physically deleted instantly

Often **soft delete** (`revoked_at`) for:

- Forensics (“which token path paid invoice #4002?)  
- Regulatory audit trails  

Periodic jobs hard-delete expired rows later.

---

## Revocation scenarios

### User-initiated logout

Mark session revoked → refresh fails → effectively ends ability to mint new accesses from that cookie.

### Credential reset

Invalidate **all sessions** for user (`UPDATE ... WHERE user_id = ?`).

### Suspected compromise

 Invalidate device-specific sessions plus force password reset.

---

## JWT + sessions mental model

| Artifact | Held by client | Held by server | Purpose |
|----------|----------------|----------------|---------|
| Access JWT | memory / memory-only store sometimes | verifies via public/symmetric keys | fast API authorization |
| Refresh opaque token | cookie or secure vault | hashed row | long-lived continuity |
| Session row | (not sent raw) — id implied | full record | revocation + audit |

---

## Mapping to AuctionSystem

This codebase does **not** implement:

- `Users` table  
- `UserSessions` table  
- `/auth/login`  

WebSocket bids are intentionally **open**.

**Bridging suggestion for learning:** annotate `AuctionService.PlaceBidAsync` with **`userId`** from claims someday — authorization rules remain in Application layer as **policy checks**.

---

## COMMON MISTAKES

1. Storing refresh tokens in **localStorage** “because SPAs need it” — prefer HttpOnly patterns or BFF.  
2. Never rotating refresh tokens — stolen refresh works until expiry.  
3. Allowing limitless concurrent sessions — increases attack surface vs user awareness.  

---

## Summary

**Sessions** reconcile **JWT convenience** with **human reality** — people lose devices and demand **immediate** logout certainty. Persisted `UserSession` rows + hashed refresh secrets + revocation timestamps are backend table stakes for SaaS-grade auth.
