# Fable Review — 2026-06-10

Deep review of the Rivet codebase (plus the `rivet-php` sidecar repo) conducted by five
parallel reviewers, one per subsystem, with the highest-severity claims independently
re-verified. Several import-pipeline findings were confirmed by running temporary xunit
repros against the real importer; the form-encoded client bug was confirmed with `tsc
--strict` and a node check.

**Overall verdict:** the core is sound — the `TsType` IR design holds up, output is
deterministic, nullable-vs-optional is genuinely modeled, string escaping goes through
Roslyn's `SymbolDisplay`, and import-side name sanitization/dedup is solid. The problems
concentrate in three patterns:

1. **Edge cases daily usage doesn't exercise** (route template syntax, inheritance,
   OpenAPI composition idioms).
2. **Silent skips instead of diagnostics** — many `return null`/`continue` paths drop
   endpoints or types from output with no warning.
3. **Gaps between subsystems** — contract vs runtime, emitter vs `rivet.ts` template,
   forward vs import type mapping, C# repo vs PHP repo.

Severity key: **HIGH** = wrong/broken output or runtime exception on realistic input;
**MED** = silent fidelity loss or trap; **LOW** = edge case, hygiene, or polish.

---

## 1. Rivet — Analysis pipeline (`Rivet.Tool/Analysis/`)

### HIGH

**A1. DELETE default status: contract says 204, runtime returns 200 — and the validator throws on the "correct" status.**
`ContractWalker.DefaultSuccessCode` (ContractWalker.cs:392-393) maps `"DELETE" => 204`
(locked in by `ContractEndpointTests` "GAP-4"), but `Define.Delete` (Endpoint.cs:32-34)
constructs `RouteDefinition("DELETE", route)` with default status 200 — only `Post`
passes 201. Consequences: (a) `Invoke` returns 200 while the generated TS client/OpenAPI
declare 204 as the success branch; (b) worse, a typed-results handler returning
`TypedResults.NoContent()` — the very status the contract advertises — hits
`TypedResultValidator.Validate` with `successStatus=200`, finds no declared response for
204, and **throws `InvalidOperationException` at request time**.
Repro: `public static readonly Define DeleteTask = Define.Delete("/api/tasks/{id}");`
+ handler returning `RivetResult` → server sends 200, contract says 204.
Fix: make `Define.Delete` pass 204, or change `DefaultSuccessCode` to 200 — but the two
sides must agree.

**A2. RouteParser regexes don't handle optional, catch-all, default-value, or brace-containing constraint params.**
`RouteParamRegex` is `\{(\w+)(?::[^}]+)?\}` (RouteParser.cs:32-39):
- `{id?}` (optional param) — no match; the param isn't recognized as a route param, so an
  `Id` input property is classified as **Query** and the literal `{id?}` placeholder is
  left in the emitted URL.
- `{*path}` / `{**path}` (catch-all) — same silent failure.
- `{id=5}` (default value) — not matched.
- `{code:regex(^\d{4}$)}` — `[^}]+` stops at the `}` inside `{4}`;
  `StripRouteConstraints` produces a corrupted route (`/x/{code}$)}`).
`RouteParserTests.cs` covers none of these. Affects ContractWalker, EndpointWalker, and
CoverageChecker identically since all three funnel through this class.

**A3. Inherited properties are silently dropped everywhere.**
`TypeWalker` (TypeWalker.cs:217) and ContractWalker (ContractWalker.cs:485, 513, 566;
also `HasFormFileProperty` at 629) use `GetMembers()` only — **no file in Rivet.Tool
references `BaseType`** (grep-verified). Repro: `record BaseDto { public Guid Id ... }`
+ `[RivetType] record TaskDto : BaseDto { public string Name ... }` → emitted `TaskDto`
has only `name`. For `Define.Get<ListQuery, X>` where `ListQuery : PagedQuery`, the
inherited `Page`/`PageSize` query params vanish from the client. No diagnostic, no test
coverage. Either walk `BaseType` chains or emit an error on non-`object` bases.

### MED

**A4. `SymbolDiscovery` never visits nested types.**
`RoslynExtensions.cs:7-21` recurses namespaces only — `type.GetTypeMembers()` is never
called. A `[RivetType]`/`[RivetContract]`/`[RivetClient]` on a type nested inside a class
is silently ignored. Same gap affects `CoverageChecker.Check` (CoverageChecker.cs:50).

**A5. Type-name collision detection compares only the last namespace segment; enums and brands have none at all.**
`TypeWalker.cs:185-194` + `GetNamespaceGroup` (654-663) uses `ns.Name`, so `A.Models.Foo`
vs `B.Models.Foo` both group as `"Models"` — the second `Foo` is **silently dropped**
(its `TypeRef` then points at the wrong shape) instead of raising the collision error.
Generic arity is ignored (`Result` vs `Result<T>` share the key `"Result"`). Enums
(TypeWalker.cs:416-441) and value-object brands (454) are keyed by simple name with
`ContainsKey`/`TryAdd` — first-wins silently, no collision path at all. The
`_visiting.Contains(name)` early-return at line 180 also skips the collision check during
cycles.

**A6. `[controller]`/`[action]` route tokens not substituted.**
`EndpointWalker.ExtractControllerRoute` (EndpointWalker.cs:303-320) returns the `[Route]`
string verbatim; no code handles `[controller]` (grep-verified).
`[ApiController, Route("api/[controller]")]` — the canonical MVC pattern — produces a TS
client calling literally `/api/[controller]/...`. Silent.

