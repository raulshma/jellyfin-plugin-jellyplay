# JellyPlay plugin ↔ client contract

One document for the wire protocol. Versioning rule: every breaking change bumps
`contractVersion` in `GET jellyplay/capabilities`; clients refuse contracts they
don't understand. Additive changes never bump it.

**Auth**: standard Jellyfin bearer (`Authorization: MediaBrowser Token="…"`).
Admin routes additionally require the `RequiresElevation` policy.
**Route prefix**: `jellyplay/` (exceptions noted).
**Bootstrap**: clients call `GET jellyplay/capabilities`; 404 means plugin absent —
the app stays fully functional and merely hides gated features.

## Capabilities

```
GET jellyplay/capabilities
→ 200 { contractVersion: 1, pluginVersion: "0.11.3", features: ["settings-sync", …], serverNow, deviceProfiles: ["", "desktop", "phone", "tv"], serverSimilarPipeline: true|false }
```

`serverSimilarPipeline` (additive, default false) reports whether the plugin's
similar-items scorer registered into the HOST's pipeline — Jellyfin 12+ hosts
only (the registration is reflection-guarded and a no-op on 10.11). When true,
the stock `GET /Items/{id}/Similar` returns the same scored list the
`recommendations` route serves, and clients should render one similar row, not
two.

Feature keys: `settings-sync`, `device-profiles`, `admin-defaults`, `config-backup`,
`events`, `messages`, `seerr-bridge`, `newsletter`, `ratings`, `custom-rows`,
`seasonal-rows`, `anime-markers`, `recommendations`, `user-ratings`, `bookmarks`,
`transcodes`, `push`, `analytics`.
Keys are removed (not emptied) when the module is unconfigured (e.g. no Seerr URL/key).

## Settings sync (`settings-sync`, `device-profiles`)

Opaque per-user JSON blobs. The server never interprets values; clients own schema
versions and migrations. Profiles are device classes (`""` base, `desktop`, `phone`, `tv`).

```
GET    jellyplay/settings?profile=&cursor=&limit=   → { head, profile, settings: [{ns, key, schemaVersion, updatedAt, deviceId, value}], nextCursor? }
GET    jellyplay/settings/changed?since=SEQ&profile=&cursor=&limit=
                                                    → same shape + additive `deleted`: [{ns, key}]
POST   jellyplay/settings                           body { profile?, deviceId, writes: [{ns, key, schemaVersion, updatedAt, value, deleted?}] }
                                                    → { head, applied: [{ns, key, updatedAt, seq, deleted?}], rejected: [{ns, key, reason}] }
DELETE jellyplay/settings/{ns}?profile=             → 204 (reset namespace — tombstone batch, see below)
GET    jellyplay/settings/resolved/{profile}?       → merged: forced-defaults > profile overlay > base > suggested-defaults
                                                    + additive `modes`: { "<ns>/<key>": "unset"|"suggested"|"forced" }
POST   jellyplay/settings/profile/{profile}         → batch into a device profile (captures a restore point first)
GET    jellyplay/settings/stream                    → SSE (events: settings.changed, settings.reset)
GET    jellyplay/settings/catalog                   → { catalogSchema, settings: [{ns, key, label, description, group, valueType, defaultValue, min, max, options, optionLabels}] }
```

**Pagination** (additive, no bump): `GET settings` and `GET settings/changed`
accept `cursor` (opaque; echo `nextCursor` back verbatim) and `limit`. The
response carries `nextCursor` ONLY when more rows follow — absent means last
page. The default limit is deliberately far above any real store (10 000 rows,
clamped to 100 000), so current clients never see a second page. `deleted[]`
is never paginated (it lists whole keys). Old clients ignore all of it.

