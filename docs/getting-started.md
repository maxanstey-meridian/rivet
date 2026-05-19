# Getting Started

Rivet reads your C# types and endpoints and generates TypeScript from them.

## Install

```bash
dotnet add package Rivet.Attributes --version "*"
dotnet tool install --global dotnet-rivet
```

## Generate

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated
```

By default this gives you:

- `types/` for generated TypeScript types
- `client/` for generated client modules
- `rivet.ts` for runtime configuration and fetch helpers

## Optional Runtime Validation

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --compile
```

This also emits:

- `schemas.ts`
- `validators.ts`

## Next

- Follow the [Tutorial](/guides/tutorial)
- Check the [CLI Reference](/reference/cli)
- See the docs site as a work in progress while the rewrite lands
