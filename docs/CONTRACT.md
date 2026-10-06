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
→ 200 { contractVersion: 1, pluginVersion: "0.11.3", features: ["settings-sync", …], serverNow, deviceProfiles: ["", "desktop", "phone", "tv"] }
```

Feature keys: `settings-sync`, `device-profiles`, `admin-defaults`, `config-backup`,
`events`, `messages`, `seerr-bridge`, `newsletter`, `ratings`, `custom-rows`,
`seasonal-rows`, `anime-markers`, `recommendations`, `user-ratings`, `bookmarks`,
`transcodes`, `push`, `analytics`.
Keys are removed (not emptied) when the module is unconfigured (e.g. no Seerr URL/key).

## Settings sync (`settings-sync`, `device-profiles`)

Opaque per-user JSON blobs. The server never interprets values; clients own schema
versions and migrations. Profiles are device classes (`""` base, `desktop`, `phone`, `tv`).

```
GET    jellyplay/settings?profile=            → { head, profile, settings: [{ns, key, schemaVersion, updatedAt, deviceId, value}] }
GET    jellyplay/settings/changed?since=SEQ&profile=   → same shape (delta since change-log cursor)
POST   jellyplay/settings                     body { profile?, deviceId, writes: [{ns, key, schemaVersion, updatedAt, value}] }
                                              → { head, applied: [{ns, key, updatedAt, seq}], rejected: [{ns, key, reason}] }
DELETE jellyplay/settings/{ns}?profile=       → 204 (reset namespace)
GET    jellyplay/settings/resolved/{profile}? → merged: forced-defaults > profile overlay > base > suggested-defaults
                                              + additive `modes`: { "<ns>/<key>": "unset"|"suggested"|"forced" }
POST   jellyplay/settings/profile/{profile}   → batch into a device profile
GET    jellyplay/settings/stream              → SSE (events: settings.changed, settings.reset)
```

**Resolved `modes`** (additive field on the resolved response only, no bump):
the tri-state provenance per key for THIS user after the user-scope merge —
`forced` = the resolved value came from a forced default (clients should
force-lock it), `suggested` = the value was filled by a suggested default
(the user had not set the key), `unset` = the value is the user's own (base
or profile overlay), including a suggested default that lost to an existing
user value (user scope wins). Absent on plain `GET settings` / `changed`
snapshots; old clients ignore it.

**LWW rule**: a write applies iff `updatedAt` is strictly greater than the stored
one. Equal timestamps reject (`stale-write`) — deterministic, no oscillation.
Clients should stamp `updatedAt` from their own wall clock (ms).

**Quotas** (server-enforced, rejected reasons: `key-too-large`, `quota-exceeded`,
`key-limit-reached`, `stale-write`): 256 KB/key, 5 MB/user, 2000 keys/user by default.

**Sync protocol for clients**: on connect → `GET settings/resolved/{myProfile}`;
apply locally if newer than local copies; keep `head`. On local change → debounce
~3s → `POST settings` with changed keys; on partial success (`rejected`) re-pull
delta. Subscribe to `settings/stream`; on `settings.changed` → `GET settings/changed?since=head`.

**SSE streams** (both `settings/stream` and `events/stream`): after every ~15s of
quiet the server writes a comment frame `: keepalive` — EventSource ignores it,
but it resets idle proxies (nginx default 60s). Comment frames carry no `id`, so
they never disturb `Last-Event-ID` resumption.

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
recorded), `reset` on namespace deletes. `seq` is the history entry id; `ts` is
unix ms; `rejects` lists at most the first 10 rejections per operation and is
absent when nothing was rejected. History recording is best-effort server-side
and never affects the sync operation itself.

**Per-key diff** (additive): each history entry also carries `fromSeq`/`toSeq`
(absent on rows recorded before the field existed) — the change-log range the
operation covered: a push brackets the head before/after the batch, a pull the
requested `since` cursor through the served head, a reset a zero-width range
at the head after (the change log records no deletions). `GET
sync/history/{seq}/keys` returns that range's change-log rows —
`{ seq, op, keys: [{ns, key, updatedAt}] }`, newest-first, `limit` default
200 clamped 1..200 — for the caller's OWN row only (404 otherwise). Reset
rows (zero-width or missing range) return `keys: []`; clients render those as
"namespace reset" without a key list. Rows pruned from the change log simply
don't appear in the diff.

Retention: history entries older than `Sync:HistoryRetentionDays` (default 30)
are deleted by the daily prune task, which also reports the pruned row counts
in its log. `status.historyRetentionDays` surfaces the configured value.

## Events (`events`) — SSE only

```
POST   jellyplay/devices                      body { deviceId, name, platform, appVersion, push? } → 204
DELETE jellyplay/devices/{deviceId}           → 204/404
GET    jellyplay/devices                      → [rows]
GET    jellyplay/events/stream                → SSE
POST   jellyplay/broadcast   [admin]          body { title, body, url? } → 202
```

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
POST jellyplay/devices   body { deviceId?, name, platform, appVersion, push?: { kind: "generic"|"ntfy"|"fcm", endpoint } }
                         → 204 | 400 {error: "deviceId-required"} | 400 {error: "invalid-push-registration"}
                         | 400 {error: "push-kind-unavailable"} (kind "fcm" but FCM not configured)
DELETE jellyplay/devices/{deviceId}                    → 204/404 (unchanged; removes the registration with the row)
GET    jellyplay/devices                               → [{deviceId, userId, name, platform, appVersion, lastSeen, push?: {kind, endpoint}}]
```