**A7. Generic `[ProducesResponseType<T>]` (.NET 7+) not recognized.**
`WellKnownTypes.cs:98` resolves only the non-generic attribute;
`ProducesResponseTypeAttribute`1` is a different symbol, so EndpointWalker.cs:600/631
skip it via `SymbolEqualityComparer` mismatch. The endpoint loses its return
type/responses with no warning.

**A8. Typed-result mapping table incomplete — union branches silently dropped.**
`WellKnownTypes.cs:154-174` covers Ok/Created/Accepted/NoContent/BadRequest/Unauthorized/
NotFound/Conflict/UnprocessableEntity only. `ProblemHttpResult`, `ValidationProblem`,
`ForbidHttpResult`, `InternalServerError<T>` (.NET 9), `JsonHttpResult<T>` are unmapped —
`Results<Ok<T>, ProblemHttpResult>` emits a union missing the error branch
(`CollectTypedResultMappings`, EndpointWalker.cs:509-537, skips unmapped args silently).

**A9. `[Range]` handling crashes on the `Range(Type, string, string)` overload; culture-sensitive parsing.**
TypeWalker.cs:592-599: the guard is `ConstructorArguments.Length >= 2`, so
`[Range(typeof(decimal), "0", "100")]` reaches `Convert.ToDouble` on an `ITypeSymbol` →
`InvalidCastException`, killing the tool. The string overload uses
`Convert.ToDouble("0.5")` under current culture — misparses under comma-decimal locales.

**A10. Controller params without binding attributes are dropped or misclassified.**
EndpointWalker.cs:370-395 + `ClassifyParam`: a complex DTO param with no `[FromBody]`
(which `[ApiController]` infers as body) classifies as `null` and is **silently dropped**
— the generated client sends no body. In mixed-upload methods, the fallback at 382-385
turns any unclassified param — including `[FromHeader]` and concrete DI services — into a
`FormField`. `FromHeader`/`FromServices` aren't in WellKnownTypes at all.

**A11. Systematic silent skips with no diagnostics.**
- `ContractWalker.BuildEndpointFromField` (ContractWalker.cs:133-144, 152-154, 163-166):
  no syntax ref / non-`VariableDeclarator` / empty chain / **non-constant route argument**
  → `return null`. A field initialized via a helper method or computed route disappears
  without a word.
- `EndpointWalker.BuildEndpoint` (EndpointWalker.cs:63-77): an explicitly
  `[RivetEndpoint]`-attributed method lacking an HTTP attribute or route → silently
  dropped.
- ContractWalker *does* warn for non-`static readonly` fields — the warn pattern exists,
  it's just inconsistently applied.

**A12. `T?` nullability on type parameters is lost.**
TypeWalker.cs:357-359: the `ITypeParameterSymbol` check precedes the
`NullableAnnotation.Annotated` check (370), so `record Wrapper<T>(T? Value)` emits
`value: T`, not `T | null`.

### LOW

- **A13.** Non-public properties are emitted: TypeWalker.cs:219 never checks
  `DeclaredAccessibility` — a `private` property on a `[RivetType]` appears as a required
  TS property. Same in ContractWalker's input-property loops.
- **A14.** `[JsonPropertyName]` on a route-bound property breaks route interpolation
  (ContractWalker.cs:578-601): route matching uses the C# name but the emitted param uses
  the JSON name, so `ClientEmitter.InterpolateRoute` leaves `{id}` unfilled.
- **A15.** Dead/duplicated code: `TypeWalker.WalkType` (224-242) inlines exact duplicates
  of its own `IsJsonIgnored`/`GetJsonPropertyName` helpers; ContractWalker.cs:318's
  `or "ProducesFile"` branch is dead; `TsReservedWords` includes `"async"` (not actually
  reserved — harmless over-escaping).
- **A16.** Unmatched GET route params are added with `parameters.Insert(0, ...)` in
  `HashSet` iteration order (ContractWalker.cs:604-611) — reversed and technically
  nondeterministic ordering.
- **A17.** camelCase collisions undiagnosed: `MyValue`/`myValue` on one record both map
  to `myValue` — duplicate TS properties, no warning. (Endpoint-level duplicates are
  warned downstream; property-level is not.)
- **A18.** Enum string-union assumption is config-dependent (TypeWalker.cs:416-441):
  camelCased member names match only `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`.
  Default converter emits PascalCase; default STJ emits numbers. Worth a doc note or
  config knob.
- **A19.** `CoverageChecker.cs:80-81` joins field→endpoint by
  `(controllerName, camelCase fieldName)` — two same-named contracts in different
  namespaces mis-join. Semantic models also re-requested per invocation in step 3 (150).

---

## 2. Rivet — Emit pipeline (`Rivet.Tool/Emit/`, `Rivet.Tool/Model/`)

### HIGH

**E1. Form-encoded endpoints generate TS that doesn't compile — and would send `{}` at runtime.** *(independently verified)*
ClientEmitter.cs:197-198 emits `body: new URLSearchParams(input.body as Record<string,
string>)` plus `formEncoded: true`, but `rivetFetch` in Templates/rivet.ts:217 declares
options as `{ body?: unknown; query?: RivetQuery; unwrap?: boolean; blob?: boolean }` —
no `formEncoded`, no URLSearchParams handling. Two failures: (1) `tsc --strict` rejects
the call with TS2353; (2) rivet.ts:223-233 treats any non-FormData body as JSON —
`JSON.stringify(new URLSearchParams(...))` evaluates to `"{}"` and sets
`Content-Type: application/json`. Only the `raw: true` path (`BuildRawFetchOptions`,
lines 340-344) handles form encoding correctly. `TypeScriptCompilationTests` has no
form-encoded fixture; `ClientEmitterTests.cs:633` only asserts the broken string is
present.

**E2. Zod assert-name collisions silently validate against the wrong schema.**
ZodValidatorEmitter.cs:35-42 keys validators by `GetAssertName` and
`DistinctBy(AssertName)` — but `TsType.GetNameSuffix` (Model/TsType.cs:57-79) is lossy:
every `StringUnion` with >3 members → `"Enum"`, every `IntUnion` → `"Enum"`, every
`InlineObject` with >3 fields → `"Object"`, and small inline objects are named by field
names only, ignoring field types. Two endpoints returning `{ value: string }` and
`{ value: number }` both map to `assertValue`; the second endpoint's client parses its
response with the wrong validator → spurious runtime ZodErrors, plus a TS type error from
the shared assert's return cast. Same for any two distinct 4+-member enums returned
directly. No test covers a suffix collision.

### MED

**E3. `unwrap: false` + Zod validation throws on any unanticipated status.**
ClientEmitter.cs:266-301: the validated `unwrap: false` branch's status-dispatch chain
ends with `else { result.data = assertSuccess(result.data); }` — a 500 with an
HTML/ProblemDetails body is validated against the *success* schema and throws a ZodError,
defeating the point of `unwrap: false`. The no-typed-errors branch (293-300) asserts the
success schema unconditionally on **every** status. The unvalidated client returns the
result object for any status; the validated client silently changes that contract.

**E4. Inline-type extraction rewrites `T | null` to `prop?: T` — diverging from the wire format.**
InlineTypeExtractor.cs:361-367 (`BuildPropertyDefinition`): a `Nullable` field becomes
`IsOptional: true` with the inner type. The extracted DTO emits `prop?: T` and omits
`prop` from JSON Schema `required` — but the server still serializes `"prop": null`,
which fails the Zod validator and is mistyped in TS. The same shape below the extraction
threshold renders correctly as `prop: T | null` (TypeEmitter.cs:55). Whether a shape got
extracted (≥2 occurrences or ≥5 fields) changes its null semantics. Untested.

**E5. JSON contract round-trip drops `IsFileEndpoint` and `QueryAuth`.**
ContractEmitter.cs:106-107 serializes both, but `JsonContractReader.ToEndpointDefinition`
(JsonContractReader.cs:38-56) omits the last two constructor args, defaulting to
`false`/`null`. Regenerating from contract JSON silently loses query-auth: the
`…Path`/`…Url` functions lose the auth-token query field (ClientEmitter.cs:380-385) and
OpenAPI loses the required query param + `x-rivet-query-auth` extension.

**E6. Generic templates referencing generics emit garbage `Foo_T` schemas.**
Both `OpenApiEmitter.CollectGenericInstances` (OpenApiEmitter.cs:941-954) and
`JsonSchemaEmitter.CollectAllGenericInstances` (JsonSchemaEmitter.cs:353-370) walk *all*
definitions including generic templates. `Wrapper<T> { PagedResult<T> Page; }` registers
a mono instance named `PagedResult_T` whose type-param map resolves `T → TypeParam(T)`,
producing a components schema full of `object` fallbacks plus a stderr warning on every
run. Duplicated flaw in both files.

**E7. Property metadata added as `$ref` siblings — dropped by OpenAPI 3.0 consumers.**
SchemaEnricher.cs:12-78 writes `description`/`default`/`example`/constraints/`format`
directly into the property schema; when the property type is a `TypeRef`/`Brand`/`Generic`
the schema is `{"$ref": ...}` and 3.0 ignores all `$ref` siblings. `MapNullable`
(OpenApiEmitter.cs:675-700) does the `allOf` wrap for nullable, but enrichment
(OpenApiEmitter.cs:800-804) doesn't — a `[RivetDescription]` on a DTO-typed property
silently vanishes from the spec.

**E8. Query-param optionality inconsistent across emitters; `TsEndpointParam.IsOptional` is dead.**
Model/TsEndpointDefinition.cs:44 declares `IsOptional` — never set, never read
(grep-verified). `ClientEmitter.IsParamOptional` (506-528) checks `Nullable` plus
contract lookups, while OpenApiEmitter.cs:146 only checks `is not TsType.Nullable` — a
JSON-contract optional non-nullable query param is optional in TS but `required: true` in
OpenAPI. Related: C# default-valued params (`int page = 1`) emit as required everywhere
(EndpointWalker.cs:371-395 ignores `HasExplicitDefaultValue`).

### LOW

- **E9.** `Exclude<number, 200 | 404>` is a type-level no-op (ClientEmitter.cs:414) — the
  DU catch-all arm overlaps the typed arms; plain `result.status === 404` doesn't narrow.
  Narrowing only works through the `isOk()`/`isNotFound()` predicates in rivet.ts.
- **E10.** No string escaping in emitted literals: TypeEmitter.cs:50 (StringUnion
  members), ZodValidatorEmitter.cs:132, `QuoteIfNeeded` (TypeEmitter.cs:138 quotes but
  never escapes `"`/`\`). Safe for Roslyn-derived names, but the JSON-contract path
  accepts arbitrary strings — a member containing `"` produces invalid TS. Also `Brand`
  of a union would emit `string | number & {...}` without parens (EmitGroupFile, line 86).
