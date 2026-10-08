# ADR-0001: One explicit serialization gate instead of host MVC JSON options

**Status:** Accepted · 2026-10-07

## Context

Jellyfin hosts plugin controllers with PascalCase MVC JSON options. Plugin-side
`MvcOptions` configurators never reach the host's effective options pipeline,
so `Ok(dto)` serializes PascalCase. The wire contract (`docs/CONTRACT.md`)
specifies camelCase.

## Decision

Every JSON response body is produced by the plugin's own gate
(`JellyPlayResponses.Camel` / `JellyPlayResponses.Error`), a Newtonsoft
serializer with a camelCase contract resolver (dictionary keys preserved, so
resx-keyed dashboard strings round-trip). Actions return `IActionResult` —
never a typed `ActionResult<T>`, because the pipeline never emits that shape
and a lying signature is worse than an honest generic one.

## Consequences

- Controllers are verbose by one wrapper call; there is no silent path that
  leaks host serialization. Any raw `StatusCode(...)` with an object body
  bypasses the gate and is forbidden — error bodies go through
  `JellyPlayResponses.Error` (pinned by ContractTruthApiTests).
- Rate limiting and admin-error formatting are enforced as filters, so the
  gate is applied even where a controller forgets.
- If the host ever exposes per-plugin JSON options, this decision can be
  revisited wholesale: the gate is one file.
- **Amendment (2026-10-08):** `GET jellyplay/mdblist/keyInfo` intentionally
  proxies the upstream MDBList body verbatim (raw `Content`, documented in
  `docs/CONTRACT.md`) and is the one deliberate exception to "every body is
  produced by the gate" — a pre-existing passthrough, out of the gate's net
  on purpose. Should more passthrough routes appear, revisit this decision.