**Tombstones** (additive, no bump): a write carrying `"deleted": true` — the
flag is the canonical and ONLY tombstone form; a JSON-`null` `value` without
the flag is stored verbatim like any value (the pre-v7 behavior) — removes
the key instead of writing a value. The server appends a
`del` change-log row,
the deletion roams to every peer through the delta's additive `deleted:
[{ns, key}]` half, and stale pushes cannot resurrect the key (a put must beat
the key's latest change-log entry — put OR del — when no live row exists).
`applied[]` entries for deletes carry `deleted: true`. Reset (`DELETE
settings/{ns}`) is a tombstone batch now: the change log records one `del` row
per removed key (it is no longer wiped), so a reset reaches every peer's next
delta and `GET sync/history/{seq}/keys` lists exactly what the reset removed.
`del` rows are exempt from the change-log retention prune (only `put` rows
age out after `Sync:ChangeLogRetentionDays`, default 30): tombstones are tiny
and ARE the anti-resurrection watermark, so a stale offline put stays
rejected no matter how old the delete is.

**Clock-skew clamp**: a write whose `updatedAt` is more than 5 minutes ahead of
the server clock is rejected with reason `clock-skew` (per-write, in
`rejected[]`). Everything else stays pure client-clock LWW.

**LWW rule**: a write applies iff `updatedAt` is strictly greater than the
stored one (and than the key's latest tombstone when the row is absent). Equal
timestamps reject (`stale-write`) — deterministic, no oscillation.
Clients should stamp `updatedAt` from their own wall clock (ms).

**Quotas** (server-enforced, rejected reasons: `key-too-large`, `quota-exceeded`,
`key-limit-reached`, `stale-write`, `clock-skew`, `device-revoked`,
`ns-quota-exceeded`): 256 KB/key, 5 MB/user, 2000 keys/user by default, plus
per-namespace byte caps (`Sync:NamespaceQuotaBytes`, adjustable admin-side):
`prefs` 3 MB, `reader` 1.5 MB, `search`/`cw`/`homelayout` 64 KB. Namespaces
absent from the map are bounded only by the per-user total. A write crossing
its namespace cap rejects with `ns-quota-exceeded`. Writes from a revoked
device (see the device registry) reject with `device-revoked`.

**Catalog**: the settings catalog is GENERATED, never hand-maintained — the
client repo's `:shared:core:datastore:generateSettingsCatalog` Gradle task
walks the client's `PreferenceSpec` declarations (the same rows the stores
persist through) and writes the artifact embedded at
`src/Jellyfin.Plugin.JellyPlay/Resources/jellyplay-settings-catalog.json`.
Human-facing metadata is derived too: `label`/`description` resolve from the
client's settings-search resource strings (the same text the in-app settings
search shows), `group` carries the client's domain grouping, `optionLabels`
mirror the enum `options` with display names (the wire values stay the raw
constants), and `min`/`max` advertise audited numeric clamps (absent when the
client declares none). The client's `checkSettingsCatalog` task (wired into
`check`) fails when the committed artifact drifts from the declarations, so
keys, types, enum
vocabularies and defaults cannot silently diverge between client and plugin.
The catalog is advisory: unknown keys stay legal on the sync surface (forward
compatibility — a newer client against an older plugin), and secrets/identity
keys are structurally excluded by the generator's allowlist policy.
`catalogSchema` bumps only on breaking artifact-shape changes.

**Resolved `modes`** (additive field on the resolved response only, no bump):
the tri-state provenance per key for THIS user after the user-scope merge —
`forced` = the resolved value came from a forced default (clients should
force-lock it), `suggested` = the value was filled by a suggested default
(the user had not set the key), `unset` = the value is the user's own (base
or profile overlay), including a suggested default that lost to an existing
user value (user scope wins). Absent on plain `GET settings` / `changed`
snapshots; old clients ignore it.

**Sync protocol for clients**: on connect → `GET settings/resolved/{myProfile}`;
apply locally if newer than local copies; keep `head`. On local change → debounce
~3s → `POST settings` with changed keys (deletes ride the same batch with
`deleted: true`); on partial success (`rejected`) re-pull delta. Subscribe to
`settings/stream`; on `settings.changed` → `GET settings/changed?since=head` —
apply `settings[]` AND remove everything in `deleted[]` (a key can be absent
from `settings[]` yet tombstoned; ignoring `deleted[]` makes deletes resurrect).

**SSE streams** (both `settings/stream` and `events/stream`): after every ~15s of
quiet the server writes a comment frame `: keepalive` — EventSource ignores it,
but it resets idle proxies (nginx default 60s). Comment frames carry no `id`, so
they never disturb `Last-Event-ID` resumption.

**SSE ids & resume** (additive, no bump): every settings-stream event's `id:` is
the user's change-log head AT PUBLISH — a reconnecting client sends its last
`id` as `Last-Event-ID`, then resumes `GET settings/changed?since=<that id>`
(pulling both `settings[]` and `deleted[]`). Events-stream ids stay the hub's
monotonic sequence; `events/stream` additionally replays from a per-user
in-memory ring (last 256 events) when the request carries `Last-Event-ID` —
replayed frames arrive (in id order) before the live feed. The ring is
best-effort: events older than the window, or everything after a server
restart, is a ring MISS — the client detects nothing and must reconcile via
the inbox (fetch messages) after every reconnect anyway. Old clients that
never send `Last-Event-ID` see exactly the old behavior.

### Sync observability (additive, under `settings-sync`)

```
GET jellyplay/sync/status
    → { head, keys, bytes, quotaBytes, quotaKeys, historyRetentionDays,
        namespaces: [{ns, keys, bytes}], perDevice: [{deviceId, lastSyncAt, lastOp}] }
GET jellyplay/sync/history?since=<unix ms, optional>&limit=<default 50, max 200>
    → { entries: [{seq, ts, deviceId, op, keysApplied, keysRejected, rejects?: [{ns, key, reason}],
        fromSeq?, toSeq?}] }
GET jellyplay/sync/history/{seq}/keys?limit=<default 200, clamp 1..200>
    → { seq, op, keys: [{ns, key, updatedAt}] }  |  404 (not found / not owned)
GET jellyplay/admin/sync/overview [admin]
    → { users: [{userId, userName, keys, bytes, lastSyncAt, deviceCount}] }
GET jellyplay/admin/sync/user/{userId} [admin]
    → { userId, userName, status: <the sync/status shape above>,
        devices: [{deviceId, name, platform, appVersion, lastSeen, model?, caps?: […], revoked}] }
