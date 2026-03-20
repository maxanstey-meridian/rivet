# CONTEXT.md

## Current State

Rivet is feature-complete through Phase 4 + VO support + grouped type emission + typed error responses via overloaded unwrap. Published to NuGet as
`Rivet.Attributes` (0.1.0) and `dotnet-rivet` (0.2.0, 0.3.0 pending). Working against CaseBridge's real `.csproj` with
17 types and 5 endpoints.

OpenAPI import pipeline migrated from raw `System.Text.Json` (`JsonElement`) to `Microsoft.OpenApi` v2.7.0. The
library handles `$ref` resolution, schema parsing, and type representation via `IOpenApiSchema`/`OpenApiSchemaReference`.
Manual `ResolveRef()`, `CloneWithType()`, JSON fingerprinting, and `$ref` resolution for requestBodies all deleted — the
library handles these automatically.

OpenAPI importer produces 0 warnings across 10 real-world specs (Stripe, GitHub, Twilio, Cloudflare, Bitbucket, Box,
Jira, Discord, Kubernetes, Petstore v3). SchemaMapper handles: `$ref`, nullable type arrays, all primitive types, objects
(inline + named), arrays, dicts, allOf/oneOf/anyOf composition (with synthetic names for contextless inline schemas),
enums without type, `const` without type (infer from value kind), bare `nullable: true`, implied objects (properties
without type), and silent fallback to `JsonElement` for genuinely untyped schemas. ContractBuilder handles JSON,
form-encoded, and multipart request bodies, plus `default` error responses (mapped to 500).

### What's shipped

- Type walker: primitives, nullables, collections, dictionaries, enums → string unions, nested records (transitive),
  generics, JsonElement/JsonNode → unknown, VOs → branded types
- Client emitter: per-controller files, route combination, route constraint stripping, implicit route param binding,
  ProducesResponseType extraction, reserved word handling (delete → remove), RivetError,
  multi-response DU types via overloaded `function` declarations (unwrap: false returns typed result DU, default throws)
- Typia: validator emission, tsc + ts-patch compilation, validated client re-emission
- Type grouping: types split into per-namespace files (`types/`), cross-referenced types promoted to `common.ts`,
  barrel `index.ts` for both `types/` and `client/`. Namespace last segment used as file name, number-suffix on
  collision. Grouping signal is not yet configurable (future feature).
- Contract style: `[RivetContract] public static class` with `RouteDefinition<T>` fields, `.Invoke()` for type-safe
  runtime execution, `RivetResult<T>` for framework-agnostic returns
- File download endpoints: `.ProducesFile(contentType)` on contracts or `[ProducesFile]` attribute on fields (supports `byte[]` and `(byte[], string)` named file tuples), emits `Blob` in TS client, `format: binary` in OpenAPI
- Typed results: `Results<Ok<T>, NotFound>` in minimal API endpoints → extracts status codes and body types
- Cross-project type discovery: types from project references (not just the source assembly) are walked transitively
- Coverage checker: `--check` flag verifies contract implementations — detects missing `.Invoke()` call sites,
  HTTP method mismatches, and route mismatches. Supports both ASP.NET controllers and minimal API (`MapGet`/etc.)
- CLI: MSBuildWorkspace with multi-SDK Homebrew discovery, file output, stdout preview, --compile flag, --check flag
- `[RivetClient]` class-level attribute: auto-discovers all public methods with HTTP attributes, no per-method
  `[RivetEndpoint]` needed. Deduplicates with `[RivetEndpoint]` if both present.
- OpenAPI 3.1 emission: `--openapi` flag, monomorphised generics, $ref integrity, SecurityConfig support
- NuGet: Rivet.Attributes (netstandard2.0), dotnet-rivet (net8.0, PackAsTool)
- Samples: AnnotationApi (attribute-based discovery), ContractApi (contract-driven discovery)

### x-rivet-* Vendor Extensions (lossless round-trip)

Three categories of vendor extensions added to OpenAPI output for lossless C# → OpenAPI → C# round-trips:

1. **`x-rivet-brand`** — Brands emit as component schemas (`{ "type": "string", "x-rivet-brand": "Email" }`) instead of
   inlining. The `Brand` arm in `MapTsTypeToJsonSchema` now produces `$ref`. Importer reads the extension in
   `IsBrandedString()` and `MapBrand()`.