- **E11.** OpenAPI conformance nits: request bodies always `required: true` even when the
  body type is `Nullable` (OpenApiEmitter.cs:241/260/278); `discriminator` attached to
  `oneOf` of inline schemas without `$ref`s or a `mapping` (590-600); `ParseJson`
  (509-511) throws a misleading error for a legitimate `null` example literal.
- **E12.** `ConvertExclusiveToOpenApi30` corrupts mixed bounds (OpenApiEmitter.cs:
  1055-1070): `Minimum = 0` + `ExclusiveMinimum = 5` → emitted constraint becomes `> 0`
  instead of `> 5`.
- **E13.** `SafeFunctionName` collision (ClientEmitter.cs:12-15, 420-433): `delete` →
  `remove` with no check against an existing `remove` endpoint → duplicate
  `export function remove` (invalid TS). Function-name uniqueness per controller is never
  enforced.

### Dead code / duplication

- **E14.** `JsonSchemaEmitter` is ~70% a copy of `OpenApiEmitter`'s schema logic:
  `MapPrimitive` (incl. identical int-range table), `BuildObjectSchema`,
  `BuildMonomorphisedSchema`, `BuildInlineObjectSchema`, `BuildTaggedUnionSchema`,
  `CollectGeneric*` — ~150 near-verbatim lines differing only in `$ref` prefix, nullable
  style, and `x-rivet-*` extensions. They have already drifted slightly (`File`/`unknown`
  handling). A shared core parameterized by dialect would collapse this and fix E6 once.
