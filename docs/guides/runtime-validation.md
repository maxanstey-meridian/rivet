# Runtime Validation

Rivet itself does not generate validators — validation, like all client-side codegen,
comes from the OpenAPI ecosystem.

Generate Zod schemas from the emitted spec with
[openapi-zod-client](https://github.com/astahmer/openapi-zod-client):

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated
npx openapi-zod-client ./generated/openapi.json -o ./src/api/zod-client.ts
```

The C#-side validation attributes (`[Range]`, `[StringLength]`, `[RegularExpression]`,
`[RivetConstraints]`, ...) flow into the spec as JSON Schema constraints, so the generated
Zod schemas enforce the same rules at the network boundary.

> Historical note: the v1 `--compile`/`--jsonschema` flags that emitted `schemas.ts` and
> `validators.ts` were removed in v2.