2. **`x-rivet-input-type`** / **`x-rivet-file`** — Multipart schemas carry `x-rivet-input-type` (record name) and
   `x-rivet-file: true` on file properties. `InputTypeName` added to `TsEndpointDefinition`, populated by
   `ContractWalker.BuildParams` from `tInput.Name`. Importer reads in `ContractBuilder.ResolveInputType`.
3. **`x-rivet-generic`** — Monomorphised schemas carry `{ name, typeParams, args }`. Importer pre-scans for these,
   builds one generic template record per unique name via reverse type substitution. `ResolveCSharpTypeCore` resolves
   `$ref` to monomorphised schemas as generic type strings (e.g. `PagedResult<TaskDto>`). `GeneratedRecord` gained
   optional `TypeParameters`; `CSharpWriter.WriteRecord` emits `<T>` suffix.

All extensions are backward-compatible — specs without `x-rivet-*` import identically to before.

### Importer Gaps (not bugs — feature work)

- **Inline enum properties → string** — Fields with `enum: ["active", "inactive"]` but defined inline (not as a named
  schema) map to `string`. No compile-time safety. ~1000 such fields in Stripe alone. Generating enums for every inline
  enum would produce huge file counts; possibly a `--inline-enums` flag.
- **Multiple security schemes** — `security: [{ "basicAuth": [] }, { "bearerAuth": [] }]` only picks the first scheme.
  Rivet contracts currently model security as a single string. Would need `string[]` or similar.
- **anyOf/oneOf union shape** — Modelled as a record with nullable variant fields (`AsX?`, `AsY?`). All fields can be
  null simultaneously, which is invalid. Not a priority unless someone tries to use these for serialization.

### Deep Review Bug Fixes (2026-03-20)

Batch fix of 21 confirmed bugs from Claude + Codex independent audits. Key changes:

- **OpenAPI**: Nullable query params emit `required: false`. Void endpoints without `.Status()` get `204` response.
  `InlineObject` handled in `MapTsTypeToJsonSchema`. `GetTypeNameSuffix` covers all TsType variants.
  Monomorphised generic names use `_` delimiter to prevent `PagedResultA`/`PagedResultAB` collision.
- **Client**: `rivetFetch` uses `!= null` instead of truthiness for body check (preserves `false`, `0`, `""`).
  Reserved word sanitization on parameter names. Unmatched route placeholder warnings at generation time.
- **TypeWalker**: `[JsonPropertyName]` and `[JsonIgnore]` attributes respected. Type name collision detection
  across namespaces. `HasErrors` flag for fail-fast.
- **ContractWalker**: Non-file properties on mixed file upload records emitted as `ParamSource.FormField`.
  Field modifier validation warning for non-static/non-readonly fields.
- **Zod**: `Dictionary`, `StringUnion`, `InlineObject`, `TypeParam` cases added to `BuildZodExpression`.
- **ValidatorEmitter**: Nullable types get distinct assert names (`assertFooNullable` vs `assertFoo`).
- **Program.cs**: Compilation errors now fail-fast (return exit code 1) instead of continuing with broken compilation.
- **EndpointBuilder**: `AcceptsFile()` added to all 4 route definition types. `Status()` throws on double-call.
- **TsType.CollectTypeRefs**: `StringUnion`, `Primitive`, `TypeParam` explicit cases for exhaustiveness.

