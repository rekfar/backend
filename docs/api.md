# The Rekfar API

Resource-oriented HTTP/JSON, versioned at `/v1` from the first endpoint
([ADR-0010](https://github.com/rekfar/docs/blob/main/adr/0010-tech-stack-dotnet-azure-sql.md)),
so a future native client is not broken by web-driven change.

## Conventions

These are decided once, in the host, and every endpoint inherits them.

| | |
| --- | --- |
| Versioning | Everything lives under `/v1`. The version is in the path, not a header. |
| Errors | RFC 9457 `application/problem+json`, always — including bare status codes like 404 and 429. Every problem carries a `traceId` that matches the server log. |
| Casing | `camelCase` JSON, in and out. |
| Rate limiting | Per caller, on the `/v1` group. Health probes sit outside it and are never throttled. |
| CORS | An explicit origin list. The API refuses to start without one, naming the environment it looked in. |
| OpenAPI | Generated from the endpoints, served at `/openapi/v1.json` **in Development only** — the document is a map of the attack surface, and CI can generate it for client generation without the production API publishing it. |
| Auth | An `HttpOnly` session cookie, issued by `POST /v1/auth/verify`. Every user-data endpoint requires it (NFR-SEC-3); the catalogue is readable anonymously by design (FR-PEAK-5). |
| CSRF | Every state-changing request under `/v1` must carry `X-Rekfar-Csrf` (any value). Safe methods do not. |

**On `X-Rekfar-Csrf`.** The session cookie is `SameSite=None` (the client and the API are on
different sites), so a browser would attach it to a cross-site form post as readily as to the
client's own request. A required header is what separates them: a header the page did not get
for free makes the request non-simple, so the browser asks for a CORS preflight first, and the
preflight is answered against the origin allowlist. The value is never checked — being able to
set it at all is the proof. A request without it is `403`.

`GET /health` is liveness only and sits outside `/v1`. It deliberately does **not**
touch the database: the free-offer database is serverless and auto-pauses, so a
DB-backed probe would either keep waking it or report unhealthy for the tens of
seconds a resume takes.

## `GET /v1/peaks`

The query the map issues on every pan and zoom (FR-MAP-2, FR-MAP-5). Anonymous.

### Parameters

| Name | Required | Description |
| --- | --- | --- |
| `bbox` | yes | The map extent, as `west,south,east,north` in WGS84 degrees. |
| `limit` | no | Maximum features to return. 1–1000, default 500. |
| `minElevationMeters` | no | Return only peaks known to reach this height. |

**On `bbox` ordering.** `west,south,east,north` is the OGC and GeoJSON ordering, and it
is exactly what Leaflet's `bounds.toBBoxString()` emits — the client passes its viewport
through without assembling anything. MapLibre's `getBounds().toArray()` needs flattening
first.

**Values are parsed as invariant decimals.** The first locale is `nb-NO`, whose decimal
separator is the comma that already separates the four values, so `8,5,61,0,9,5,62,0`
is rejected as eight values rather than quietly read as some other extent.

### A large extent is capped, not refused

At national zoom an extent can cover the whole catalogue. Rather than reject it, the
endpoint returns the `limit` highest peaks in view and sets `truncated`. Zoomed out you
get the significant peaks rather than an arbitrary slice, and the client can invite the
user to zoom in instead of drawing a partial picture as though it were the whole one.

### Response

`200 OK`, a GeoJSON `FeatureCollection` (RFC 7946). `attribution` and `truncated` are
foreign members, which the specification permits.

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "id": 10002,
      "geometry": { "type": "Point", "coordinates": [8.3126, 61.6363] },
      "properties": {
        "name": "Galdhøpiggen",
        "elevationMeters": 2469,
        "prominenceMeters": 2372,
        "utnoUrl": null
      }
    }
  ],
  "attribution": "© Kartverket",
  "truncated": false
}
```

GeoJSON rather than a flat array because both renderers consume it directly — Leaflet
through `L.geoJSON`, MapLibre as a `geojson` source — so nothing transforms the payload
before drawing it, and a later trails or cabins layer speaks the same format.

**`coordinates` is `[longitude, latitude]`.** That is GeoJSON's order, and the reverse of
both how a coordinate is spoken and how SQL Server's `geography::Point` takes one.

Notes on the properties:

- `elevationMeters` is `null` until ingestion has sampled a terrain model — SSR carries no
  height. Null means unknown, not sea level. Such peaks are still returned, and sort last.
- `prominenceMeters` and `utnoUrl` are likewise nullable; every view must render without
  them.
- **`attribution` is a licence condition, not a courtesy.** Kartverket's data is CC BY 4.0
  (NFR-LEGAL-2), so the credit travels with the payload rather than relying on each client
  to remember it. Render it wherever the data is shown.

Retired peaks are never returned. They stay in the table because somebody may have logged
them, but they are not part of the catalogue any more.

Responses carry `Cache-Control: public, max-age=…`. Reference data changes only when the
Kartverket ingestion job runs, so a map being panned should not re-ask the origin for an
extent it already has.

### Errors

`400` with a specific `detail` for a missing, malformed, out-of-range or inverted `bbox`,
and for a `limit` outside 1–1000. `429` when the rate limit is exceeded, with `Retry-After`.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Invalid bbox",
  "status": 400,
  "detail": "'bbox' west must be less than east.",
  "traceId": "00-a693bc57e2b12ee8acf10f783ec62d09-9c144833752ea685-00"
}
```