- **E15.** `ContractEmitter.ContractEndpoint`/`ContractResponseType`/
  `ContractEndpointExample` are field-for-field mirrors of the model records plus two-way
  mapping — the mirror layer is exactly where E5 lives. The model records already carry
  the right `JsonIgnore` attributes.
- **E16.** `InlineTypeExtractor.GenerateName` has 5 overloads (58-108); production uses
  only the 6-arg forms — the 58-65 and 81-84 overloads are test-only.
- **E17.** `ZodValidatorEmitter.BuildTaggedUnionVariantExpression` (141-148) is a no-op
  wrapper — its `InlineObject` arm is byte-identical to `BuildZodExpression`'s.
- **E18.** `EmitPipeline.cs:105-119` vs `167-180`: in compile mode every controller
  client is written twice (once `ValidateMode.None`, then overwritten with `Zod`).
- **E19.** Determinism is otherwise clean. Only nit: brands within a type-group file emit
  in walker order while enums are sorted (TypeEmitter.cs:83-101) — stable, just
  inconsistent.

---

## 3. Rivet — Import pipeline (`Rivet.Tool/Import/`)

All HIGH/MED findings below were reproduced with temporary xunit tests against the real
importer. Forward/import type mappings were checked for mutual consistency and are fine
(`date`/`date-time`/`uuid`/`int64`/`uint8`/`decimal` both directions;
`x-rivet-csharp-type` covers the rest).

### HIGH

**I1. `$ref` alias schemas produce dangling type names — generated C# does not compile.**
SchemaMapper.cs:95-101: `MapSchemas` registers `SchemaNameMap["Alias"] = "Alias"` *before*
skipping alias entries (`schema is OpenApiSchemaReference → continue`), so no `Alias`
type is ever emitted — but `TryResolveSchemaReference` (270-274) returns the sanitized id
because `WouldGenerateType` (evaluated on the proxied target) is true. Confirmed output:
`record Holder(Alias? Thing)` with only `Real.cs` generated — CS0246, no warning.
Repro: `"Real": {object}, "Alias": {"$ref": "#/components/schemas/Real"}, "Holder":
{props: {thing: $ref Alias}}`. Fix: map alias keys to the *target's* mapped name.

**I2. `allOf` wrapping a non-object ref (e.g. an enum) → dangling type name, does not compile.**
SchemaMapper.cs:134-137 skips emitting an allOf record when the merged record has 0
properties, but `SchemaClassifier.WouldGenerateType` (SchemaClassifier.cs:124-127)
returns true for *any* allOf, so refs to the skipped schema resolve to its name.
Confirmed: `record Holder(NullableStatus Status)` with only `enum Status` generated. This
is exactly the OpenAPI 3.0 allOf-wrapped-enum idiom NSwag/openapi-generator emit, so it
will hit real specs. Same root cause as I1: `WouldGenerateType` and `MapSchemas` must
agree — ideally derive both from one classification.

**I3. Synthetic parameter-input records collide across tags — one contract silently gets the wrong input type.**
`ContractBuilder.ResolveParamInputType` (ContractBuilder.cs:209-217) names the record
`{fieldName}Input` with the tag prefix stripped. `members_getById` and `orders_getById`
both synthesize `GetByIdInput`; `mapper.AddExtraRecord` has no name dedup, two
`Types/GetByIdInput.cs` files with different shapes are emitted, and `Program.cs:175-179`
last-write-wins — confirmed: `MembersContract.GetById` ends up accepting
`long OrderNumber`. The `HasMappedSchema(recordName)` guard at 212 has the same flaw: a
components schema named `GetByIdInput` is "reused" without any shape comparison. Fix:
qualify by contract (`MembersGetByIdInput`) or dedupe-with-shape-check.

### MED

**I4. Nested allOf + sibling properties: middle layer's own properties dropped from descendants.**
RecordSynthesizer.cs:41-45: when an allOf element is a ref whose target itself has allOf,
`ResolveAllOfRecord` recurses into the target's `AllOf` only — the target's sibling
`properties` are never merged. Confirmed: `Base{id}` ← `Mid: allOf[Base]+{midProp}` ←
`Leaf: allOf[Mid]+{leafProp}` produces `Leaf(Id, LeafProp)` — **MidProp silently
missing**. Two-level inheritance chains are common in real specs.

**I5. Inline object with both `properties` and `additionalProperties` → properties silently discarded.**
`SchemaMapper.ResolveObjectType` (598-602) checks `AdditionalProperties` first and
returns `Dictionary<string, T>`, dropping declared properties. (Named schemas take the
opposite branch in `MapSchemas` and drop the `additionalProperties` instead — inconsistent
in both directions.)