Deferred: numeric `format` in OpenAPI (#19/#20) — requires model change to `TsType.Primitive`. Circular ref tests (#21) — existing `_visiting` guard works, just needs test coverage.

### Known Issues / Polish

- MSBuildWorkspace warning when Rivet.Attributes was a ProjectReference (non-fatal, resolved by switching to NuGet
  package)
- NU1701 warnings when tool targets net8.0 but resolves Microsoft.Build packages (cosmetic, doesn't affect output)
- Standalone .cs file mode lost the AspNetStubs.cs sample (Codex removed it during restructuring) — low priority,
  MSBuildWorkspace mode is the real workflow
- No watch mode / MSBuild target for regeneration on build yet
- No `dotnet rivet init` scaffolding command
- `RealWorldImportTests.GitHub_Api_Imports_And_Compiles` pre-existing failure (unrelated to deep review fixes)

---

## Future: Cross-Platform Validation DSL

### The Problem

Validation logic is duplicated across server and client. FluentValidation on the C# side, manual checks or Zod schemas
on the TS side. They drift. A field gets a new `MaxLength(200)` on the server, the frontend doesn't know, the user gets
a 422 with no useful message.

The ideal: define validation rules once, enforce them on both sides with identical behavior and good error messages. The
server is authoritative, but the client can give instant feedback before the round-trip.

### What Exists Today

| Tool                          | Platform | Authoring DX      | Cross-platform               | Error messages                 |
|-------------------------------|----------|-------------------|------------------------------|--------------------------------|
| JSON Schema + ajv/NJsonSchema | Both     | Verbose, ugly     | Yes (via schema)             | Generic, bad                   |
| Typebox                       | TS-first | Good in TS        | Sort of (JSON Schema export) | Generic                        |
| FluentValidation              | C# only  | Great             | No                           | Great                          |
| Zod                           | TS only  | Great             | No                           | Great                          |
| JsonLogic                     | Both     | OK for conditions | Yes                          | No — boolean only, no messages |

Nothing does "author in C# with good DX, run on both sides with field-level error messages."

### The Idea

A small, opinionated validation DSL. Not a general-purpose rule engine — a curated set of 15-20 rule types that cover
95% of form/DTO validation.

**Three components:**

1. **Rule spec** — just data, serializable to JSON, no code. A `sealed record` in C#, a `type` in TS. Rivet emits the
   types for free.

2. **C# interpreter** — reads the rules, validates at runtime. Replaces FluentValidation for cases where you want
   cross-platform validation. ~200-300 lines.

3. **TS interpreter** — same rules, same behavior, runs client-side. ~50-100 lines. Could be a composable (
   `useValidation(rules)`) or a plain function.

**Rule types (curated, not exhaustive):**

| Rule        | Param      | Example                                     |
|-------------|------------|---------------------------------------------|
| `required`  | —          | Field must be present and non-empty         |
| `minLength` | `number`   | `minLength: 8`                              |
| `maxLength` | `number`   | `maxLength: 200`                            |
| `min`       | `number`   | Numeric minimum                             |
| `max`       | `number`   | Numeric maximum                             |
| `email`     | —          | Email format                                |
| `url`       | —          | URL format                                  |
| `regex`     | `string`   | Custom pattern                              |
| `oneOf`     | `string[]` | Value must be one of the listed options     |
| `matches`   | `string`   | Must match another field (password confirm) |
| `uuid`      | —          | UUID/GUID format                            |

Each rule carries an optional `message` override. Default messages are built into both interpreters.

**Authoring in C# (builder pattern):**

```csharp
public static class UserValidation
{
    public static readonly FieldRules Email = Field.Rules()
        .Required("Email is required")
        .Email("Must be a valid email")
        .MaxLength(320);

    public static readonly FieldRules Password = Field.Rules()
        .Required()
        .MinLength(8, "Password must be at least 8 characters")
        .MaxLength(72)
        .Regex(@"[A-Z]", "Must contain an uppercase letter");
}
```

**Serialized form (what the API returns / what Rivet emits as a type):**

```json
[
  { "type": "required", "message": "Email is required" },
  { "type": "email", "message": "Must be a valid email" },
  { "type": "maxLength", "param": 320 }
]
```

**TS usage:**

```typescript
import { validate } from "@rivet/validation";
import type { FieldRule } from "~/generated/rivet/types";

const rules: FieldRule[] = field.validation; // from API response
const result = validate(value, rules);
// { valid: false, errors: ["Must be a valid email"] }
```

### How It Fits With Rivet

Rivet already emits DTOs that include validation specs as data (e.g. `PublicFormFieldDto` with a
`validation: FieldRule[]` property). The TS types are free. The missing piece is the dual interpreters.

This could be:

- A separate NuGet package (`Rivet.Validation`) + npm package (`@rivet/validation`)
- Or a standalone project that Rivet recommends but doesn't depend on

### Key Design Decisions (Not Yet Made)

- **Does the C# interpreter replace FluentValidation entirely, or wrap it?** FluentValidation has features this DSL
  intentionally doesn't (cross-field validation, async validators, database lookups). The DSL should complement it, not
  replace it.
- **How do validation rules get from the server to the client?** Embedded in the DTO? Separate endpoint? Baked into the
  generated types at build time?
- **Should the TS interpreter integrate with Nuxt UI form validation?** @nuxt/ui v4 has its own validation pattern. The
  interpreter should produce output that plugs in cleanly.
- **Does this need Rivet at all, or is it fully standalone?** The rule types are just records — any serialization works.
  Rivet makes the TS types automatic, but the validation engine is independent.

### Status

Parked. Rivet ships first. This is a separate project that builds on the same "shared types" philosophy but tackles
shared behavior, which is fundamentally harder.
