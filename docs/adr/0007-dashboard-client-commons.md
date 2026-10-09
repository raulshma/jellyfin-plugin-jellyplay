# ADR-0007: One dashboard client commons, kept C#/JS mirrors pinned by tripwire tests

**Status:** Accepted · 2026-10-09

## Context

The three dashboard pages (index, sync, yaml) each carried a private copy of
the client API surface — auth headers, error extraction, fetch wrappers, the
i18n pass — and the copies had already drifted (only index parsed
`problems[]`; yaml had no error extraction; three placeholder conventions
coexisted). Separately, the admin-defaults validation contract exists twice by
necessity — server `ValidateValue` and the dashboard's `validateDefaultRow`
mirror the same descriptor — and the pages' en fallback literals were not
pinned to the resx.

## Decision

- **One commons module** (`Pages/jellyplay-common.js`, served as
  `configurationpage?name=JellyPlayCommon.js`) owns auth headers, the wire
  error-body contract (`{ error, message, problems[] }`), the fetch wrappers
  (GET/POST/DELETE), `fmt`/`alertFmt`/`alertText` (ONE placeholder
  convention) and `applyStrings`. Pages add page logic only. A page that
  re-declares any of these fails `DashboardJsTripwireTests`.
- **The C#/JS validation mirror stays** — dying fast on an out-of-range
  default is worth a second implementation — but the string/i18n half of the
  mirror is pinned: every `fmt`/`data-i18n` key must exist in
  `DashboardStrings.resx`, and every page must load the commons before its
  page script.
- Full-contract error extraction is the commons'; the richer shape (an
  `Error` carrying `.problems`) is what every page now sees.

## Consequences

- A wire-error or auth change lands in one file; the `problems[]` class of
  drift cannot recur silently.
- A new dashboard page is thin by construction: load the commons, write page
  logic, add the tripwire-checked keys to the resx.
- The resx cannot drop or rename a key a page still asks for without a test
  failure.