**I6. `List<IFormFile>` misses the `Microsoft.AspNetCore.Http` using — does not compile.**
CSharpWriter.cs:23 checks `p.CSharpType is "IFormFile" or "IFormFile?"` — exact match
only. A multipart array-of-files schema produces `List<IFormFile> Files` with no using —
CS0246. Same exact-match flaw in `WriteContract` (156-158). Fix: `Contains("IFormFile")`.

**I7. Stale `fileContentType` when a lower JSON 2xx supersedes a binary 2xx.**
`ContractBuilder.ResolveOutputType` (267-300): the binary branch sets `fileContentType`
but the JSON branch never clears it. Responses `{"202": application/pdf(binary), "200":
application/json}` confirmed to emit `Define.Post<Dto>(...).Status(200)
.ProducesFile("application/pdf")` — typed JSON output *and* ProducesFile on one endpoint.

**I8. Top-level `required` on an allOf schema is ignored — properties wrongly optional.**
`RecordSynthesizer.ExtractProperties` reads `schema.Required` only per allOf element;
`MergeWithSiblingProperties` (169-172) early-returns when the composing schema has no
sibling `properties`. `"Derived": {"allOf":[{"$ref":"Base"}], "required":["id"]}` — the
standard "inherit and tighten" pattern — confirmed to produce `[RivetOptional] string? Id`.
Affects wire optionality through the whole forward pipeline.

**I9. `2XX` range success responses silently dropped.**
ContractBuilder.cs:255 (`int.TryParse(statusStr)`) skips `2XX`; `ResolveErrorResponses`
also skips it. An operation whose only response is `"2XX"` with a JSON schema imports as
a void endpoint — no output type, no `[rivet:unsupported]` marker, no warning.
Inconsistent with the otherwise-good marker discipline.

### LOW

- **I10.** Media-type parameters defeat content-type matching
  (`TryGetSchemaForContentType`, ContractBuilder.cs:612, exact dictionary lookup):
  `application/json; charset=utf-8` is treated as unsupported (it does at least emit a
  marker). Strip parameters before matching.
- **I11.** Parse diagnostics silently discarded: OpenApiImporter.cs:13-14 ignores
  `readResult.Diagnostics`; a spec with recoverable parse errors imports with no
  indication unless `Document` is null.
- **I12.** Multi-scheme security collapsed to first scheme (`DetectGlobalSecurity`,
  OpenApiImporter.cs:86-92; `ResolveSecurity`, ContractBuilder.cs:641-647): OR
  alternatives and scopes dropped without warning; `scheme.Reference?.Id` can return null
  on the first iteration even when a later requirement resolves.
- **I13.** Parameter metadata loss in synthesized input records (ContractBuilder.cs:
  198-201): description/deprecated/constraints dropped; path/query/header/cookie
  distinction erased — after round-trip, header and cookie params re-emit as query
  params. Worth a marker.
- **I14.** `MapIntEnum` (SchemaClassifier.cs:429): `Math.Abs(intVal)` throws
  `OverflowException` for `int.MinValue`.
- **I15.** `ResolveConstType` (SchemaMapper.cs:465-483) receives const as a string, so
  JSON `"true"`/`"42"` strings are indistinguishable from boolean/number consts.
- **I16.** Duplication worth consolidating: `GetExtensionString` exists twice
  (ContractBuilder.cs:736-749 duplicates SchemaClassifier.cs:267-280);
  `TryResolveNullableOneOf` (SchemaMapper.cs:333-361) re-implements
  `SchemaClassifier.IsNullableOneOf`; `SynthesizeInlineEnum`/`SynthesizeInlineIntEnum`
  (566-594) are copy-paste twins; `ResolveErrorResponses`' status normalization
  duplicates `NormalizeStatusCode` and can drift.

**Checked and found OK:** discriminator → `As*` wrapper records (documented intentional);
`additionalProperties: true/false`, bare/empty objects; 3.1 type arrays vs 3.0
`nullable: true`; nullable oneOf; schema-name dedup after PascalCasing; enum member
dedup; identifier sanitization incl. leading digits; circular refs; string escaping via
`SymbolDisplay.FormatLiteral`; format round-trips (custom formats via `RivetFormat`,
`DateTimeOffset`/`JsonNode` via `x-rivet-csharp-type`). Despite suspicion, essentially
**no dead code** in this subsystem — `SchemaClassifier`'s 568 lines are breadth, not
bloat.

---

## 4. Rivet — Attributes library (`Rivet.Attributes/`, ships on NuGet)

### HIGH

**R1. Duplicate `.Returns()` for the same status throws an unrelated LINQ exception at request time.**
`TypedResultValidator.ResolveExpectedResponseType` (TypedResultValidator.cs:68) uses
`SingleOrDefault(r => r.StatusCode == actualStatusCode)`; the builder happily appends
duplicates (EndpointBuilder.cs:117-135). `.Returns<A>(404).Returns<B>(404)` → the first
production 404 throws `InvalidOperationException: Sequence contains more than one
matching element` with no route context. Fix: validate duplicates in `Returns()`
(consistent with the `.Status()` double-call guard) or use `FirstOrDefault`.

### MED

**R2. Per-request reflection in the hot path.**
TypedResultValidator.cs:120-132: every `Invoke` on the typed-results path calls
`branch.GetType().GetInterfaces()` plus a LINQ scan for `IValueHttpResult<>`. Runs on
every request for every contract endpoint. The branch-type set per route is tiny and
fixed — a `static ConcurrentDictionary<Type, Type?>` cache eliminates it.

**R3. Mutable builder state on shared `static readonly` singletons.**
The documented pattern stores fully-built `RouteDefinition`s in `static readonly` fields,
but every builder method (EndpointBuilder.cs:15-203) mutates in place and returns `this`.
Any runtime call to a builder method silently mutates global state for all requests, with
no thread safety and no freeze/seal step. At minimum a doc warning; better, throw after
first `Invoke`, or go copy-on-write (`CopyStateTo` already half-implements it).

