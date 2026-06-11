# Rivet.Tests/js

Vendored node tooling for the test suite. Run `npm install` in this directory once;
everything the tests shell out to is then local and offline-deterministic
(no `npx` downloads at test time):

- `zod` — schema runtime for `ZodValidatorEmitterTests` (`test-schemas.mjs`).
- `@stoplight/spectral-cli`, `openapi-typescript`, `typescript` — the OpenAPI
  conformance gate (`OpenApiConformanceTests.cs`): spectral lint (ruleset:
  `.spectral.yaml`, `spectral:oas`) and openapi-typescript → `tsc --strict`
  over every emitted spec.
- `openapi-fetch` — the Phase 2 parallel-run consumer
  (`SampleProjectOpenApiFetchTests.cs`): the hand-written `createClient<paths>`
  consumer type-checked and dual-run against the generated rivet.ts client.
