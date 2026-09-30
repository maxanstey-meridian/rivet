# Rivet.Tests/js

Vendored node tooling for the test suite. Run `pnpm install` in this directory
once (`task install` at the root does it); everything the tests shell out to is
then local and offline-deterministic (no `npx` downloads at test time):

- `zod` and `test-schemas.mjs` — no test runs them; they are left over from the
  Zod emitter removed in v2.
- `@stoplight/spectral-cli`, `openapi-typescript`, `typescript` — the OpenAPI
  conformance gate (`OpenApiConformanceTests.cs`): spectral lint (ruleset:
  `.spectral.yaml`, `spectral:oas`) and openapi-typescript → `tsc --strict`
  over every emitted spec.
- `openapi-fetch` — the Phase 2 parallel-run consumer
  (`SampleProjectOpenApiFetchTests.cs`): the hand-written `createClient<paths>`
  consumer type-checked and dual-run against the generated rivet.ts client.
