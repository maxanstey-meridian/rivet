# Rivet

tRPC-style end-to-end type safety for .NET → TypeScript. Roslyn reads your C# types and endpoints directly — no OpenAPI schema in the middle — and emits a typed TS client with optional runtime validation at the fetch boundary.

**What it is:**
1. A **type generator** — `[RivetType]` on a sealed record emits a TS `type`
2. A **client generator** — `[RivetEndpoint]` on a handler method emits a typed fetch function
3. A **validated client** (optional) — compile step wires Typia validators into the client so every response is runtime-checked at the boundary

Two attributes, one typed client. Flat functions, not HTTP ceremony.

**Why:** OpenAPI round-trip (C# → JSON schema → Kubb → TS) loses nullable semantics, mangles enums, can't express discriminated unions, and forces controller ceremony for metadata. Roslyn has the full type graph — sealed records, non-nullable defaults, `required` properties — and can emit strictly richer TS types than any schema intermediary.

**The pitch:** tRPC/oRPC give you end-to-end type safety when your server is TypeScript. Rivet gives you the same DX when your server is .NET.

| | tRPC | oRPC | Rivet |
|---|---|---|---|
| Server | TypeScript | TypeScript | .NET |
| Client | TS, shared types | TS, shared types + runtime validation | TS, Roslyn-extracted types + Typia validation |
| Schema layer | None (shared code) | None (shared code) | None (Roslyn reads C# directly) |
| Transport | HTTP/WS | HTTP/WS | HTTP (reads ASP.NET route attributes) |

**Accepted limitations:**
- Records only — no inheritance, no polymorphism
- No generic type parameters at MVP (add later)
- No `IFormFile` / multipart — manual escape hatch
- No runtime validation at MVP — types + client only (validators are Phase 4, optional)

**Type discovery strategy:**
- `[RivetType]` on Application-layer types (Commands, Results, DTOs) — explicit, self-documenting, and appropriate for that layer (declaring "this is part of the public API surface" is an Application concern)
- Domain types (enums, value objects) are discovered transitively — the type walker follows property references from attributed records and emits them automatically. No attribute needed on domain types.
- Domain stays clean. If `CreateMessageCommand` references `MessageVisibility`, the walker follows and emits the enum. No `[RivetType]` on the enum.
- Escape hatch: optional namespace-based convention scanning (`ScanNamespaces` config) for domain types that aren't transitively reachable. Unlikely to be needed in practice.

---

## What It Looks Like

### C# (input)

```csharp
[RivetType]
public sealed record CreateMessageCommand(Guid SubmissionId, string Body, MessageVisibility Visibility);

[RivetType]
public sealed record MessageDto(Guid Id, string Body, string AuthorName, DateTime CreatedAt);

// Domain enum — NO attribute. Discovered transitively via CreateMessageCommand.Visibility.
public enum MessageVisibility { Internal, Public }

// Endpoint handler — [RivetEndpoint] is a marker only, route is read from [HttpPost]
[RivetEndpoint]
[HttpPost("/api/submissions/{id}/messages")]
public static Task<MessageDto> CreateMessage(
    [FromRoute] Guid id,
    [FromBody] CreateMessageCommand body)
```

### TypeScript (output — after `dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet`)

```typescript
// generated/rivet/types.ts
export type CreateMessageCommand = {
  submissionId: string;
  body: string;
  visibility: "Internal" | "Public";
};

export type MessageDto = {
  id: string;
  body: string;
  authorName: string;
  createdAt: string;
};

// generated/rivet/client.ts — types-only, no runtime validation
export const createMessage = (
  id: string,
  body: CreateMessageCommand,
): Promise<MessageDto> =>
  rivetFetch("POST", `/api/submissions/${id}/messages`, { body });
```

### TypeScript (output — after `dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet --compile`)

```typescript
// generated/rivet/client.ts — same file, now with runtime validation wired in
import { assertMessageDto } from "./build/validators.js";

export const createMessage = async (
  id: string,
  body: CreateMessageCommand,
): Promise<MessageDto> => {
  const raw = await rivetFetch("POST", `/api/submissions/${id}/messages`, { body });
  return assertMessageDto(raw);
};
```

Same file, same imports for the consumer. The compile step upgrades the client in-place — every response is now runtime-validated at the fetch boundary. If the server sends unexpected data, you get a clear Typia assertion error, not a silent `undefined` three components later.

---

## Type Mapping

| C# | TypeScript |
|---|---|
| `string`, `Guid` | `string` |
| `int`, `long`, `decimal`, `double` | `number` |
| `bool` | `boolean` |
| `DateTime`, `DateTimeOffset`, `DateOnly` | `string` |
| `T?` (nullable value type) | `T \| null` |
| `string?` (nullable ref) | `string \| null` |
| `List<T>`, `T[]`, `IEnumerable<T>` | `T[]` |
| `Dictionary<string, T>` | `Record<string, T>` |
| `sealed record` with `[RivetType]` | `type { ... }` |
| `enum` with `JsonStringEnumConverter` | string union: `"A" \| "B" \| "C"` |
| Nested record reference | follow + emit referenced type |

---

## Architecture

Rivet is a two-stage pipeline. Stage 1 is a CLI tool that uses Roslyn workspace APIs to analyse the compilation and emit `.ts` files (no Node dependency). Stage 2 is an optional compile step that produces pre-built runtime validators.

**Important: Rivet is a CLI tool, not a source generator.** `IIncrementalGenerator` can only emit `.g.cs` files into the C# compilation — it cannot write arbitrary `.ts` files to disk. Rivet uses `MSBuildWorkspace` to open the project, analyse attributed symbols via the same Roslyn APIs (`INamedTypeSymbol`, `IPropertySymbol`, `NullableAnnotation`, etc.), and write `.ts` output to disk. This is the same model as `dotnet-ef` or `dotnet-format`.

```
Rivet.sln
├── Rivet.Attributes/      # NuGet: thin marker attributes, no dependencies
├── Rivet.Tool/             # NuGet: dotnet tool — Roslyn workspace analysis + TS emission
└── Rivet.Tests/            # Snapshot tests: sample records → expected .ts output
```

### Packaging — two NuGet packages, zero npm install

| Package | Type | What consumer does |
|---|---|---|
| `Rivet.Attributes` | NuGet library | `<PackageReference>` in `.csproj` — adds `[RivetType]`, `[RivetEndpoint]` |
| `rivet` | NuGet dotnet tool | `dotnet tool install --global rivet` or `dotnet tool install --local rivet` |

Consumer's `package.json` is never touched. For the compile step (`dotnet rivet --project <path.csproj> --output <dir> --compile`), the tool manages its own Node dependencies:
- Requires `node` on PATH (any frontend dev already has this)
- On first compile, bootstraps `typescript` + `typia` to a tool-local cache (`~/.rivet/`)
- Pins versions, reuses on subsequent runs
- Same model as `dotnet-ef` managing its own internal dependencies

### Stage 1 — Roslyn CLI (`dotnet rivet --project <path.csproj> --output <dir>`)

The CLI opens the target project via `MSBuildWorkspace`, finds attributed symbols, and emits:
1. **types.ts** — from `[RivetType]` records + transitively referenced types (enums, value objects)
2. **client.ts** — typed fetch functions from `[RivetEndpoint]` methods (types-only, no runtime validation)
3. **validators.ts** — `typia.createAssert<T>()` / `typia.createValidate<T>()` calls (source only — not usable until compiled)

Output goes to a configurable directory (CLI arg or MSBuild property, default `../ui/generated/rivet/`).

### Stage 2 — Typia compile (`dotnet rivet --project <path.csproj> --output <dir> --compile`, optional)

Compiles validators and re-emits `client.ts` with validation wired into every fetch function:

```
generated/rivet/
├── types.ts              # Stage 1 — pure types
├── client.ts             # Stage 1: types-only client → Stage 2: re-emitted with validator calls
├── validators.ts         # Stage 1 — typia source (import-only, not usable directly)
└── build/
    ├── validators.js     # Stage 2 — pre-compiled runtime validators
    ├── validators.d.ts   # Stage 2 — type declarations
    ├── types.js
    ├── types.d.ts
    └── tsconfig.json      # generated, targets this directory
```

**Key insight:** The compile step upgrades `client.ts` in-place. Before compile, the client returns unvalidated typed data (trust the server). After compile, every response passes through `assertFoo(raw)` at the fetch boundary. Same file, same imports for the consumer — the upgrade is transparent.

Consumer project references `Rivet.Attributes` (runtime only — thin marker attributes, no Roslyn dependency).

### What each command does

| Command | Requires | Output | Use when |
|---|---|---|---|
| `dotnet rivet --project <path.csproj> --output <dir>` | .NET SDK only | `types.ts`, `client.ts`, `validators.ts` (source) | Types + client. No Node needed. |
| `dotnet rivet --project <path.csproj> --output <dir> --compile` | Node + tsc + typia | `build/*.js` + `build/*.d.ts` | You want runtime validation. |

---

## Phases

### Phase 0 — Scaffold + Roslyn Hello World (2–3 days)

**What:** Working CLI tool that opens a project via `MSBuildWorkspace`, discovers `[RivetType]` records, and emits a dummy `.ts` file listing them. Proves the pipeline.

- [ ] Solution structure: `Rivet.Attributes` (net10.0), `Rivet.Tool` (net10.0, `dotnet tool`), `Rivet.Tests`
- [ ] `[RivetType]` and `[RivetEndpoint]` marker attributes (thin, no dependencies)
- [ ] CLI opens target `.csproj` via `MSBuildWorkspace`, gets `Compilation`, finds attributed symbols
- [ ] Emit a dummy `.ts` file listing discovered type names
- [ ] Test project: sample records in a test `.csproj`, run CLI, verify discovery output

**Complexity:** Medium. `MSBuildWorkspace` has quirks — NuGet package resolution, project loading failures, needing `Microsoft.Build.Locator`. The Roslyn symbol analysis itself (`INamedTypeSymbol`, `GetAttributes()`) is straightforward once you have the compilation.

**Key risk:** `MSBuildWorkspace` reliability across different SDK versions and project configurations. Mitigate by testing against CaseBridge's actual `.csproj` early.

---

### Phase 1 — Type Emitter (3–4 days)

**What:** Walk the type graph of `[RivetType]` records and emit valid TS type declarations.

- [ ] Recursive type walker: `INamedTypeSymbol` → extract properties → map to TS types
- [ ] Handle: primitives, nullables, collections, dictionaries, nested record references
- [ ] String enum detection: enum with `JsonStringEnumConverter` → TS string union
- [ ] Circular reference detection (records referencing each other)
- [ ] TS emitter: renders `export type Foo = { ... }` with camelCase property names
- [ ] Output to configurable directory via MSBuild property
- [ ] Snapshot tests: sample records → expected `.ts` output, byte-for-byte comparison

**Complexity:** Medium. The type walk is a straightforward recursive traversal (`INamedTypeSymbol` → `IPropertySymbol` → `ITypeSymbol`). The fiddly part is nullable context — Roslyn's `NullableAnnotation` on type symbols tells you if a reference type is `?`, but the compilation's nullable context must be enabled.

**Decisions:**
- C# `required` → TS required property. `init` without `required` + nullable → optional (`?:`).
- Single `types.ts` file at MVP. Split per module later.

---

### Phase 2 — Client Emitter (4–5 days)

**What:** Scan `[RivetEndpoint]` attributed methods, extract route/method/types, emit typed fetch functions.

- [ ] `[RivetEndpoint]` is a marker only — read HTTP method + route from ASP.NET attributes (`[HttpPost("...")]`, `[Route("...")]`)
- [ ] Extract parameter types from method signature: `[FromBody]`, `[FromQuery]`, `[FromRoute]`, route template params
- [ ] Infer return type from method return (`Task<T>` → unwrap `T`)
- [ ] Skip DI-injected params (use cases, `CancellationToken`, `HttpContext`, etc.) — don't emit to client
- [ ] Route template parsing: `{submissionId}` → function param `submissionId: string`
- [ ] Emit flat exported functions: `export const createMessage = (...) => rivetFetch(...)`
- [ ] Emit a `rivetFetch` base function with configurable base URL + headers callback
- [ ] Handle void returns (no response body)
- [ ] Snapshot tests: sample endpoints → expected `client.ts` output

**Complexity:** Medium-High. `[RivetEndpoint]` is a marker — the HTTP method and route are read from ASP.NET's own attributes (`[HttpPost]`, `[HttpGet]`, etc.), avoiding route duplication. The method signature carries parameter and return types. Main challenge: reliably distinguishing body/query/route params from DI-injected services in the parameter list.

**Param classification — no heuristics needed:**
- Has `[FromBody]` → request body
- Has `[FromQuery]` → query parameter
- Has `[FromRoute]` → route parameter
- No attribute → skip (DI service, `CancellationToken`, etc.)

Only attributed params are emitted to the client. This eliminates the hardest part of the original design — distinguishing API params from DI-injected services.

---

### Phase 3 — Generics, Polish, Packaging (3–4 days)

**What:** Handle generic types, improve DX, prepare for use outside CaseBridge.

- [ ] Generic record support: `PagedResult<T>` → `PagedResult<T>` in TS with type parameter
- [ ] `JsonNode` / `JsonElement` → `unknown` (explicit escape hatch)
- [ ] Watch mode: MSBuild target that regenerates on build
- [ ] Source map comments: `// Generated from CreateMessageCommand`
- [ ] Per-module output splitting (optional, configurable)
- [ ] README, NuGet package, dotnet tool CLI (`dotnet rivet --project <path.csproj> --output <dir>`)

**Complexity:** Medium-High. Generics need open vs. closed type parameter tracking in the type walker. Everything else is DX polish.

---

### Phase 4 — Validated Client (optional, additive)

**What:** Emit `validators.ts` with Typia assertion calls, compile them, and re-emit `client.ts` with validators wired into every fetch function. The client becomes runtime type-safe at the boundary.

- [ ] ValidatorGenerator: emit `validators.ts` with `typia.createAssert<T>()` for each return type used by `[RivetEndpoint]` methods
- [ ] `dotnet rivet --project <path.csproj> --output <dir> --compile`: bootstrap Node deps to `~/.rivet/`, invoke `tsc` with Typia transformer, output to `build/`
- [ ] Re-emit `client.ts` with validator imports + `assertFoo(raw)` calls wrapping every `rivetFetch` return
- [ ] Generated `tsconfig.json` in output directory targeting the `build/` folder
- [ ] Validators import types from sibling `types.ts` — single source of truth
- [ ] Snapshot tests: sample records → expected `validators.ts` source, plus compile round-trip test verifying the re-emitted `client.ts`

**Complexity:** Medium. The validator emitter is mechanical (one `createAssert<T>()` per return type). The compile step shells out to `tsc` with the Typia transformer config. The client re-emission is template-based — same code path as Phase 2, with an extra import + assert wrapper. Typia does the hard work.

**Key design decisions:**
- Rivet emits the Typia source calls, Typia's transformer does the AOT compilation — no runtime reflection
- `build/` output is `.js` + `.d.ts` — consumers import pre-compiled validators, never run the transformer
- The compile step re-emits `client.ts` in-place — consumer imports don't change, the client just gets upgraded
- Stage 2 is purely additive: Phases 0–3 ship without Node, `validators.ts` source is inert until compiled
- Node deps (`typescript`, `typia`) are managed by the tool in `~/.rivet/`, not in the consumer's `package.json`

---

## Timeline

| Phase | Days | Cumulative |
|---|---|---|
| Phase 0 — Scaffold | 2–3 | 2–3 |
| Phase 1 — Types | 3–4 | 5–7 |
| Phase 2 — Client | 4–5 | 9–12 |
| Phase 3 — Polish | 3–4 | 12–16 |
| Phase 4 — Validated Client | 2–3 | 14–19 |

**Usable on CaseBridge after Phase 2** — types + typed client, enough to replace Kubb entirely. No Node dependency for Phases 0–3.

Phase 1 alone has standalone value as a type-only replacement for Kubb's TS output.

Phase 4 is additive — wires runtime validation into the client, but Phases 0–3 work standalone without Node.

---

## Migration Path (CaseBridge)

1. Add `[RivetType]` to existing command/result/DTO records (mechanical, no logic change)
2. Run generator alongside Kubb — compare output, validate parity
3. Switch `ui/app/api/*-schemas.ts` imports from `~/generated/ts/` to `~/generated/rivet/`
4. Migrate one module's controllers → minimal API endpoints with `[RivetEndpoint]`
5. Validate generated client matches existing Kubb client usage
6. Roll out remaining modules
7. Remove Kubb, OpenAPI decorators, controller classes
8. Optionally keep OpenAPI generation (from Rivet metadata) for Scalar docs / external consumers

---

## Existing Tools Evaluated

### TypeGen (v7, 230 stars, active)
Convention-based C# → TS generator. One `[ExportTsInterface]` attribute per type, no per-property config. Built-in defaults: `Guid` → `string`, camelCase props, collections → `T[]`. `DateTime` → `Date` by default (overridable to `string` via global config).

**Why not:** Enums always emit as TS `enum`, never string union types. No way to get `"Draft" | "Active"` — only `enum Status { Draft = "Draft" }`. Fundamental mismatch with CaseBridge's `JsonStringEnumConverter` + string union expectation. Also: one file per type, no single `types.ts` output mode.

### NTypewriter (v0.5.9, 149 stars, pre-1.0)
Template-based generator using Scriban. Zero attributes needed — filter by convention (namespace, name suffix). Full Roslyn code model access. Can emit string unions, arbitrary output format, single-file output.

**Why not:** Pre-1.0, master branch stale since Jan 2021 (releases through Oct 2024). Bus factor concern. You're writing and maintaining Scriban templates — essentially authoring a mini codegen tool. At that point, the type walker is ~200-300 lines of direct Roslyn code; owning it is simpler than depending on a fragile intermediary.

### Verdict
The type-emission part where existing tools exist is also where they don't fit the opinions (string unions, single-file output, opinionated date mapping). The client-emission part (typed fetch functions from endpoints) has no existing solution outside OpenAPI intermediaries. Building Rivet's type walker directly (~200-300 lines) is less total complexity than adopting + working around either tool's constraints.

---

## Resolved Questions

- **Source generator vs CLI tool:** CLI tool using `MSBuildWorkspace`, not `IIncrementalGenerator`. Source generators can only emit `.g.cs` into the compilation, not `.ts` files to disk. Same Roslyn analysis APIs, different hosting model.
- **Route duplication:** `[RivetEndpoint]` is a marker attribute only. HTTP method and route are read from ASP.NET's own attributes (`[HttpPost("...")]`, `[Route("...")]`). Single source of truth for routing.
- **Attribute vs convention for type discovery:** Attributes on Application-layer types (Commands, Results, DTOs). Domain types (enums, value objects) discovered transitively by the type walker following property references. Domain stays clean, no codegen dependency.
- **Node dependency management:** Tool manages its own Node deps (`typescript`, `typia`) in `~/.rivet/`. Consumer's `package.json` is never touched. Only requirement: `node` on PATH.
- **Typia runtime:** Bundled into `build/` output by the compile step. Consumer never installs `typia`.
- **tsc version pinning:** Pinned by the tool in `~/.rivet/`. Consumer's TS version is irrelevant.
- **Naming:** "Rivet" for now. "xRPC" was considered (xUnit convention) but AT Protocol's XRPC (Bluesky) already exists. Revisit naming before public release.
- **Existing tools (TypeGen, NTypewriter):** Evaluated and rejected. TypeGen can't emit string union types (only TS `enum`). NTypewriter is pre-1.0 with stale maintenance. The type walker is ~200-300 lines — owning it is simpler than working around either tool's constraints. See "Existing Tools Evaluated" section.

---

## Open Questions

- **Module splitting:** One `types.ts` + `client.ts` at MVP. Split per module later if the files get large.
- **NuGet or local?** Start as a project reference in CaseBridge repo, extract to NuGet when stable.
- **`MSBuildWorkspace` SDK compatibility:** `MSBuildWorkspace` + `Microsoft.Build.Locator` can be fragile across SDK versions. Test against CaseBridge's actual `.csproj` early in Phase 0.
- **`rivetFetch` shape:** Should it accept a generic config object (base URL, headers, interceptors) or a raw `fetch` function? Leaning toward accepting a `fetch`-compatible function so it works with Nuxt's `$fetch`, raw `fetch`, or test mocks.