DELETE jellyplay/admin/sync/user/{userId}/devices/{deviceId} [admin] → 204 | 404 (user owns no such device)
GET jellyplay/admin/sync/export?userId=&format=csv|json&limit=<default 200, max 1000> [admin]
    → (json) { userId, exportedAt, history: [{seq, ts, deviceId, op, keysApplied, keysRejected,
               rejects?: [{ns, key, reason}], fromSeq?, toSeq?, keys: [{ns, key, updatedAt}]}] }
    |  (csv)  text/csv download — one row per per-key diff entry, header
              seq,ts,deviceId,op,keysApplied,keysRejected,fromSeq,toSeq,ns,key,keyUpdatedAt
              (operations without a usable diff range render as one row with empty key columns)
    |  400 {error: "unsupported-format"}
```

`status.head` is the user's current change-log seq (same cursor
`changed?since=` uses); `keys`/`bytes` are the user's live totals against the
configured quotas (256 KB/key, 5 MB/user, 2000 keys by default);
`namespaces` aggregates settings rows per namespace (all profiles folded);
`perDevice` folds the latest recorded operation per device.

`history` lists the server-recorded sync operations newest-first for the
caller's user: `push` on each accepted batch (with applied/rejected counts and
the approximate byte size of what applied), `pull` on each `changed?since=`
delta serve (`keysApplied` = returned count; full `GET settings` reads are NOT
recorded), `reset` on namespace deletes (tombstone batches), `wipe` on device
revocations (tombstone batch over the revoked device's rows). `seq` is the
history entry id; `ts` is unix ms; `rejects` lists at most the first 10
rejections per operation and is absent when nothing was rejected. History
recording is best-effort server-side and never affects the sync operation
itself.

**Per-key diff** (additive): each history entry also carries `fromSeq`/`toSeq`
(absent on rows recorded before the field existed) — the change-log range the
operation covered: a push brackets the head before/after the batch, a pull the
requested `since` cursor through the served head, and a reset/wipe the
tombstone batch it appended (non-zero-width since tombstones entered the
change log — its key list IS the list of removed keys). `GET
sync/history/{seq}/keys` returns that range's change-log rows —
`{ seq, op, keys: [{ns, key, updatedAt}] }`, newest-first, `limit` default
200 clamped 1..200 — for the caller's OWN row only (404 otherwise). Only
rows with a zero-width or missing range (pre-v7 resets, no-op operations)
return `keys: []`. Rows pruned from the change log simply don't appear in
the diff.

Retention: history entries older than `Sync:HistoryRetentionDays` (default 30)
are deleted by the daily prune task, which also reports the pruned row counts
in its log. `status.historyRetentionDays` surfaces the configured value.

### Admin drill-down, live monitor & preview (additive, elevation-gated)

Dashboard-facing glue over the same record the observability endpoints serve.

```
GET jellyplay/admin/users [admin]
    → { users: [{userId, userName}] }
GET jellyplay/admin/settings/preview?userId=&profile= [admin]
    → the resolved merge (the exact shape of GET settings/resolved/{profile},
      additive modes map included) for ANY user — pure read: nothing is
      written, no restore point and no history entry. The dashboard's
      authoring simulator (pick user + profile → merged view) rides this.
GET jellyplay/admin/stream [admin]
    → SSE (event: sync.op)
```

`sync.op` — one event per RECORDED sync operation (push / pull / reset /
wipe), broadcast to every elevated subscriber regardless of the user the
operation belongs to:

`{type: "sync.op", userId, op, deviceId, keysApplied, keysRejected, ts}`

Ids ride the hub's monotonic sequence (no per-user anchor and no replay
ring): the stream is a live view; `sync/history` + `admin/sync/export` are
the durable record. Payloads carry raw user ids — the surface is
elevation-gated. `DELETE jellyplay/admin/sync/user/{userId}/devices/{deviceId}`
is the drill-down's revoke action: identical semantics to the owner's
`DELETE jellyplay/devices/{id}` — including the caps gate (capped devices:
row flagged `revoked`, excluded from push, writes rejected, settings rows
tombstone-wiped with a recorded `wipe` operation; capless legacy devices:
plain unregister).

The dashboard page is `configurationpage?name=JellyPlaySync` (linked from the
main JellyPlay config page); its strings resolve through the same
`dashboard-strings` table.

### Restore points (additive, under `settings-sync`)

Rolling per-user snapshots of the WHOLE settings store (all profiles). One is
captured automatically before each admin defaults push (`admin-push` origin)
and before every cross-profile batch write (`profile-copy` origin); clients
can create their own (`manual`). Rolling keep-last 5 is enforced at insert;
age retention is `Sync:SnapshotRetentionDays` (default 30, daily prune).

```
GET  jellyplay/settings/snapshots                 → [{id, createdAt, origin, keys, bytes}]  (newest-first)
POST jellyplay/settings/snapshots                 → { id }  (manual capture)
POST jellyplay/settings/snapshots/{id}/restore    → { head, applied: […], rejected: […] }  |  404 (not owned)
```

Restore = a diff-first tombstone pass — only the keys present now but absent
from the snapshot are tombstoned (re-tombstoning keys the re-apply would
immediately recreate is pure change-log noise) — followed by the snapshot
re-applied, server-stamped one millisecond past the NEWEST stamp any
overlapping live row carries (a client clock may run up to the skew ceiling
into the future), so the restore provably wins LWW regardless of client
clocks. It rides the ordinary batch pipeline: change log, anchored
`settings.changed` SSE and history recording all happen. `rejected` is
normally empty; entries there mean the restored value crossed a quota as-is.

### Export / import (additive, under `settings-sync`)

```
GET  jellyplay/settings/export             → { exportedAt, pluginVersion, catalogSchema, catalogSettings,
                                             profiles: [{profile, settings: [{ns, key, schemaVersion, updatedAt, deviceId, value}]}],
                                             modes: { "<profile>": { "<ns>/<key>": "forced"|"suggested"|"unset" } } }