- `push` present with a valid `kind` ("generic" | "ntfy" | "fcm") and a
  non-blank `endpoint` (the full publish URL; for `fcm` the FCM registration
  token) → validated and **overwritten**; re-POSTing the same `deviceId`
  re-registers idempotently (distributors may rotate endpoints). POSTing
  without a `push` block preserves any existing registration. Invalid kind or
  blank endpoint → 400 `invalid-push-registration`; kind `fcm` while FCM is
  unconfigured → 400 `push-kind-unavailable`.
- `GET jellyplay/devices` returns only the requesting user's own devices, and
  the `push` block (containing the secret endpoint URL) is included **only**
  there — never echoed for another user in any non-admin surface. The field is
  omitted when the device has no push registration.

**Dispatch audiences** (identical to the SSE events they mirror):

- `new-media` → the server's new-media audience: `admins` reaches only admin
  users' devices, `all` (default) reaches every user's devices.
- `broadcast` (admin broadcast, incl. Seerr-driven activity) → every user.
- `message` (on message **creation** only; edits do not re-push) → the
  message's audience (`all` → every user, `admins` → admins, `users` → the
  explicit user ids).

Not pushed: settings changes, session/playback/lockout events.

**Per-device payloads** (both `Content-Type: application/json`, `POST`):

- `generic` — body `{"title": …, "body": …, "kind": "new-media"|"broadcast"|"message", "itemId": …?}`
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
  (`data` values are always strings; `itemId` only when set). When FCM is
  unconfigured (or the token cannot be minted) `fcm` devices are skipped with
  a server-side Debug log.

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
GET jellyplay/mdblist/keyInfo  [admin]
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
GET jellyplay/animemarkers/items?seriesId=&from=&to=          → { markers }
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
POST jellyplay/admin/pushDefaults/{userId?} [admin] → { users, keysPushed }
GET  jellyplay/admin/configBackup [admin]          → JSON download (admin defaults + messages)
POST jellyplay/admin/configBackup [admin]          body backup JSON → restore
```

## Plugin config (dashboard + YAML editor)

Dashboard page: events, Seerr, ratings, newsletter sections, a tri-state
client-defaults editor (the `jellyplay/defaults` map + push/backup/restore
actions), and the YAML editor at `configurationpage?name=JellyPlayYaml`;
API: `GET/POST jellyplay/config/yaml` [admin]. The pages' `data-i18n` text
resolves through:

```
GET jellyplay/dashboard-strings?lang=  → { key: localizedString }   (en embedded; not feature-gated)
```

`POST jellyplay/defaults` stores the WHOLE map it receives (a removed key is a
key absent from the body — there is no per-key unset verb).

## Versioning & deprecation

- Additive endpoints/fields: no bump, old clients ignore.
- Renames/removals/semantic changes: bump `contractVersion`; keep the previous
  contract shippable for one minor plugin release when feasible.
- Feature keys are the stability boundary: never ship a behavior change under an
  existing feature key without a contract bump.