**R4. net9.0-only TFM excludes net8 LTS consumers; XML docs not shipped.**
Rivet.Attributes.csproj:4 targets only `net9.0` (STS, EOL May 2026) with a full
`FrameworkReference Microsoft.AspNetCore.App`. Multi-target `net8.0;net9.0;net10.0`.
Also missing `<GenerateDocumentationFile>true</GenerateDocumentationFile>` — consumers
get no IntelliSense docs from the package.

**R5. `RouteDefinition<TInput, TOutput>` lacks the single-result `Invoke<T1>` overload.**
The other three definition shapes have a one-type-argument `Invoke<T1>`
(EndpointBuilder.cs:315, 391, 482); the input+output variant starts at `Results<T1,T2>`.
A handler returning just `Created<TOutput>` can't use the typed-results path. Adding it
is non-breaking.

### LOW

- **R6.** Example builder methods (`RequestExampleJson`/`RequestExampleRef`/
  `ResponseExampleJson`/`ResponseExampleRef`, EndpointBuilder.cs:137-160) are silent
  no-ops that exist only for Roslyn to read — nothing in the XML docs says so.
- **R7.** `CopyStateTo` (EndpointBuilder.cs:62-74) doesn't copy `_statusSet`, so
  `.Status(202).Accepts<T>().Status(204)` bypasses the double-`.Status()` guard.
- **R8.** Implicit conversions to `Define` return `default!` (null) — intentional
  Roslyn-only trick, but an undocumented NRE footgun on a public NuGet surface
  (Endpoint.cs:7-9 etc.).
- **R9.** `RivetConstraintsAttribute` sentinel design (RivetConstraintsAttribute.cs:11-16)
  can't represent an explicit `uniqueItems: false` — lost on round-trip. Doc note.
- **R10.** `FileRouteDefinition` (EndpointBuilder.cs:536-574) has no `Invoke` at all and
  is hardcoded to GET — `Define.File` can't express a POST upload despite `AcceptsFile()`
  existing on the base.

---

## 5. Rivet — CLI / tool host (`Program.cs`, `CliParser.cs`, `CompilationLoader.cs`)

### HIGH

**C1. `rivet file.cs` mode crashes in the published single-file binaries.**
`CompileFromFiles` (CompilationLoader.cs:172-178) builds references from
`typeof(object).Assembly.Location` — empty string under `PublishSingleFile=true`
(exactly what release.yml ships for all four RIDs), so `Path.GetDirectoryName(...)!`
yields null/empty and `MetadataReference.CreateFromFile` throws an unhandled exception.
Release smoke tests only exercise `--from`/`--from-openapi`, so this mode is dead in the
shipped binaries. Fix: probe `AppContext.BaseDirectory` /
`AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")`.

### MED

**C2. MSBuild SDK fallback probing is macOS-only, but linux-x64/win-x64 binaries ship.**
CompilationLoader.cs:108-148: when `MSBuildLocator.RegisterDefaults()` fails (the normal
case for a self-contained apphost), fallback probes `DOTNET_ROOT`,
`/usr/local/share/dotnet`, Homebrew paths only. `--project x.csproj` from the released
linux/windows binaries dies with an unhandled `InvalidOperationException`.

**C3. A flag missing its value, or any typo'd flag, falls into the positional-files bucket.**
CliParser.cs:30-31, 73-75: `rivet --project` (no value) makes the literal string
`"--project"` the project path → unhandled `FileNotFoundException` with stack trace.
`--ouput dir` treats both tokens as `.cs` files. There is no unknown-option error at all.
Fix: reject args starting with `-` in the default case; error on flags missing values.

**C4. No top-level exception handling.**
`Program.Run` never catches: MSBuild-not-found (C2), `FileNotFoundException` from
`CompileFromFiles`, malformed JSON in `JsonContractReader.Read` (Program.cs:140) and
`OpenApiImporter.Import` (Program.cs:166). `rivet --from <(echo 'not json')` → stack
dump. A single try/catch printing `error: {message}` + exit 1 fixes the lot.

### LOW

- **C5.** Dead `Stopwatch` in Program.cs:32 (never read; the `using System.Diagnostics`
  exists only for it).
