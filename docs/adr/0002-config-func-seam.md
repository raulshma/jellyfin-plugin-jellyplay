# ADR-0002: Configuration crosses a Func seam; the plugin singleton stays at the composition root

**Status:** Accepted · 2026-10-07

## Context

`JellyPlayPlugin.Instance.Configuration` is a process-wide static. Services
that read it inline ( Ratings, Rows, Seerr, Newsletter, Anime, cache, tasks )
cannot be constructed in tests without the plugin host, which is exactly why
that half of the codebase had zero interface-level tests while Settings, Push
and Analytics — which already received `Func<TConfig>` — were fully tested.

## Decision

Services declare the config section they need as a constructor parameter
(`Func<SeerrConfig>`, `Func<RatingsConfig>`, …). `PluginServiceRegistrator`
(the composition root) is the only place that reads `JellyPlayPlugin.Instance`
besides `Plugin.cs` itself. Two documented exceptions are lifecycle, not
reads: the YAML editor and Seerr reprovision call
`UpdateConfiguration`/`SaveConfiguration`, which are plugin-instance
operations.

## Consequences

- New services must not read the singleton; the pattern to copy is any
  existing constructor in `Services/`. Tests construct services with
  `() => new SomeConfig { … }`.
- Configuration reads are late-bound (the Func re-reads on each call), which
  matches the dashboard's live-edit behavior — do not cache config values in
  fields.