POST jellyplay/settings/import?deviceId=      body (the same bundle shape) → { head, applied: […], rejected: […] }
```

The export is the caller's own rows for every stored profile plus the
resolved tri-state modes maps (admin defaults appear only through `modes`,
never as rows) and the settings-catalog stamp. Import re-applies the bundle
for the caller through the ordinary batch pipeline with a SERVER-NOW
timestamp — it beats anything older than now (per LWW) but never clobbers a
legitimately newer local change pushed after the import.

## Events (`events`) — SSE only

```
POST   jellyplay/devices                      body { deviceId, name, platform, appVersion, model?, caps?, push? } → 204
POST   jellyplay/devices/{deviceId}           body { name?, model? } → 204/404   (rename; null fields keep their value)
DELETE jellyplay/devices/{deviceId}           → 204/404   (caps-gated: revoke + wipe for capped devices, plain unregister for capless legacy ones — see below)
GET    jellyplay/devices                      → [rows]
GET    jellyplay/events/stream                → SSE (sends `Last-Event-ID` for ring replay)
POST   jellyplay/broadcast   [admin]          body { title, body, url? } → 202
```

**Device registry v7** (additive): registrations may self-report `model`
(informational) and `caps` (a JSON array of capability strings, overwritten on
each registration). Device rows additionally carry `model`, `caps` (the parsed
array) and `revoked`. `DELETE jellyplay/devices/{deviceId}` is CAPS-GATED. A
device that registered caps (v7 clients — the app always sends at least
`"silent-push"`) is REVOKED, not row-removed: the row survives flagged
`revoked: true`, is excluded from every push fan-out, re-registering it is
rejected (`400 {error: "device-revoked"}`), and — crucially — every settings
row that device wrote is tombstone-wiped (one recorded `wipe` operation, one
anchored `settings.changed` event) so its keys cannot linger on other
devices; revocation is irreversible for these devices only. A device that
NEVER registered caps (a legacy pre-v7 client) gets the OLD semantics
instead — a plain unregister: the row (and its push registration) is removed,
no revoked flag, no wipe, and re-registering the same deviceId later
succeeds. Legacy clients call DELETE as their routine push-detach
(distributor loss) and re-register with the same stable deviceId — revoking
there would brick them. A `400 {error: "device-revoked"}` is also returned by
registration of a revoked id; settings writes carrying a revoked deviceId
reject per-write with reason `device-revoked`.

The optional `push` registration block is documented under [Push](#push-push--plugin-side-push-notifications).

SSE event names + payloads (JSON):
- `new-media`: `{type, itemId, seriesId?, seasonIndex?, title, episodeCount, libraryId?, ts}` (episodes grouped per season window). Delivery respects the server's new-media audience setting: `admins` reaches only admin SSE subscribers, `all` (default) reaches every subscriber.
- `broadcast`: `{type, title, body, url?, ts}` (admin broadcasts and Seerr webhook activity)
- `session-started` / `playback-started` / `user-locked-out`: `{type, username, ts}`

## Push (`push`) — plugin-side push notifications

The plugin is the push server: when `Push:Enabled` is set in the plugin
configuration, new-media / broadcast / message events are ALSO fanned out to
device-registered ntfy and generic UnifiedPush HTTP endpoints, and — when FCM
is configured — through Google FCM for devices registered with the `fcm` kind
(Play-Store client builds; UnifiedPush/ntfy remain the self-hosted default
and need no third-party service). Push is **fire-and-forget with no delivery
guarantee and no retry** (per-endpoint 10s budget, failures logged
server-side only) — SSE remains the reliable in-session channel. The `push`
feature key is only advertised when the module is enabled.

**FCM transport** (`fcm` kind) prerequisites: `Push:FcmProjectId` (the
Firebase project id) and `Push:FcmServiceAccountJson` (the RAW Firebase
service-account key JSON, pasted in full; the service account must hold a
Firebase Cloud Messaging admin role). The plugin authenticates to FCM with an
OAuth2 JWT-bearer grant on that key and caches the access token until five
minutes before expiry. The service-account JSON is a server-held secret: it
never appears in any log or API response — the admin overview reports only a
boolean `fcmConfigured`. FCM delivery additionally requires a client build
with the Firebase SDK; the device's endpoint is its FCM registration token.

**Registration** (additive on the device registry):

```
POST jellyplay/devices   body { deviceId?, name, platform, appVersion, model?, caps?: ["silent-push", …], push?: { kind: "generic"|"ntfy"|"fcm", endpoint } | null }
                         → 204 | 400 {error: "deviceId-required"} | 400 {error: "invalid-push-registration"}
                         | 400 {error: "push-kind-unavailable"} (kind "fcm" but FCM not configured)
                         | 400 {error: "device-revoked"} (registry v7: the device was revoked)
