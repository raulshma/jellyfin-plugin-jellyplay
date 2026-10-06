# ADR-0004: CONTRACT.md is executable — the route table is pinned by tests, not by a WebApplicationFactory

**Status:** Accepted · 2026-10-07

## Context

`docs/CONTRACT.md` is the plugin's deepest interface — the document Android
clients compile against — yet nothing verified it: route spellings, the
contract version, and feature keys could drift silently. A full
WebApplicationFactory harness (in-memory HTTP server hosting the controllers)
was considered to assert wire behavior end-to-end.

## Decision

Pin the contract's structural claims with `ContractTruthApiTests` instead of
standing up a host:

- **Bidirectional route mirror** — every controller route (reflection over
  route attributes) must appear in CONTRACT.md, and every documented
  `jellyplay/` route must be served. Parameter names and placeholders are
  normalized away; verbs and path shape are exact.
- **contractVersion and every feature key** must appear in the document.
- **The error-body gate** (`{ "error": "code" }`, camelCase) is pinned.

Behavioral coverage stays at the service level, through real seams (fake
transports, fake clocks, real SQLite) — the seam tests, not the HTTP
plumbing.

## Consequences

- Adding a route without documenting it (or deleting one the doc still lists)
  fails CI. The doc cannot rot.
- The harness does not prove auth policies, model binding, or serialization of
  a live request — if those become failure hot spots, revisit with a
  WebApplicationFactory; this ADR should be updated then, not silently
  bypassed.