- **C6.** Duplicate `--compile`-requires-`--output` check (Program.cs:35-39 vs
  EmitPipeline.cs:48-52 — keep the pipeline one; `RunFromContract` bypasses Program's).
- **C7.** `--check`/`--routes` silently dropped in `--from` mode (CliParser.cs:82-85
  doesn't forward them) — `rivet --from contract.json --routes` does codegen instead of
  listing routes, no warning.
- **C8.** `rivet --project foo.cs` (non-csproj path) silently emits empty output with
  exit 0 (CliParser.cs:96 + CompilationLoader.cs:157 — empty files array, empty
  compilation, zero errors).
- **C9.** `RoutePrinter` (RoutePrinter.cs:37-46) emits ANSI escapes unconditionally — no
  `Console.IsOutputRedirected`/`NO_COLOR` check; `rivet --routes > routes.txt` is full of
  escape garbage. Also rows go to stdout but the count goes to stderr.
- **C10.** Preview SDK directories sort as 0.0 in fallback probing
  (CompilationLoader.cs:150-155): `Version.TryParse("10.0.100-rc.1...")` fails. Strip at
  the first `-`.
- **C11.** Multi-targeted projects (`<TargetFrameworks>` plural) load one TFM with no
  selection mechanism and no test; `MSBuildLocator.RegisterDefaults()` ignores the target
  project's `global.json`. Document as known limitations.

---

## 6. Rivet — Packaging / build hygiene

- **P1 (MED).** Version duplicated in Rivet.Attributes.csproj and Rivet.Tool.csproj (both
  `0.34.3`). publish.yml overrides via `-p:Version=$TAG` so NuGet artifacts are
  consistent, but local `task pack` uses the csproj values and the two must be bumped in
  lockstep by hand. Hoist `<Version>` into `Directory.Build.props` (currently contains
  only `RollForward`).
- **P2 (LOW).** global.json pins `"version": "9.0.0"` while Rivet.Tests targets net10.0
  and CI uses 10.0.x. `rollForward: latestMajor` papers over it; bump the pin to match CI.
- **P3 (LOW).** Rivet.Tool.csproj:38 has a blanket `NoWarn NU1701` — scope it to the
  offending PackageReference.
- **P4 (LOW).** Taskfile: `dev` duplicates `docs:dev`; `task test` runs `[Category=Local]`
  publish tests (multi-minute self-contained publish + cross-compile) that CI filters
  out — add a `test:fast` mirroring CI's filter.
- **P5 (INFO).** rivet-contract-schema.json is internally consistent and test-validated,
  but has no version field — with `additionalProperties: false`, older tools hard-fail on
  any new kind. Add a `contractVersion` field before 1.0.

---

## 7. Rivet PHP (`rivet-php` repo — `~/Sites/medway/rivet-php`)

Overall: the reflector is real and pulling its weight (~1,520 LOC src, solid
unit/integration/golden-contract tests, no shell-out/eval, loud diagnostics). The
handwaving lives in the **Laravel/Symfony adapter paths** — the headline use case, and
the least-tested — and in silent fidelity holes daily use wouldn't surface.

### HIGH

**PHP1. `rivet/php-reflector/` in this repo is a dead husk.**
Contains only `.idea/` and a stale `.phpunit.result.cache`; the code moved to
`maxanstey-meridian/rivet-php` (commit 924fc4c). Delete the directory. Also
rivet-php's `composer.json` `support.source` still points at the old in-repo path.

**PHP2. No DateTime/Carbon handling anywhere — dates emit as empty objects.**
`grep DateTime|Carbon` over rivet-php src and tests returns nothing. In
`PropertyWalker::resolveNamedType` (PropertyWalker.php:120-135),
`public \DateTimeImmutable $createdAt` passes `class_exists` → enqueued →
`processClass` emits a `DateTimeImmutable` type with **zero properties**, and the
property refs that empty object — silently wrong, since JSON dates are strings.
`DateTimeInterface` is worse: `class_exists` is false for interfaces → dangling `ref`
never defined in the contract. For a Laravel sidecar, this is the biggest real-world
fidelity hole.

**PHP3. Adapters never filter framework params; the README claims they do.**
`EndpointBuilder::buildEndpoint` (EndpointBuilder.php:69) skips unknown types only when
`$knownFqcns !== []`, which is threaded through only by `ControllerWalker` (the
standalone `--dir` path). `EndpointBuilder::walkRoutes` — used by **both**
`LaravelRouteWalker` and `SymfonyRouteWalker`, i.e. the actual `php artisan
rivet:reflect` path — calls `buildEndpoint` without it. So `Illuminate\Http\Request
$request` (the most common controller param in existence) is classified as a **body**
param and the framework Request class is walked into the contract. The README's
"Framework parameters... are filtered out automatically" is false for the adapters. Zero
tests cover an `Illuminate\Http\Request` param. Route-model-binding params (`User $user`
with `{user}`) → body, not route.

### MED

**PHP4. `$ref` variable shadowed by enum reflection in `buildEndpoint`.**
EndpointBuilder.php:~85: the BackedEnum branch does `$ref = new \ReflectionEnum(...)`,
clobbering the controller's `ReflectionClass`. The post-loop
`$namespace = $ref->getNamespaceName()` (used to resolve `#[RivetRequest]/#[RivetResponse]`
string refs) and the missing-response diagnostic then use the **enum's**
namespace/name. Masked in tests only because the fixtures share one namespace.

**PHP5. `T[]` suffix syntax is fatal; the `@var` regex swallows variable names.**
`TypeParser` has no `[]` suffix handling: `@var string[]` → RuntimeException. Worse,
`extractVarType` (PropertyWalker.php:~233) regex captures the whole rest of the line, so
the perfectly legal `@var list<string> $items The items` also throws. In the
`PropertyWalker::walk`-only path (ReflectCommand.php:74) it's an **uncaught fatal**.
`string[]` is the most common PHPDoc array syntax in the wild.

**PHP6. `array<int, T>` becomes `Record<string, T>`.**
TypeParser.php:~147 discards the key type and emits `dictionary` (string-keyed). The
standard PHPDoc list convention `array<int, Foo>` should map to `array`. Tests only
cover `array<string, int>`.

**PHP7. FormRequest validation rules not implemented; every response hardcoded 200.**
No FormRequest handling at all — honest scope-cutting (README defers to v2), but the
Laravel adapter can't derive request types from the dominant Laravel idiom. Related:
`statusCode: 200` is hardcoded even for POST (EndpointBuilder.php:~150).

**PHP8. Golden fixture hand-duplicated across repos.**
`rivet/Rivet.Tests/Fixtures/php-golden-contract.json` is a manual copy of
`rivet-php/tests/Integration/SampleApp/golden-contract.json` — byte-identical today, no
sync mechanism. When rivet-php's emitter changes, the C# "E2E" tests keep passing against
a stale contract. `PhpLaravelE2ETests` never actually runs PHP.

### LOW

- **PHP9.** Laravel adapter blanket-`require_once`s every PHP file under `app/`
  (Laravel/RivetReflectCommand.php:36-44) — files with top-level side effects fatal the
  artisan command. The standalone `ReflectCommand` does it right (lazy
  `spl_autoload_register` keyed on the FQCN map); reuse that.
- **PHP10.** Assorted: `ClassFinder` token scan doesn't exclude `Foo::class`/`new class`
  (bogus FQCNs, mostly defused downstream); multi-method routes keep only the first verb,
  Symfony routes with no method restriction skipped entirely; types keyed by
  `getShortName()` — same-name DTOs in different namespaces silently collide;
  `key?: T` shape syntax maps to `nullable` instead of `optional`; `list<int,string>`
  (wrong arity) silently returns `ref "list"`; quoted literals have no escape handling.
- **PHP11.** rivet-php README overclaims: beyond PHP3, the Limitations section says
  untyped arrays emit `unknown[]` while the code emits plain `unknown`
  (PropertyWalker.php:~100), and nothing warns that `T[]` or `@var ... $name` lines are
  fatal.

---

## 8. Tests (`Rivet.Tests/`)

Strong suite overall — real e2e (tsc --noEmit, dotnet publish, sample builds, real-world
OpenAPI fixtures, gap-analysis metrics). Weaknesses:

- **T1 (MED).** `CompilationHelper.CreateCompilationWithProjectReference`
  (CompilationHelper.cs:118-135) skips the `GetDiagnostics()` error check, nullable
  context, and `LanguageVersion.Latest` that the other helpers have —
  `TransitiveEndpointTests` can pass against source that doesn't compile, and nullable
  annotations are evaluated in a nullable-disabled context (exactly what those tests
  probe).
- **T2 (MED).** The compile→discover→walk→group→emit pipeline is privately
  re-implemented in ≥8 test files (DeepReviewFixTests.cs:36, FormFileTests, 
  MetadataAttributeTests, inline in ClientEmitterTests ×20, ValidatorEmitterTests,
  TypeScriptCompilationTests, ContractEndpointTests...). One `EmitClientFromSource`
  helper would delete several hundred lines.
- **T3 (MED).** `DeepReviewFixTests.cs` is a 1,341-line, 40-fact bolt-on organized by
  when bugs were found rather than what they test. Redistribute into per-feature files
  before the next review adds a sibling.
- **T4 (MED).** ~1,000+ `Assert.Contains` assertions on exact two-space-indented emitter
  output (OpenApiImporterTests: 266, KitchenSinkImportTests: 149, TypeEmitterTests: 108,
  ClientEmitterTests: 100). Any cosmetic emitter change is a mass-edit event; no
  snapshot/golden-file mechanism exists except the PHP fixture.
- **T5 (LOW).** Three fixture-loading conventions (`AppContext.BaseDirectory`,
  cwd-relative `../../..`, `FindRepoRoot()`); `CompilationHelper.FindFile` matches by
  `EndsWith`, so `"types.ts"` can match the wrong file.
- **T6 (LOW).** Coverage gaps: `CompilationLoader` (the real `--project` MSBuild path)
  has no direct test outside `[Category=Local]`; `RoutePrinter` untested; CliParser has
  no tests for missing flag values, unknown flags, or `--project` precedence;
  `SelfContainedPublishTests` never runs the published binary in file mode or `--project`
  mode — exactly the two modes broken in self-contained builds (C1, C2);
  `ContractInvokeTypedResultsTests` has no duplicate-`.Returns()` test (R1);
  `TypeScriptCompilationTests` has no form-encoded fixture (E1).

---

## 9. Samples & docs

- **S1 (INFO).** No API drift in samples — ContractApi/AnnotationApi/ImportDemo all match
  the current attribute API; SampleProjectTests round-trips ContractApi through the real
  walker and builds ImportDemo.
- **S2 (LOW).** ImportDemo's `Generated/` is checked in but never regenerated by tests —
  if importer output changes shape, the sample silently shows stale output.
- **D1 (INFO).** CLI docs are accurate — every flag in README/getting-started exists in
  CliParser with matching semantics. No phantom flags.
- **D2 (LOW/MED).** Docs site links to pages that don't exist locally: index.md and
  getting-started.md link `/reference/cli` and `/guides/tutorial`; README links several
  `/guides/*` pages; docs/ contains only index, getting-started, and a stub
  php-limitations.md ("This page is being rewritten").

---

## Suggested fix priority

1. **A1** (DELETE 204/200 divergence — one-line fix plus a decision about which side is
   right) and **E1** (form-encoded client — template + emitter must agree; add a
   form-encoded fixture to TypeScriptCompilationTests).
2. **I1 + I2** (shared root cause: make `WouldGenerateType` and `MapSchemas` derive from
   one classification), then **I3** (qualify synthetic input records by contract).
3. **A3** (walk `BaseType` chains — silent data loss in the most common OO pattern).
4. **C1/C2/C3/C4** (released-binary dead modes + CLI robustness — small fixes, large
   embarrassment-prevention; extend the publish smoke tests to cover file/`--project`
   modes).
5. **R1** (`SingleOrDefault` landmine), **E2** (Zod assert collisions), **E4**
   (extraction nullable→optional rewrite).
6. **PHP3 + PHP2** (adapter param filtering + DateTime) if rivet-php is actively used.
7. One-liners along the way: **I6** (IFormFile using), **M-class import fixes**
   (I7/I8/I9), **A9** (Range overload crash), **E12**, **I14**.
8. Structural cleanups when convenient: **E14** (merge JsonSchemaEmitter/OpenApiEmitter
   schema cores), **E15** (drop ContractEmitter mirror records), **T2/T3** (test helper
   consolidation), **P1** (hoist version), **PHP1** (delete the husk).