POST jellyplay/devices/{deviceId}   body { name?, model? } → 204/404   (rename; null fields keep their value)
DELETE jellyplay/devices/{deviceId}                    → 204/404 (caps-gated revoke + wipe / plain unregister — see the Events section)
GET    jellyplay/devices                               → [{deviceId, userId, name, platform, appVersion, lastSeen, model?, caps?: […], revoked, push?: {kind, endpoint}}]
```

- `push` present with a valid `kind` ("generic" | "ntfy" | "fcm") and a
  non-blank `endpoint` (the full publish URL; for `fcm` the FCM registration
  token) → validated and **overwritten**; re-POSTing the same `deviceId`
  re-registers idempotently (distributors may rotate endpoints). POSTing
  without a `push` block preserves any existing registration. An explicit
  JSON `null` (`"push": null`) **detaches**: the push registration is
  cleared server-side while the device row survives (the client's
  toggle-off / distributor-revoked path — unlike DELETE, which revokes
  capped v7 devices instead of removing the row; capless legacy devices are
  plain-unregistered). Invalid kind or
  blank endpoint → 400 `invalid-push-registration`; kind `fcm` while FCM is
  unconfigured → 400 `push-kind-unavailable`; a revoked deviceId → 400
  `device-revoked` (a revoked device cannot reinstate itself).
- `model` and `caps` (registry v7, additive): self-reported hardware model
  and capability strings, overwritten on each registration. `caps` is what
  gates the silent `sync-nudge` push (below). A device can update them any
  time by re-POSTing.
- `GET jellyplay/devices` returns only the requesting user's own devices, and
  the `push` block (containing the secret endpoint URL) is included **only**
  there — never echoed for another user in any non-admin surface. The field is
  omitted when the device has no push registration. Revoked devices remain
  listed with `revoked: true`.

**Dispatch audiences** (identical to the SSE events they mirror):

- `new-media` → the server's new-media audience: `admins` reaches only admin
  users' devices, `all` (default) reaches every user's devices.
- `broadcast` (admin broadcast, incl. Seerr-driven activity) → every user.
- `message` (on message **creation** only; edits do not re-push) → the
  message's audience (`all` → every user, `admins` → admins, `users` → the
  explicit user ids).
- `sync-nudge` (registry v7, silent) → delivered ONLY to devices whose
  registered `caps` include `"silent-push"`, and only for users with no live
  settings-stream SSE subscriber (the push is the "you're not connected"
  trigger to flush the sync queue). The cap gate protects old clients: a
  client that never advertised `silent-push` never receives `sync-nudge`, so
  unknown kinds can never surface as a visible notification.
  The SSE gate is a per-USER approximation of "not connected" (per-device
  gating needs device-tagged subscriptions, deferred): one live
  settings-stream subscriber — say the user's desktop — silences nudges to
  that user's OTHER, offline devices. `sync-nudge` is best-effort anyway; a
  missed nudge only delays the flush, which the SSE stream
  (`settings.changed` after reconnect), the periodic pull or the client's
  next focus-triggered flush still drains.

Not pushed as notifications: session/playback/lockout events.

**Per-device payloads** (both `Content-Type: application/json`, `POST`):

- `generic` — body `{"title": …, "body": …, "kind": "new-media"|"broadcast"|"message"|"sync-nudge", "itemId": …?}`
  (`itemId` present only when set).
- `ntfy` — endpoint is the full publish URL (e.g. `https://ntfy.sh/mytopic`);
  body is the ntfy JSON publish format with the topic extracted from the last
  URL path segment:
  `{"topic": …, "title": …, "message": …, "tags": ["jellyplay"], "priority": "default", "headers": {"X-JellyPlay-Kind": …, "X-JellyPlay-ItemId": …?}}`
  (machine fields ride ntfy ≥ 2.x's per-message `headers` map; `X-JellyPlay-ItemId`
  only when set).
- `fcm` — endpoint is the FCM registration token; the plugin POSTs
  `https://fcm.googleapis.com/v1/projects/{FcmProjectId}/messages:send` with
  `Authorization: Bearer <OAuth2 access token>` and body
  `{"message": {"token": <registration token>, "notification": {"title": …, "body": …}, "data": {"kind": …, "itemId": …?}, "android": {"priority": "NORMAL"}}}`
  (`data` values are always strings; `itemId` only when set; the silent
  `sync-nudge` kind ships DATA-ONLY — the `notification` block is omitted).
  When FCM is unconfigured (or the token cannot be minted) `fcm` devices are
  skipped with a server-side Debug log.

**Admin overview**:

```
GET jellyplay/admin/push/overview [admin]
    → { enabled, fcmConfigured, devices: [{deviceId, userName, deviceName, kind, endpointHost, registeredAt}] }
```

`endpointHost` is the endpoint's host only — never scheme, port or path (the
ntfy topic is a secret too; `fcm` rows surface an empty host — the endpoint
is a registration token, not a URL). `userName` resolves via the host's user
manager (raw id fallback); `registeredAt` is the device's first-registration
time (unix ms; pre-existing rows surface their last-seen time).

