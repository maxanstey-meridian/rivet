# CLI Reference

## Forward Generation (OpenAPI)

OpenAPI 3.1 is the tool's output. `--output <dir>` writes `<dir>/openapi.json`;
omit it to preview the spec on stdout.

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated
dotnet rivet Contracts.cs Types.cs --output ./generated
```

### Explicit spec path

`--openapi <path>` overrides where the spec is written (relative paths resolve
against `--output`). When given, it is the sole writer.

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --openapi ../spec/openapi.json
```

### Security

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --security bearer
```

Accepted forms: `bearer`, `bearer:jwt`, `cookie:<name>`, `apikey:<in>:<name>`.

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
