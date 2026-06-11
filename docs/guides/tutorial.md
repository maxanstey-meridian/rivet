# Tutorial

This guide is being rewritten.

For now:

1. Install `Rivet.Attributes` and `dotnet-rivet`.
2. Point Rivet at your `.csproj`.
3. Run `dotnet rivet --project path/to/Api.csproj --output ./generated` — this writes `openapi.json`.
4. Generate TypeScript from the spec: `npx openapi-typescript ./generated/openapi.json -o schema.d.ts`, then consume it with `openapi-fetch`.

Use [Getting Started](/getting-started) for the short path and [CLI Reference](/reference/cli) for exact commands.