## Messages (`messages`)

```
GET    jellyplay/messages                     → { messages: [{id, title, body, color, linkUrl, linkLabel, startsAt?, endsAt?, order, createdAt, read}] }
POST   jellyplay/messages/{id}/read           → 204
GET    jellyplay/admin/messages    [admin]    → { messages: [...] } (raw rows)
POST   jellyplay/admin/messages    [admin]    body { id?, title, body, color, linkUrl?, linkLabel?, audience {type: all|admins|users, userIds[]}, startsAt?, endsAt?, orderIndex }
DELETE jellyplay/admin/messages/{id} [admin]  → 204/404
```

Audience `users` matches by Jellyfin user id (string). Window: message visible
while `now ∈ [startsAt, endsAt]` (nulls open-ended).

## Seerr bridge (`seerr-bridge`)

```
POST   jellyplay/seerr/login                  body { authType: password|quickconnect, username?, password?, quickConnectSecret? } → 200 | 401 {error}
GET    jellyplay/seerr/status                 → { configured, serverUrl?, linked, createdAt? }
GET    jellyplay/seerr/validate               → { valid } — a real validation: GET {seerr}/api/v1/auth/me is issued with the stored session cookie; any 2xx is valid, 401/403/transport failure/expiry is not (60s server-side cache per user)
DELETE jellyplay/seerr/logout                 → 204/404
ANY    jellyplay/seerr/{**path}               → proxy to {seerr}/api/v1/{path} with the user's stored session (401 {seerr-not-linked} when unlinked)
GET    jellyplay/seerr/radarr/calendar        → proxied calendar (non-admin friendly)
GET    jellyplay/seerr/sonarr/calendar
POST   jellyplay/seerr/webhook   [anon]       inbound Seerr webhook; requires header X-JellyPlay-Webhook-Secret → 200 | 400 (bad JSON) | 401 (bad/missing secret, constant-time compared) | 429 {error: "rate-limited"} (30/min per remote client; rate-limit identity is the remote IP, or the X-Forwarded-For first hop only when the server's TrustProxyHeaders flag is set)
GET    jellyplay/seerr/webhookInfo [admin]    → { autoProvision, secretConfigured, webhookPath, baseUrl, baseUrlSource: "config"|"request" } — baseUrl is the effective externally reachable Jellyfin address (configured value, else derived from the calling request; "request" derivation is informational until persisted via reprovision)
POST   jellyplay/seerr/reprovision [admin]    → { provisioned, baseUrl, baseUrlSource: "request" } | 502 — re-registers the webhook into Seerr using the base URL derived from the incoming request (reverse-proxy path base included) and persists it into config so startup auto-provision works afterwards
```

Startup auto-provision runs only when the base URL is configured; otherwise it
is skipped with a warning (no blind localhost attempts). The Seerr API key
never leaves the server. Client direct-connection mode remains
the fallback when the capability is absent.

## Newsletter (`newsletter`) — root-level routes (pre-existing client stubs)

```
POST /newsletter/send  [admin]   → 204 | 400 { error: "smtp-unconfigured" } — weekly digest from last 7 days
POST /newsletter/test  [admin]   → 204 | 400 { error: "smtp-unconfigured" }
```

## Ratings (`ratings`)

```
GET jellyplay/mdblist/ratings?imdbId=      → { imdbId, ratings: [{source, score?, votes?, url?}] } | 404
GET jellyplay/mdblist/keyInfo  [admin]     → served VERBATIM: this endpoint intentionally proxies the upstream MDBList body through as-is (raw `Content`, not the plugin's camelCase gate)
POST jellyplay/mdblist/clearCache?imdbId=  [admin] → 204
GET jellyplay/tmdb/seasonRatings?tmdbId=&seasonNumber=  → { season, episodes: {"<n>": {tmdbScore, tmdbVotes, entries[]}} }
GET jellyplay/tmdb/nextEpisode?tmdbId=     → { name?, airDate? }
GET jellyplay/imdb/charts                  → { chart: "top250", entries: [{rank, title, year?, imdbId?, rating?}], fetchedAt }
```

## Rows (`custom-rows`, `seasonal-rows`)

```
GET jellyplay/rows               → { rows: [{title, source, limit}] }  (admin-defined catalog)
GET jellyplay/rows/items?title=   → { title, source, items: [{title, year?, imdbId?, tmdbId?, localItemId?}] } | 404
GET jellyplay/seasonal/row?keyword= → RowResult (keyword optional; auto-picked by season)
```

`localItemId` set when the title matched a local library item — clients render
native cards for those, deep-link/ex otherwise. Row sources: `letterboxd`,
`imdb`, `mdblist`, `tmdb` (TMDB v3 `GET /list/{id}` using the ratings TMDB API
key). All upstream fetches are circuit-broken — an open breaker answers from
cache if present, otherwise 404.

## Anime markers (`anime-markers`)

```
GET jellyplay/animemarkers/series?seriesId=&providerSeriesId= → { seriesId, aniListId?, malId?, markers: [{type: filler|mixed|canon|recap, episodeNumber, note?}] }
GET jellyplay/animemarkers/items?seriesId=&from=&to=[&providerSeriesId=] → { markers }
```

