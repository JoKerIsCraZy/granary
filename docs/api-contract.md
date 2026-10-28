# Silo native API contract for the Windows client

This document describes the part of Silo's native API that the Windows client uses: discovery,
sign-in, tokens and profiles, the catalog, playback protocol v3, stream authentication, progress,
and intro-skip markers. It is derived from the current code, not from assumptions.

**Derived from** (2026-10-24):

| Repository | Commit |
|---|---|
| `Silo-Server/silo-server` | `dbf41c54` |
| `Silo-Server/silo-android` | `0545b85d` |
| `Silo-Server/silo-apple` | `e9ea909d` |

How it was checked: every section was fact-checked claim by claim against the server code, the
generated contract and the reference clients (about 800 claims). Each reported error was confirmed
by an independent second check before it was corrected. Nothing was run against a live server;
items that need one are marked "not verified" in the appendix or listed in 13.4.

The server is pre-1.0 and the API can still change. The code is the source of truth: when this
document and the server disagree, the server wins and this document gets fixed. Before implementing
an area, re-check it against the generated contract `silo-server/contracts/api/v2/openapi.json`
(and the fixtures in `silo-server/contracts/api/v2/fixtures/`). Paths in this document are relative
to the root of the named repository.

### Contents

1. [Which API: `/api/v2` only](#1-which-api-apiv2-only)
2. [Wire conventions](#2-wire-conventions)
3. [Adding a server](#3-adding-a-server)
4. [Sign-in](#4-sign-in)
5. [Tokens and refresh](#5-tokens-and-refresh)
6. [Profiles, profile headers, and client identification](#6-profiles-profile-headers-and-client-identification)
7. [Catalog](#7-catalog)
8. [Playback protocol v3](#8-playback-protocol-v3)
9. [Media delivery and stream authentication](#9-media-delivery-and-stream-authentication)
10. [Progress, resume, and completion](#10-progress-resume-and-completion)
11. [Markers, intro skip, chapters, and trickplay](#11-markers-intro-skip-chapters-and-trickplay)
12. [Realtime events](#12-realtime-events)
13. [Consequences for the Windows client](#13-consequences-for-the-windows-client)
- [Appendix: where docs and code disagree](#appendix-where-docs-and-code-disagree)

## 1. Which API: `/api/v2` only

The Windows client uses Silo's **native** API, not the Jellyfin-compatible one, and specifically
**`/api/v2`**:

- `/api/v1` is a frozen alpha contract, kept only through a pre-1.0 bridge and then retired; its
  routes will answer `410 Gone`, except the retained `/api/v1/health` and `/api/v1/ready` probes (`silo-server/docs/architecture/api-contract.md:11-41,1743-1766`).
- `/api/v2` is Silo 1.0's first stable native API and becomes additive-only at the 1.0 lock.
- The contract says updated native apps **require v2**: they confirm the server contract with
  `GET /api/v2/system/info`, show a clear "server update required" message on a v1-only server, and
  never fall back to v1 or replay a failed v2 request against v1
  (`api-contract.md:1314-1331`).
- Both reference clients follow this. Android calls `/api/v2` for everything except an
  unauthenticated root `GET /health` liveness call
  (`silo-android/docs/api-v2/android-migration-status.md`; that route does not exist on the server,
  see 3.1); Apple keeps exactly one v1 path, the `/api/v1/health` liveness probe
  (`silo-apple/docs/native-api-v2.md`).

The v2 operations are generated with Huma from Go types into OpenAPI 3.1. Every running server
serves its exact contract at `/api/v2/openapi.json` and a Swagger UI at `/api/v2/docs`.

### How to read this document

Sections 2–6 cover the rules every request follows and how a user gets signed in. Section 7 covers
browsing. Sections 8–11 cover playback end to end. Section 12 covers the realtime sockets. Section 13
turns all of it into concrete decisions for the Windows client. The appendix lists every place
where docs and code disagree.

Where a section says what Android or Apple does, that describes their current behaviour as a
reference, not a requirement.

## 2. Wire conventions

Source: `silo-server/docs/architecture/api-contract.md:596-773,922-1036`,
`silo-server/internal/apiv2/problem.go`.

### 2.1 Requests and responses

- Base path `/api/v2`. JSON member and query names are `snake_case`. Query booleans are lowercase
  `true`/`false`; arrays are repeated keys; no trailing slashes.
- JSON only: send `Accept: application/json` (or omit it; `*/*` and `application/*` also select
  JSON); an `Accept` that excludes JSON is `406 not_acceptable`. A JSON request body must carry
  `Content-Type: application/json` (`charset=utf-8` is the only allowed parameter) and no
  `Content-Encoding`, otherwise `415 unsupported_media_type`; bodies over 1 MiB are
  `413 payload_too_large`.
- **Omit optional request members; send `null` only where the schema declares the member
  nullable** (a PATCH "clear", e.g. `ProfileUpdate.pin`). Anywhere else an explicit `null` is
  `422 validation_failed` (fixture `login_provider_null.json`), so the serializer skips nulls by
  default.
- **Unknown query parameters are rejected** with `422 validation_failed` (`errors[].code`
  `unknown_parameter`). Parameters that a
  capability document gates (image widths, `include_items`, `type=video_with_episodes`, …) may
  only be sent after the document advertises them.
- Required response members are always present, but a member the schema declares nullable
  (`type: [..., "null"]`) can be an explicit `null`, the documented "known to have no value"
  state. Many of these are required, for example `Profile.max_advisory_age` (null = no limit),
  `ProfileVerification.expires_at` (null today, because profile tokens do not expire),
  `PlaybackConnection.learned_limit` and `LoginSession.last_seen_at`. Model them as nullable
  (`int?`, `DateTimeOffset?`, nullable references). Other optional members are **omitted** (not
  `null`) when unknown. Arrays are never `null`, except the admin access-policy allowlists
  (`library_ids`, an access group's `allowed_permissions`), where `null` means unrestricted.
  Response enums can gain values: always keep an "unknown" fallback when deserializing.
- A single resource is a bare object; a collection is `{ "items": [...], "page"?: { "has_more", "next_cursor"? } }`
  (`page` omitted for bounded lists). `201` carries `Location`; `204` has no body; async work is
  `202` with a job resource.
- IDs are opaque strings. Numeric keys (`library_id`, `file_id`, `person_id`) are canonical
  positive decimals; send them back unchanged. Content IDs look like `movie-tmdb-228064`,
  `episode-tvdb-296762-1-5`, or `local-<hex>`; URL-escape them in paths.
- Instants are RFC 3339 UTC with milliseconds (`...Z`); dates are `YYYY-MM-DD`. Durations normally
  carry the unit in the name (`position_seconds`); exceptions are listed in [7.9](#79-unit-and-shape-quirks).
- Pagination: `limit` (default 50, max 200 unless the operation declares its own default or a
  lower maximum, e.g. `listPeople` 20/100; above the maximum is `422`) plus an opaque `cursor`.
  Cursors are bound to the operation, viewer, filters, sort, page size and image parameters; a bad
  or stale cursor is `400 invalid_cursor` → restart from page 1. No offset paging.
- Sorting: `sort=field` (ascending), `sort=-field` (descending), several keys comma-separated. An
  unknown or repeated field is `422 validation_failed` with `errors[].code` `invalid_sort_field`
  at `query.sort`.
- Caching: JSON defaults to `Cache-Control: no-store`. Capability documents use
  `private, no-cache` with a strong `ETag`; revalidate with `If-None-Match` (→ `304`).
- Retry safety is per operation (natural idempotency, client-minted operation IDs such as
  `stop_id`, or "non-retryable"). There is no global `Idempotency-Key`.

### 2.2 Errors: RFC 9457 Problem Details

Every v2 error is `application/problem+json`:

```json
{
  "type": "https://siloserver.org/docs/api/v2/problems/validation_failed",
  "title": "Validation failed",
  "status": 422,
  "detail": "The request did not pass validation; see errors.",
  "instance": "urn:silo:request:000000000000000000000003",
  "errors": [{ "location": "header.x-profile-id", "code": "required", "detail": "..." }]
}
```

- The **problem code is the last path segment of `type`**. Branch on it, never on `detail`.
- `instance` matches the `X-Request-ID` response header (useful for support logs; it is not a
  secret).
- `Retry-After` accompanies `429` and applicable `503` responses.

Shared codes a client must handle (full catalog in `silo-server/internal/apiv2/problem.go:26-112`):

| Status | Code | Client reaction |
|---|---|---|
| 400 | `malformed_request`, `invalid_cursor` | Bug / restart paging from page 1 |
| 401 | `authentication_required`, `invalid_token` | Refresh the access token, retry once. Not on `POST /api/v2/auth/login` (wrong credentials, 4.1) or `POST /api/v2/auth/refresh` (sign in again, 5.1) |
| 401 | `token_refresh_required` | Refresh and retry; do **not** sign out |
| 401 | `session_expired` | Login session revoked: sign in again |
| 403 | `profile_verification_required` | Ask for the profile PIN |
| 403 | `password_change_required` | Account has a temporary password (see [4.1](#41-password-sign-in)) |
| 403 | `permission_denied` | Not allowed (also: disabled account at login) |
| 404 | `not_found` | Missing or hidden resource (also: unknown `X-Profile-Id`) |
| 409 | `capability_disabled`, `capability_not_configured`, `conflict` | Feature off / state conflict |
| 410 | `playback_session_ended` | An administrator ended the playback session: leave playback (sections 8.7, 9.5, 10.1) |
| 410 | `device_login_expired` | Only when approving another device's expired sign-in request (not in the MVP); a polling device sees `status: expired` instead |
| 410 | `client_upgrade_required` | Reserved for the retired `/api/v1` tombstone; no v2 handler emits it today |
| 422 | `validation_failed` | Bug (or a gated parameter sent without its capability) |
| 429 | `rate_limited` | Wait for `Retry-After` |
| 500 | `internal_error` | Server fault: show a generic error with the `instance`; keep tokens |
| 501 | `capability_unsupported` | Feature not built into this server |
| 503 | `dependency_unavailable` | Retry after `Retry-After` (bounded backoff when it is absent); keep tokens |

Problem codes are additive: handle a code the client does not know by its HTTP status.

### 2.3 Capability documents

Optional features are discovered through typed capability documents, never by version sniffing
(`server_version` and `contract_digest` are diagnostics only). Every document has an opaque
`revision`, a `state` (`available` | `disabled` | `not_configured` | `unsupported`; treat unknown
values as unavailable) and, when account/profile-scoped, an effective `allowed`. Refetch them after
sign-in, profile changes, app resume, and before feature-specific UI.

Documents the MVP reads: `GET /api/v2/playback/capabilities`, `GET /api/v2/home/capabilities`,
`GET /api/v2/user/libraries/capabilities`, `GET /api/v2/catalog/search/capabilities`,
`GET /api/v2/images/capabilities`, `GET /api/v2/progress/capabilities`,
`GET /api/v2/capabilities/personal-lists`, `GET /api/v2/auth/device/capability`.

## 3. Adding a server

### 3.1 Endpoints

| Operation | Auth | What matters |
|---|---|---|
| `GET /api/v2/system/info` (`getSystemInfo`) | public | `api_major` (2), `server_version` and `contract_digest` (diagnostics only), `links { openapi, capabilities, identity }` |
| `GET /api/v2/system/identity` (`getServerIdentity`) | public | `server_id`: UUID, stable per deployment across restarts and URL changes. Self-asserted; authorizes nothing |
| `GET /api/v2/system/setup` (`getSetupStatus`) | public | `needs_setup`, `wizard_completed` |
| `GET /api/v2/theme/branding` | public | `server_name` (display name) |
| `GET /api/v2/auth/providers` (`listAuthProviders`) | public | `password_login` (bool), `items[]` |
| `GET /api/v2/system/connections` (`getServerConnections`) | bearer, account only | `endpoints[]`, optional `local_route { spki_sha256, url? }` |

v2 has no health operation. The retained liveness probe is `GET /api/v1/health`
(`{ "status": "ok", ... }`). There is no root `/health` route on the server
(`silo-server/internal/api/router.go:3155-3157`): `GET /health` falls through to the web UI and
answers `200 text/html`, so it is not a liveness signal. Android's `HealthApi` still calls root
`/health`; do not copy it. A v2 client can simply use `system/info` for reachability.

### 3.2 Adding-a-server flow (as Android does it)

1. Build candidate URLs from the user input: a bare host is tried as `https://host`, then
   `http://host`, then `http://host:8090`; `host:port` without a scheme is tried as
   `https://host:port`, then `http://host:port`; an explicit scheme is tried only with that scheme
   (and the given port, if any), with no `:8090` fallback. Normalize
   (trim, drop trailing `/`, lower-case scheme and host, keep port and path).
2. `GET /api/v2/system/info` without credentials (3 s timeout):
   - `200` with `api_major == 2` → v2 server.
   - `404` whose body is exactly Go's plain `404 page not found`, optionally followed by a single
     `\n` (Go's `http.NotFound` writes `404 page not found\n`; any other text does not count, and
     `Content-Type` is ignored) → **v1-only server**: show "this server must be updated" and stop.
     (An updated client never falls back to v1.)
   - Anything else (timeout, TLS error, HTML page, other status) → no verdict. Android still calls
     `GET /api/v2/system/setup` on the same candidate: a successful v2 answer there accepts the
     candidate, and only a failed setup call moves on to the next candidate.
3. `GET /api/v2/system/setup`: if `needs_setup`, tell the user to finish setup in the web UI (the
   setup wizard is out of MVP scope).
4. Save the server (URL, display name from `theme/branding`, `server_id` from `system/identity`) and
   go to sign-in.

Plain `http://` origins require explicit user consent before credentials are sent (Android
`silo-android/shared/.../network/CleartextOriginConsent.kt:16-25`).

### 3.3 Addresses and discovery

- The client keeps the address the user saved. A matching `server_id` is never a reason to switch
  addresses automatically. Two saved URLs of the same deployment are two entries, linked by
  `server_id`.
- LAN discovery (optional): DNS-SD `_silo._tcp.local` with TXT `v=1`, `id=<server_id>`; confirm a
  candidate with `GET http://<addr>:<port>/api/v2/system/identity` matching the TXT `id`
  (`silo-server/docs/architecture/server-discovery.md:10-45`). Windows can browse this with the
  built-in DNS-SD APIs.
- "Local route" (optional, post-MVP): an HTTPS LAN shortcut to the same server, pinned by
  `local_route.spki_sha256` from `system/connections`
  (`silo-server/docs/architecture/server-identity.md:95-161`). It needs certificate pinning, also
  for media requests (see [9.7](#97-lan-local-route)).

## 4. Sign-in

Sign-in calls are **public** (no bearer, no profile headers) but carry the device headers from
[6.4](#64-client-identification-headers), because the server records them on the new login session.
Login, device start, device poll and verify-pin are **non-retryable**: never replay them
automatically after an uncertain outcome.

### 4.1 Password sign-in

`POST /api/v2/auth/login` (`login`)

Request: `{ "username": string (1..254), "password": string (1..1024) }`. Omit `provider`: without
it the server routes by account (local password, otherwise the LDAP plugin).

`200` returns `TokenPair`:

```json
{
  "access_token": "...",
  "refresh_token": "...",
  "expires_in": 28800,
  "user": {
    "id": "1", "username": "alex", "email": "", "role": "user",
    "permissions": [], "download_allowed": true, "password_change_required": false
  }
}
```

| Status / code | Meaning | Client action |
|---|---|---|
| `401 invalid_token` | Wrong username or password (there is no dedicated code) | "Wrong username or password" |
| `403 permission_denied` | Account disabled | Show message |
| `403 local_login_disabled` | Local password sign-in turned off | Offer device sign-in |
| `403 not_permitted` / `password_expired` / `account_required`, `409 email_in_use` / `identity_linked_elsewhere`, `503 provider_unavailable` | Directory (LDAP) outcomes | Show message |
| `429 rate_limited` + `Retry-After` | 10 failed attempts in 15 min lock the name for 15 min; per-IP bucket 20/min | Show remaining time |
| `503 dependency_unavailable` (`Retry-After: 1`) | Server busy hashing passwords | Retry after the delay |
| `422 validation_failed` | Empty field or explicit `null` | Bug |

Temporary password: when `user.password_change_required` is true, the session may only call
`GET /api/v2/account/me`, `GET /api/v2/account/password/capability` (length limits for the new
password), `POST /api/v2/account/password` (`{ current_password, new_password }` → `204`) and
`POST /api/v2/auth/logout`; everything else is `403 password_change_required`. After the
change, refresh the tokens to drop the restriction. Change-password errors
(`silo-server/internal/apiv2/account.go`, `internal/api/handlers/auth_password.go`): `422` at
`body.current_password` (wrong current password; not a `401`, so the auth handler never replays it),
`422` at `body.new_password` (too short, too long, or the temporary password again), `403
permission_denied`, `409 conflict` (password sign-in disabled), `429` and `503`. (Android ignores this flag; the Windows client
should show a change-password screen.)

If `GET /api/v2/auth/providers` reports `password_login: false`, the server is OAuth-only: offer
device sign-in, which works for any account because the approving device can sign in any way.

### 4.2 Device / QR sign-in

Source: `silo-server/internal/apiv2/device_login.go`, `silo-server/internal/auth/device_login.go`,
`silo-server/docs/architecture/device-login.md`.

1. `GET /api/v2/auth/device/capability` (public) → `{ state, cancel, opened_signal, protocol_versions: [2], revision, ... }`.
   Offer the flow only when `state == "available"`.
2. `POST /api/v2/auth/device/start` (public) → `201`. Body (all optional):
   `{ "device_name": "<PC name>" (≤128), "device_platform": "windows" (≤64) }`. Response:
   - `device_code`: secret, never displayed.
   - `user_code`: 8 digits as `4821-7730`; display as `4821 7730`.
   - `verification_uri` (`<base>/activate`) and `verification_uri_complete`
     (`<base>/activate?code=48217730`), which is the **QR payload**.
   - `expires_in` (900 s), `expires_at`, `interval` (3 s in code).
   - `match_code` (legacy words): do not display.
3. `POST /api/v2/auth/device/poll` (public) with `{ "device_code" }`. Every state answers `200`:
   `{ status, poll_after, opened, expires_at?, tokens?, profile_id, profile_token, ... }`, where
   `status` ∈ `pending`, `approved`, `denied`, `consumed`, `canceled`, `expired`.
   - `approved` carries `tokens` (a `TokenPair`) exactly once. The collecting poll consumes the
     request; if that response is lost, the next poll says `consumed` and the user must start over.
   - `opened: true` means someone opened the code on another device ("continue on your phone").
   - Poll every `poll_after` seconds. There are no OAuth-style `slow_down` errors; throttling is
     `429` (bucket 120/min). Unknown code → `404`.
4. `POST /api/v2/auth/device/cancel` with `{ "device_code" }` when the user leaves the screen
   (idempotent).

The approver opens `verification_uri_complete` in a browser (web `/activate` page) or types the code
in a signed-in Silo app, which calls `GET /api/v2/auth/device?code=` and then
`POST /api/v2/auth/device/approve` or `/deny`.

Android TV behaviour to mirror (`silo-android/shared/.../repository/DeviceSignInMachine.kt`,
`DeviceLoginRepository.kt`): poll immediately, then every `poll_after`; back off on network errors,
`5xx` and `429` (doubling, max 30 s); keep a local deadline from `expires_in` (moved by `expires_at`
on pending, corrected for clock skew); on `expired`, `consumed`, `canceled` or `404`, replace the code
in place; QR encodes `verification_uri_complete` (error correction M); show `<host>/activate` and the
spaced code.

### 4.3 Not in the MVP

External sign-in (OIDC via the system browser: `/auth/oauth/*`), network-identity sign-in, signup,
invitations, password reset and first-run setup exist in v2 but are out of scope.

## 5. Tokens and refresh

### 5.1 Server behaviour

- Access and refresh tokens are HS256 JWTs; treat them as opaque. Defaults (admin-configurable):
  access **8 h**, refresh/login session **30 days**. Always use `expires_in` from the response.
- Every authenticated request re-checks the login session without caching, so revocation is
  immediate (`silo-server/internal/api/middleware/auth.go:79-202`).
- `POST /api/v2/auth/refresh` (`refreshSession`, public): body `{ "refresh_token" }` →
  `200 { access_token, refresh_token, expires_in }` (no `user`). The session's expiry slides to
  now + 30 days.
- **Rotation without reuse detection**: every refresh issues a new refresh token, but older refresh
  and access tokens stay valid until their own `exp` (no token hash is stored:
  `silo-server/internal/models/session.go:16-52`). Concurrent refreshes therefore all succeed.
  Always store the newest pair. This is inferred from code and may change before 1.0, so the client
  must still behave as if rotation were strict (single-flight).

| Refresh answer | Meaning | Client action |
|---|---|---|
| `200` | New pair | Store both tokens atomically |
| `401 session_expired` | Session revoked, expired, account disabled or deleted | Sign out (keep the server entry) |
| `401 invalid_token` | Refresh token invalid, expired or wrong type | Sign out |
| `400 malformed_request` | Body is not parseable JSON (client bug) | Sign out (both reference clients treat `400` as terminal) |
| `422 validation_failed` | `refresh_token` blank, missing or over 4096 chars, or an unknown member | Keep tokens and log a bug; never send a blank token (neither reference client signs out) |
| `503 dependency_unavailable` (`Retry-After: 5`) | Database unreachable | Keep tokens, retry later |
| `503 provider_unavailable` | External sign-in re-check failed | Keep tokens, retry later |

`POST /api/v2/auth/logout` (bearer, no body) → `204`; revokes the session together with its
access, refresh and profile tokens. Clear local credentials even if the call fails.

### 5.2 Client rules (from Android and Apple)

Android's Ktor `SiloAuthPlugin` (`silo-android/shared/src/commonMain/kotlin/org/siloserver/silo/network/AuthInterceptorImpl.kt`):

1. **Proactive refresh**: before sending, refresh when the access token expires within 60 s (margin
   capped at half the lifetime); expiry = save time + `expires_in` (lines 654-753,
   `ProactiveRefreshPolicy.kt:28-38`).
2. **Refresh on 401** for every same-origin request except `/auth/login` and `/auth/refresh`
   (lines 755-840). Replaying is safe: a `401` comes from the auth middleware before the handler
   runs, so the original request had no effect.
3. **Single-flight**: one refresh mutex per HTTP client (Android shares one client, and so one
   mutex, across all servers and checks the server id and origin inside the lock; a per-server
   mutex is equally correct). Inside the lock, compare the `Authorization` value the
   failed request sent with the stored token; if it already changed, retry with the new token
   without a network call. Otherwise refresh, store, and retry the original request **once**
   (lines 148-249). Abandon the refresh if the user switched server or signed out while waiting.
4. Refresh `400`/`401`/`403` → clear that server's tokens and profile, keep the server entry, go to
   sign-in. Any other failure keeps the credentials.
5. Media requests use the same rules with their own mutex (see [9.4](#94-token-lifetime-during-long-playback)).

Improvements the Windows client adopts:

- From Apple (`silo-apple/iosApp/iosApp/Networking/HTTPClient.swift:150-170,2453-2510`): on a
  refresh `503` (`provider_unavailable` or `dependency_unavailable`), fail the waiting requests
  with that `503` instead of the original `401`. Windows additionally forwards `Retry-After`.
- From the server's own 401 mapping, which neither reference client implements
  (`silo-server/internal/apiv2/gates.go:311-327`): branch on the 401 problem code
  (`token_refresh_required` → refresh and retry once, never sign out; `session_expired` → the
  refresh will fail, sign out).

### 5.3 Storage on Windows

Store per server: access token, refresh token, absolute access expiry, token lifetime, selected
profile id and profile token. Decided: use a DPAPI-protected file (`ProtectedData`, `CurrentUser` scope) in
local, non-roaming app data with one record per server. The Credential Locker (`PasswordVault`)
roams credentials with the Microsoft account and is capped per app, which does not suit
installation-bound session tokens. Never log tokens, `Authorization` headers, profile tokens or
`device_code`.

## 6. Profiles, profile headers, and client identification

An **account** is a login (`users` row); a **profile** is a household member on an account. Several
profiles share one account; `is_primary` marks the household parent, which is not the admin role.

### 6.1 Listing profiles

`GET /api/v2/profiles` (`listProfiles`): bearer required, profile headers optional (the picker runs
before a profile is chosen). Envelope `{ items[], avatar_upload_enabled, ... }`. Item fields the
picker needs: `id` (string), `name`, `is_primary`, `has_pin`, `is_child`, `avatar`,
`avatar_source` (`none` | `preset` | `upload`), `avatar_url?`. Further fields carry preferences
(`language`, `subtitle_language`, `subtitle_mode`, `quality_preference`, `max_playback_quality`,
`auto_skip_intro`, …) and restrictions (`max_content_rating`, `allowed_library_ids[]`, …).

### 6.2 Selecting the acting profile

- Send `X-Profile-Id: <id>` on every request, plus `X-Profile-Token: <token>` when that profile
  `has_pin`.
- Unknown or foreign id → `404 not_found`; PIN profile without a valid token → `403
  profile_verification_required`; a token sent for a profile without a PIN is ignored.
- 279 operations require `X-Profile-Id` (catalog, home, playback start, progress, …): a missing
  header is `422 validation_failed` at `header.x-profile-id`. 28 "household-gated" operations
  (`getWatchState`, trickplay, markers, subtitles, downloads) require it once any profile on the
  account has a PIN or restrictions. Account-only operations (`/account/me`, logout,
  `/system/connections`, device approve) take no profile header.
- Simplest correct rule: once a profile is selected, send the pair on **every** same-origin
  authenticated request (Android does this, media included), except `verify-pin`.

Sources: `silo-server/internal/api/middleware/viewer_access.go:36-107`,
`silo-server/internal/api/middleware/profile.go:15-40`,
`silo-server/docs/architecture/api-contract.md:836-883`.

### 6.3 PIN verification

`POST /api/v2/profiles/{id}/verify-pin` with `{ "pin": "1234" }` (1..72 bytes). Send it **without**
profile headers.

- `200 { "valid": true, "profile_token": "...", "expires_at": null }`; a wrong PIN is
  `200 { "valid": false }`.
- Profile without a PIN or unknown → `404`. 5 failures per profile per 5 min lock it for 5 min →
  `429 rate_limited` with `Retry-After`. Busy → `503` with `Retry-After: 1`. Non-retryable.
- The profile token has **no time expiry**. It is bound to the login session and the profile's PIN
  revision: it survives access-token refresh and app restarts, and dies on logout, session
  revocation, a PIN change or removal, or profile deletion
  (`silo-server/docs/architecture/profile-verification-tokens.md:11-50`). Store it with the
  session.
- When a request that carried `X-Profile-Id` gets `403 profile_verification_required`, clear the
  selected profile (not the account session) and return to the profile picker (Android:
  `silo-android/shared/.../repository/ProfileVerificationRecovery.kt:62-141`).

### 6.4 Client identification headers

There is no device registration. At sign-in (including the device-code poll), the server records
`X-Silo-Device-Id`, `-Name` and `-Platform` on the login session, which the user's session list
shows. Playback start stores the `X-Silo-Client*` identity on each playback session (admin Activity
page, playback logs). The device id also keys per-device settings and the playback device registry,
and `X-Silo-Client-Family` selects a settings layer:

| Header | Windows value | Rules |
|---|---|---|
| `X-Silo-Device-Id` | Random UUID created once per installation, persisted | ≤128 chars `[A-Za-z0-9._:-]`; sent once (a repeated or invalid header is `422`) |
| `X-Silo-Device-Name` | Computer name | ≤120 chars |
| `X-Silo-Device-Platform` | `windows` | ≤40 chars |
| `X-Silo-Client` | App working title | Labels sessions; clamped, never rejected |
| `X-Silo-Client-Version` | App version | |
| `X-Silo-Client-Build` | Build number, if any | |
| `X-Silo-Client-Channel` | `release` / `dev` | |
| `X-Silo-Client-Family` | `desktop` | Selects the `profile_client` settings layer (`tv`, `mobile`, `tablet`, `desktop`, `web`) |

Send them on every request to the server origin, including the public sign-in and device-poll
calls; never to other origins (artwork CDNs, S3). Sources:
`silo-server/internal/auth/client_device.go:14-55`, `silo-server/internal/apiv2/device_header.go:24-57`,
`silo-server/docs/settings-api.md:724-781`.

### 6.5 Multiple servers

Android stores one registry entry per saved URL (`id`, `url`, display name, last profile id,
last used, contract verdict `UNKNOWN`/`V2`/`UPDATE_REQUIRED`, verified `server_id`) and a separate
credential slot per entry. Switching servers swaps the active slot and re-probes `system/info`.
Signing out keeps the entry but drops its tokens and profile; removing a server wipes its slot
(`silo-android/shared/src/androidMain/kotlin/org/siloserver/silo/network/AndroidServerRegistry.kt`,
`EncryptedTokenManagerImpl.kt`). A refresh in flight when the server changes is discarded.

### 6.5.1 Branding

`GET /api/v2/theme/branding` (public, no sign-in) → `{ "server_name", "login_subtitle", "accent_color"?,
"wordmark_url"?, "mark_url"?, "favicon_url"?, "login_bg_url"?, "storage_available" }`. The web paints `accent_color`
over its theme (`web/src/lib/accentColor.ts`): only `#rgb` or `#rrggbb` counts (the admin's setting is free text,
`branding.accent_color`); it recolours the primary actions, the focus ring and the selection, and labels on it turn
white when white contrasts more than the dark label `#141417`. Granary reads it for the active server
(`ServerAccent`) and keeps its own red when there is none or the read fails.

### 6.6 Settings the client reads

Profile and device settings live on the server (manifest
`silo-server/contracts/settings/v1/manifest.json`, revision 23).

- Read: `GET /api/v2/settings/values/effective?keys=a&keys=b` with `X-Profile-Id` and
  `X-Silo-Device-Id` (needed for keys that allow `profile_device`; see [11.2](#112-intro-skip-mode)) →
  `{ items: [{ key, value, source, scope?, ... }], revision }`. A key the server's contract does not
  know fails the **whole batch** with `422`
  (`silo-server/internal/apiv2/settings.go:1027-1048`), so check keys against
  `GET /api/v2/settings/contract` first on older servers.
- Write: `PUT /api/v2/settings/values/{key}?scope=profile|profile_device` with `{ "value": ... }`.
- The preference columns in `GET /api/v2/profiles` (`subtitle_mode`, `quality_preference`, …) go
  stale after settings writes; use them only as a fallback for old servers.

| Key | Scopes | Default | Used for |
|---|---|---|---|
| `playback.preferred_quality` | profile, profile_device | `auto` | start/replan `quality_preference` (lower-case) |
| `playback.max_bitrate_kbps` | profile, profile_device | `null` (no cap) | start `bandwidth_cap_kbps` (omit when null) |
| `playback.audio_language`, `playback.subtitle_language`, `playback.subtitle_mode`, `playback.show_forced_subtitles` | profile, profile_device, profile_library, profile_series | –, –, `auto`, `true` | normally read through watch-detail `effective_*` ([8.8.1](#881-initial-track-selection-before-start)) |
| `playback.subtitle_appearance` | profile, profile_device | style object | text subtitle styling in mpv |
| `playback.intro_skip_mode` | profile, profile_device | `ask` | [11.2](#112-intro-skip-mode) |
| `playback.auto_play_next`, `playback.next_up_prompt_seconds` | profile, profile_device | `true`, `30` | [10.7](#107-next-episode) |
| `player.video_skip_back_seconds`, `player.video_skip_forward_seconds` | profile | `10`, `30` | skip buttons, arrow keys, media keys ([13.5](#135-system-media-controls-and-input)) |
| `player.subtitle_sync_ms`, `player.audio_sync_ms`, `player.playback_speed` | profile(_device) | – | player offsets and speed |
| `player.resume_rewind_seconds`, `player.passout_threshold` | **client_local** (never on the server) | `7`, `3` | resume rewind, auto-advance streak limit |
| `ui.next_up_mode`, `ui.disabled_library_ids`, `ui.library_order` | profile | `combined`, –, – | Home row behaviour, hidden libraries, library order |
| `ui.card_overlays`, `ui.card_overlays_enabled` | profile | server defaults, `true` | card badges ([6.6.1](#661-card-badges)) |

#### 6.6.1 Card badges

The web's card overlays (`web/src/lib/overlays`, `hooks/useOverlayPrefs.ts`), which Granary reads and edits the same
way, so a profile's badges look alike in both.

- `ui.card_overlays` is the document `{ "version": 2, "preset": "classic|minimal|vibrant|pill|square", "order": [ids],
  "items": { id: { enabled, position: "top-left|top-right|bottom-left|bottom-right", accentColor?, showIcon? } } }`.
  Older values are a JSON string of it, or version 1: a flat map of id to `{ enabled, position }`. Read every form;
  write the object. Keep ids you do not draw (`imdb_top_250`, `rt_certified_fresh`): another client chose them.
- Without a profile value, `GET /api/v2/settings/overlay-config` gives the server's: `{ defaults: JSON string | null,
  enabled, quick_actions_* }`. Reset deletes both keys (`DELETE /api/v2/settings/values/{key}?scope=profile`); a copy
  of the server's values would freeze the profile at them.
- The server refuses the whole document over one id its settings manifest lacks. `GET
  /api/v2/settings/contract/capabilities` → `{ api_version: 1, manifest_revision }`: `advisory_age` came with revision
  13, `request_status` with 15. While the revision is unknown, send only ids the server stored before.
- A server whose effective-settings read refuses the keys (`422`) predates them: show its defaults, offer no editing.
- Rotten Tomatoes badges (`rating_rt`, `rating_rt_audience`) are offered only while `GET /api/v2/capabilities/ratings`
  lists their source (`rt_critic`, `rt_audience`); a server without the operation (`404`) shows none.

## 7. Catalog

Every operation in this section needs the bearer token and, unless noted, `X-Profile-Id` (plus
`X-Profile-Token` for a PIN-locked profile). A missing required profile header is
`422 validation_failed` at `header.x-profile-id`. Exceptions: `listUserLibraries` and
`getImageCapabilities` (profile optional), `getWatchState` (required once any profile on the account
has a PIN or access limits), and `getArtwork` (public, signed URL).

### 7.1 Home

| Operation | Request | Response |
|---|---|---|
| `listHomeSections` | `GET /api/v2/home/sections` + image params | `{ "sections": Section[] }`: every row with its cards |
| `getHomeSectionItems` | `GET /api/v2/home/sections/{id}/items` + image params | bare `Section`; unknown/hidden id → `404` |
| `getHomeLayout` | `GET /api/v2/home/layout?include_items=0..4` | `{ "sections": SectionLayoutEntry[], "included_sections": Section[] }` (send `include_items` only if `getHomeCapabilities` reports `include_items_max`) |
| `dismissHomeItem` / `undismissHomeItem` | `PUT` / `DELETE /api/v2/home/dismissals/{surface}/{item_id}` | `204` |

`Section`: `id` (opaque), `section_type` (open string set), `title`, `featured`, `item_limit`,
`is_custom`, `customized`, `total_count` (lower bound), `items: CatalogItem[]`. Section rows take no
`limit`/`cursor`; a row returns up to `item_limit` cards (Home default 100). "See all" is
`GET /api/v2/catalog?source=section&scope=home&section_id=<id>` (cursor-paged; a library row uses
`scope=library&library_id=<id>`, and `scope` defaults to `library`). A section listing keeps the
section's own order and accepts only `limit`, `cursor`, `skip_total`, `seek`, `group` and image
params besides the scope ids: `q`, `name_prefix`, `type`, `genre`, `sort`, `match` and the other
filters are `422`, so a "See all" grid hides the browse grid's sort, filter and A–Z controls.

- **Continue Watching**: `section_type == "continue_watching"`. The same type also backs "Continue
  Listening/Reading", so filter cards by `type` (movie/episode/series). With the default profile
  setting `ui.next_up_mode = combined` the row merges in-progress and next-up cards (one per series);
  with `separate` the server adds a `next_up` row. Row-specific card fields: `item_source`
  (`in_progress` | `next_up`), `position_seconds`, `duration_seconds` (progress bar),
  `progress_updated_at` (needed for dismissal).
- **Recently Added**: `section_type == "recently_added"` (default rows "Recently Added Movies" and
  "Recently Added TV"; TV groups arrivals by show, returning an `episode` card for a single episode
  and a `series` card otherwise).
- Never key logic on a section `id`; admins can rename and add rows.
- Dismissal: choose the surface **per card**, not per row. A card with `progress_updated_at` uses
  `continue_watching` (body `{ "progress_updated_at" }`); a card without it but with `series_id`
  (a next-up card, also inside a combined Continue Watching row) uses `next_up` (body
  `{ "series_id" }`); sending `continue_watching` for it is `422`. A card with neither has no
  remove action. Dismissing an episode or series card drops the whole show from both rows until
  the profile watches it again, so remove every card of that series locally.

Android calls only `GET /home/sections` (decoded off the UI thread; 100-card rows are ~1 MB),
refetches rows that came back empty with `total_count > 0` via `getHomeSectionItems` (max 4
concurrent), hides rows with `total_count == 0`, and renders `continue_watching`/`next_up` rows as
backdrop cards with a progress bar
(`silo-android/shared/.../apiv2/HomeSectionsV2Api.kt:32-58`,
`silo-android/shared/.../viewmodel/HomeSectionHydrator.kt:22-52`).

### 7.2 Cards: `CatalogItem`

Required: `content_id`, `type`, `title`, `status`, `genres[]`, `keywords[]`. `type` is documented as
`movie`, `series`, `season`, `episode`, `audiobook`, `ebook`, `podcast`, `podcast_episode` but is an
open string. Useful optional members: `series_id`, `series_title`, `season_number`,
`episode_number`, `year`, `release_date`, `runtime` (minutes), `content_rating`, `overview`,
`poster_url`, `backdrop_url`, `logo_url`, `poster_thumbhash`, `backdrop_thumbhash` (there is no
`logo_thumbhash`), `position_seconds`,
`duration_seconds`, `progress_updated_at`, `item_source`, `play_content_id` (episode to play for a
series/season card), `user_state { played, is_favorite, in_watchlist, not_interested }`,
`overlay_summary` (resolution, hdr, codecs, audio channels, …), `badges[]`, `rating_imdb`,
`rating_tmdb`, `added_at`. There is no subtitle field: compose "S1 · E5" or the year on the client
(`silo-server/internal/apiv2/catalog_types.go:19-90`).

### 7.3 Libraries

- `GET /api/v2/user/libraries` (`listUserLibraries`) → `{ "items": [{ "id", "name", "type", "sort_order", "poster_url"? }] }`.
  `type` is free text (`movies`, `series`, `mixed`, …); sort by `sort_order`. The libraries a profile browses are the
  web's `useUserLibraries`: read with the profile settings `ui.disabled_library_ids` (left out) and `ui.library_order`
  (those it names first, in its order, the rest after) in one `GET /api/v2/settings/values/effective?keys=…`. Both are
  id arrays, or the older JSON string of one; keep whole numbers above 0, each once. The server drops hidden libraries
  itself only for a profile with a library limit (`include_hidden=true` lists them, for the screen that unhides them).
  Preferences that cannot be read hide nothing for the moment and are asked for again. The sidebar (read again once
  five minutes old, the web's staleTime), the Calendar's libraries and a library's page use this list; a hidden
  library's page is "not available". The Collections page's server collections keep every library, as the web's
  (`useServerCollections`, `GET /api/v2/collections/server`); only the web's Home-row collection picker
  (`useAllUserCollections`) goes through the filtered list.
- Library landing rows: `GET /api/v2/library/{id}/sections` → `{ "sections": Section[] }` and
  `GET /api/v2/library/{id}/sections/{section_id}/items` → `Section`.
- All items of a library: `GET /api/v2/catalog?source=query&library_id=<id>[&type=movie|series]`.
  Android maps the library type to `type` (movies → `movie`, series/tv → `series`, mixed → none).
- Collections of a library: `GET /api/v2/library/{id}/collections` (groups plus ungrouped
  collections); a collection's titles are `GET /api/v2/catalog?source=library_collection&library_id=<id>&collection_id=<id>`,
  personal ones `source=user_collection` without a library id (as the web client does).
- The Collections page: `GET /api/v2/collections` → `{ "items": PersonalCollection[] }`, the profile's own in its
  order, then the ones other profiles on the login share, by creator (`creator_profile_id`; split own from shared
  against the acting profile; `collection_type` `manual`, `smart` or an import source, a synced list). Server
  collections: `GET /api/v2/collections/server` → `{ "libraries": [{ "library_id", "library_name", "total_count",
  "collections": LibraryCollectionCard[] }] }`, each library cut to its first few; the rest come from that library's
  `GET /api/v2/library/{id}/collections` (`collections`, same order). A collection in several libraries opens without
  a library id, across all of them.
- Shuffle: there is no random sort (the OpenAPI text lists `random`, but the server's allowlist in
  `catalog/query_definition.go` does not, so `sort=random` is `422`). `POST /api/v2/shuffles` with
  `{ "scope": { "kind": "library", "id": "<id>" } }` returns `current`, a random playable movie or
  episode; `409` when the library has nothing playable. Check the shuffle capability for the scope first.

### 7.4 Browse: `GET /api/v2/catalog`

| Parameter | Values |
|---|---|
| `source` | `query` (default), `section`, `library_collection`, `user_collection`, `favorites`, `watchlist`, `history`, `person` |
| `scope` | `home` \| `library` (only with `source=section`) |
| `library_id`, `section_id`, `collection_id`, `person_id` | scope ids |
| `q` | text search (see 7.5) |
| `name_prefix` | a literal prefix of the sort title (`LIKE 'x%'`) that narrows the listing; the web's A–Z bar does not use it (see the letter index below) |
| `type` | `movie`, `series`, `episode`, `video` (movies + series), `audiobook`, `ebook`, `manga`; unknown values are silently ignored |
| `genre`, `status`, `year_min`, `year_max`, `content_rating` | simple filters |
| `sort` | one term: `title`, `added_at`, `release_date`, `year`, `runtime`, `rating_imdb`, `rating_tmdb`, `resolution`, `progress`, `date_viewed`, `plays`, … (`-` prefix = descending; no prefix = ascending). `relevance` only with `q`. |
| `limit`, `cursor` | 1..200 (default 50), opaque cursor |
| `seek` | 0..10,000,000: zero-based list position a page starts at, with the first page's `window_cursor` as `cursor` |
| `skip_total` | `true` → `total` is an estimate |

Response `CatalogBrowseCollection`: `items`, `page { has_more, next_cursor? }`, `total`,
`total_exact`, `window_cursor`, `effective_sort?`. Next page: same parameters plus
`cursor=<next_cursor>`. Default sort without `q` is `title` ascending. Cursors are bound to every
parameter but `skip_total` (`limit` and the image parameters included); a page may come back short with
more to follow.

**Windowed paging** (the web's `useCatalogWindow`, Granary's `LibraryGridViewModel`): the first page
(with its total) sizes the grid to every title; page `p` of size `n` is the same parameters with
`cursor=<window_cursor>&seek=p·n&skip_total=true`, so a scroll or jump far down loads only the pages
around the view (the web keeps one page either side), without the titles before them. Right after a
full page the next one can continue from that page's `next_cursor` instead, which is cheaper on title
order. A page whose `page.has_more` is false ends the listing there.

**Letter index**: `POST /api/v2/catalog/letters` with `{ library_id?, type?, match?, groups?, order? }`
(`asc` | `desc`, the title order counted) answers `{ total, letters: [{ letter, count, position }], runs:
[{ letter, position }] }` for a title-ordered query-source listing: letters are `A`–`Z` and `#` (digits,
symbols, other scripts; a sort title such as "Hobbit, The" files under H, accented Latin initials fold to
their base letter), `position` is the letter's first title, a `seek`. A collation can sort some of a
letter's titles apart (accented initials after Z under C), so the letter at a list position comes from
`runs`: the last run starting at or before it. The body takes rule groups only: the simple filters of
`GET /api/v2/catalog` become the groups the server makes of them (genre `contains`, status `is` and the
years `between` / `gte` / `lte` in one `"all"` group; one rating as an `"all"` group, several as an
`"any"` group). `search/capabilities.letter_index` tells whether the server has it. The web's bar jumps
by scrolling the windowed grid to `position` and marks the letter of the top row
(`web/src/components/catalog/LetterBar.tsx`, `pages/LibraryBrowse.tsx`).

Rule-based filters use `POST /api/v2/catalog/query` with a JSON body of `source`, `scope`,
`section_id`, `library_id`, `collection_id`, `person_id`, `q`, `name_prefix`, `type`, `match`,
`group`, `skip_total`, `query_limit`, `seek`, `cursor`, `limit` (1..100), `sort` (field) plus
`order` (`asc` | `desc`), and `groups: [{ "match": "all" | "any", "rules": [{ "field", "op", "value" }] }]`.
Image params stay in the query string; the next page resends the same body with `cursor` in the
body (a changed body answers `invalid_cursor`). The body has **no** `genre`, `status`, `year_min`,
`year_max` or `content_rating`: express them as rules (`{ "field": "genre", "op": "contains", "value": "Crime" }`,
`{ "field": "year", "op": "between", "value": [1990, 1999] }`, one
`{ "field": "content_rating", "op": "is", "value": "R" }` per rating in a `"match": "any"` group).
Any unknown body member is `422` (`unknown_field`). Facet menus: `GET /api/v2/catalog/filters`.

Android uses GET without rule groups and POST otherwise; page size 42 (phone) / 100 (TV); default
sort `-added_at` (phone) / `title` (TV) (`silo-android/shared/.../apiv2/CatalogV2Api.kt:29-225`).

### 7.5 Search

- Titles: `GET /api/v2/catalog?q=<text>[&type=...][&limit=...]`; relevance order by default.
  `GET /api/v2/catalog/search/capabilities` reports the provider (`postgres` | `meilisearch`) and
  flags such as `people_media_scope` and `video_with_episodes_scope` (send
  `type=video_with_episodes` only when that flag is true). A search deadline (3 s) answers `503`
  with `Retry-After`; an expired Meilisearch ranking session answers `invalid_cursor`.
- People: `GET /api/v2/catalog/people?q=&limit=1..100&media_scope=video` → `{ "items": Person[] }`
  (only when `people_media_scope` is true). Filmography:
  `GET /api/v2/catalog?source=person&person_id=<id>&sort=-year`.
- Android: 300 ms debounce, `limit=60` (phone) / 40 (TV), people search in parallel with
  `limit=20` (`silo-android/.../SearchViewModel.kt:142-333`).

### 7.6 Item detail: `GET /api/v2/catalog/items/{id}`

Query: `library_id`, `file_id`, image params; optional header `X-Silo-Device-Id` (resolves
device-specific audio/subtitle preferences). Accepts movie, series, season and episode ids.

`CatalogItemDetail` = `CatalogItem` plus: `original_title`, `tagline`, `overview`, `studios`,
`networks`, `countries`, `first_air_date`, `last_air_date`, `season_count`, `episode_count`,
`ratings[{ source, name, score, display }]` (render as given, max 3), `cast[{ name, character, order, person_id?, photo_url? }]`,
`crew[{ name, job, person_id?, photo_url? }]`, `user_state`, `user_data` (`played`,
`watched_count`, `unplayed_count`, `in_progress_count`, `is_in_progress?`, `position_seconds?`,
`duration_seconds?`, `last_file_id?`), `play_content_id`, `play_season_number` (next episode for a
series/season), `versions[]` (`FileVersion`: `file_id`, `resolution`, `codec_video`, `codec_audio`,
`container`, `hdr`, `file_size`, `duration` (s), `bitrate` (kbps), `audio_tracks[]`,
`subtitle_tracks[]`, `video_tracks[]`, `chapters[]`, `edition_key?`), `playback_variants[]`,
`subtitles[]`, `videos[]` (trailers), `extras[]`.

### 7.7 Series → seasons → episodes

- `GET /api/v2/catalog/series/{id}/seasons` → `{ "items": Season[] }`; `Season`: `content_id`,
  `season_number` (0 = specials), `title`, `episode_count`, `is_specials?`, `air_date?`,
  `overview?`, `play_content_id?`, `poster_url?`, `user_data?`.
- `GET /api/v2/catalog/series/{id}/seasons/{num}/episodes` → `{ "items": Episode[] }`; `Episode`:
  `content_id`, `season_number`, `episode_number`, `title`, `runtime` (min), `overview?`,
  `air_date?`, `still_url?`, `user_data?`, `files?[{ file_id, resolution?, hdr, file_size }]`.
- `GET /api/v2/catalog/items/{season_id}/episodes` lists episodes of a season item (a series id is
  `404`).
- Next episode: `play_content_id` on series/season detail and seasons; `/watch` has no
  next-episode field. Android picks the initial season (in progress → partly watched → first
  unplayed → first; specials last) and resolves the next episode from the season lists.

### 7.8 Watch detail: `GET /api/v2/watch/{id}`

The call right before playback (movie or episode; a series is `422`). Query: `file_id` (preferred
version; fills its on-demand markers), `library_id`, image params; header `X-Silo-Device-Id`.
Returns `WatchDetail`: `content_id`, `type`, `title`, series context, `poster_url`/`backdrop_url`
(for the OS media controls), `user_data` (resume, see [10.5](#105-resume)), `versions[]`
(`WatchFileVersion`: `file_id`, `resolution`, `codec_video`, `codec_audio`, `container`, `hdr`,
`file_size`, `duration_seconds`, `bitrate`, `audio_tracks[]`, `subtitle_tracks[]`,
`video_tracks[]`, `chapters[]`, `marker_segments[]`, `trickplay_available`), `subtitles[]`, and
top-level `effective_*` preference fields (`effective_subtitle_language`,
`effective_subtitle_mode`, `effective_show_forced_subtitles`, `effective_subtitle_track_signature`,
`effective_version_resolution`/`_hdr`/`_codec_video`/`_edition_key`). The audio choice is per
version: `versions[].effective_audio_track_index` and `versions[].effective_audio_language`.

Android flow before playback: `GET /watch/{id}` → choose the file (preferred → `last_file_id` →
quality preference) → start position (see [10.5](#105-resume): resume minus
`player.resume_rewind_seconds`, default 7 s, for resume points ≥ 30 s) → audio and subtitle
tracks ([8.8.1](#881-initial-track-selection-before-start)) → `POST /api/v2/playback/start`
(`silo-android/androidTvApp/.../TvVideoPlaybackStarter.kt:85-246`).

### 7.9 Unit and shape quirks

- `bitrate` is **kbps** everywhere (the watch-detail description says bits/s, but the column is
  kbps: `silo-server/internal/scanner/probe.go:386-390`).
- Catalog detail and versions use `duration` (s), `runtime` (min) and markers as `{ start, end }`;
  `/watch` uses `duration_seconds` and `{ start_seconds, end_seconds }`. Model both.
- `CatalogItemDetail.extras[].file_id` is an integer, unlike other ids.

### 7.10 Images

- Image members (`poster_url`, `backdrop_url`, `logo_url`, `still_url`, `photo_url`,
  `thumbnail_url`, …) are presigned, short-lived URLs, either root-relative
  (`/api/v2/artwork/<key>?exp=<unix>&sig=<sig>`) or absolute (S3/CDN). Resolve root-relative values
  against the origin of the response that supplied them. Empty or absent → placeholder. Granary
  deviates on purpose: it appends root-relative values to the saved base URL. The server has no
  base-path setting and mints them from its own root (`internal/artworkurl/artworkurl.go`), so behind a
  path-prefix proxy the base URL is the right root; without a prefix both give the same URL.
- `GET|HEAD /api/v2/artwork/{key}` is **public**: the signature authorizes. Send **no**
  `Authorization` header to artwork URLs (absolute presigned URLs may reject a second auth
  mechanism). An expired or wrong signature answers `404` like a missing object: on a `404` for a
  URL past its `exp`, refetch the owning card or detail for a fresh URL instead of caching the
  placeholder.
- Caching: revisioned keys (`…/w500.<rev>.webp`) are `private, max-age=<until exp>, immutable`;
  mutable keys are `private, no-cache` with an `ETag`. Only a revisioned key names immutable bytes,
  so only it may be cached by path with its authorization items dropped (`exp`, `sig`, `verify`,
  `X-Amz-*`); at the default 4 h TTL its URL is re-signed once per UTC day. A mutable key (avatar,
  library poster, collection image) is replaced in place and gets a new URL every 15-minute bucket:
  keep its full URL as the cache identity and revalidate with its `ETag` (`If-None-Match` → `304`).
  Apple's model: `silo-apple/iosApp/iosApp/Components/Images/ArtworkCacheKey.swift:7-55`.
  Granary (`Granary.Core.Artwork`) tells the two apart by the response, not the key's shape: an
  `immutable` answer is cached under the URL without its authorization items, in memory and on disk;
  any other answer only in memory under its full URL, and is downloaded again (without `ETag`) once
  the URL changes.
- Sizes: `image_size=small|medium|large|original` or per-slot pixel widths (`poster_width`,
  `backdrop_width`, `logo_width`, `still_width`, `photo_width`, 1..7680), only when
  `GET /api/v2/images/capabilities` reports `state: available` and lists the slot in
  `slot_width_params`. Keep the same widths across a paged listing (cursors are bound to them).

### 7.11 Favorites, watchlist, watched

- `PUT` / `DELETE /api/v2/favorites/{item_id}` and `/api/v2/watchlist/{item_id}` → `204`
  (idempotent). Membership comes from `user_state` on cards and detail; point checks use
  `GET /api/v2/{favorites|watchlist}/{item_id}/membership` → `{ item_id, is_member, added_at? }`
  (gate on `GET /api/v2/capabilities/personal-lists` → `membership_supported`). Avoid the older
  entry-or-404 `GET /api/v2/favorites/{item_id}`: bursts of 404s can trip intrusion detection.
- `POST` / `DELETE /api/v2/watched/{id}` → `204` (movie, episode, season or series; non-retryable).
- `POST /api/v2/history/remove` `{ targets: [{ content_id, scope }] }` → `204`: clears the titles' watch history,
  watched state and resume progress for the profile. `scope` is `show` for a series or season, else `item`
  (the web's `lib/historyRemoval.ts`).

### 7.12 Recommendations (For You)

- `GET /api/v2/recommendations/discover` (image size parameters) → `{ "items": [{ "type", "title", "kind"?, "key"?,
  "items": CatalogItem[] }] }`. Rows of `type` `cluster` or `similar_users_liked` come from the profile's own
  history (the web then calls the page personal). `kind` names the section that lists the row in full.
- `GET /api/v2/recommendations/section/{kind}[?key=…]` → one such row, up to 60 cards (the default and maximum of `limit`). `kind` is one of
  `for-you-main`, `cluster`, `similar-users`, `popular`, `recently-added`, `top-rated`, `genre`; `key` is a cluster's
  index or a genre's name.
- `GET /api/v2/recommendations/taste-profile` → `{ "top_genres", "favorite_directors", "signal_counts", "updated_at"? }`.
- "More like this": `GET /api/v2/recommendations/similar/{item_id}?limit=` → `{ "items": CatalogItem[] }`.

### 7.13 Calendar

- `GET /api/v2/calendar?start=YYYY-MM-DD&end=YYYY-MM-DD[&filter=][&timezone=][&library_id=]` → `{ "events": [{ "date",
  "items": CalendarEvent[] }] }`, only days with airings. `end` is at most 30 days after `start`; `timezone` is an IANA
  zone the days are reckoned in (absent is UTC; Windows ids need converting); `filter` is `following`, `trending`,
  `everything` (also `popular`, and the legacy `all`, `favorites`, `watchlist`).
- `CalendarEvent`: `content_id`, `type` (`movie`, `episode`, `season_premiere`), `title` (the movie's or series'),
  `series_id`, `season_number`, `episode_number`, `episode_title`, `air_at` (instant, when known) or `air_time` (HH:MM in
  the network's zone), `badges` (`series_premiere`, `season_premiere`, `finale`), `poster_url`, `watched`. The episode
  may not be in a library yet: the web opens the series, not the episode.

### 7.14 Notifications

- `GET /api/v2/notifications/capabilities` → `{ "in_app": { "enabled" }, "series": { "available", "mute" }, … }`; the
  web shows its inbox unless `in_app.enabled` is false or the call fails.
- `GET /api/v2/notifications?status=all|unread&limit=25[&cursor=]` → `{ "items": NotificationItem[], "page": { "has_more",
  "next_cursor" }, "read_cutoff" }`, newest first. `NotificationItem`: `id`, `type` (`episode.available`,
  `request.fulfilled`, `request.approved`, `request.declined`; open set), `created_at`, `read_at` (null while unread),
  `series_id`, `series_title`, `episode_id`, `season_number`, `episode_number`, `episode_title`, `poster_url`,
  `reason_flags` (per type: `favorite`, `watchlist`, `continue_watching`, `next_up`, `follower`; requests carry
  `media_type`, `title`, `reason`, `tmdb_id`; ignore other keys).
- `GET /api/v2/notifications/unread-count` → `{ "count" }`. `POST /api/v2/notifications/{id}/read` → `204`;
  `POST /api/v2/notifications/read-all` with `{ "through": read_cutoff }` marks what that listing saw, nothing newer.
- `GET` and `PUT /api/v2/notifications/preferences`: `enabled` (the master switch), `notify_favorites`,
  `notify_watchlist`, `notify_continue_watching`, `notify_next_up`; `PUT` takes only the switches that change.
- `GET /api/v2/notifications/series?status=active|inactive&limit=50&image_size=small[&cursor=][&q=][&reason=][&sort=]`
  → `{ "items": NotificationSeries[], "page" }` while `series.available`: `active` keeps the series that notify,
  `inactive` the ones the preferences silence or the profile muted. `reason` is `favorite`, `watchlist`,
  `continue_watching` or `next_up` (whether or not it is on); `sort` is `title`, `year`, `added_at` or
  `latest_episode_added`, descending with a leading `-`. `NotificationSeries`: `item` (a `CatalogItem`), `reasons` and
  `active_reasons` (the four flags; active ones are those the preferences let notify), `muted`, `notifies`, and with an
  active Next Up `next_up_after` `{ season_number, episode_number }` (only a later episode notifies on that reason).
- `PUT` / `DELETE /api/v2/notifications/series/{series_id}/mute` → `204` while `series.mute`: mutes or unmutes one series.

### 7.15 Requests

- `GET /api/v2/requests/status` → `{ "requests_enabled", "download_progress_supported", "follow_supported",
  "season_requests_supported", "watchlist_requests", … }`; the web shows Requests only while `requests_enabled`.
- `GET /api/v2/requests/mine?limit=1..50[&cursor=]` → `{ "items": MediaRequest[], "page" }`, newest first; the web walks
  pages up to 100. `MediaRequest`: `id`, `media_type` (`movie`, `series`), `tmdb_id`, `title`, `year`, `poster_path` (a
  TMDB path: `https://image.tmdb.org/t/p/{w154|w342}{path}`), `status`, `outcome`, `state` (the derived one the pages
  show: `pending`, `approved`, `processing`, `partially_available`, `available`, `declined`, `cancelled`, `failed`),
  `outcome_reason`, `last_error`, `library_content_id`, `seasons`, `season_progress`, `download` (`phase`, `percent`,
  `estimated_completion_at`, `updated_at`), `targets`, `created_at`, `updated_at`.
- `POST /api/v2/requests/{id}/cancel` with `{ "reason"? }` → the request; the owner can withdraw it while `outcome` is
  `active` and `status` is `pending`, or `approved` with no `targets` yet.
- `GET /api/v2/requests/discover` → `{ "items": [{ "key", "title", "page", "total_pages", "results": RequestMediaResult[] }] }`.
  `RequestMediaResult`: `tmdb_id`, `media_type`, `title`, `year`, `poster_path`, `availability` (`available` when the
  library has it), `library_content_id`, `in_watchlist`, `request` (`requestable`, `reason`, `status`, `state`,
  `following`, `requested_by_viewer`).
- A title's page: `GET /api/v2/requests/detail/{media_type}/{tmdb_id}` → `RequestMediaDetail` (the result's members
  plus `tagline`, `overview`, `backdrop_path`, `content_rating`, `runtime`, `number_of_seasons`, `number_of_episodes`,
  `status`, `first_air_date`, `last_air_date`, `vote_average`, `vote_count`, `imdb_id`, `director`, `creators`, `genres`,
  `networks`, `production_companies`, `cast` (`name`, `character`, `order`, `profile_path`), `seasons`
  (`season_number`, `name`, `air_date`, `episode_count`, `availability` `missing|partial|available`, `requested`,
  `poster_path`), `recommendations`); 404 for a title TMDB does not know or the profile may not see.
  `request.request_id` names the request when it is the account's own (or the viewer is an admin).
- `POST /api/v2/requests` with `{ "media_type", "tmdb_id", "title", "year"?, "overview"?, "poster_path"?,
  "backdrop_path"?, "seasons"? }` → `201` with the request. Not idempotent: never retried. Without `seasons` the server
  picks a series' aired seasons still missing, or the whole series when none aired. `GET /api/v2/requests/{id}` → one
  request (`requested_by_user_id` says whose; only the owner may cancel).
- Follows: `PUT`/`DELETE /api/v2/requests/follows/{media_type}/{tmdb_id}` (hear when someone else's request lands).
- Watchlist of TMDB titles (while `requests_enabled` and `watchlist_titles_supported`): `PUT
  /api/v2/watchlist/titles/{media_type}/{tmdb_id}` (no body) → `{ "item_id"?, "request": RequestMediaState }`, which says
  whether the add also requested the title (`watchlist_requests`); `DELETE` → `204`. `GET
  /api/v2/watchlist/titles?limit=1..200&cursor=` → `{ "items": [{ "tmdb_id", "media_type", "title", "year", "poster_path",
  "release_date" (`YYYY-MM-DD`), "content_rating", "vote_average", "status", "added_at", "request": RequestMediaState }],
  "page" }`, newest first: the entries the library does not have. `status` is `active`, `needs_review` (TMDB deleted the
  id and several titles could replace it) or `removed`; read an unknown value as active. The web reads every page (a
  repeated cursor fails the read) to sort and count the list; a read moves titles the library now has onto the library
  watchlist, server side.
- A Discover row in full: `GET /api/v2/requests/discover/{key}?page=1..500` (`key` one of the hub's, such as
  `trending_movies`; 400/404/422 for one the server does not know) → one section, plus `next_page` when a rating limit made
  the server read several TMDB pages to fill this one. The next page is `next_page` once the row named one (a page
  without it is then the last), else `page + 1` up to `min(total_pages, 500)`, TMDB's last page. Pages shift between
  reads: a title an earlier page listed shows once.
- Studios, networks, genres: `GET /api/v2/requests/discover/{studios|networks|genres}` → `{ "items": [{ "slug",
  "display_name", "logo_url", "gradient_from", "gradient_to", "series_supported", "tmdb_id" }] }`. `logo_url` is a full
  TMDB image URL, through a filter (`https://image.tmdb.org/t/p/w780_filter(duotone,ffffff,bababa)/x.png`); a client
  takes it only from `image.tmdb.org` over https. A genre's colours are `#RRGGBB`.
- Their titles: `GET /api/v2/requests/discover/browse/{studio|network|genre}/{slug}?sort=popularity|vote_average|release_date&page=`
  (a genre also `&media_type=movie|series`, required) → `{ "kind", "slug", "display_name", "logo_url", "media_type",
  "sort", "page", "total_pages", "results" }`; a studio lists movies, a network series. 404 for an unknown slug. The next
  page is `page + 1` up to `min(total_pages, 500)`.
- Search: `GET /api/v2/requests/search?q=&media_type=all|movie|series&page=1..500` (profile-scoped; a blank `q` is a
  validation problem) → `{ "page", "total_pages", "total_results", "results": RequestMediaResult[] }`, TMDB's page of 20.
  The web asks only for a text of two characters or more, and only while `requests_enabled`; it leaves out what the
  library has (`availability` `available`) and reads pages up to `min(total_pages, 500)`.

### 7.16 Administration

Every call needs an administrator account (`role` `admin`, `Account.IsAdmin`); anyone else gets 403. Granary reads the
web's Dashboard and Activity and sends Activity's commands; nothing else of the admin API is used.

- `GET /api/v2/admin/stats` → `{ "active_streams", "total_movies", "total_movie_files", "total_shows", "total_show_files",
  "total_files", "total_storage_bytes", "total_users", "advisory_titles", "watch_providers" }`.
- `GET /api/v2/admin/server/status` → `{ "started_at", "restart_required", "health": { "errors_24h", "warnings_24h",
  "postgres", "redis" } }`; a component is `{ "configured", "ok"?, "latency_ms"? }` (configured before up: no Redis is
  healthy). `GET /api/v2/admin/system/build` → `{ "available", "build_number", "display", "revision", "dirty" }`; the web
  shows `"{build_number} · {display}"` when the number is above 0. `GET /api/v2/admin/nodes` → `{ "items": [{ "id",
  "name", "type", "enabled", "healthy", "active_jobs", "egress_kbps" }] }`; a disabled node counts on neither side of
  "healthy".
- `GET /api/v2/admin/stats/timeseries?hours=` → `{ "egress_bytes", "resolution_seconds", "from", "to", "points": [{ "t",
  "streams", "direct", "remux", "transcode", "egress_kbps", "download_egress_kbps" }] }`. Minutes the sampler did not
  write are missing (a gap, not zero); a last point older than two minutes means "now" is unknown.
- `GET /api/v2/admin/stats/playback-activity?hours=` → `{ "hours", "bucket_seconds", "profiles_active_24h", "from", "to",
  "buckets": [{ "hour", "direct", "remux", "transcode" }] }`, only the buckets that saw a start; past two days the server
  groups by day. `GET /api/v2/admin/stats/top-activity?days=` → `{ "titles": [{ "media_item_id", "title", "plays",
  "total_seconds" }], "profiles": [{ "profile_id", "profile_name", "user_id", "username", "plays", "total_seconds" }] }`.
- `GET /api/v2/admin/users?limit=200` → `{ "items": [{ "id", "username", "email", "role", "enabled", "is_owner",
  "created_at", "last_active_at" }], "page" }`. `GET /api/v2/libraries` answers an administrator with each library's
  `enabled`, `paths` and scan state.
- `GET /api/v2/admin/sessions` → `{ "items": AdminPlaybackSession[] }`: who (`user_id`, `username`, `profile_id`,
  `profile_name`), what (`media_title`, `media_type`, `content_id`, `series_name`, `season_number`, `episode_number`,
  `episode_name`, `poster_url`, `media_file_id`), how (`play_method`; `effective_play_method` is `direct`, `remux`,
  `direct_stream` or `transcode` when the server knows, else read `video_decision`, `audio_decision` and
  `transcode_audio`), the source and target codecs, `output_container`, `output_protocol`, `stream_bitrate_kbps`,
  `transcode_hw_accel`, where (`position_seconds`, `file_duration`, `is_paused`, `started_at`), the client
  (`client_label`, `client_label_full`, `client_name`, `client_version`, `client_ip`), the route (`routing_execution`,
  `routing_egress` and their node names; older servers only `reporting_node` and `node_display_name`),
  `stream_location`, and `has_playback_control` (the player takes pause, resume and messages).
- Commands: `POST /api/v2/admin/sessions/{id}/pause|resume|stop|message` with `{ "command_id" (a UUID), "sequence",
  "message"? }` → `202` (or `200` replaying a retry) `{ "command_id", "delivery" ("dispatched", or "fallback_scheduled":
  no lane, the server ends the session after a deadline), "outcome", "sequence" }`. The sequence must rise within a
  session: Granary takes the clock in milliseconds, above the last it sent. 409 refuses a stale or conflicting command,
  404 a session that ended, `dependency_unavailable` a server without playback control. `POST …/{id}/terminate` with
  `{}` → `{ "session_id", "authority_revoked", "already_revoked", "client_notified", "delivery", "durable_state" }`: the
  session's playback authority is revoked for good, the player told as best it can be.
- `GET /api/v2/admin/ips?ip=&days=30&limit=50[&cursor=]` → `{ "items": [{ "user_id", "username", "first_seen",
  "last_seen", "request_count" }], "page" }`: the accounts seen at an address.

### 7.17 A profile's own collections, made and changed

Only the profile that made a collection (`creator_profile_id`) changes it; the others it is shared with read it.
Writes to a collection and to its titles' order carry the version they were read at (`If-Match`, the `ETag` of the
read): a missing one answers 428, one changed since 412, after which the client reads again.

- `GET /api/v2/collections/capabilities` → `artwork` (absent counts as yes), `poster_collages`, `item_reorder`, ….
- `GET /api/v2/collections/{id}` → `PersonalCollection` with an `ETag`; it carries no poster (the list does).
  `PersonalCollection` adds `description`, `include_in_server_collections` (on the Collections tab) and
  `display_query_definition` ("Show only": `{ match: "all", groups: [{ match: "all", rules: [{ field: "watched", op: "is",
  value: true|false }, { field: "type", op: "is", value: "movie"|"series" }] }] }`, no group for everything).
- `POST /api/v2/collections` `{ name, description, is_shared, include_in_server_collections, display_query_definition?,
  collection_type: "manual" }` → `201` `PersonalCollection`; not repeatable. `PATCH /api/v2/collections/{id}` with every
  one of those fields (an empty fragment `{ match: "all", groups: [] }` clears Show only) → `200` with the new `ETag`.
  `DELETE /api/v2/collections/{id}` → `204`.
- Titles of a manual collection: `GET …/{id}/items?limit=200[&cursor=]` → `{ items: [{ media_item_id, position, title? }],
  page }`; `PUT …/{id}/items/{item_id}` `{ position }` → `204` (stored as given, others not moved; one already in it
  stays); `DELETE …/{id}/items/{item_id}` → `204`. Neither takes a version. Cards for them come from
  `GET /api/v2/catalog?source=user_collection&collection_id=`.
- Order: `GET …/{id}/items/order` → `{ ordered_ids, has_more }` with its own `ETag` (`has_more` above 200 titles: no
  reordering); `PUT …/{id}/items/order` `{ ordered_ids }` (every title) with `If-Match` → `200` and the new `ETag`.
- `GET /api/v2/collections?contains_item=<content_id>`: the profile's own manual collections carry `contains`.

## 8. Playback protocol v3

The server decides **how** a file is delivered; the client reports honestly what it can play and
executes the plan. Authority: `silo-server/docs/architecture/playback-protocol-v3.md` (written
against `/api/v1`; where it differs, the v2 code wins and the difference is listed in the
appendix). Code: `silo-server/internal/apiv2/playback.go`,
`silo-server/internal/playback/protocol_v3.go`, `capabilities_v3.go`, `plan_v3.go`,
`silo-server/internal/api/handlers/playback_v3.go`, `playback_service.go`.

### 8.1 Flow at a glance

```
GET  /api/v2/watch/{id}                      choose version (file_id), resume, tracks, markers
GET  /api/v2/playback/capabilities           installation_id, features, deliveries
POST /api/v2/playback/start                  → 201 PlaybackDecision (plan or terminal)
     load stream.url into the player at timeline.player_start_seconds
POST /api/v2/playback/route-events           plan_selected, first_frame, ...
POST /api/v2/playback/{sid}/progress         every 10 s (heartbeat), section 10
POST /api/v2/playback/{sid}/replan           on failure, seek outside window, track/quality change
DELETE /api/v2/playback/{sid}                stop with stop_id and final sample, section 10.3
```

Every playback operation needs the bearer token and `X-Profile-Id` (plus `X-Profile-Token` for a
PIN profile), **including** the capabilities read. Every request body is strict: unknown members
are `422` (`additionalProperties: false`), body cap 1 MiB. Every mutation carries the
`installation_id` from capabilities; a mismatch is `409 installation_changed` → refetch
capabilities and start a new attempt.

### 8.2 Identifiers

| ID | Minted by | Format | Used in |
|---|---|---|---|
| `playback_attempt_id` | client, one per user-initiated playback | `^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$` (use a UUID) | start, replan, events |
| `replan_request_id` | client, one per replan (reused when resending the same replan) | same | replan |
| `plan_attempt_id` | client, one per adopted plan (correlation only) | 8–128 chars (UUID) | replan, events |
| `event_id` | client | canonical UUID | route events |
| `stop_id` | client, once per stop, kept on retries | canonical UUID | stop |
| `sequence` | client, per progress sample | int64 ≥ 1, increasing | progress, stop |
| `session_id` | server | canonical UUID | paths |
| `plan_id`, `plan_attempt_key` | server | opaque (`plan:…`, `v3:…`); echo byte-for-byte | replan |

### 8.3 `GET /api/v2/playback/capabilities`

`200 PlaybackCapabilities`: `installation_id` (UUID), `revision`, `state`, `allowed`,
`protocol_versions` (`[3]`), `features[]`, `deliveries[]` (`original_http`,
`server_remux_progressive`, `server_remux_hls`, and `server_transcode_hls` only when transcoding is
enabled), plus bandwidth-probe fields when `connection_speed_v1` is advertised. Supports
`If-None-Match`.

Proceed only if `state == "available"`, `allowed`, `3 ∈ protocol_versions`, and `features`
contains `playback_plan_v3`, `neutral_playback_v3_contract_v1` and `sequenced_progress_v1`. Then
check every decision: `protocol_version == 3` and `server_features ⊇ {playback_plan_v3, neutral_playback_v3_contract_v1}`.
There is no negotiation and no fallback. Unknown features are ignored.

Features the server advertises today include `header_authenticated_media_v1`,
`embedded_subtitles_v1`, `software_video_decode_v1`, `seek_reanchor_v1`, `output_change_v1`,
`plan_invalidated_v1`, `plan_source_duration_v1`, `subrip_sidecar_v1`,
`hls_subtitle_renditions_v1`, `buffering_quality_v1`, `playback_plan_summary_v1`,
`plan_active_quality_v1`, `marker_thumbnails_v1`, `authorized_media_origins_v1`
(`silo-server/internal/playback/protocol_v3.go:195-227`). The capabilities document alone also lists
`sequenced_progress_v1`, `fixed_media_file_v1`, `marker_segments_v1`, `trickplay_v1`,
`bandwidth_probe_v1` and `connection_speed_v1`
(`silo-server/internal/api/handlers/playback_service.go:195`); never look for these in a decision's
`server_features`.

### 8.4 `POST /api/v2/playback/start`

Headers: bearer, `X-Profile-Id`, device headers ([6.4](#64-client-identification-headers)).
`X-Silo-Device-Id` feeds the per-device registry and learned bandwidth limits; the per-device
audio-language layer at start reads the legacy `X-Device-ID` header instead (see 8.8.1).

**Top-level body** (`PlaybackStartBody`, `silo-server/internal/apiv2/playback.go:53-77`):

| Field | Required | Rules |
|---|---|---|
| `installation_id` | yes | from capabilities |
| `protocol_version` | yes | `3` |
| `client_features` | yes | strings, ≤ 64 × 128 chars |
| `file_id` | yes | media-file (version) id as a **string** (from watch detail) |
| `profile_id` | yes | must equal `X-Profile-Id` |
| `playback_attempt_id` | yes | 8.2 |
| `quality_preference` | yes | `auto` (default) or a rung label / `1080p` etc.; unknown values become `auto` with a warning |
| `subtitle_fidelity_preference` | yes | `preserve` or `compatible` |
| `start_position` | no | 0–31,536,000 s; omitted = server resume point; `0` = start over |
| `audio_track_id` / `audio_track_index` | no | `file:{file_id}:audio:{n}`; send it explicitly ([8.8.1](#881-initial-track-selection-before-start)) |
| `subtitle_track_id` / `subtitle_track_index` | no | **omitted = subtitles off**; the server applies no saved subtitle preference, so the client resolves it ([8.8.1](#881-initial-track-selection-before-start)) |
| `allow_alternate_versions` | no | `false` pins the file through replans |
| `bandwidth_cap_kbps`, `bandwidth_estimate_kbps` | no | 100–1,000,000; the estimate only from a real measurement |
| `bandwidth_estimate_source` | no | `probe` or `observed_rebuffer`; requires `bandwidth_estimate_kbps` and `buffering_quality_v1` |
| `observed_throughput_kbps`, `learned_limit` | no | only with `buffering_quality_v1` (else `422`); `learned_limit` is `ignore` or `clear` |
| `metered` | no | deprecated and ignored |
| `progress_persistence` | no | `server` (default) |
| `client_capabilities` | yes | below |
| `client_playback_context` | yes | below |

**`client_capabilities`** (`protocol_v3.go:517-531`): required `video_evidence` and
`audio_evidence` (`exact` | `platform_attested` | `declared`), `codecs_video`,
`codecs_video_hardware`, `codecs_audio`, `containers` (lists, lower-case), `hdr` (bool). Optional:
`max_resolution` (`"<N>p"` or `"4k"`; anything else means no limit), `hdr_details`
(`hdr10`, `hdr10_plus`, `hlg`, `dolby_vision_profiles`, optional bounds),
`audio_passthrough` (required `passthrough_codecs`, `spatializer_enabled` (bool), `max_channels`
(0–64); optional `entries[{ codec (required), channel_counts, layouts }]`; passthrough is granted
only at `exact` audio evidence with `layout_aware_passthrough` in `client_features` and an entry
whose `channel_counts` and `layouts` contain the source's; `output.audio_passthrough`, when sent,
replaces this one),
`video_decode[≤64]` (`{codec, hardware, decoder_name, profiles[], levels[], bit_depths[], max_width, max_height, max_frame_rate, max_bitrate_kbps}`).

**`client_playback_context`** (`protocol_v3.go:605-642`): `protocol_version: 3`, `form_factor`
(e.g. `desktop`), `app_version` (all required, as are `device`, `output` and `deliveries` below; `device` and
`output` have no required members of their own, so send `{}` rather than omitting them or sending
`null`, either of which is `422`), optional `app_build`, `app_channel`,
`device { platform, os_version, manufacturer, model }`, `output` (`hdr_details` = decoder ∩ display,
`audio_passthrough`, `current_sink`, `output_context_id`, `display { hdr_evidence: exact|unknown, hdr_types, display_id }`;
sending `display` requires `hdr_details`), and `deliveries`, a map keyed `original_http`,
`progressive`, `hls`. Each present class requires `enabled`, `supported_on_device`, `containers`,
`video_codecs`, `audio_decode_codecs`, `audio_passthrough_codecs`,
`subtitles { embedded_text, sidecar_text, ass_styling, embedded_bitmap, sidecar_bitmap, font_attachments, native_embedded? }`
(`native_embedded[≤16]`: `{ container, codecs[1–32], track_identity: ffmpeg_stream_index | container_track_id, ass_styling, font_attachments }`,
all required; used only on `original_http` and only with `embedded_subtitles_v1`),
`features`, `auth_header_refresh`, `validated_claims`, `transformations`. **An omitted class is
unavailable; an empty list inside a class means unconstrained.**

Minimal valid shape (illustrative). It declares only `original_http`, so the server can offer no
remux or transcode fallback; a real client also declares `hls`, and `progressive` if it can play
chunked fMP4:

```json
{
  "installation_id": "<uuid>", "protocol_version": 3,
  "client_features": ["playback_plan_v3", "header_authenticated_media_v1"],
  "file_id": "42", "profile_id": "<X-Profile-Id>", "playback_attempt_id": "<uuid>",
  "quality_preference": "auto", "subtitle_fidelity_preference": "preserve",
  "client_capabilities": {
    "video_evidence": "declared", "audio_evidence": "declared",
    "codecs_video": ["h264", "hevc"], "codecs_video_hardware": ["h264", "hevc"],
    "codecs_audio": ["aac", "ac3", "eac3"], "containers": ["mkv", "mp4"], "hdr": false
  },
  "client_playback_context": {
    "protocol_version": 3, "form_factor": "desktop", "app_version": "0.1.0",
    "device": { "platform": "windows" }, "output": {},
    "deliveries": {
      "original_http": {
        "enabled": true, "supported_on_device": true, "containers": [], "video_codecs": [],
        "audio_decode_codecs": [], "audio_passthrough_codecs": [],
        "subtitles": { "embedded_text": true, "sidecar_text": true, "ass_styling": true,
                       "embedded_bitmap": true, "sidecar_bitmap": false, "font_attachments": true },
        "features": [], "auth_header_refresh": false, "validated_claims": [], "transformations": []
      }
    }
  }
}
```

**Evidence tiers** (protocol §3, `capabilities_v3.go:123-168`):

| Tier | Video rule | Audio |
|---|---|---|
| `exact` | Walk `video_decode`: codec match with `hardware: true` (or `false` when `software_video_decode_v1` is sent); `profiles` match (ffprobe strings: `high`, `main 10`), `levels` at-least (H.264 `41`; HEVC general_level_idc, `153` = 5.1), `bit_depths` exact, `max_*` bounds. Empty list / 0 = unconstrained | Only tier that can earn passthrough |
| `platform_attested` | Same walk; profile and level skipped for hardware entries | Decode/copy only |
| `declared` | Flat membership in `codecs_video` / `codecs_video_hardware` | Decode/copy only |

At `exact`/`platform_attested`, a codec listed flat without a `video_decode` entry is refused
(warning `evidence_insufficient_for_direct`).

**Claims and features that change server behaviour**: `validated_claims` on `original_http`:
`client_selected_audio_track_v1` (without it, a non-default audio track forces a remux) and
`client_managed_dynamic_range_v1` (HDR/DV originals are delivered to SDR outputs and the client
tone-maps). Client features: `header_authenticated_media_v1` ([9.1](#91-two-media-auth-modes-the-windows-client-uses-header-auth)),
`software_video_decode_v1`, `embedded_subtitles_v1`, `layout_aware_passthrough`,
`plan_invalidated_v1`, `subrip_sidecar_v1`, `hls_subtitle_renditions_v1`, `buffering_quality_v1`,
`client_video_transformations_v1`, `device_quirks_v1`. Header auth, authorized origins, software
decode, subrip, HLS renditions and marker thumbnails are **sticky** for the attempt.

**Route choice** (`plan_v3.go:322-593`): direct play (`original_http`) only when the class is
enabled, the container is in the top-level `containers`, video passes the tier, the dynamic range
fits the output (or a client range claim covers it), the audio codec is decodable, the audio track
is the default or `client_selected_audio_track_v1` is claimed, quality preserves the source, and the
selected subtitle needs no burn-in. Otherwise: progressive remux → HLS remux → transcode.

### 8.5 Decision response: `201 PlaybackDecision`

Every decision is `201`: `protocol_version`, `server_features[]`, `outcome`
(`playable` | `adaptation_unavailable`), `session_id` (playable only), and `playback_plan` or
`terminal { reason, message, retryable }`.

| Plan member | Content |
|---|---|
| `plan_id`, `plan_attempt_key`, `session_id`, `expires_at` | opaque; `expires_at` = now + 24 h |
| `delivery` | `original_http` (direct play), `server_remux_progressive` (fMP4, forward-only, no ranges), `server_remux_hls` (video copy), `server_transcode_hls` (transcode) |
| `stream` | `url`, `protocol`, `headers`, `header_refresh`, … ([9.2](#92-delivery-urls)) |
| `timeline` | [8.6](#86-timeline-model) |
| `selected_tracks` | `{ audio?: { id, index? }, subtitle?: { id, index? } }` |
| `subtitle` | mode + inventory ([8.8](#88-tracks-audio-and-subtitles)) |
| `effective_recipe` | delivered `video_codec`, `audio_codec`, `width`, `height`, `frame_rate`, `bitrate_kbps`, `dynamic_range`, `audio_channels` |
| `claims` | video/audio/subtitle fidelity facts (HDR kept, passthrough, ASS styling) |
| `transformations[]`, `applied_quirks[]`, `runtime_corrections[]` | a client that cannot honour a listed client-side item must reject the plan via replan |
| `available_qualities[]`, `active_quality?` | quality menu ([8.11](#811-quality-and-buffering)) |
| `decision_reason`, `degradation_warnings[{ code, message }]` | open registries; never branch on unknown values |
| `requested_media_file_id`, `effective_media_file_id` | strings; differ after an edition fallback (track ids refer to the effective file) |
| `source` | `media_file_id`, `duration_seconds` (omitted when unknown), container, codecs, profile, level, bit depth, size, fps, bitrate, `dynamic_range` (`sdr`, `hdr10`, `hdr10_plus`, `hlg`, `dolby_vision`, `hdr_unknown`), DV details, audio codec/channels |

Other statuses: `404 not_found` (file missing or hidden), `409 idempotency_conflict` (same
`playback_attempt_id` with a different body), `409 installation_changed`, `403`, `422` (invalid
body, a client bug), `503`. Replaying an identical start returns the stored decision; after the
session ended it returns a terminal `session_expired` with `retryable: true` → mint a new attempt.

Terminal reasons include planner, subtitle-policy and transport reasons (protocol §7.3) plus
`bitrate_policy_unavailable` and `route_preparation_failed` (retryable). Show `terminal.message`
and offer retry only when `retryable`.

### 8.6 Timeline model

`timeline`: `source_start_seconds`, `stream_origin_seconds`, `player_start_seconds`,
`timeline_offset_seconds`, `can_seek_anywhere`, `seek_restoration`
(`player_position` | `source_position`), optional `seek_window_start_seconds` /
`seek_window_end_seconds`.

- `source = player + timeline_offset_seconds`. Everything the client reports or receives
  (progress, stop, `start_position`, replan `position_seconds`, markers, chapters, trickplay,
  subtitle cues) is **source** time.
- Direct play: offset 0, `player_start` = requested position, `can_seek_anywhere: true`,
  `seek_restoration: player_position`; seek locally.
- Transcode with a known runtime of at most 100,000 s: same as direct play.
- Transcode with an unknown runtime (no `source.duration_seconds`) or a longer one is served from
  FFmpeg's growing playlist: origin and offset are the 2 s segment boundary at or before the
  requested position, `player_start = requested − origin`, open-ended window,
  `can_seek_anywhere: false`, `source_position`. Handle it like a copy remux. Always take the
  offset and seek rules from the plan's `timeline`, never from `delivery`.
- Copy remux: origin and offset are the keyframe before the requested position, `player_start =
  requested − origin`; seeks outside the produced window need a `seek_reanchor` replan.
- Source time is container-relative (first container timestamp = 0); if the player keeps the
  container's own start time, subtract it.
- Never replace `source.duration_seconds` with the player's duration.

### 8.7 Replan

`POST /api/v2/playback/{session_id}/replan` → `200 PlaybackDecision`.

Required: `installation_id`, `protocol_version`, `playback_attempt_id`, `replan_request_id`,
`failed_plan_id`, `plan_attempt_id`, `plan_attempt_key`, `attempted_plan_keys[≤16]`,
`attempt_count` (1–8), `quality_preference`, `position_seconds` (source), `selected_tracks` (`{}`
allowed), `client_capabilities`, `client_playback_context`. Optional: `operation` (default
`failure_recovery`), `failure { classification, message, decoder_name }`, `local_mutations`,
`client_features`. Send the full start-time `client_features` list on **every** replan: v2
validates the replan body against its own list before merging, so buffering fields
(`bandwidth_estimate_source`, `observed_throughput_kbps`, `learned_limit`) without
`buffering_quality_v1`, or a client transformation without `client_video_transformations_v1`, are
`422`. Sticky features stay pinned whatever is sent.

| `operation` | Trigger | Rule |
|---|---|---|
| `failure_recovery` | Route or decoder failure | needs `failure.classification` |
| `seek_failure_recovery` | A seek failed | needs `failure.classification`; quality, output and tracks must equal the current plan |
| `seek_reanchor` | Seek outside the window (or `can_seek_anywhere: false`) | same equality rule |
| `track_change` | Audio/subtitle change the player cannot do locally | no `failure`; omitted subtitle = off |
| `quality_change` | Quality menu or buffering step-down | non-blank `quality_preference`; no `failure` |
| `output_change` | Display or audio output changed | no `failure` |

Classifications with server meaning: `subtitle_embedded_failed`, `subtitle_hls_rendition_failed`
(disable that subtitle route for the attempt). `sourceRefused` (with an absolute proxy stream URL
the server retries the same plan through the API origin) and the legacy intent classifications
`quality_changed`, `audio_track_changed`, `subtitle_track_changed` and `output_route_changed` also
change server behaviour, so never use them as free text. Everything else is free-form diagnostics.

Client rules:

- Store plan keys verbatim. On a failure, append the failed key and send every key tried in this
  chain; on an intent operation, reset the history and the count. The server keeps no durable
  failed-key history and does not enforce `attempt_count`: stop at 8 yourself.
- A returned key that is already in the failure list is usually a loop. It is not one after a node
  recovery (multi-node setups), where the server re-runs the same recipe on another node: adopt the
  plan, count it against the 8-attempt budget, and stop with an error when the budget is spent.
- Resend an unanswered replan with the **identical** body and `replan_request_id`.
- `404 not_found` → session gone: start a new attempt at the current position.
  `410 playback_session_ended` → leave, do not restart. `409 conflict` → an identical replan is
  still in flight, `failed_plan_id` is no longer current, or the same `replan_request_id` was reused
  with a different body; v2 gives all three the same type, so branch on the status only.
  `503` / `500` → retry.

**A lost connection is not a failed route** (protocol §6.2): after the first frame, pause, keep the
position, and retry a no-op `track_change` with backoff (1 s doubling to 15 s, ~12 attempts). A
network error, a `5xx`, `408` or `429` waits for the next try. Any other answer means the session
did not survive: `404` (for example after a server restart), any `409` (on v2 a replan in flight,
a stale plan and `installation_changed` cannot be told apart reliably), `403`/`422`, or a decision
without a plan. Then start a new attempt at the saved position with the current tracks (if that
start answers `409 installation_changed`, drop the cached capabilities first). `410
playback_session_ended` still means leave. Never report an outage as
`failure_recovery`. Android instead waits for reachability and reopens the same URL locally,
recording `local_mutations: ["transport_reopen"]`.

**`plan_invalidated_v1`**: the server may withdraw a copy plan (`video_copy_unsafe`) through the
control socket. A client that sends this feature must have the socket open, `ack` the command,
replan with `failure_recovery`, and send `result completed` within 8 s; otherwise the server stops
the session. Android and Apple do not send the feature; the MVP doesn't either. Without it (or without a live
socket), a copy-unsafe verdict stops the session mid-playback: the copy stream fails and
progress/replan answer `404`, so the client starts a new attempt at the current position, which
then plans a transcode.

### 8.8 Tracks: audio and subtitles

- Track ids are `file:{media_file_id}:{audio|subtitle}:{ordinal}`. The audio ordinal is the
  position in the version's `audio_tracks` array from watch detail.
- Subtitles use one dense combined space (external, then embedded in container order, then
  downloaded). `playback_plan.subtitle.inventory` is authoritative: select by echoing `track_id` or
  `combined_index`; never count tracks yourself.

| `subtitle.mode` | Client action |
|---|---|
| `off` | nothing |
| `render` + `artifact { url, mime_type, format, timing_origin_seconds: 0 }` | fetch the sidecar, add it as an external track; cues are source time (subtract `timeline_offset_seconds`) |
| `render` + `embedded { stream_index, container_track_id? }` | direct play only: select that stream (`stream_index` = FFmpeg stream index) |
| `render`/`convert` + `hls_rendition { group_id, name, format: "vtt" }` | transcode only: select the HLS subtitle rendition |
| `convert` | server WebVTT artifact |
| `burn_in` | subtitles are in the video |

Server decision order: native embedded (needs `embedded_subtitles_v1` and a matching
`native_embedded` entry) → sidecar text → PGS as `.sup` sidecar; DVD/DVB bitmaps are burned in
unless native.

Switching during playback:

- **Audio** switches locally when the delivered stream contains the track (direct play contains all
  tracks; claim `client_selected_audio_track_v1` and verify the switch), otherwise `track_change`.
- **Subtitles**: every `delivery: sidecar` inventory entry carries a fetchable `url` in every mode,
  including `off`, so the client can build its full menu without a new plan. Switch such an entry
  locally (mpv `sub-add` / `sid`) whenever `subtitle.mode` is not `burn_in`. Use `track_change`
  only for `burn_in_only` entries, for HLS renditions, and to leave a burn-in plan. This is Apple's
  behaviour (`silo-apple/iosApp/iosApp/Screens/Player/ProtocolV3/ProtocolV3SubtitleSelection.swift:56-74`).
- A failed native or rendition mount → `failure_recovery` with the matching classification.

Saving picks: neither start nor replan saves preferences. Save explicit user picks (never
automatic ones) with:

- `PUT /api/v2/audio-prefs/{key}` with `{ "audio_track_index": n (≥ -1), "audio_language"?, "track_signature"?: { language, title, embedded_title, codec, layout, channels, default } }` → `204`;
- `PUT /api/v2/subtitle-prefs/{key}` with `{ "subtitle_track_index": n (-1 = off), "subtitle_language"?, "subtitle_mode"?, "show_forced_subtitles"?, "external_subtitle_path"?, "track_signature"?: { codec, forced, hearing_impaired, label, language, source } }` → `204`;
- `DELETE` on the same path returns the series to Auto. `{key}` is the series id for an episode and
  the `content_id` for a movie (`silo-server/internal/api/handlers/playback.go:941-946`). Both need
  `X-Profile-Id`.

#### 8.8.1 Initial track selection (before start)

There is no inventory before start, so the start request is the one place where the client maps
watch-detail tracks to selection indexes itself.

- **Audio**: always send `audio_track_index` explicitly: the user's pick, else
  `versions[i].effective_audio_track_index` of the chosen version (an ordinal into that version's
  `audio_tracks`). Reason: when the field is omitted, the server's resolver reads the device layer
  from the legacy `X-Device-ID` header, not from `X-Silo-Device-Id`
  (`silo-server/internal/apiv2/playback.go:146-149,490`), while watch detail resolves it from
  `X-Silo-Device-Id`; the played track could then differ from the one the detail page showed. Apple
  sends the index explicitly.
- **Subtitles**: start never applies a saved subtitle preference; an omitted subtitle is off. The
  client resolves it from the top-level watch-detail fields `effective_subtitle_language`,
  `effective_subtitle_mode` (`auto` | `always` | `off`), `effective_show_forced_subtitles` and
  `effective_subtitle_track_signature`. Each is **omitted when it equals the contract default**
  (language: none, mode: `auto`, show forced: `true`), so a missing field is not "unknown".
  Algorithm (Android's shared resolver,
  `silo-android/shared/src/commonMain/kotlin/org/siloserver/silo/model/playback/AutoSubtitleResolver.kt:85-166`,
  mirrored by Apple's `PlaybackPrefsResolver.swift`): mode `off` → off; prefer a track matching the
  track signature; with no language set, only `always` picks a track; in `auto` mode, when the
  audio language equals the preferred subtitle language, pick the forced track in that language if
  show-forced is on, else off; otherwise pick the best track in the preferred language (full
  dialogue before forced, plain before SDH, embedded before external before downloaded, tracks that
  need burn-in last); if nothing matches and show-forced is on, any forced track.
- **Index space**: watch-detail `subtitle_tracks` are listed embedded-first and their `index` is the
  FFmpeg stream index, but the start request's `subtitle_track_index` uses the combined ordinal,
  which counts **external tracks first**: an external track's ordinal is its position among the
  externals; an embedded track's ordinal is the number of externals plus its position among the
  embedded tracks (`silo-server/internal/playback/subtitle_inventory_v3.go:116-150`; Android's
  `combinedSubtitleSelectionIndexes` in `PlaybackSubtitleChoices.kt:31-38`). Never send the
  watch-detail `index`. After start, use only inventory ids.

### 8.9 Control socket (optional)

`GET /api/v2/playback/sessions/control/capabilities`; ticket via
`POST /api/v2/playback/sessions/{sid}/control/ws-ticket` with `{ installation_id }` →
`{ ticket, expires_in ≤ 30, max_connection_seconds: 14400, protocol: "silo.playback-control.v2" }`;
connect `GET /api/v2/playback/sessions/{sid}/control/ws` with
`Sec-WebSocket-Protocol: silo.playback-control.v2, silo.ticket.<ticket>` and send
`{ "type": "hello", "session_id", "client": { "name", "version" }, "capabilities": { "commands": [...] } }`.
The server sends `command` frames (`pause`, `unpause`, `stop`, `terminate`, `display_message`,
`server_restarting`, `plan_invalidated`) that need an `ack` and a `result` before `deadline_ms`
(a missed deadline stops the session), and `event` frames (`markers_updated`, `subtitle_ready`, …).
Without the socket, an admin stop surfaces as `410` on progress. The MVP can skip it; Android
connects for every session.

### 8.10 Route events

`POST /api/v2/playback/route-events` → `202 { event_id, outcome: "accepted" }`. Required:
`installation_id`, `event_id` (UUID), `protocol_version`, `playback_attempt_id`, `event`,
`diagnostics` (`{}` if empty; ≤ 32 allowlisted keys, values ≤ 256 chars). Optional: `session_id`,
`plan_id`, `plan_attempt_id`, `plan_attempt_key`, `failure_classification`, `fallback_reason`.

Events: `plan_selected`, `plan_invalidated`, `plan_failed`, `first_frame`, `terminal`, `stopped`,
`runtime_correction_applied|succeeded|failed`, `seek_reanchor_requested`, `seek_reanchored`,
`plan_summary`. Diagnostics only: `429` (120/attempt/min) and `403` mean drop, never retry. A
start terminal is reported with `event: "terminal"` and no session or plan fields. Useful keys:
`decoder_name`, `first_frame_ms`, `video_codecs`, `video_width`, `video_height`, `error_code`,
`audio_output_mode`, `network_transport` (full allowlist in protocol §7.5).

### 8.11 Quality and buffering

- `available_qualities[]` (`{ label, display_name, height, bitrate_kbps, preserves_source }`) is the
  menu; on v2 the source entry is removed and the list may be empty. Picking a rung sends
  `quality_change` with its `label`; "Auto" sends `auto`. Always send the current preference on
  replans.
- With `buffering_quality_v1` (opt-in): on Auto, after 2 stalls ≥ 1 s within 2 min or one stall
  ≥ 6 s (ignoring the first 10 s and outages), send `quality_change` with `auto`,
  `bandwidth_estimate_kbps` = 70 % of the delivered bitrate,
  `bandwidth_estimate_source: "observed_rebuffer"` and `observed_throughput_kbps`.
- Server transformations (`audio_to_aac`, `video_to_h264`, `hdr_to_sdr_tonemap`, …) describe bytes
  already transformed. A client that tone-maps itself (mpv with libplacebo) can claim
  `client_managed_dynamic_range_v1` to keep HDR originals as direct play.

### 8.12 Conformance checklist (protocol §12 and code)

1. Gate on capabilities (8.3).
2. Fill every required v2 field, send no unknown fields; lists lower-case; `file_id` a string;
   `protocol_version` 3 at the top level and in the context.
3. Report evidence honestly; at `exact`/`platform_attested`, an omitted or zero `max_*` bound and
   an empty `profiles`/`levels` list mean "unconstrained", which is a claim of full support: fill
   real bounds or stay at `declared`.
4. Fresh `playback_attempt_id` per playback; reuse only to retry an unanswered identical start.
5. Treat `adaptation_unavailable` as a decision: show `terminal.message`, honour `retryable`, send
   a `terminal` route event.
6. Echo `plan_id`/`plan_attempt_key` byte-for-byte; accumulate failed keys (≤ 16, count ≤ 8).
7. Honour the timeline (8.6).
8. After start, use only inventory ids for subtitles; before start, compute the combined ordinal as
   in 8.8.1.
9. Reject plans with unknown client-side runtime corrections or transformations via a
   `failure_recovery` replan (free-form `failure.classification`, plan key in
   `attempted_plan_keys`). These only appear when the client opts in with
   `client_video_transformations_v1` or `device_quirks_v1`, which the MVP does not send;
   server-executed transformations need no action.
10. Progress every 10 s; stop with a stable `stop_id`; `404` = session gone, `410` = leave.
11. Never report a lost connection as `failure_recovery`; resend unanswered replans unchanged.
12. If the control socket is open, answer every command before `deadline_ms`.
13. Route events are diagnostics: drop on `429`/`403`; allowlisted keys only.

Golden vectors: `silo-server/internal/playback/testdata/protocol_v3/conformance_matrix.json`,
`attempt_keys.json`, and the v2 fixtures `silo-server/contracts/api/v2/fixtures/playback_*.json`.

### 8.13 What the reference clients report

- **Android (Media3)** reports `exact` evidence from `MediaCodecList` hardware decoders only (one
  `video_decode` entry per decoder with profiles, levels, bit depths, max size/fps/bitrate),
  intersects HDR with the display, probes audio passthrough per channel layout, disables the
  `progressive` class, and does **not** send `software_video_decode_v1`
  (`silo-android/android-shared/src/androidMain/kotlin/org/siloserver/silo/common/player/PlaybackCapabilityDetector.kt`,
  `MediaCodecCapabilitiesProbe.kt`, `AudioCapabilityManager.kt`).
- **Apple's FFmpeg-based engine** is the closest analogue to mpv: `declared` evidence, no
  passthrough, `client_managed_dynamic_range_v1`, `client_selected_audio_track_v1`,
  `software_video_decode_v1`, and `native_embedded` for mkv (`subrip, ass, ssa, webvtt,
  hdmv_pgs_subtitle, dvd_subtitle, dvb_subtitle`) and mp4 (`mov_text`) with `ffmpeg_stream_index`
  (`silo-apple/iosApp/iosApp/Screens/Player/ProtocolV3/ApplePlaybackV3Capabilities.swift:40-237`).

The Windows capability report (which tier, which decoders, how to probe D3D11VA, which subtitle
claims) is a Phase 1/4 design item; section 13 lists the starting point.

## 9. Media delivery and stream authentication

### 9.1 Two media-auth modes; the Windows client uses header auth

Each playback attempt picks one mode at `start`, and the choice is sticky for the attempt (a
replan cannot add or drop it; to change mode, stop and start a new attempt):

- **Header-authenticated mode**: the client sends `header_authenticated_media_v1` in top-level
  `client_features` (the server advertises it in its v2 capabilities). Plan URLs are root-relative
  paths on the API origin and carry **no credential**. Every request needs
  `Authorization: Bearer <access token>`.
- **Legacy mode** (no opt-in): URLs additionally carry a signed stream token: `?st=<JWT>` on the
  API origin, where it is only a restart-reconstruction reference and the bearer is still required,
  or a token path segment on a proxy node, which authenticates by itself.

Apple always uses header-auth mode (`silo-apple/iosApp/iosApp/Screens/Player/ProtocolV3/ApplePlaybackV3Capabilities.swift:39-74`);
Android still uses legacy mode
(`silo-android/shared/src/commonMain/kotlin/org/siloserver/silo/model/playback/PlaybackProtocolV3.kt:151-184`).
The protocol text treats header auth as the direction for native clients
(`silo-server/docs/architecture/playback-protocol-v3.md:748-905`), and it keeps tokens out of URLs
and logs. **The Windows client opts into `header_authenticated_media_v1`.**
`authorized_media_origins_v1` (absolute proxy-node URLs, still header-authenticated) is optional.
Without it the server rejects every proxy route (`route_client_unsupported`) and silently plays from
the API origin, even when the administrator set `prefer_proxy` (the default); under `proxy_only` the
start fails (`silo-server/internal/api/handlers/playback_v3.go:1356,3331-3354`,
`internal/noderouting/routes.go:100-102`). The node is the server's choice (round robin among healthy
proxies), never the client's. With the feature the plan's stream URL is absolute and carries no
credential: `{node}/stream/v3/{sid}[?seek=S]` or `{node}/stream/v3/{sid}/master.m3u8` with relative
`segment/{name}` (pv3:3163-3181, 4538-4553); subtitles, fonts and every control call stay on the API.
The node checks the bearer itself (`internal/proxy/mediagrant.go:62-146`). Apple sends the feature only
when the capabilities list it, takes it as agreed only when the start answer's `server_features`
names it too, and accepts only HTTPS (HTTP if the API is HTTP), the session's own `/stream/v3/{sid}`
paths and no query but `seek` (`ApplePlaybackV3Capabilities.swift:65-74`,
`PlaybackSessionBridge.swift:1270-1276`, `StreamRequest.swift:83-96,137-219`). A node the client
cannot reach or refuses is reported as `failure_recovery` with `sourceRefused`: the server replays the
same plan through the API origin and does not count its key (pv3:5418-5423,5655-5668). The Windows
client does all of this.

Mode selection: `silo-server/internal/playback/protocol_v3.go:51-55,195-227`,
`silo-server/internal/api/handlers/playback_v3.go:208-238`.

### 9.2 Delivery URLs

`playback_plan.stream`: `url`, `protocol` (`http_progressive` | `hls`), `headers`, `header_refresh`,
optional `container`, `mime_type`, `header_refresh_url`. In current code `headers` is always `{}`
and `header_refresh` is always `none` (`silo-server/internal/playback/plan_v3.go:497-1344`).

| Delivery | Protocol | URL in header-auth mode |
|---|---|---|
| `original_http` (direct play) | `http_progressive` | `/api/v2/stream/{session_id}` |
| `server_remux_progressive` | `http_progressive` | `/api/v2/stream/{session_id}[?seek=S]` |
| `server_remux_hls`, `server_transcode_hls` | `hls` | `/api/v2/playback/transcode/{session_id}/master.m3u8` |

URLs are root-relative: prefix the saved server base URL (both native clients concatenate strings
rather than doing RFC 3986 resolution, which matters for servers behind a path prefix). Replay URLs
byte-for-byte; never add, drop or move query parameters (§4.2). The single exception is
`timestamp_offset` on a `.vtt` subtitle sidecar URL ([9.6](#96-subtitle-delivery)). Transcode nodes are never visible
in this mode: the API relays manifests and segments on the same URLs.

| Route | Range | HEAD | Notes |
|---|---|---|---|
| `/stream/{sid}` direct play | yes (incl. multi-range), ETag/If-Range/304/412/416 | yes | `silo-server/internal/playback/directplay.go:89-129` |
| `/stream/{sid}` progressive remux | **ignored** (always `200`, chunked, starts at `seek`) | yes | Forward-only; seeking elsewhere needs a `seek_reanchor` replan. |
| `master.m3u8` | no | no | `Cache-Control: no-store` |
| `segment/{name}` | yes | no | Segment URIs in the manifest are relative (`segment/<name>`); default segment length 2 s. |

No v2 media, subtitle or font route redirects.

### 9.3 Request authentication

Every media, manifest, segment, subtitle and font request runs the full auth chain (JWT signature
and `exp` with **no leeway**, an uncached login-session lookup, the rate limiter, viewer access,
session ownership): `silo-server/internal/api/middleware/auth.go:99-202`,
`silo-server/internal/auth/jwt.go:271-297`,
`silo-server/internal/api/middleware/viewer_access.go:36-107`.

- `Authorization: Bearer <access token>`: required.
- `X-Profile-Id`: optional on media routes; if sent it must be the session's profile (else `403`;
  unknown id → `404`).
- `X-Profile-Token`: required only when `X-Profile-Id` names a PIN-locked profile.
- Recommendation: send the same `Authorization` + profile headers as for API calls, and only to the
  API origin.

There is no grace period and no session-bound acceptance of expired tokens. A response that has
already started keeps streaming after `exp`, but every **new** request (seek, FFmpeg reconnect,
every HLS segment and playlist reload) is re-validated. The server also closes responses that make
no write progress for 180 s by default (operator-configurable via `SILO_STREAM_WRITE_STALL_TIMEOUT`;
a paused player that stops reading), so a long pause forces a new request; do not depend on the
exact value.

### 9.4 Token lifetime during long playback

- Access token default lifetime **8 h** (`auth.access_token_expiry`,
  `silo-server/internal/config/config.go:618-619`); administrators can shorten it.
- `POST /api/v2/auth/refresh` keeps the same login session and does **not** revoke older access
  tokens (`silo-server/internal/auth/service.go:1137-1243`). A token frozen into the player stays
  valid until its own `exp`; profile tokens survive refresh.
- Nothing refreshes media credentials server-side (`header_refresh: none`). The protocol's remedy is
  to refresh out of band and retry or reload the URL with the new header (§4.1:870-877).

How the reference clients solve it:

- Android injects a fresh `Authorization`/`X-Profile-*` snapshot on every Media3 `open()`, only for
  the saved server origin, strips them on cross-origin hops, and on a `401` runs a single-flight
  refresh (mutex + "token already changed?" double check) and retries once
  (`silo-android/android-shared/src/androidMain/kotlin/org/siloserver/silo/common/player/AuthenticatedDataSourceFactory.kt:62-278`,
  `.../player/MediaAuthSession.kt:34-230`).
- Apple runs its engine behind a **loopback HTTP server on 127.0.0.1**; each upstream request asks a
  resolver for the current headers, and the app refreshes proactively when within
  min(60 s, 10 % of lifetime) of `exp` (`silo-apple/iosApp/iosApp/.../PlaybackMediaAuthorization.swift:211-228`,
  `MediaAccessTokenExpiry.swift:6-17`).

Constraints for libmpv, where FFmpeg reuses the headers set at load time
(`http-header-fields`) for reconnects and HLS segments:

1. Refresh proactively **before** each `loadfile`, so the frozen token has its full lifetime.
2. If a token can expire during the session (elapsed time including pauses > remaining lifetime),
   either (a) set new `http-header-fields` and reload the same URL at the current position (fine for
   direct play and HLS; a progressive remux needs a `seek_reanchor` replan), or (b) run a small
   loopback proxy that injects the current headers per request (Apple's approach). The choice is a
   Phase 1 spike item.
3. `?token=` in the URL is **not** an option: it is the same short-lived JWT, it leaks into node
   relay URLs and logs, and for long HLS titles the server switches to `#EXT-X-DEFINE` query
   substitution, which FFmpeg's HLS demuxer does not support.

### 9.5 Errors on media URLs

v2 media routes collapse error codes to the HTTP status before the first byte (a 404 is always
`not_found`); an error after bytes were sent aborts the connection
(`silo-server/internal/apiv2/stream_response_writer.go:24-46`).

| Status / problem | Cause | Client action |
|---|---|---|
| `401 invalid_token` / `authentication_required` | Expired or missing token | Refresh, then reload with new headers. |
| `401 token_refresh_required` | Role changed | Refresh, retry once; do not sign out. |
| `401 session_expired` | Login session revoked | Terminal; sign in again. |
| `403 profile_verification_required` | PIN profile without valid profile token | PIN entry. |
| `403 permission_denied` | Session of another user/profile | Terminal for this attempt. |
| `404 not_found` | Session gone or not rebuildable, segment not yet available, file missing | Ambiguous: follow the reconnect rule (§6.2 of the protocol, see [8.7](#87-replan)); Android retries 404 for up to 90 s. |
| `410 playback_session_ended` | Session stopped, expired (idle reaper) or admin-terminated: media routes do not distinguish | If the viewer is still watching, ask a JSON route: send a progress sample (or replan). `404` there = expired → start a new attempt at the current position; `410` there = admin stop → leave. |
| `409 conflict` | Route unbound | Replan. |
| `429 rate_limited` + `Retry-After` | Limiter (defaults 120 req/s, 6000/min per IP) | Back off. |
| `503 dependency_unavailable` | Temporary (session check, viewer-access timeout, transcode not ready) | Retry with bounded backoff. Only the auth and viewer-access gates set `Retry-After` (5 s / 1 s); media-handler 503s ("manifest not ready", "transcode temporarily unavailable") carry none, so fall back to a client delay (Android: 1, 2, 4, 8 s within 90 s). If it persists, replan. |
| 5xx / network after first frame | e.g. node loss | Reconnect rule, not `failure_recovery`. |
| `422 validation_failed` on manifest/segment with `X-Silo-Tone-Map-Execution-Error` | Server tone-map recipe can no longer run (`source_revision_changed`, `source_preflight_rejected`) | Do not retry the URL; replan with `failure_recovery`. |

Client network timeouts must exceed ~45 s: the server may hold a manifest for up to 30 s and a
segment for ~12 s (plus up to 30 s after a seek restart).

### 9.6 Subtitle delivery

`playback_plan.subtitle.inventory` is the authoritative list (external, then embedded, then
downloaded). Each entry: `track_id` (`file:{id}:subtitle:{n}`), `combined_index`, `source`,
`codec`, `language`, `label`, `forced`, `default`, `hearing_impaired`, `delivery`
(`sidecar` | `burn_in_only`), `url` (sidecar only), `font_bundle_url` (embedded ASS/SSA),
`sync_key`. Select by `track_id` or `combined_index`; never count entries. Selection semantics are
in [8.8](#88-tracks-audio-and-subtitles).

Sidecar URL: `/api/v2/stream/{sid}/subtitles/{combined_index}{ext}?file_id=F&<identity pin>` (use it
verbatim):

| Ext | Content | MIME |
|---|---|---|
| `.vtt` | Text conversions (SRT, mov_text, …); `convert` mode is always VTT | `text/vtt` |
| `.ass` | ASS/SSA lossless | `text/x-ssa` |
| `.srt?original=1` | Original SRT, only if the client sent `subrip_sidecar_v1` | `application/x-subrip` |
| `.sup` | Embedded PGS, lossless | `application/octet-stream` |

- DVD/DVB bitmap tracks are `burn_in_only`.
- Cue times are absolute **source** time. For `.vtt` only, the client may append
  `timestamp_offset=<s>` (typically `-timeline_offset_seconds`); otherwise apply the offset as a
  subtitle delay in the player.
- Font bundle for embedded ASS: `GET /api/v2/stream/{sid}/subtitles/{n}/fonts?...` is a JSON
  operation returning `{ "items": [{ "name", "data" (base64) }] }`; send
  `Accept: application/json`.
- Sidecar requests need the same bearer and do not count as session activity.
- Stored subtitles (`GET /api/v2/subtitles/{media_file_id}`) are metadata only; they become playable
  as `source: downloaded` inventory entries.

For mpv: `sub-add <base URL + inventory url>` with the current headers; fetch and decode the font
bundle for sidecar ASS. Sources: `silo-server/internal/playback/subtitle_inventory_v3.go:157-323`,
`silo-server/internal/api/handlers/stream.go:345-779`, protocol §8.

### 9.7 LAN "local route"

A LAN route to the same server is HTTPS with a pinned self-signed key (SPKI SHA-256 from
`GET /api/v2/system/connections`; `silo-server/docs/architecture/server-identity.md:119-142`).
The server never tells a client it is on the LAN: the client finds out itself.

`GET /api/v2/system/connections` (account bearer, no profile) answers `server_id`, `current`,
`endpoints[]` and `local_route { spki_sha256, url? }`; `url` is the administrator's `server.local_url`,
and `local_route` is absent when the API port does not answer TLS
(`silo-server/internal/apiv2/system_identity.go:134-150,210-232`). The key is derived from
`SECRET_KEY`, so rotating that key changes the pin; the certificate is made per process for no name.
Client rules (`server-identity.md:126-147`): learn the pin only over a saved HTTPS address without
following redirects; reduce each candidate (`local_route.url`, `_silo._tcp` adverts with a matching
TXT `id`) to private IPv4 (RFC 1918) addresses and try `https://ip:same-port`; accept only the pinned
key; `GET /api/v2/system/identity` there must answer the saved server's id; keep the saved address
as the credential owner, move back as soon as the route fails to connect, check again when the network
changes. Apple implements all of it, Bonjour included, with a 3 s probe, never on cellular, and re-reads
the pin every 15 minutes; its API calls and media both move, media only when it is on the saved origin
(`LocalRouteController.swift:41-56,237-333,488-525`). Android does the same without Bonjour
(`LocalRouteController.kt:62-82`, `LocalRouteInterceptor.kt:21-117`).

The Windows client follows Apple without Bonjour so far (`local_route.url` only): `LocalRouteMonitor`
checks, `LocalRouteHandler` moves the API calls but sign-in (`/api/v2/auth/`), writes included as Android moves
them, repeating one on the saved address only when the route did not connect, and, behind the media proxy (9.4), the
media; FFmpeg never sees the pinned connection. The server keys connection state (`playback/connection`, learned limits, probe results) by the
path a request came over (`local` for a private client address, else a keyed hash of it), and it builds absolute
URLs such as `bandwidth_probe_url` from the address it was reached at.

## 10. Progress, resume, and completion

All positions in this section are **source-timeline seconds** (see [8.6 Timeline](#86-timeline-model)).
Session progress and stop are playback mutations: bearer token plus the profile headers from
[section 6](#6-profiles-profile-headers-and-client-identification).

### 10.1 Progress samples

`POST /api/v2/playback/{session_id}/progress` (`updatePlaybackProgress`). `session_id` is a
canonical UUID.

| Field | Type | Notes |
|---|---|---|
| `installation_id` | string (UUID), required | From `GET /api/v2/playback/capabilities`. |
| `sequence` | int64 ≥ 1, required | Client-minted, one per sample, per session. |
| `position` | double ≥ 0, finite, required | Source-timeline seconds. |
| `is_paused` | bool, required | The only way the server learns about a pause. |

There is no duration field; the server uses the probed file duration.

`200` returns `PlaybackMutation`: `{ "outcome": "applied" | "replayed" | "stale_sample", "accepted"?: { "sequence", "position", "is_paused" } }`.
`accepted` is the latest committed sample.

Sequence rules (compare-and-set on the attempt row, `last_sequence < sequence`):

- Higher than the last committed sequence → `applied`, even if `position` went backwards (a rewind
  is a valid resume point).
- Lower → `stale_sample`: nothing is written and it does **not** count as a heartbeat; `accepted`
  carries the newer sample.
- Equal with the identical payload → `replayed` (side effects rerun idempotently; counts as
  activity). Equal with a different payload → `409 progress_conflict`.
- Gaps are allowed. A retry must resend the exact same `(sequence, position, is_paused)`.
- The sequence space belongs to the session and continues across replans of that session. A new
  `start` begins a new sequence space.

| Status / problem code | Meaning | Client action |
|---|---|---|
| `422 validation_failed` | Bad body, `installation_id` not a UUID, non-finite position | Bug; do not retry. |
| `409 installation_changed` | `installation_id` is not this server's | Refetch capabilities; start a new attempt. |
| `403 permission_denied` | Session belongs to another account/profile | Leave playback. |
| `404 not_found` | Session unknown, expired (24 h after start or last replan), stopped, or reaped as idle | Session is over. If the viewer is still watching, start a new attempt at the saved position. |
| `410 playback_session_ended` | An administrator ended the session | Leave playback; do **not** restart. |
| `409 progress_conflict` | Same sequence, different payload | Bug. |
| `503 dependency_unavailable` | Store unavailable | Retry the identical sample. |

Server-side effects of an applied or replayed sample: the live session is always updated. The
resume position is updated too (last write wins), but only when the session persists progress:
persistence is off for a session started with `progress_persistence: "client"` and for a session
whose file is one part of a multi-part item (`presentation_part_total` > 1). Such a session
persists nothing at progress or stop (no resume point, completion, history or scrobbles). A
position ≤ 0 is never persisted. Below `playback.min_resume_threshold`
(admin setting, default 5 % of duration) the resume write is skipped. Session progress does
**not** publish a realtime event.

Sources: `silo-server/internal/apiv2/playback.go:200-205`,
`silo-server/internal/api/handlers/playback_service.go:351-477`,
`silo-server/internal/playback/protocol_store_v3.go:44-64,474-509`,
`silo-server/internal/playback/planstore/postgres.go:427-448`.

### 10.2 Liveness: progress is the heartbeat

- The server sweeps every 15 s and ends sessions idle for **45 s** (at least 5 min when a proxy
  node serves the media); the window is **30 min** when the latest sample said `is_paused: true`
  (`silo-server/internal/playback/session.go:405-423`).
- Activity = start, a replan that returns a playable plan, progress (`applied`/`replayed`), HLS
  playlist/segment requests, open progressive streams, valid control-socket frames. Route events,
  `/sync/progress` and subtitle sidecar requests do **not** count.
- Client rule from the protocol: post progress **every 10 s while the session is open, playing or
  paused**. Reporting a pause promptly is worthwhile (it switches to the 30-min window and drives
  watch-provider pause scrobbles).
- An expired session is finalized by the server; later progress gets `404`.

Sources: `silo-server/docs/architecture/playback-protocol-v3.md:309-379`,
`silo-server/internal/playback/session.go:405-423,1003-1029`.

### 10.3 Stop

`DELETE /api/v2/playback/{session_id}` (`stopPlayback`) **with a JSON body** (in .NET, build an
`HttpRequestMessage` with `Content`):

`{ "installation_id", "stop_id", "sequence"?, "position"?, "is_paused"? }`

- `stop_id`: canonical UUID minted **once**, persisted, and reused on every retry.
- `sequence` and `position` appear together or not at all (else `422`). The final sample is applied
  only if its sequence is newer than the last progress, so send it as `last_sequence + 1`.
- The first stop wins. It writes resume and history (neither below `playback.min_resume_threshold`,
  default 5 %), tears down the session/transcode, and denies the stream URLs (`410` afterwards).
- `200 { "outcome": "stopped", "stop_id", "accepted"?, "history_id"? }`. Every later DELETE (any
  `stop_id`, also after expiry or an admin stop) returns `200 { "outcome": "replayed", ... }` with
  the stored receipt. `404` only when the row is unknown or expired.
- If no sample with a position > 0 was ever sent, nothing is recorded.

Sources: `silo-server/internal/apiv2/playback.go:206-212,413-426`,
`silo-server/internal/api/handlers/playback_service.go:523-656`.

### 10.4 What Android does

- Reports every 10 s (`PROGRESS_REPORT_INTERVAL_MS`) while the session is active, paused included;
  no extra reports on pause/seek; first report 10 s after the plan is adopted
  (`silo-android/android-shared/.../player/PlaybackSessionLifecycle.kt:683-749`).
- Keeps the sequence in a durable per-attempt journal starting at 1; saves the pending sample
  before sending; resends an unconfirmed sample verbatim before allocating a new one; validates the
  receipt and sets the local counter to `max(local, accepted.sequence)`
  (`silo-android/shared/.../repository/SequencedPlayback.kt:626-660`).
- After 3 consecutive failed ticks (~30 s) it shows "Playback progress is pending"; `410` leaves
  playback.
- Stop: flushes a final sample, journals a stop with a new `stop_id`, retries up to 3 times
  (250/500 ms backoff) on network errors and `503`; `404` counts as settled. An unsent stop is
  replayed once before the next start for the same server/profile
  (`SequencedPlayback.kt:665-738`, `silo-android/docs/playback/sequenced-api-v2.md:24-49`).
- Android does not use `/api/v2/sync/progress` or `/api/v2/progress`.

### 10.5 Resume

- Source of the position: `GET /api/v2/watch/{id}` → `user_data.position_seconds` (omitted when 0),
  `duration_seconds`, `is_in_progress`, `played`, `last_file_id`
  (`silo-server/internal/apiv2/watch.go:218-231`). `GET /api/v2/progress` lists all progress but has
  no Android caller.
- `start_position` in the start request: **omitted** → the server applies the stored resume point,
  or 0 when there is none or the stored progress carries the watched flag. That flag stays set
  through a rewatch, so watch detail can report `position_seconds`, `is_in_progress: true` and
  `played: true` while an omitted start begins at 0: **send the position explicitly to resume**.
  **Present** → exactly that position, `0` = start over. Range 0–31,536,000.
- Android sends an explicit override (`0.0` for Start Over); otherwise `position_seconds` minus
  the resume rewind (client-local `player.resume_rewind_seconds`, default 7 s, applied only when
  the position is ≥ 30 s); it omits `start_position` when there is no saved position. It picks the
  version from `user_data.last_file_id` (`silo-android/androidApp/.../MobileVideoPlaybackStarter.kt:236-318`).
- `DELETE /api/v2/progress/{media_item_id}/position` (`clearResumePosition`) resets a movie or
  episode resume point to 0 (gate on `GET /api/v2/progress/capabilities` → `clear_resume_position`).
- `POST /api/v2/sync/progress` (batch, **milliseconds**) and the progress bootstrap snapshots are
  for offline sync and out of MVP scope.
- **Multi-part items** (a version with `presentation_part_total > 1`, grouped in watch detail's
  `playback_variants[{ variant_id, parts[{ part_index, versions[], default_file_id }], part_count, total_duration_seconds }]`):
  session progress persists nothing for them (10.1). Resume must then be reported item-absolute
  through `POST /api/v2/sync/progress` (`items[{ media_item_id, position_ms, duration_ms }]`); an
  omitted `start_position` maps the stored absolute resume onto the right part
  (`silo-server/internal/api/handlers/playback_v3.go:3680-3756`). No reference client does this for
  video; the MVP follows them (13.4).

### 10.6 Completion and "next up"

- The **server** decides completion: `position / file_duration > playback.watched_threshold`
  (admin setting, default 90 %), checked on every applied sample and at stop. Credits markers play
  no part. On completion the resume position resets to 0
  (`silo-server/internal/userstore/threshold.go:4-51`,
  `silo-server/internal/userstore/pgstore/progress.go:63-94`).
- The client only has to send a final sample near the end; explicit marking is not required.
- `POST /api/v2/watched/{id}` / `DELETE /api/v2/watched/{id}` (`markWatched` / `unmarkWatched`,
  `204`) are for manual UI actions. Unmark also clears the resume position. `markWatched`,
  `unmarkWatched` and `clearResumePosition` are tagged `non_retryable`.
- Android opens "Up Next" at `credits.start`, or at `duration − playback.next_up_prompt_seconds`
  (default 30) without a credits marker; auto-advance follows `playback.auto_play_next` (default
  true). Advancing stops the current session and starts the next episode at 0; the next episode is
  resolved on the client from the current season's episodes plus those of the next regular season
  (`silo-android/androidApp/.../PlayerViewModel.kt:3003-3019,5510-5721`). Details in 10.7.

### 10.7 Next episode

No v2 operation returns a next episode, and `/watch` has none. Android and Apple resolve it the
same way:

1. **Resolve**: `GET /api/v2/catalog/series/{series_id}/seasons` and
   `.../seasons/{current}/episodes`; add the episodes of the lowest-numbered later season that is
   not specials (`is_specials`). The next episode is the smallest (season, episode) strictly after
   the current one (`silo-android/shared/src/commonMain/kotlin/org/siloserver/silo/playback/NextEpisodeResolver.kt:24-34`).
   A multi-part item plays its next part before moving on.
2. **Prompt**: when playback crosses `credits.start` of the playing version going forward, or
   `duration − playback.next_up_prompt_seconds` (default 30; 0 = only at the end) without a credits
   marker. Resuming inside the credits does not trigger it.
3. **Countdown**: only if `playback.auto_play_next` (default true) and the unattended-advance streak
   is below `player.passout_threshold` (client_local, default 3, 0 = off). A prompt opened before the
   end counts down the remaining playback and advances at end of file; one opened at end of file uses
   a 10 s countdown. "Play now" resets the streak; pause cancels the countdown but keeps the prompt;
   dismiss hides it until the end.
4. **Advance**: stop the current session (DELETE with the final sample), then `GET /watch/{next}`
   and a new start with a fresh `playback_attempt_id`, explicit `start_position: 0` and no resume
   rewind. Carry the audio/subtitle choice over by language, forced flag and codec (never by index),
   or rely on the saved preferences that the next episode's watch detail returns in `effective_*`.

## 11. Markers, intro skip, chapters, and trickplay

### 11.1 Where markers come from

The playback start decision contains **no** markers or chapters. The watch detail carries them:
`GET /api/v2/watch/{id}?file_id=<playing file>` returns per version:

- `marker_segments[]`: `{ "kind": "intro" | "credits" | "recap" | "preview", "start_seconds", "end_seconds", "thumbnail_url"?, "thumbnail_thumbhash"?, "thumbnail_capture_seconds"? }`,
  in source-time order. A kind may occur more than once; act on the range that contains the
  current position.
- Singular `intro`, `credits`, `recap`, `preview` objects `{ "start_seconds", "end_seconds" }` as
  compatibility projections (one range each).

Use the entry of the version that is actually playing (`source.media_file_id` in the plan). The
item-level singular fields mirror one selected file: the `file_id` version when it is one of the
item's versions, otherwise the first playback variant's default file; they fall back to the first
version with that kind only when nothing was selected
(`silo-server/internal/catalog/detail.go:4194-4206`). Read the per-version entry anyway, so a
version switch never depends on the server's pick. Passing `file_id` also makes the server fill
that file's on-demand markers. Ranges are `[start, end)` in
source seconds.

Dedicated reads, if needed: `GET /api/v2/markers/files/{file_id}` (preferred) and
`GET /api/v2/markers/items/{item_id}` (picks the first accessible file) return `FileMarkers`:
`file_id`, `marker_segments`, and `intro`/`credits`/`recap`/`preview` as
`{ "start_seconds"?, "end_seconds"?, "source"?, "provider"?, "confidence"?, "algorithm"?, "detected_at"? }`
(`{}` when absent). Sources: `silo-server/internal/apiv2/markers.go:21-48`,
`silo-server/docs/markers-api.md:202-248`.

During playback, the per-session control socket can push `markers_updated` (see
[8.9](#89-control-socket-optional)); Android re-reads `getWatchState(file_id)` after a reconnect or a
replan to another file.

### 11.2 Intro-skip mode

Setting `playback.intro_skip_mode`: `never` | `ask` | `always`, **default `ask`**; scopes `profile`
and `profile_device`, resolved `profile_device → profile → default`
(`silo-server/contracts/settings/v1/manifest.json:615`).

- Read: `GET /api/v2/settings/values/effective?keys=playback.intro_skip_mode` →
  `items[{ key, value, source, scope? }]`, `revision`.
- Write: `PUT /api/v2/settings/values/playback.intro_skip_mode?scope=profile_device` with
  `{ "value": "always" }` (Android writes at `profile_device` scope).
- Both the read and the write need `X-Profile-Id` **and** `X-Silo-Device-Id`: for a key that
  allows `profile_device`, a missing device header is `422 validation_failed` at
  `header.X-Silo-Device-Id` (the server does not fall back to the profile value). The `device_id`
  query parameter is no substitute; it names *another* registered device of the profile.
- Related keys: `playback.auto_skip_credits` (false), `playback.auto_skip_recap` (false),
  `playback.auto_play_next` (true), `playback.next_up_prompt_seconds` (30, range 0–120).
- The deprecated `playback.auto_skip_intro` (bool) is mirrored for one release; use it only when the
  settings contract revision is below 7.

Behaviour (`silo-server/docs/design/2026-08-16-intro-skip-mode.md:106-197`), with a 5 s wall-clock
timer:

- `never`: nothing.
- `ask`: entering the intro shows "Skip Intro". Selecting it seeks to `end` and resolves the intro;
  Back hides and resolves it. A timeout hides the pill **without** resolving, so seeking back in
  offers it again. Seeking out hides it; seeking back in restarts the timer.
- `always`: entering the intro seeks to `end` immediately and shows "Intro skipped / Watch Intro".
  Selecting it seeks to `start` and resolves; the timeout resolves.
- Shared: a resolved intro never prompts again in that session. Pause freezes the timer;
  not-playing states shorter than 1500 ms count as rebuffering and are ignored. The timer starts
  only once playback is actually running.

Android (`silo-android/shared/.../domain/player/IntroAutoSkipController.kt`) keys an intro by
`sessionId:fileId:start:end`, uses only the singular `intro` of the playing version (so a second
intro segment is never offered), and shows a manual skip pill for recap and credits that seeks to
the range end.

### 11.3 Chapters

Chapters exist only in version metadata: `WatchFileVersion.chapters[]` in `getWatchState` and
`FileVersion.chapters` in catalog detail/versions:
`{ "index", "title", "start_seconds", "end_seconds", "source": "embedded", "thumbnail_url"?, "thumbnail_thumbhash"? }`
(`silo-server/internal/apiv2/watch.go:168-176`). mpv reads container chapters itself on direct play,
but not through HLS remux or transcode routes, so the client should use the server list.

### 11.4 Trickplay (optional)

When a version has `trickplay_available: true`:
`GET /api/v2/watch/{id}/trickplay?file_id=` →
`{ file_id, interval_ms, thumbnail_width, thumbnail_height, tile_columns, tile_rows, thumbnail_count, sheets[{ index, url }], expires_at }`;
`404 not_found` when there are no previews. Sheet URLs carry their own authorization, so send no
bearer header: each is either a signed root-relative `/api/v2/artwork/<key>?exp=<unix>&sig=<sig>`
path (resolve it against the server as for other artwork, [7.10](#710-images)) or an absolute
presigned S3 URL. Refetch after `expires_at`. Thumbnail `i` is on sheet
`i / (cols·rows)`, column `i % cols`, row `(i % (cols·rows)) / cols`
(`silo-server/internal/apiv2/watch_trickplay.go:60-110`). Android does not implement trickplay.

## 12. Realtime events

`/api/v2/events/ws` keeps Home and detail views fresh; a minimal client can skip it and refetch on
navigation.

- Ticket: `POST /api/v2/events/ws-ticket` (bearer, optional profile headers) →
  `{ "ticket", "expires_in" (≤ 30 s), "max_connection_seconds": 300, "protocol": "silo.events.v2" }`.
  Single use; never reuse a ticket to reconnect.
- Connect: `GET /api/v2/events/ws` with
  `Sec-WebSocket-Protocol: silo.events.v2, silo.ticket.<ticket>`, then within 5 s send
  `{ "type": "subscribe", "channels": [...] }`, or declare the channels on the URL
  (`?channels=user_state,catalog`), which needs no subscribe frame. A connection with no accepted
  channel after 5 s gets an `error` frame and close code 1008.
- Frames: `hello`, `subscribed { channels, rejected }`, `snapshot`,
  `event { channel, event, event_id, timestamp, data }`, `error`. The connection lasts at most
  5 min or until the token expires; `{ "type": "access_changed" }` followed by close code 4001 means
  refetch and reconnect.
- Useful events: `user_state.changed { profile_id, content_id, series_id?, change }` (change ∈
  `progress`, `watched`, `favorite`, `watchlist`, `history`, `home_dismissal`, `not_interested`;
  only on a profile-bound ticket), `user_settings.changed { key, scope, profile_id? }` (no value;
  re-resolve), `catalog.item.changed`, `catalog.library.changed`. The `catalog` channel repeats
  each change under legacy names (`metadata.updated`, `library.changed`, `library.item_added`), so
  act only on the `catalog.*` names or debounce refetches.
- Session progress on another device publishes **nothing**; Continue Watching on another device only
  changes after a refetch.

Android uses it only to refresh Home: it subscribes to `user_state` and `catalog` and refetches Home
on `subscribed`, on any catalog event, and on a `user_state` event for the active profile, behind a 2 s
debounce, reconnecting after 1 s doubling to 30 s (`silo-android/shared/.../network/HomeRealtimeClient.kt`,
`repository/HomeRealtimeCoordinator.kt`). The web (`components/RealtimeEventsProvider.tsx`) subscribes
`catalog`, `user_state` and `notifications` (plus `user_settings` and the administrator channels) after
`hello`, refreshes Home at once on a membership change but once per 30 s window on `progress`, batches
catalog events over 2 s, keeps the unread badge from the `notifications` snapshot (≤ 25 unread rows, a
lower bound) and from `notification.created` / `notification.read`, and reconnects after 1 s (at once
after `access_changed`). Sources: `silo-server/docs/realtime-api.md:23-106`,
`silo-server/internal/api/handlers/events_ws.go:735-739`.

Granary (`Realtime/EventsLane`, `Realtime/RealtimeEvents`): one connection per signed-in profile, opened
with the window and replaced on a profile or server switch, none while signed out. A profile-bound
ticket (bearer and profile headers, no body), the protocol and `silo.ticket.<ticket>` offered, `subscribe`
to `catalog`, `user_state`, `notifications` after `hello`; frames up to 1 MiB. The connection ends with
`max_connection_seconds`; a lost one comes back after 1 s, doubling to 30 s until one subscribes and holds
longer than 1 s, and at once after `access_changed` or close code 4001 on a connection that held that long
(one closed sooner waits like any loss, so a server that closes every connection is not asked in a loop).
A server answering the ticket with 404 or 501 gets no lane until the next sign-in, profile switch or start.
What it does:
- `user_state.changed` for the profile (`progress` aside), any `catalog` event, `access_changed`, and a
  later connection back after more than 5 s without one (the lane's first has no gap): one
  `ViewerStateChanges.Report()` once the burst has been quiet for 2 s, at most 30 s after its first change.
  Home reloads in place while shown; other pages compare the version on their next visit.
- `user_state.changed` with `progress`: one report per 30 s window, none while this app plays.
- Echoes: the events name no device, so Granary's own changes come back. News of the profile's state within
  5 s of a change the app reported itself is dropped, and a reload waiting only for such news is dropped
  when the app reports (its own reload covers it); a waiting catalog or access change still reloads.
- Any `notifications` event or snapshot: the sidebar's unread count is read again after 1 s of quiet
  (`GET /notifications/unread-count`), rather than counted locally.
- Left out: `user_settings` (card badges and other profile settings are read on sign-in and profile
  switch), the web's toasts for new notifications, the open inbox's own list (only the count follows),
  the request lists, the administrator channels (`jobs`, `sessions`, `tasks`, `scans`, `settings`,
  `download_preparations`, `history_import`; Admin → Activity polls), and the web's reconnect with a
  growing wait when `notifications` is rejected with `profile_required` (logged; the other channels go on).

### 12.1 Playback control lane

Each playback session has a control socket of its own, which the web player and Android keep while a title plays; an
administrator's pause, stop, terminate and message (7.16) reach a player only through it, and without one the session
reports `has_playback_control: false`. Sources: `silo-server/docs/realtime-api.md` ("Owner-bound playback control
handshake (v2)"), the web's `player/hooks/usePlaybackRealtime.ts` and `player/realtime-protocol.ts`, Android's
`network/PlaybackRealtimeClient.kt` and `playback/PlaybackCommandDispatch.kt`.

- Ticket: `POST /api/v2/playback/sessions/{session_id}/control/ws-ticket` (bearer and profile headers) with
  `{ "installation_id"? }` → `{ "ticket", "expires_in" (≤ 30 s), "max_connection_seconds" (14400), "protocol":
  "silo.playback-control.v2" }`; single use, for the caller's own session only (403 otherwise, 409 for another
  installation, 404 once the session ended).
- Connect: `GET /api/v2/playback/sessions/{session_id}/control/ws`, offering `silo.playback-control.v2` then
  `silo.ticket.<ticket>`; the server selects only the protocol. No token in the URL, no bearer header. Frames over
  64 KiB close it (1009).
- Once open the player sends `{ "type": "hello", "session_id", "client": { "name", "version" }, "capabilities": {
  "commands": [...] } }`; the server issues only the commands named. A command is `{ "type": "command", "command_id",
  "session_id", "name", "payload", "issued_by": { "kind": "admin" | "server" } }`; the player answers `{ "type": "ack",
  "command_id", "session_id", "status": "accepted" }`, does it, then `{ "type": "result", …, "status": "completed" |
  "rejected", "error"? }`. A pause, resume or stop not confirmed before its deadline ends the session.
- Commands: `pause`, `unpause`, `play_pause`, `seek` (`position` | `position_seconds` | `seconds`), `set_volume`
  (`volume` | `level`, 0 to 1), `stop`, `terminate`, `display_message` (`title`?, `message`), `server_restarting`,
  `server_shutting_down`; also `play_media`, `set_audio_track`, `set_subtitle_track`, `plan_invalidated`, which
  Granary does not announce. Events (`type: "event"`: `markers_updated`, `subtitle_ready`, …) Granary ignores.
- Granary (`PlaybackControlLane`): a lane per session that plays, a fresh ticket per connection, back after 0.5, 1, 2,
  5, 10, then 30 s; none on a server that answers the ticket with 404, 501 or 503, and none after an administrator's
  terminate. Stop and terminate answer first and then end playback with the administrator's words ("An administrator
  stopped / ended this playback session.", or their own message); a message shows as the web's notice for 8 s.

## 13. Consequences for the Windows client

### 13.1 Decisions this contract implies

1. **v2 only.** Probe `GET /api/v2/system/info` when adding a server and on every switch; a `404`
   with Go's `http.NotFound` body (`404 page not found`, with or without the single trailing `\n`)
   means "server too old". No v1 code path anywhere.
2. **One HTTP pipeline for the API** (a `DelegatingHandler` chain over `HttpClient`) that adds the
   bearer token, the profile pair and the `X-Silo-*` device headers to same-origin requests only,
   performs proactive refresh (60 s margin) and single-flight refresh on `401` with the
   "token already changed?" check, and maps Problem Details to typed errors by code.
3. **Serializer settings:** `snake_case`, skip nulls on write, tolerate unknown members and unknown
   enum values on read (System.Text.Json source generation with
   `JsonIgnoreCondition.WhenWritingNull` and string enums with a fallback).
4. **Secrets per server** in a DPAPI-protected file (`CurrentUser` scope, local app data): access
   token, refresh token, expiry, profile id, profile token (5.3). Never in logs.
5. **Profiles:** picker after sign-in; `verify-pin` without profile headers; on
   `403 profile_verification_required`, back to the picker without signing out.
6. **Playback** in header-auth mode (`header_authenticated_media_v1`), with progress every 10 s,
   a persisted `stop_id`, and the replan rules from 8.7. Refresh the access token right before every
   `loadfile`, and pass `Authorization` (plus the profile pair) through mpv's
   `http-header-fields`. How to survive a token expiring mid-playback (reload with new headers vs a
   loopback proxy) is decided in the Phase 1 spike.
7. **Resume:** read `user_data` from watch detail and send `start_position` explicitly.
8. **Markers** from watch detail (`marker_segments` of the playing version), intro-skip mode from
   `playback.intro_skip_mode` (default `ask`), chapters from the version's `chapters[]`.

### 13.2 Capability report for mpv: starting point (to validate in Phase 1/4)

mpv decodes with FFmpeg, in software or through D3D11VA/DXVA2 hardware acceleration, and renders
HDR with libplacebo. An honest, conservative first report:

- `video_evidence: "declared"` with `codecs_video` = codecs FFmpeg decodes and that the machine can
  play smoothly, and `codecs_video_hardware` = codecs with a D3D11VA decoder profile on this GPU.
  Moving to `platform_attested` with real `video_decode` entries is the next step and gives the
  server real bounds: profiles from `ID3D11VideoDevice::GetVideoDecoderProfile`, bit depths from
  `CheckVideoDecoderFormat` (NV12/P010), and `max_width`/`max_height`/`max_frame_rate` by probing
  candidate sizes with `ID3D11VideoDevice1::GetVideoDecoderCaps` or `GetVideoDecoderConfigCount`.
- At `declared` the server only checks codec membership (both lists count the same); it does not
  bound bit depth, frame rate or (apart from `max_resolution`) size. So list only codecs this
  machine plays smoothly at whatever the library contains, and leave `hevc` out of both lists on a
  weak CPU. `software_video_decode_v1` has no effect at `declared`; it matters only at
  `platform_attested`, where it lets `hardware: false` `video_decode` entries count. Send it then
  only if those entries carry honest bounds (`bit_depths`, `max_width`, `max_height`,
  `max_frame_rate`).
- `audio_evidence: "declared"`, `codecs_audio` = everything FFmpeg decodes; no passthrough in the
  MVP.
- `containers`: `mkv` (covers WebM), `mp4` (covers mov/m4v), `avi`, `ts`, `m2ts`, `ogg`, `flv`,
  `wmv`, plus other ffprobe format names mpv plays (the scanner falls back to the first ffprobe
  format name, e.g. `mpeg` for .mpg/.vob).
- `validated_claims` on `original_http`: `client_selected_audio_track_v1` (mpv switches audio
  tracks itself) and, once libplacebo tone mapping is confirmed, `client_managed_dynamic_range_v1`.
- Subtitles: `sidecar_text`, `ass_styling`, `font_attachments`, `embedded_text`, `embedded_bitmap`
  true; `embedded_subtitles_v1` with `native_embedded` entries for mkv and mp4 using
  `track_identity: "ffmpeg_stream_index"` (mpv's `track-list/N/ff-index`), as Apple does.
- HDR facts come from the monitor the window is on (the DXGI output with the largest intersection
  with the window, read with `IDXGIOutput6::GetDesc1`; re-probe when `IDXGIFactory1::IsCurrent`
  turns false). `ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020` means HDR10/PQ output is
  active; DXGI cannot report HDR kinds that are supported but not active. Send them as
  `output.display` with `hdr_evidence: "exact"` and `hdr_types`, or `hdr_evidence: "unknown"` when
  the probe fails, and always also `output.hdr_details` (decoder ∩ display).
- **Do not derive `max_resolution` from the monitor.** Under `quality_preference: auto` it is a
  delivery ceiling: a 4K source on a 1080p monitor would lose direct play and be transcoded, although
  mpv scales to the window itself. Send the decoder ceiling instead (Apple sends `2160p`) or omit it.
- Delivery classes: `original_http` and `hls` enabled; `progressive` (remux) only after the spike
  confirms that forward-only streams behave in mpv.

### 13.3 Not in the MVP

Control socket and `plan_invalidated_v1`, `buffering_quality_v1`, the realtime events socket,
the LAN local route, trickplay, `/sync/progress` and progress bootstrap, external (OIDC) sign-in,
signup, invitations, setup wizard, downloads, audiobooks, ebooks, watch together, admin.

### 13.4 Open questions for the Phase 1 spike

1. mpv rebases the start time by default (`--rebase-start-time=yes`; set it explicitly). Confirm
   that `time-pos` is 0 at `stream_origin_seconds` for HLS and progressive copy remux and at the
   container start for direct play. (Affects `timeline_offset_seconds`.)
2. Does mpv's `sub-add` send `http-header-fields` when fetching a sidecar URL?
3. Can FFmpeg in the chosen libmpv build play an HLS WebVTT subtitle rendition with
   `X-TIMESTAMP-MAP`?
4. Does mpv's `ff-index` match the server's `stream_index` for embedded subtitle selection?
5. Token expiry mid-playback: does reloading with new headers at the current position work
   seamlessly enough, or is a loopback proxy needed?
6. Which FFmpeg version does the libmpv build bundle (redirect handling, HLS features)?
7. **Decided**: multi-part items (10.5) behave as in every reference client. None of them saves
   resume for multi-part video: Android never calls `/sync/progress`; Apple and the web player use it
   only for offline progress and audiobooks. The MVP plays the parts in sequence (start at the first
   part; then the lowest-numbered later part of the same presentation and edition, preferring the
   same resolution, as in
   `silo-android/shared/src/commonMain/kotlin/org/siloserver/silo/playback/PlaybackParts.kt`) and
   does not save resume for them.

### 13.5 System media controls and input

- **SMTC metadata**: title = `WatchDetail.title`; subtitle line = `<series_title> – S01E05` for
  episodes, otherwise the year. Artwork: `WatchDetail.poster_url` is documented for system media
  controls (a poster for movies, the episode still for episodes); `backdrop_url` is the series
  backdrop (`silo-server/internal/apiv2/watch.go:61`). The URLs are presigned and expire: download
  the bytes without `Authorization` and hand them to SMTC as a stream
  (`RandomAccessStreamReference.CreateFromStream`) rather than passing the URL.
- **Timeline** shown to SMTC: duration = `playback_plan.source.duration_seconds`, position in
  source time (8.6).
- **Skip intervals** for skip buttons, arrow keys and media keys: `player.video_skip_back_seconds`
  (default 10) and `player.video_skip_forward_seconds` (default 30), profile scope.
- **Keyboard map** of the server's web player, as a reference
  (`silo-server/web/src/player/hooks/useKeyboardShortcuts.ts`): Space/K play-pause, F fullscreen,
  M mute, C captions, Left/Right skip by the profile intervals, Up/Down volume ±5 %; keys are
  ignored while a text field or menu has focus.
- Enable the SMTC "Next" button only when a next episode has been resolved (10.7).

## Appendix: where docs and code disagree

These were found while deriving this document. The code is authoritative; the entries explain why
this document deviates from a server doc, a fixture, or a reference client. Items marked "not
verified" need a live server or the Phase 1 spike.

### Discovery, sign-in, tokens, profiles

- Android's `HealthApi` calls a root `/health` (`silo-android/shared/.../network/api/HealthApi.kt:23`)
  and its migration doc calls that the unversioned liveness route; the server mounts health only at
  `/api/v1/health` (`silo-server/internal/api/router.go:3155-3157`). Apple uses `/api/v1/health`.
- Device poll interval is 3 s in code (`silo-server/internal/auth/device_login.go:65`); fixtures
  show 5. Access-token fixtures show `expires_in: 3600`, the default is 8 h. Use returned values.
- verify-pin fixture has an `expires_at` timestamp; production returns `null`.
- Refresh-token reuse (old tokens stay valid) is undocumented.
- OpenAPI lists no `429` for device start/poll/cancel, but they are rate-limited.
- Android comments say access-policy changes invalidate PIN tokens; the server ties them only to
  the PIN revision.
- Android's relative-URL rebasing drops a reverse-proxy path prefix
  (`AuthInterceptorImpl.kt:920-932`); `system/connections` endpoint URLs are origin-only, while the
  device-login base keeps the path.
- Android ignores `password_change_required` and `Retry-After` on login/PIN lockouts.
- A disabled account looks different per path: login `403 permission_denied`, refresh
  `401 session_expired`, API key `401 invalid_token`.
- `client_upgrade_required` is in the catalog but no v2 handler emits it.

### Catalog

- `bitrate`: `getWatchState` documents bits/s (`silo-server/internal/apiv2/watch.go:78`, fixture
  `8000000`), `FileVersion` documents kbps (`catalog_items.go:338`); the scanner stores kbps.
- The "unit in the field name" rule is not followed everywhere (`duration`, `runtime`,
  `{start, end}` markers in catalog detail vs `duration_seconds`, `{start_seconds, end_seconds}` in
  `/watch`).
- `CatalogItemDetail.extras[].file_id` is an integer despite "all IDs are strings".
- `listCatalogItems` documents sort keys `rating` and `random`, which the code rejects with
  `422 invalid_sort_field`.
- `queryCatalogItems` documents "default added date, newest first"; the code defaults to `title`
  ascending without `q`.
- `type` is not validated: an unknown value is ignored and the request becomes unscoped
  (`catalog_parser.go:472-478`). The contract's `422` rule covers unknown fields only, so a
  misspelled `type` silently widens the result. Send only the documented values.
- The configuration that distinguishes Continue Watching/Listening/Reading (`continue_type`) and
  Recently Added Movies/TV (`media_scope`) is absent from the `Section` objects of
  `listHomeSections`/`listLibrarySections`; it is readable only through `getProfileSectionSettings`
  (member `config`).
- Fixtures use fake ids (`movie:heat-1995`, section `continue_watching`); real default section ids
  are `default-continue-watching`, `system-next-up`, ….
- Android still uses the discouraged entry-or-404 favorites/watchlist read.

### Playback protocol

- The protocol document uses v1 paths and envelopes (`GET /playback/capability`,
  `{error, message}`, `400`/`426`); v2 uses `/playback/capabilities`, Problem Details and `422`.
- The v2 capability body has no `enabled` or `transformations` and requires `X-Profile-Id`; the
  protocol document says no profile is needed.
- v1 codes `stale_playback_plan`, `idempotency_key_reused` and `replan_in_progress` all map to v2
  `409 conflict` (only `detail` differs), although the protocol asks clients to wait specifically on
  `replan_in_progress`.
- Body caps 256 KiB / 32 KiB in the protocol document vs 1 MiB on v2.
- The v3 JSON Schemas are lax (`additionalProperties: true`, optional `quality_preference` and
  `client_features`); the v2 OpenAPI requires `quality_preference` on start and replan and
  `client_features` on start (optional on replan), and rejects unknown members.
- Media file ids are integers in the v3 schemas and golden files, strings on v2.
- `decision-response.schema.json` requires a non-empty `available_qualities` with a source entry;
  v2 strips the source entry and allows `[]`.
- `attempt_count` is described as bounding the chain; the code only validates its range.
- The playback capabilities handler sets `Cache-Control: no-store` itself
  (`silo-server/internal/apiv2/playback.go:367`), but the capability registration adapter overwrites
  it with `private, no-cache` plus an `ETag` (`silo-server/internal/apiv2/capability_response.go:100-125`),
  so `docs/playback-api.md:28` is right about the wire behaviour.
- Android does not implement the "404 → new attempt" rule for v2 sessions, the §6.2 reconnect
  algorithm, or `plan_invalidated_v1`; it sends `max_resolution: "sd"`, which parses as no limit.

### Media delivery and stream auth

- `header_refresh: session` and `header_refresh_url` (protocol §4:744-746) are never emitted; plans
  always carry `none` and empty `headers`. The `auth_header_refresh` capability is ignored.
- OpenAPI says header-authenticated media requires the `Authorization` header and the profile
  selector (`silo-server/internal/apiv2/playback_delivery.go:105`); the API origin also accepts
  `?token=` and admits requests without `X-Profile-Id`. Only the proxy enforces header-only.
- `docs/playback-api.md:361-367` says plan URLs carry `st`; stale for header-auth mode.
- `docs/playback-api.md:458-459` says Apple and Android use `/api/v1`; both call
  `/api/v2/playback/start`.
- OpenAPI for `getPlaybackSubtitle` omits `embedded_stream_index`, `external_subtitle_key` and
  `timestamp_offset` (all accepted) and lists `st`, which §4.2 says subtitle URLs never carry.
- v2 media errors lose their legacy codes (`playback_session_not_found` → `not_found`), while the
  protocol text still names the legacy codes.

Not verified (Phase 1 spike):

- FFmpeg/mpv handling of a WebVTT HLS subtitle rendition with `X-TIMESTAMP-MAP`.
- Whether mpv's `sub-add` uses `http-header-fields` for the subtitle fetch.
- Which FFmpeg version the chosen libmpv build bundles.

### Progress, markers, realtime

- `playback-protocol-v3.md:381-401` still describes the v1 progress and stop calls: both answer
  `204`, stop takes no body, a repeated DELETE answers `404`, an ended session is
  `404 playback_session_not_found`. On v2 both answer `200 PlaybackMutation`; progress for an
  unknown, client-stopped or expired session is `404 not_found`, and for an admin-ended session
  `410 playback_session_ended` (not declared in `openapi.json` for `updatePlaybackProgress`). v2 stop
  requires a JSON body `{ installation_id, stop_id }` (a DELETE without a body is `422`), and a
  repeated stop answers `200 replayed` until the attempt row expires.
- `silo-android/docs/api-v2/android-migration-status.md` lists `/api/v2/progress` as migrated (no
  production caller) and omits `/playback/{id}/progress` and `DELETE /playback/{id}`, which Android
  does use (`PlaybackV2Api.kt:352-379`).
- `progress-bulk.md:43-50` says Android feeds progress through sync; Android's `syncProgress` is a
  stub and only sequenced session progress is used.
- `PlaybackSessionLifecycle.kt:47-49` claims 404 renewal and outage recovery for progress; the
  reporter bypasses both for sequenced (all v2) sessions.
- Markers allow repeated kinds (`marker_segments`), but Android reads only the singular
  `intro`/`credits`/`recap`/`preview` fields (intro drives skip, credits drives auto-advance) and
  ignores `marker_segments`.
- `markWatched` is described as a no-op when repeated but tagged `non_retryable`.

Not verified (needs device testing in Phase 1/4):

- Whether mpv's `time-pos` is rebased for MPEG-TS sources or HLS segments carrying source PTS
  (relevant for `timeline_offset_seconds`).
- Whether chapter times are adjusted for a non-zero container start.
- Rate limits on progress beyond the generic `429`.
