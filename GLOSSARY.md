# JellyPlay domain glossary

The shared vocabulary for the plugin, its dashboard, and the client. When code,
docs, or conversation use one of these words, they mean exactly this.

## Contract

**Contract** — the wire protocol between the plugin and JellyPlay clients,
documented in `docs/CONTRACT.md`. Every route, response shape, and error body
is contract; anything not in the document is not. **contractVersion** bumps on
breaking changes; **feature keys** are the stability boundary — no behavior
change under an existing key without a bump.

**Capability probe** — `GET jellyplay/capabilities`, the single bootstrap
call. A 404 means the plugin is absent; the client hides gated features and
stays functional.

**Serialization gate** — `JellyPlayResponses.Camel` / `JellyPlayResponses.Error`,
the one module that produces every JSON body on the wire. No controller or
service writes an object body any other way; a result filter rewrites
stragglers and a contract test pins the exclusivity.

## Devices & push

**Device registry** — the per-user set of known devices (idempotent
registration, unregistration, owner-scoped listing). Registration carries an
optional **push registration**.

**Push directive** — the normalized intent of a registration's push block:
**attach** (validate + overwrite), **preserve** (block absent), **detach**
(explicit JSON null; registration cleared, device row kept). The wire's three
shapes map one-to-one onto these.

**Push kind** — how a device receives push: `generic` (raw JSON POST),
`ntfy` (publish URL + topic), or `fcm` (Firebase; needs configured
credentials). Endpoints are **secrets** — only ever echoed to their owner,
and only the host is surfaced to admins.

**Dispatch** — the fire-and-forget fan-out of a notification to the
push-registered devices of an audience. No delivery guarantee, no retry; SSE
is the reliable in-session channel.

## Settings sync

**Settings sync** — opaque per-user JSON blobs synced across a user's
devices. The server never interprets values; clients own schema versions.

**Namespace** — the first path segment of a setting key; the unit of reset.
**Profile** — a device class (`""` base, `desktop`, `phone`, `tv`) overlaying
the base.

**Last-write-wins (LWW)** — a write applies iff its `updatedAt` is strictly
greater than the stored one; equal timestamps reject as `stale-write`.

**Change log** — the per-user, monotonic record of applied writes. **Head**
is the user's current cursor; clients pull deltas with `since=head`.

**Admin defaults** — tri-state per-key defaults an admin pushes into user
accounts: **forced** (always overwrite) or **suggested** (fill gaps only).

## Events & messages

**Broadcast** — an admin-sent announcement to every connected client (SSE +
push). Seerr webhook activity is published as a broadcast too.

**Audience** — who receives an event or message: `all`, `admins`, or explicit
user ids.

**New-media pipeline** — the module owning everything between the host's
ItemAdded event and the emission: episode grouping, the library allow-list,
dedup, and the ONE emission gate (`ShouldEmit`, enabled + library + dedup
decided exactly once per emission). The watcher is a thin adapter (host
subscription + timer); EventService only shapes payloads and fans out.

**Client commons** — the dashboard pages' one client-side module
(`jellyplay-common.js`, served as `configurationpage?name=JellyPlayCommon.js`):
auth headers, the wire error-body contract, the fetch wrappers and the i18n
pass. Pages carry page logic only; a page that re-declares the plumbing fails
the JS tripwire tests.

## Seerr bridge

**Seerr bridge** — the plugin-side proxy to a Jellyseerr/Overseerr instance.
The plugin is the **auth boundary**: the Seerr API key never reaches the
client. A **session** is a user's Seerr cookies, encrypted at rest and never
exposed.

## Playback analytics

**Playback session** — one recorded finished playback, deduplicated to one
row per (user, item, **start-minute bucket**). `StartedAt` is the bucket;
consequently a session's reported duration may stretch by up to a minute.

**Rollup** — the derived per-day, per-user aggregate of playback sessions.
Rollups are retained forever; raw sessions are not. Disabling analytics is a
data-erasure switch.

## Hardening

**Abuse containment** — per-key sliding-window rate limits on expensive
mutating routes (settings batch, broadcast, anonymous webhook intake). Keyed
on user id for authenticated routes, on client identity for anonymous ones.
Error bodies always have the shape `{ "error": "code" }` in camelCase.

**Resilient fetch** — the file cache → circuit breaker → fetch → parse
pipeline for the TTL-cached external sources (TMDB, MDBList, IMDb charts,
Letterboxd, seasonal lists): one TTL policy, one spoofed browser user agent
for every scraped source (API sources use the plugin's own client), and
failure shaped as "no data" (null), never an exception leaking to a route.
The pipeline is single-flight: concurrent cold fetches for the same source
coalesce into one upstream hit, and a failed fetch clears its slot so the
next caller retries. The Fribb anime id index rides the same `FetchAsync`
seam (breaker + failure shaping in the fetcher) with its own staleness window
and index memoization above the pipeline — one breaker locality per source.
