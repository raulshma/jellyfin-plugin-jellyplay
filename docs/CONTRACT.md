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
→ 200 { contractVersion: 1, pluginVersion: "1.0.0", features: ["settings-sync", …], serverNow, deviceProfiles: ["", "desktop", "phone", "tv"] }
```

Feature keys: `settings-sync`, `device-profiles`, `admin-defaults`, `config-backup`,
`events`, `messages`, `seerr-bridge`, `newsletter`, `ratings`, `custom-rows`,
`seasonal-rows`, `anime-markers`, `recommendations`, `user-ratings`, `bookmarks`, `transcodes`.
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
POST   jellyplay/settings/profile/{profile}   → batch into a device profile
GET    jellyplay/settings/stream              → SSE (events: settings.changed, settings.reset)
```

**LWW rule**: a write applies iff `updatedAt` is strictly greater than the stored
one. Equal timestamps reject (`stale-write`) — deterministic, no oscillation.
Clients should stamp `updatedAt` from their own wall clock (ms).

**Quotas** (server-enforced, rejected reasons: `key-too-large`, `quota-exceeded`,
`key-limit-reached`, `stale-write`): 256 KB/key, 5 MB/user, 2000 keys/user by default.

**Sync protocol for clients**: on connect → `GET settings/resolved/{myProfile}`;
apply locally if newer than local copies; keep `head`. On local change → debounce
~3s → `POST settings` with changed keys; on partial success (`rejected`) re-pull
delta. Subscribe to `settings/stream`; on `settings.changed` → `GET settings/changed?since=head`.

## Events (`events`) — SSE only

```
POST   jellyplay/devices                      body { deviceId, name, platform, appVersion } → 204
DELETE jellyplay/devices/{deviceId}           → 204/404
GET    jellyplay/devices                      → [rows]
GET    jellyplay/events/stream                → SSE
POST   jellyplay/broadcast   [admin]          body { title, body, url? } → 202
```

SSE event names + payloads (JSON):
- `new-media`: `{type, itemId, seriesId?, seasonIndex?, title, episodeCount, libraryId?, ts}` (episodes grouped per season window)
- `broadcast`: `{type, title, body, url?, ts}` (admin broadcasts and Seerr webhook activity)
- `session-started` / `playback-started` / `user-locked-out`: `{type, username, ts}`

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
GET    jellyplay/seerr/validate               → { valid }
DELETE jellyplay/seerr/logout                 → 204/404
ANY    jellyplay/seerr/{**path}               → proxy to {seerr}/api/v1/{path} with the user's stored session (401 {seerr-not-linked} when unlinked)
GET    jellyplay/seerr/radarr/calendar        → proxied calendar (non-admin friendly)
GET    jellyplay/seerr/sonarr/calendar
POST   jellyplay/seerr/webhook   [anon]       inbound Seerr webhook; requires header X-JellyPlay-Webhook-Secret
GET    jellyplay/seerr/webhookInfo [admin]
POST   jellyplay/seerr/reprovision [admin]    re-register the webhook into Seerr
```

The Seerr API key never leaves the server. Client direct-connection mode remains
the fallback when the capability is absent.

## Newsletter (`newsletter`) — root-level routes (pre-existing client stubs)

```
POST /newsletter/send  [admin]   → 204 | 400 (SMTP unconfigured) — weekly digest from last 7 days
POST /newsletter/test  [admin]   → 204
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
native cards for those, deep-link/ex otherwise.

## Anime markers (`anime-markers`)

```
GET jellyplay/animemarkers/series?seriesId=&providerSeriesId= → { seriesId, aniListId?, malId?, markers: [{type: filler|mixed|canon|recap, episodeNumber, note?}] }
GET jellyplay/animemarkers/items?seriesId=&from=&to=          → { markers }
```

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