### Example

```bash
curl "http://localhost:5199/v1/peaks?bbox=7.5,61.3,8.8,61.8"
```

## Sign in

Registration and login are the same flow
([ADR-0017](https://github.com/rekfar/docs/blob/main/adr/0017-passwordless-email-sign-in.md)).
There is **no password anywhere in Rekfar** and no register endpoint: an address nobody has
used gets an account the first time somebody proves they can read it, and verifying the code
*is* the email confirmation.

### `POST /v1/auth/code`

Emails a one-time code. Anonymous.

```json
{ "email": "kari@example.no" }
```

`202 Accepted`, with an empty body, **whether or not the address has an account, and whether
or not a code was actually sent.** That is the contract, not an implementation detail: any
other answer would let an anonymous caller ask this endpoint which addresses are registered.
A request beyond the address's allowance is answered the same way and sends nothing.

`400` only for a malformed address, which is a fact about the request rather than about an
inbox. `429` when the *caller* has asked too often — a much smaller budget than the general
`/v1` limit, because this endpoint sends email on an anonymous caller's say-so (NFR-SEC-4).

### `POST /v1/auth/verify`

```json
{ "email": "kari@example.no", "code": "428913" }
```

`200 OK` with the profile — the same shape `GET /v1/me` returns — and a `Set-Cookie` carrying
the session. Returning the profile here saves a client that has just signed in a second round
trip to render itself.

`401` for a code that is wrong, expired, already used, or for an address with no account: one
message for all four, because distinguishing them would answer the question the code endpoint
refuses to. `429` once the address has used up its guesses.

**About the code.** Six digits, from ASP.NET Core Identity's TOTP-style email token provider.
It is derived from the user's security stamp and the clock, so **nothing is stored** — there is
no token table in the schema and none is needed. Three consequences are worth knowing:

- **It is valid for roughly ten minutes**, as a window around its issue rather than a countdown
  from it.
- **It is single-use.** The API remembers that a code has signed somebody in and refuses it
  afterwards. That memory is in the process, which is one more reason `maxReplicas` is 1.
- **A code re-requested within a few minutes is the same code**, because it is derived from the
  clock in coarse steps. A code that has already been used therefore stays used until the step
  rolls over — so signing in twice in quick succession may need a short wait. This is the cost
  of a token that survives the container restarting between the request and the reply, which on
  a scale-to-zero deployment it will.

### `POST /v1/auth/signout`

Ends this session, server-side and not only by clearing the cookie (FR-ACC-2). `204`.

### `POST /v1/auth/signout-all`

Ends every session on every device by rotating the security stamp. `204`.

With no password to change, this is the **only lever a user has over a device they no longer
hold**, which is why it ships in the MVP rather than later. Other devices stop working within
the security-stamp validation interval — **five minutes** — rather than instantly; that
interval *is* the window in which a revoked session still works. The calling device's own
cookie is cleared immediately.

Outstanding sign-in codes are derived from the same stamp, so they stop verifying too.

### The session

| | |
| --- | --- |
| Cookie | `__Host-rekfar.session` — `HttpOnly`, `Secure`, `SameSite=None`, path `/`, no domain |
| Lifetime | 90 days, **sliding**, renewed on use. No absolute cap in Phase 1 |
| Revocation | `signout` (this device) or `signout-all` (everywhere), within five minutes |

Long on purpose. With a password, an expired session costs the user a form they can fill from
memory; here it costs an email round-trip — latency, a spam folder, and a device that may not
have the inbox on it. The session is long and revocation is explicit rather than time-based
([the MVP plan §7](https://github.com/rekfar/docs/blob/main/architecture/user-accounts-mvp-plan.md)).

There is no device or session list in Phase 1.

## `GET /v1/me`

The signed-in user. Requires a session.

```json
{
  "email": "kari@example.no",
  "displayName": "Kari Nordmann",
  "locale": "nb-NO"
}
```

Three fields, because that is what an account is (P9, NFR-PRIV-2). `email` is the credential
and cannot be changed here. Default privacy is FR-ACC-4 and is not on the wire until trips can
act on it.

A new account's `displayName` is seeded from the address's local part — the column is NOT NULL
and nothing else about the person is known yet.

## `PATCH /v1/me`

```json
{ "displayName": "Kari Nordmann", "locale": "nn-NO" }
```

Both fields are optional; one that is absent or `null` is left as it is, which is what makes
this a PATCH. Returns the profile as it now stands.

- `displayName` is trimmed, and must be 1–80 characters afterwards.
- `locale` is a BCP-47 tag the server's ICU data actually knows, at most 16 characters. `nb-NO`
  is the first locale.

`400` names which field was wrong, and nothing is applied when either is — a rejected locale
cannot leave a new display name behind it.

## Not implemented yet

The resource list in the
[application architecture](https://github.com/rekfar/docs/blob/main/architecture/04-application-architecture.md)
names `/v1/trips`, `/v1/plans`, `/v1/wishlist`, `/v1/stats` and the rest. None of them exist
yet; `/v1/auth` and `/v1/me` above are the first that do.

One consequence is visible in this endpoint: sequence 4.1 of that document has the map
asking which peaks the user has bagged. That needs authentication and trips, so it is
deferred — `properties` gains a `bagged` member when they arrive, which is additive.
