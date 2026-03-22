# Runtime Validation

Compile-time type safety catches shape mismatches at the network boundary. If the server sends unexpected data, you get a clear error instead of a silent `undefined` three components later.

## What `--compile` does

Rivet emits [Zod 4](https://zod.dev) validators for every response type and re-emits the client to call them at every fetch boundary.

- **Validation engine:** Zod 4 `fromJSONSchema()` at runtime
- **Requires:** `zod` in your consumer project
- **Output:** `schemas.ts` (JSON Schema definitions) + `validators.ts` (Zod wrappers)

The generated client imports `assertFoo()` functions from `validators.ts` and calls them on every response.

## Command

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --compile
```

## Output structure

```
generated/
├── types/...
├── client/...                 # imports from validators.js
├── rivet.ts
├── schemas.ts                 # standalone JSON Schema definitions
└── validators.ts              # Zod wrappers (directly usable, no compile step)
```

## What changes in the client

Without `--compile`, the client trusts the server response:

```typescript
// Without validation
const data = await res.json();
return data as TaskDto;
```

With `--compile`, every response passes through a runtime assertion:

```typescript
// With validation
const data = await res.json();
return assertTaskDto(data); // throws if shape doesn't match
```

## When validation fails

If the server sends data that doesn't match the expected type, the validator throws with a clear message describing which property failed and what type was expected. This surfaces immediately at the fetch boundary rather than propagating as `undefined` through your component tree.

## Standalone JSON Schema

If you want the schemas without wiring validation into the client:

```bash
dotnet rivet --project Api.csproj --output ./generated --jsonschema
```

This emits `schemas.ts` only — use it with `fromJSONSchema()`, ajv, or any JSON Schema consumer.

## When to use

- **Development:** Catch contract drift early when backend and frontend are developed in parallel
- **Integration testing:** Verify that real API responses match the expected types
- **Production:** Optional — adds a small runtime cost but catches unexpected API changes immediately
