# Authentication — JWT deep dive

This document explains **JSON Web Tokens (JWT)** the way backends use them in production — structure, signing vs encryption, common **claims**, access vs refresh split, verification flow, pitfalls — and explicitly notes that **AuctionSystem does not ship JWT middleware** today (study material + future extension).

---

## What a JWT is (plain English)

A JWT is a compact string that usually carries three base64-url pieces:

```
HEADER.PAYLOAD.SIGNATURE
```

The client sends it typically as:

```http
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImVtYWlsIjoiYSIsImV4cCI6MTcxNDQ2MjAwMH0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c
```

**WHY THIS MATTERS:** Stateless-ish auth — your API can validate without hitting the session store on **every** request (though many systems still check revocation lists for high security).

---

## Structure

### 1. Header (algorithm + type)

Decoding the first segment (example):

```json
{
  "alg": "HS256",
  "typ": "JWT"
}
```

- **alg** — signing algorithm (HMAC SHA-256, RSA, ECDSA…).  
- **typ** — usually `JWT`.

### 2. Payload (claims)

Decoding the second segment (example — illustrative):

```json
{
  "sub": "user-123",
  "email": "alex@example.com",
  "jti": "ce5c2b2c-2c3b-4c3b-8c3b-2c3b4c3b4c3b",
  "iat": 1714458400,
  "exp": 1714462000
}
```

**Common claims:**

| Claim | Meaning |
|-------|---------|
| **sub** | Subject — stable user identifier (string). |
| **email** | Often embedded for UI; **do not** treat as proof without verification path. |
| **jti** | JWT ID — unique token instance; useful for revocation lists. |
| **iat** | Issued-at (seconds since epoch). |
| **exp** | Expiration — validators **must** reject after this time. |
| **iss** | Issuer — who minted token (your auth service URL). |
| **aud** | Audience — intended API(s); prevents token reuse across services. |

### 3. Signature

Third segment verifies integrity:

```
signature = SIGN( signing_key, base64url(header) + "." + base64url(payload) )
```

If **any byte** changes in header/payload, signature check fails → **tampering detected**.

---

## Example “decoded JWT” narrative

Suppose you decode a token and see `"exp"` in five minutes:

- Clients should **refresh** before cutoff or handle **401 + refresh flow**.  
- APIs must **reject** expired tokens unconditionally.

**COMMON MISTAKE:** Parsing JWT in the browser **for security decisions**. Anyone can base64-decode payloads — **trust only server verification**.

---

## Signed — not encrypted (usually)

JWTs are typically **integrity-protected**, not **confidential**:

- Payload is **readable** by anyone holding the token (browser storage leak = data leak).  

**Do not put** full credit card numbers, government IDs, or secrets in JWT claims.

**WHY THIS MATTERS:** If you need privacy on the wire, use **TLS** (always) and **avoid sensitive claims** in JWT; or use **Nested JWT / JWE** (encrypted JWT) — less common in typical REST APIs.

---

## Why access tokens expire (short lifetime)

If a token leaks (XSS, shoulder-surfing log), shorter **exp** shrinks the attacker’s window.

Typical pattern:

- **Access token:** 5–15 minutes (API calls).  
- **Refresh token:** days/weeks (only at `/auth/refresh` with extra controls).

---

## Why refresh tokens exist

Long-lived access tokens = long-lived stolen sessions.

Flow:

```
Login
  → access JWT (short) + refresh token (opaque string in DB)
```

Client stores refresh **more carefully** (HttpOnly cookie often — not accessible to JS).

On access expiry:

```
POST /auth/refresh
Cookie: refresh=<opaque>
→ New access JWT (+ rotate refresh optionally)
```

**REAL WORLD EXAMPLE:** SPA with **Bearer** access tokens + **refresh** cookie is common; purely localStorage for refresh is discouraged.

---

## Validation flow at the API boundary

ASCII:

```
Incoming request
       │
       ▼
┌──────────────────┐
│ Extract Bearer   │
└────────┬─────────┘
         ▼
┌─────────────────────────────┐
│ Verify signature           │
│ (symmetric HS256 vs        │
│  asymmetric RSA/ECDSA)     │
└────────┬────────────────────┘
         ▼
┌─────────────────────────────┐
│ Validate exp / iss / aud    │
└────────┬────────────────────┘
         ▼
┌─────────────────────────────┐
│ Optional revocation check     │
│ (jti blacklist, session row) │
└────────┬────────────────────┘
         ▼
   HttpContext.User populated
```

**ASP.NET Core** pattern:

- `JwtBearer` authentication middleware  
- `[Authorize]` on controllers  

**This repo:** **Not configured** — all auction endpoints are open for demo clarity.

---

## Example request hitting a protected auction (hypothetical)

### Request

```http
POST /api/auctions/99/bids HTTP/1.1
Host: api.example.com
Authorization: Bearer eyJhbG...
Content-Type: application/json

{ "bidAmount": 205 }
```

### Response (401 if missing/expired)

```http
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer error="invalid_token", error_description="The token expired at ..."
```

---

## Algorithms — quick mental map

| alg | Typical use |
|-----|-------------|
| HS256 | Symmetric secret — single service validates (simpler, shared secret headache at scale). |
| RS256 / ES256 | Asymmetric — auth service signs with **private key**; APIs validate with **public key** (better multi-service rollout). |

**WHY THIS MATTERS:** At scale you publish **Jwks_uri** rotating public keys; APIs fetch keys without sharing long-lived symmetric secrets broadly.

---

## COMMON MISTAKES

1. **Trust claims without signature verification** — trivially forgeable.  
2. **Long-lived HS256 secret** in 12 microservices — secret sprawl.  
3. **Storing PII** in JWT for convenience.  
4. **No `aud` check** — token meant for Auth service accidentally accepted by Payments service.  

---

## How JWT would overlay AuctionSystem later

Rough sketch:

```
Program.cs → AddAuthentication(JwtBearer)
          → JwtBearerOptions { Authority / Issuer / Audience }
Controllers → [Authorize] on sensitive routes
Application → IUserContext (user id from claims)
```

**Honest takeaway:** JWT is **not magical** — it shifts **authentication proof** into a validated object you **still** combine with **authorization checks** (“can user X auction Y?”).

---

## Summary table

| Concern | JWT role |
|---------|----------|
| **Who** issued token | iss / signing key topology |
| **Who** token describes | sub / claims |
| **Expiry** | exp / refresh dance |
| **Tampering protection** | signature |
| **Privacy of payload** | **Not** JWT’s primary job |

For session storage and revocation details, continue to **`USER_SESSION_FLOW.md`**.
