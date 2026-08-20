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
| Auth | None yet. The catalogue is readable anonymously by design (FR-PEAK-5); every user-data endpoint that follows will require authentication. |

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

## Not implemented yet

The resource list in the
[application architecture](https://github.com/rekfar/docs/blob/main/architecture/04-application-architecture.md)
names `/v1/auth`, `/v1/me`, `/v1/trips`, `/v1/plans`, `/v1/wishlist`, `/v1/stats` and the
rest. None of them exist yet.

One consequence is visible in this endpoint: sequence 4.1 of that document has the map
asking which peaks the user has bagged. That needs authentication and trips, so it is
deferred — `properties` gains a `bagged` member when they arrive, which is additive.
