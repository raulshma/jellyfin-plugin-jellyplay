# ADR-0006: One clock seam — TimeProvider everywhere, store prunes take injected cutoffs

**Status:** Accepted · 2026-10-09

## Context

Services took `TimeProvider?` (or, in Analytics, an older `Func<DateTimeOffset>`
idiom), but the timestamps that matter most leaked past the seam onto the
ambient wall clock: the change-log `historyTs` (the stamp that competes with
LWW watermarks), the admin-defaults push stamps, and the audit export. Two
idioms (`Func<DateTimeOffset>` vs `TimeProvider`) forced every new module to
choose, and every test file rolled its own fake. Retention prunes read
`DateTimeOffset.UtcNow` inside the store, so their tests could only pin the
cutoff indirectly (one assertion was vacuous; another faked a clock with
`retentionDays: 0`).

## Decision

- Every service that stamps time takes `TimeProvider?` and reads it via
  `GetUtcNow()` — `Func<DateTimeOffset>` is retired (Analytics converted).
- A sync op's `historyTs` is the SAME clock as its LWW ceiling: one stamp site
  per operation, always the injected clock.
- The store never reads the wall clock for retention: `PruneChangeLog`,
  `PruneSyncHistory` and `PruneSnapshots` take a unix-ms cutoff, mirroring the
  analytics prunes. The scheduled task (`ChangeLogPruneTask`) stamps the
  cutoffs from its own `TimeProvider`.
- Tests share ONE fake (`Jellyfin.Plugin.JellyPlay.Tests.FakeTimeProvider`);
  homemade per-file fakes are gone.

## Consequences

- A test driving `SettingsService` pins the history stamp, the LWW ceiling and
  the prune cutoff on the same clock — previously unobservable.
- Admin-pushed writes and the restore/import writes they compete with under
  LWW are stamped on the same clock.
- A new service has one idiom to copy, not a vocabulary decision.