Provider-id resolution: `providerSeriesId` (when given) is preferred; otherwise
the ids come from the series' Jellyfin `ProviderIds` (`anilist`, `mal`, `tvdb`,
`tmdb` — any of them) and are cross-mapped to `(anilistId, malId)` through the
Fribb anime list. AnimeFillerList markers are keyed on a title slug (the
explicit `providerSeriesId`, else the slugified series name); when the slug
yields nothing the result degrades to Tenrai-only markers. A series with no
markers at all resolves to 404 (negative results are cached briefly).

Admin-managed series overrides: `Anime:SeriesOverrides` in the plugin
configuration (dashboard "Anime overrides" editor or YAML; config-only, no
separate API) maps a Jellyfin series id to explicit `aniListId`/`malId` and
takes precedence over the auto-derivation above for that series — resolution
order is explicit override > provider-id auto-derive > name-slug fallback.

## Recommendations (`recommendations`)

```
GET jellyplay/items/{itemId}/similar?limit=12 → { items: [{itemId, name, score}] }
```

## User data (`user-ratings`, `bookmarks`)

```
GET   jellyplay/userratings/mine?filter=likes|dislikes|rated → { ratings: [{itemId, name, itemType, likes?, rating?, lastPlayedDate?, playCount}] }
GET   jellyplay/bookmarks/{itemId}     → { bookmarks: [{id, itemId, position, chapterIndex?, label, notes, createdAt, updatedAt}] }
POST  jellyplay/bookmarks/{itemId}     body { id?, position, chapterIndex?, label, notes } → BookmarkDto
DELETE jellyplay/bookmarks/{itemId}/{bookmarkId} → 204/404
```

**Deprecated** (retained one release, then removed at the next contract
bump): the dedicated bookmark routes above are superseded by the `books`
NAMESPACE on the general settings-sync protocol — clients sync bookmarks as
opaque `books/{itemId}/{positionTicks}` settings rows whose value carries
the full payload (including the EPUB CFI this route's fixed DTO drops).
The routes stay servable so pre-migration clients keep working; the
`bookmarks` feature key stays advertised for the same window.

## Transcodes (`transcodes`) — the active-streams monitor

Route ids keep the historic `transcodes` name; the data is every actively
playing session (direct play included — the host's `SessionInfo.TranscodingInfo`
is null for direct plays, so filtering on it would hide them) with the
re-encode detail when the server is transcoding.

```
GET    jellyplay/transcodes/active  [admin] → { transcodes: [{sessionId, userName?, deviceName?, itemName?, videoCodec?, audioCodec?, playMethod?, videoBitrate?, transcodeReasons?: [namedFlagBit...], positionTicks, isPaused}] }
DELETE jellyplay/transcodes/active/{sessionId} [admin] → 204/404
GET    jellyplay/transcodes/mine            → filtered to caller's username
```

`videoCodec`/`audioCodec` are null for direct-play axes (`IsVideoDirect` /
`IsAudioDirect`); `transcodeReasons` decomposes the host's `[Flags]`
`TranscodeReason` into its named bits (`"VideoCodecNotSupported"`, ...) and is
absent when not transcoding.

## Analytics (`analytics`) — playback activity recording & admin reporting

When `Analytics:Enabled` is set in the plugin configuration, finished playback
sessions are recorded server-side from the host's session pipeline (the same
session data the transcode monitor folds, captured at playback stop / graceful
close instead of live) and reported through an **admin-only** reporting surface
plus a per-user "Your watching" endpoint (the caller's own rows only). The
`analytics` feature key is only advertised while the module is enabled.

```
GET jellyplay/admin/analytics/overview?days=30 [admin]
    → { days, totals: {plays, playSeconds, transcodeSeconds, uniqueUsers, uniqueItems},
        perDay:   [{day, plays, playSeconds, transcodeSeconds}],
        perUser:  [{userId, userName, plays, playSeconds, transcodeSeconds}],
        topItems: [{itemId, itemName, itemType, plays, playSeconds}] }
GET jellyplay/admin/analytics/sessions?userId=&since=&limit= [admin]
    → { sessions: [{id, userId, itemId, itemName, itemType, seriesName?, playMethod,
                    videoCodec?, audioCodec?, bitrate?, transcodeReasons?: [namedFlagBit...],
                    positionTicks, durationTicks?, startedAt, endedAt, clientName?, deviceName?}] }
GET jellyplay/analytics/me?days=30            (any authenticated user, NOT elevation-gated)
    → { days, totals: {plays, playSeconds, transcodeSeconds, uniqueItems},
        perDay:   [{day, plays, playSeconds, transcodeSeconds}],
        topItems: [{itemId, itemName, itemType, plays, playSeconds}] }
```

- `analytics/me` is the admin overview's shape minus `perUser` (and
  `uniqueUsers`, which would always be the caller), scoped strictly to the
  caller's own rows — the established userratings/bookmarks caller-scoping
  pattern. `days` defaults to 30, clamped 1..365.

- `days` defaults to 30, clamped 1..365; the window is the last `days` UTC
  days. `perDay`/`perUser`/`totals` fold the daily per-user rollups
  (`transcodeSeconds`/`transcodeReasons` classify via `PlayMethod`).
