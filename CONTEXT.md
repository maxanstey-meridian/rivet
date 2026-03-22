# CONTEXT.md

### Importer Coverage (verified 2026-03-22)

All 6 real-world specs (Stripe, GitHub, Box, Twilio, PetStore, Httpbin) import with **0 compile errors** and
**100% endpoint coverage** (except Box 99.3% — 2 OPTIONS endpoints intentionally excluded).

Inline enums fully supported via `SynthesizeInlineEnum` — fingerprint-deduped, single-value discriminators → `string`
by design. `application/*+json` body content types (json-patch, merge-patch, strategic-merge-patch) now resolve.

### Remaining Gaps (feature work, not bugs)

- **Multiple security schemes** — Only picks the first scheme. Would need `string[]` on contracts.
- **anyOf/oneOf union shape** — Modelled as nullable variant fields (`AsX?`, `AsY?`). All fields can be null
  simultaneously. Not a priority unless used for serialization.
- **CBOR/YAML body content types** — `application/apply-patch+cbor` and `+yaml` are genuinely unsupported.
  Kubernetes has 26 such PATCH operations (resolved via `+json` fallback).
- **OPTIONS/HEAD methods** — Intentionally excluded (CORS preflight, not business logic).

### Known Issues / Polish

- No watch mode / MSBuild target for regeneration on build yet
- No `dotnet rivet init` scaffolding command
- Circular ref guard (`_visiting`) works but has no dedicated test coverage

---

## Future: Cross-Platform Validation DSL (parked)

Define validation rules once in C# (builder pattern → serializable JSON spec), run identical interpreters on both
C# and TS sides with field-level error messages. ~15-20 curated rule types (required, minLength, email, regex, etc.).
Separate packages: `Rivet.Validation` + `@rivet/validation`. Rivet ships first.
