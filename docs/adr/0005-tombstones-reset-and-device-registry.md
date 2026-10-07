# ADR-0005: Tombstones with an anti-resurrection invariant, reset-as-tombstone-batch, and a revoke-not-delete device registry

**Status:** Accepted · 2026-10-07

## Context

Schema v6 had no delete op. A key removed on one device simply stopped being
pushed; the row lived on server-side and on every peer, and the delta pull
(`changed?since=`) could only express "here are current values" — never "this
key is gone". `DELETE settings/{ns}` (reset) made it worse: it deleted the
settings rows AND the namespace's change-log rows, so peers that had already
consumed the log never learned the reset happened and kept their copies
forever. Devices were flat rows: `DELETE devices/{id}` removed the row, which
meant a discarded phone could re-register itself and come straight back with
its stale writes.

## Decision

**The change log records ops, not just values.** Schema v7 adds `Op`
(`'put'` | `'del'`, existing rows backfilled `'put'`). A tombstone write
removes the settings row and appends a `'del'` change-log row; the delta's
additive `deleted: [{ns, key}]` half carries it to peers. The wire form is
`"deleted": true` — the flag ONLY. A JSON-`null` `value` without the flag is
a stored value like any other (the pre-v7 bytes): the null tolerance first
shipped with v7 proved non-additive (a pre-v7 client writing a literal null
stored `"null"` bytes; under the tolerance the same write deleted the key)
and was removed.

**Puts must beat the key's latest change-log entry.** With no live row, the
LWW watermark is the key's most recent change-log entry — put OR del. This is
the anti-resurrection invariant: an in-flight put stamped before a delete
cannot re-create the row after the delete lands. A live row always implies it
is the newest entry, so the extra lookup only runs on the absent-row path.

**Reset is a tombstone batch.** `DELETE settings/{ns}` stops wiping the
change log; it deletes each row and appends its `'del'` entry atomically, then
publishes one anchored `settings.reset` event. Every peer's next delta sees
the deletions; `sync/history/{seq}/keys` lists exactly what the reset removed
(the batch's change-log range is the diff).

**Revocation, not deletion — caps-gated.** `DELETE devices/{deviceId}` flags
the row `revoked` instead of removing it, but only for devices that registered
`caps` (v7 clients — the app always sends at least `"silent-push"`). A revoked
device is excluded from every push fan-out, its re-registration answers `400
device-revoked`, and its settings writes reject per-write with the same
reason. The data half of revoke is a **wipe**: every settings row that device
wrote (all profiles) is tombstone-batched exactly like a reset, recorded as a
`wipe` history operation and announced by one anchored `settings.changed`
event — so the revoked device's keys are removed from its peers too, and its
own queued re-pushes cannot resurrect them (the invariant above). Revocation
is irreversible by design, for capped devices only: a discarded device gets a
new identity (new deviceId), not a pardon. A device that never registered
caps — a legacy pre-v7 client — keeps the OLD semantics on DELETE: a plain
unregister (row and push registration removed, re-register with the same
stable deviceId fine), because legacy clients call DELETE as their routine
push-detach on distributor loss; revoking there would permanently brick them
(and wipe settings they did not lose).

## Consequences

- Deletes, resets, restores (tombstone batch + server-stamped re-apply) and
  wipes all ride one mechanism; the delta, the SSE anchor and the history
  diff never need to know which one produced a `'del'` row.
- Deletes are cheap again storage-wise: tombstones are EXEMPT from the
  `ChangeLogRetentionDays` prune (only `'put'` rows age out) — a tombstone IS
  its key's anti-resurrection watermark, so pruning it would let a stale
  offline put resurrect a long-deleted key. Tombstone rows are tiny and kept
  forever; deletes therefore never expire from the delta either — a peer
  offline past retention still learns them (the previously accepted 30-day
  miss window is gone).
- `GET devices` lists revoked rows (`revoked: true`) forever — the registry
  is an audit surface, not just a directory; row counts only grow with
  genuinely new devices (capless legacy devices still disappear on DELETE).
- Old clients ignore `deleted[]`/`Op` entirely (additive wire): they re-fetch
  current values and see the post-delete world on their next full pull,
  they just don't learn deletions promptly. Nothing regresses.
- `DELETE settings/{ns}` (reset → tombstone batch) and `DELETE devices/{id}`
  (→ revoke + wipe) changed semantics without a `contractVersion` bump —
  deliberate exceptions, reasoned here: the old reset wiped the change log,
  so peers kept stale keys regardless (no client could regress), and the
  devices revoke is caps-gated to v7 clients (capless legacy devices keep
  the plain-unregister behavior).
