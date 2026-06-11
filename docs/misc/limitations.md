# Limitations

Verified limits of the current tool, so you don't discover them in production.

## Runtime

- Rivet performs **no runtime data validation**: response body shape is not
  checked (extra properties on returned objects reach the wire), constraint
  attributes (`[Range]`, ...) are not checked by Rivet — enforcement is the host
  framework's job ([recipes](/guides/runtime-validation#enforcing-constraints-at-runtime);
  `[RivetConstraints]` is a `ValidationAttribute` and participates) — and
  `Define.File` endpoints have no runtime enforcement at all. Full statement:
  [Runtime Validation](/guides/runtime-validation).
- Typed-result validation checks C# type assignability and declared status codes —
  it is not JSON Schema validation.
- Framework results without a status code (`ChallengeHttpResult`,
  `SignOutHttpResult`) cannot be validated; use `.SkipValidation()`.

## Generation

- Input must compile: generation aborts on compilation errors, and on type-name
  collisions between namespaces (component names are global).
- Enum values are emitted camelCased — the spec matches a camelCase
  `JsonStringEnumConverter`; if your API serializes enums differently (e.g. as
  integers), the spec will not match the wire.
- `TimeSpan` and `BigInteger` have no schema mapping — they emit an untyped (empty)
  schema with a diagnostic (`RIV1009`/`RIV1010`). Escape hatch: expose the value as a
  `string` property (ISO 8601 for `TimeSpan`, digits for `BigInteger`) or as a number
  when the range allows.
- Security scheme definitions come from the `--security` flag; `.Secure("name")`
  with no matching definition emits a default bearer scheme with a warning.
- The spec reflects *declared* C# types and the default System.Text.Json
  conventions. `[JsonPolymorphic]`/`[JsonDerivedType]` hierarchies emit as
  `oneOf` + `discriminator`; runtime polymorphism *without* those registrations,
  custom serializer settings, and validation living outside attributes
  (e.g. FluentValidation) are invisible to the spec.
- `info.title`, `info.version`, and `servers` come from the `--title`, `--version`,
  and `--server` flags (defaults: `"API"` / `"1.0.0"` / no `servers` block); there
  is no flag for `info.contact`, `info.description`, or `info.license`.

## Importer (`--from-openapi`)

A one-shot onboarding scaffold, not a sync tool. Callbacks, webhooks, links,
response-header schemas beyond `string` (header name/description/`required`
do import), parameter serialization styles, multi-scheme security, and
security scheme types are out of scope (discriminator dispatch imports as a
`[JsonPolymorphic]` hierarchy when the `oneOf` mapping is usable, and falls
back loudly otherwise) — the full
honest list, including every diagnostic marker and warning category, is in the
[Import Profile](/reference/import-profile).

## Formats

- OpenAPI 3.1 is the only public output. The contract JSON consumed by `--from` is
  an internal IR shared with the sibling runtimes.
- The v1 TypeScript/Zod generators (`--compile`, `--jsonschema`) were removed in
  v2; those flags exit with an error pointing at the OpenAPI ecosystem
  replacements.
