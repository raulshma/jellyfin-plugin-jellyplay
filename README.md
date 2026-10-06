# jellyfin-plugin-jellyplay

Companion Jellyfin server plugin for the [JellyPlay](https://github.com/raulshma/JellyPlay) client.
One plugin, every JellyPlay-exclusive server feature:

| Module | What it gives JellyPlay |
|---|---|
| **Settings sync** | Per-user, per-device-profile client settings/profile sync (last-write-wins), live via SSE |
| **Admin defaults** | Tri-state (unset / suggested / forced) client defaults, push-to-users, config backup/restore |
| **Events & messages** | New-media events, admin broadcasts and a rich inbox message system over per-user SSE streams |
| **Seerr bridge** | Server-side Seerr SSO (password + Quick Connect), full catch-all API proxy, webhook auto-provisioning, calendar proxy |
| **Newsletter** | `POST /newsletter/send` + `/newsletter/test` backend (SMTP) the JellyPlay client already calls |
| **Ratings** | MDBList aggregates, TMDB episode/season ratings, IMDb charts — server-held keys, cached |
| **Custom + seasonal rows** | Letterboxd / IMDb / MDBList lists and seasonal keyword rows resolved against your library |
| **Anime markers** | Filler / recap / canon badges (AnimeFillerList + Tenrai), MAL↔AniList mapping (Fribb list) |
| **Recommendations** | Server-scored similar items (genres / tags / people / studios / franchise) |
| **User data** | One-response personal ratings + book-reader bookmark/notes sync |
| **Transcodes** | Admin transcode dashboard data + per-user download progress |

## Install

Add the plugin repository to your Jellyfin server (Dashboard → Plugins → Repositories):

```
https://raw.githubusercontent.com/raulshma/jellyfin-plugin-jellyplay/main/manifest.json
```

Then install **JellyPlay** from the catalog and restart.

## Requirements

- Jellyfin with a **.NET 10 runtime** (Jellyfin 12.x line). The plugin targets `net10.0` by design; older hosts are not supported.
- Client: JellyPlay (the plugin is consumed exclusively by it; nothing breaks for other clients).

## Client contract

- All routes live under `jellyplay/` (except the pre-existing client stubs at `newsletter/send`, `newsletter/test`).
- Auth: standard Jellyfin `Authorization: MediaBrowser Token="..."` (user access token, or API key for the Seerr webhook route).
- `GET jellyplay/capabilities` is the single bootstrap probe: `{ contractVersion, features[], pluginVersion }`. Clients must feature-gate on it and tolerate 404 (plugin absent).
- `GET jellyplay/settings/catalog` serves the known client settings the dashboard's defaults editor renders from (keys, types, enum options, defaults).

See `docs/CONTRACT.md` for the full API surface.

## Development

```
dotnet build
dotnet test
./build.sh   # produces dist/Jellyfin.Plugin.JellyPlay.zip + manifest entry
```

Deploy to a local server for testing: `./local.sh <path-to-jellyfin-plugin-dir>`.

The settings catalog (`src/Jellyfin.Plugin.JellyPlay/Resources/jellyplay-settings-catalog.json`)
is GENERATED from the JellyPlay client's preference declarations — never edit
it by hand. After a client release changes its settings, run the client
repo's `./gradlew :shared:core:datastore:generateSettingsCatalog` and commit
the refreshed artifact here (the client's `checkSettingsCatalog` task keeps
the two in lockstep).
