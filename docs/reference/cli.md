# CLI Reference

The docs are being rewritten, but these are the current core commands:

## Forward Generation

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated
```

## Runtime Validation

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --compile
dotnet rivet --project path/to/Api.csproj --output ./generated --jsonschema
```

## OpenAPI

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --openapi
dotnet rivet --project path/to/Api.csproj --output ./generated --openapi --security bearer
```

## Checks And Listing

```bash
dotnet rivet --project path/to/Api.csproj --check
dotnet rivet --project path/to/Api.csproj --routes
```

## Import (onboarding scaffold)

One-shot scaffold for adopting Rivet on an existing API — the generated C#
becomes the source of truth afterwards. See the
[Import Profile](/reference/import-profile) for what imports cleanly, every
diagnostic category, and what is out of scope.

```bash
dotnet rivet --from-openapi spec.json --namespace MyApp.Contracts --output ./src/
```

Omit `--output` to preview generated output to stdout.
