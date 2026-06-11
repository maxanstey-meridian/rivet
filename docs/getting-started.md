# Getting Started

Rivet reads your C# types and endpoints with Roslyn and emits an OpenAPI 3.1 spec.

## Install

```bash
dotnet add package Rivet.Attributes --version "*"
dotnet tool install --global dotnet-rivet
```

## Generate

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated
```

This writes `./generated/openapi.json`. Omit `--output` to preview the spec on stdout.
Add `--title`, `--version`, and `--server` to set the spec's `info` and `servers`
metadata — see the [CLI Reference](/reference/cli).

## Consume the Spec

Generate TypeScript types and a typed client from the spec with the standard OpenAPI
tooling:

```bash
npx openapi-typescript ./generated/openapi.json -o ./src/api/schema.d.ts
npm install openapi-fetch
```

```ts
import createClient from "openapi-fetch";
import type { paths } from "./api/schema";

const api = createClient<paths>({ baseUrl: "https://api.example.com" });

const { data, error } = await api.GET("/api/tasks/{id}", {
  params: { path: { id: taskId } },
});
```

Want runtime validation? Generate Zod schemas from the same spec with
[openapi-zod-client](https://github.com/astahmer/openapi-zod-client).

## Next

- Follow the [Tutorial](/guides/tutorial)
- Check the [CLI Reference](/reference/cli)
- See [OpenAPI Emission](/guides/openapi-emission) for what flows into the spec
- Read [Runtime Validation](/guides/runtime-validation) for what Rivet does and
  doesn't enforce at runtime
