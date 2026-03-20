# Runtime Validation

Compile-time type safety catches shape mismatches at the network boundary. If the server sends unexpected data, you get a clear error instead of a silent `undefined` three components later.

## What `--compile` does

Rivet emits a `validators.ts` file containing [typia](https://typia.io) assertion calls for every response type. With `--compile`, Rivet runs `tsc` with the typia transformer to compile these into runtime assertion functions.

The generated client is re-emitted to call these validators at every fetch boundary — every API response is validated against its expected TypeScript type at runtime.

## Prerequisites

- Node.js on PATH (required for `tsc` + typia transformer)
- No additional npm packages needed — Rivet bundles the necessary typia setup

## Command

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --compile
```

## Output structure

```
generated/
├── types/...                  # same as without --compile
├── client/...                 # re-emitted with validator calls
├── rivet.ts                   # same
├── validators.ts              # typia source (assertion functions)
└── build/
    ├── validators.js          # compiled runtime assertions
    └── validators.d.ts        # type declarations
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

If the server sends data that doesn't match the expected type, typia throws a `TypeGuardError` with a clear message describing which property failed and what type was expected. This surfaces immediately at the fetch boundary rather than propagating as `undefined` through your component tree.

## When to use

- **Development:** Catch contract drift early when backend and frontend are developed in parallel
- **Integration testing:** Verify that real API responses match the expected types
- **Production:** Optional — adds a small runtime cost but catches unexpected API changes immediately
