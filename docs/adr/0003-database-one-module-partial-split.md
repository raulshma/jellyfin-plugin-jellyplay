# ADR-0003: The database is one module, split by partial class — not per-feature store interfaces

**Status:** Accepted · 2026-10-07

## Context

`JellyPlayDatabase` exposes ~49 methods across eight feature areas over one
SQLite file with one RW-lock. An architecture review considered splitting it
into per-feature store modules (SettingsStore, AnalyticsStore, …) behind
interfaces.

## Decision

The database stays ONE type: its methods are genuinely deep (each hides SQL,
locking, WAL and quota rules), it passes the deletion test (removing it would
scatter raw SQL across ~10 callers), and every method shares the same
connection/transaction core — interface-splitting would create N seams with
one adapter each (hypothetical seams). For file locality the implementation is
split by table group into partial class files:

- `Storage/JellyPlayDatabase.cs` — core: schema, migrations, connection,
  integrity, plumbing.
- `Storage/SettingsTables.cs` — settings, change log, sync history,
  footprints, admin defaults.
- `Storage/AnalyticsTables.cs` — playback sessions, rollups, maintenance.
- `Storage/RegistryTables.cs` — devices, messages, bookmarks, Seerr sessions,
  backup restore.

## Consequences

- A feature's storage change lands in its own file; merge conflicts shrink
  without a caller-visible change.
- Do not introduce `IJellyPlayDatabase` or per-store interfaces unless a
  second adapter actually appears; tests exercise the real SQLite file.
- Invariants that a schema index enforces live in the same file as the index —
  e.g. `InsertPlaybackSession` minute-buckets `StartedAt` itself; callers may
  pass any timestamp.
