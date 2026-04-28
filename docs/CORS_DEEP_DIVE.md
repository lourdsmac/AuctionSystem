# CORS — deep dive (and how it applies here)

This document explains **Cross-Origin Resource Sharing (CORS)** — why browsers enforce it differently than `curl`, how **preflight `OPTIONS`** works, practical header meanings, pitfalls like `AllowAnyOrigin` + credentials, and how **AuctionSystem** configures CORS for local development.

---

## The same-origin policy (browser-only rule)

A browser page loaded from `https://app.example.com` running JavaScript **cannot** read arbitrary responses from `https://api.other.com` **unless** the API explicitly allows that origin via CORS.

**Non-browser clients** (`curl`, server-to-server, mobile native apps using URLSession without web security model) **ignore CORS** — they follow DNS/TLS only.

**WHY THIS MATTERS:** Interviewers want you to articulate that **CORS is not an API firewall** — it’s a **browser consent mechanism** that protects **user data** from being silently exfiltrated by malicious third-party sites calling private APIs using the user’s cookies.

---

## Simple vs “non-simple” requests

### Simple request (sketch)

Methods: `GET`, `HEAD`, `POST` with limited content types and **no** custom headers like `Authorization` (historical rules — always verify current spec lists).

Flow:

```
Browser
  GET /api/auction
  Origin: http://localhost:5173
      │
      ▼
Server responds with resource + optional CORS headers
```

If `Access-Control-Allow-Origin` **doesn't** match → JS cannot read response body (**opaque error in console**).

### Preflight (`OPTIONS`)

When request is non-simple — e.g. JSON `POST` with `Content-Type: application/json` plus custom headers — browser performs:

```
OPTIONS /api/foo HTTP/1.1
Host: api.example.com
Origin: http://localhost:5173
Access-Control-Request-Method: POST
Access-Control-Request-Headers: authorization,content-type
```

Expectations:

```
HTTP/1.1 204 No Content (or 200 OK)
Access-Control-Allow-Origin: http://localhost:5173
Access-Control-Allow-Methods: GET, POST, OPTIONS
Access-Control-Allow-Headers: authorization,content-type
Access-Control-Max-Age: 600
```

After success, browser sends actual `POST`.

ASCII timeline:

```
Browser JS fetch (POST JSON + Authorization + custom hdr)
           │
           ▼ "preflight"
        OPTIONS ─────────────► API
           │◄──────────── Allow-* headers OK?
           │
           ▼ actual call
          POST ─────────────► API
```

**REAL WORLD EXAMPLE:** Dashboard SPAs triggering long `Access-Control-Allow-Methods` caches via `Access-Control-Max-Age`.

---

## `Access-Control-Allow-Origin`

Values:

```
Access-Control-Allow-Origin: https://trusted-app.example.com
```

vs dangerous pattern (when exposing cookies):

```
Access-Control-Allow-Origin: *
```

**Why wildcard is risky with credentials:**

If `*` + `Access-Control-Allow-Credentials: true` — **browsers reject** (spec forbids). Even when allowed, wildcard means **any** website’s JS could read responses if other mistakes happen.

**WHY THIS MATTERS:** For cookie-based auth, you list **explicit** origins.

**This repo:** `WithOrigins("http://localhost:5173", ...)` — explicit allowlist (good pattern).

---

## `Access-Control-Allow-Credentials`

```http
Access-Control-Allow-Credentials: true
```

**What:** Tells browser it may send **cookies / Authorization** on cross-origin XHR/fetch responses access.

Needs matching **specific** `Allow-Origin` (not `*`).

---

## Browser vs backend calls distinction

```
curl https://api/secret
✔ Reads body — server auth still enforced token-wise
```

Browser JS malicious site scenario:

```
fetch('https://api/secret', { credentials: 'include' })
❌ Browser blocks readable response unless CORS allows attacker origin
```

**Attack thwarted:** User’s silent cookies weren’t leaked to villain site scripts.

SERVER STILL NEEDS **`Authorization` checks** regardless — rogue agents bypass CORS intentionally.

---

## Development proxy pattern (alternative to widening CORS)

Many teams during dev:

```
Vite dev server (localhost:5173)
  proxies /api → dotnet (localhost:5088)
```

Browser sees **same-origin** `/api/**` requests → fewer CORS issues.

**AuctionSystem frontend** supports proxy in `vite.config.ts` (`/api`, `/ws`).

**COMMON MISTAKE:** Production still needs correct CORS or same-site hosting architecture — dev-only proxy masks misconfigurations until staging.

---

## How AuctionSystem configures CORS (actual code path)

Configured in **`Program.cs`:**

```csharp
policy.WithOrigins(corsOrigins)
      .AllowAnyHeader()
      .AllowAnyMethod();
```

Origins from `appsettings.json` **`Cors:FrontendOrigins`**.

Effects:

| Scenario | Likely outcome |
|----------|----------------|
| SPA on `5173`, API on `5088`, fetch without proxy | Allowed if origin listed |
| Forgot to add HTTPS staging origin later | Broken until origins updated |
| `AllowAnyOrigin` swap | Would relax cross-site restrictions globally — avoid in prod |

**WHY THIS MATTERS:** This code is intentional **developer ergonomics**, not carte blanche wildcard.

---

## Edge cases summary

| Case | Explanation |
|------|-------------|
| Server returns JSON but lacks CORS headers | Fetch fails JS-side — server still mutated DB if `POST`. |
| CORS relaxed but JWT missing | Client gets 401 — different issue (`Authorization`). |
| WebSockets | Browser still enforces Origin header at upgrade handshake; server may validate `.WebSockets.RemoteEndpoint` nuances — treat as orthogonal to HTTP CORS but related policy surface. |

---

## COMMON MISTAKES checklist

1. Thinking CORS == security against attackers — attackers use **`curl`** / scripted bots.  
2. Mixing wildcard origin + credentials blindly.  
3. Only testing with Postman (`curl`-like path) forgetting browser path.  

---

## Summary narrative (interview-ready)

Browsers sandbox sites to protect users mixing sessions. **CORS responses** selectively opt-in resource sharing. **OPTIONS preflight** negotiates richer requests. Explicit **Allow-Origin** beats `*` whenever tokens or cookies matter. **Dev proxies** hide pain locally but **staging** must validate real cross-origin behavior.

See also: `API_HEADERS_AND_SECURITY.md`, `FRONTEND_BACKEND_FLOW.md`.