- `topItems` (top 10 by plays) and `uniqueItems` come from raw session rows,
  so they degrade to empty/0 for days whose raw rows are already pruned —
  never an error. `userName` resolves via the host's user manager (raw id
  fallback, like every admin surface).
- `sessions` is the raw feed, newest-first; `since` (unix ms) filters on
  `endedAt`, `limit` defaults to 50 (max 200).

**Retention & disable semantics**: raw sessions are pruned after
`Analytics:RawRetentionDays` (default 90); the derived daily rollups are
retained forever. **Disabling analytics is a data-erasure switch**: the next
daily maintenance pass (`JellyPlay: analytics maintenance`) purges ALL
recorded analytics data (raw + rollups), so turning the module off wipes its
history. Rollup recomputes are idempotent and never re-derive a day from
pruned raw rows.

**Recording rules** (anti-noise, applied before anything is stored):

- Only sessions with an item id and a resolvable user are recorded.
- Skipped: wall time < 30 s or watch position < 60 s (scrub-away noise).
- Dedup: at most one row per (user, item, start-minute bucket); `startedAt`
  is the minute-bucketed start (unix ms), `endedAt` the real end.
- Sessions that never receive a stop event (client dropped, next item started,
  server restart) are closed gracefully: a same-user/device switch to another
  item ends the previous one immediately, and in-flight sessions with no
  progress for > 10 minutes are closed at their last-seen time on the next
  observed event. Unfinished in-flight state is memory-only — a server restart
  abandons it rather than fabricating rows.
- Recording is fire-and-forget: it never throws into the host event pipeline
  and never blocks playback. Not recorded: failed starts, live TV channel
  flips below the noise floors, non-library items (no id).

## Admin defaults & backup (`admin-defaults`, `config-backup`)

```
GET  jellyplay/defaults [admin]                    → { "<ns>/<key>": {mode: unset|suggested|forced, value?} }
POST jellyplay/defaults [admin]                    body same shape → 204
POST jellyplay/admin/pushDefaults/{userId?} [admin] → { users, keysPushed, dryRun? }
GET  jellyplay/admin/configBackup [admin]          → JSON download (admin defaults + messages)
POST jellyplay/admin/configBackup [admin]          body backup JSON → restore
```

**Push dry-run** (additive, no bump): `POST
jellyplay/admin/pushDefaults/{userId?}?dryRun=true` simulates the push and
writes NOTHING — no rows, no restore points, no history. `keysPushed` is 0
and the response carries the additive `dryRun` report:
`{wouldApply, wouldReject, wouldRejects: [{userId, ns, key, reason}] (capped
at the first 100), problems: [catalogValidationProblem…]}`. `wouldRejects`
reason is always `stale-write` (the same LWW rule the batch pipeline
applies); `problems` lists the catalog-validation findings in the STORED
defaults maps (global + per-target user scopes).

## Plugin config (dashboard + YAML editor)

Dashboard page: events, Seerr, ratings, newsletter sections, a tri-state
client-defaults editor (the `jellyplay/defaults` map + push/backup/restore
actions), and the YAML editor at `configurationpage?name=JellyPlayYaml`;
API: `GET/POST jellyplay/config/yaml` [admin]. The pages' `data-i18n` text
resolves through:

```
GET  jellyplay/config/yaml [admin]                 → { value: "<yaml of the full plugin configuration>" }
POST jellyplay/config/yaml [admin]                 body { value } → { error, message } — round-trips the YAML through UpdateConfiguration (webhook secret auto-generated when blank)
GET jellyplay/dashboard-strings?lang=  → { key: localizedString }   (en embedded; not feature-gated)
```

`POST jellyplay/defaults` stores the WHOLE map it receives (a removed key is a
key absent from the body — there is no per-key unset verb).

## Versioning & deprecation

- Additive endpoints/fields: no bump, old clients ignore. The schema-v7 wave
  (tombstones, pagination, registry, snapshots, export/import, quotas,
  silent push, admin observability) is entirely additive: `deleted[]`,
  `nextCursor`, `modes`, `Op`, device `caps`/`revoked` and the new routes
  are ignored by clients that predate them, and the defaults (limit 10 000,
  un-paged reads, no `Last-Event-ID` header) preserve the pre-v7 wire
  byte-for-byte.
- Silent push is gated by caps, not versions: `sync-nudge` is delivered only
  to devices whose registered `caps` include `"silent-push"`, so old
  clients — which render unknown push kinds as visible notifications — can
  never receive it, whatever the plugin version.
- Renames/removals/semantic changes: bump `contractVersion`; keep the previous
  contract shippable for one minor plugin release when feasible. The
  dedicated bookmark routes are the current example: deprecated in favor of the
  `books` namespace, retained one release (see User data).
- Deliberate exceptions shipped under `contractVersion` 1 (reasoned in
  ADR-0005): `DELETE settings/{ns}` (reset became a tombstone batch) and
  `DELETE devices/{id}` (became revoke + wipe) changed semantics without a
  bump. The old reset wiped the change log, so peers kept their stale keys
  regardless — no client could regress; the devices revoke is caps-gated to
  v7 clients, and capless legacy devices keep the plain-unregister behavior.
- Feature keys are the stability boundary: never ship a behavior change under an
  existing feature key without a contract bump.
