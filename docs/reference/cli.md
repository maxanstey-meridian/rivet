# CLI Reference

## Forward Generation (OpenAPI)

OpenAPI 3.1 is the tool's output. `--output <dir>` writes `<dir>/openapi.json`;
omit it to preview the spec on stdout.

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated
dotnet rivet Contracts.cs Types.cs --output ./generated
dotnet rivet ./src/generated --output ./generated   # a directory = every .cs under it
```

### Explicit spec path

`--openapi <path>` overrides where the spec is written (relative paths resolve
against `--output`). When given, it is the sole writer.

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --openapi ../spec/openapi.json
```

### Spec metadata and security

`--title`, `--version`, and `--server` set the spec's `info.title`, `info.version`,
and `servers` entries; `--security` sets the default security scheme. All four are
emit-time CLI data — they describe the deployment, not the contracts, and are not
recovered by `--from-openapi` import.

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated \
  --title "Orders API" --version 2.3.0 \
  --server https://api.example.com --server https://staging.example.com \
  --security bearer
```

- `--title <text>` — `info.title` (default `API`).
- `--version <text>` — `info.version` (default `1.0.0`). Rivet has no
  print-tool-version flag, so `--version` always means the spec version.
- `--server <url>` — adds a `servers` entry; repeat for multiple. Accepts absolute
  `http(s)` URLs or paths starting with `/`. With no `--server`, no `servers`
  block is emitted at all.
- `--security <spec>` — accepted forms: `bearer`, `bearer:jwt`, `cookie:<name>`,
  `apikey:<in>:<name>`.

## From Contract JSON

`--from` consumes a Rivet contract JSON document (produced by the sibling runtimes,
rivet-ts and rivet-php) and emits the same OpenAPI spec. The contract JSON is an
internal intermediate representation, not a public format — OpenAPI is the only
public output:

```bash
dotnet rivet --from contract.json --output ./generated
```

## Checks And Listing

```bash
dotnet rivet --project path/to/Api.csproj --check
dotnet rivet --project path/to/Api.csproj --routes
```

`--check` verifies contract coverage (missing implementations, route/method
mismatches — see [Contract Coverage](/guides/contract-coverage)); without
`--output`, any warning exits with code `1`. `--routes` lists every discovered
endpoint (method, route, handler) and exits. `-q`/`--quiet` suppresses generation
output (useful with `--check` in CI).

### Drift gate (`--verify`)

If you commit the generated spec (and artifacts derived from it, like
`schema.d.ts`), nothing otherwise stops the C# and the committed spec drifting
apart when someone forgets to regenerate. `--verify` is the CI gate: it derives
the spec exactly as a normal run would, compares it against the existing file
(`--output <dir>/openapi.json`, or the `--openapi` override), and exits `1` on
any difference — without writing anything. Emission is deterministic, so a
checkout where someone regenerated and committed always passes.

```bash
# CI step — fails the build when the committed openapi.json is stale
dotnet rivet --project src/Api/Api.csproj --output ./generated --verify
```

Pass the same `--title`/`--version`/`--server`/`--security` flags you generate
with, or the comparison will (correctly) flag the metadata difference.

## Diagnostics

Every warning the tool writes to stderr carries a stable `RIV`-prefixed ID in
the canonical format `warning RIV1001: <message>` — grep or baseline by ID,
never by message text. See the [Diagnostics Reference](/reference/diagnostics)
for the full table of IDs, triggers, and remediations.

## Import (onboarding scaffold)

One-shot scaffold for adopting Rivet on an existing API — the generated C#
becomes the source of truth afterwards. See the
[Import Profile](/reference/import-profile) for what imports cleanly, every
diagnostic category, and what is out of scope.

```bash
dotnet rivet --from-openapi spec.json --namespace MyApp.Contracts --output ./src/
```

Omit `--output` to preview generated output to stdout.

## Removed in v2

`--compile` and `--jsonschema` (TypeScript/Zod generation) were removed in v2: TS/Zod
generation moved to the OpenAPI ecosystem. Generate types with
[openapi-typescript](https://github.com/openapi-ts/openapi-typescript), a client with
[openapi-fetch](https://github.com/openapi-ts/openapi-typescript/tree/main/packages/openapi-fetch),
and Zod schemas with [openapi-zod-client](https://github.com/astahmer/openapi-zod-client).
Invoking either flag exits with an error explaining the replacement.
