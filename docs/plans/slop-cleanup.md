# Rivet slop cleanup spec

Status: ready. Decisions made 2026-09-29 (§4).
Baseline: `main` @ `3fd003a` (2026-09-29). Line numbers below are from that commit; re-locate by symbol name if they have moved.
Source: independent slop hunt (2026-09-29), cross-checked against `reviews/2026-09-09-adversarial/REVIEW.md`, `reviews/2026-09-14-fix-investigation/FIX-PLAN.md` and `docs/plans/post-v0.42.0-remediation.md`. Each item carries a `NEW` / `ALREADY-COVERED` tag relative to those documents.

This file is self-contained. A worker (human or subagent) should be able to pick up one work package (WP) from this file alone.

---

## 1. Objective

Remove hand-rolled machinery, duplicated logic, dead code and ceremony from Rivet **without changing supported behaviour**, and fix the handful of real bugs the hunt surfaced. Target: roughly 5,000–6,000 LOC removed across `Rivet.Tool`, `Rivet.Attributes`, `Rivet.Tests` and `tools/`.

Non-goals:

- No new product features, no IR redesign, no rename of the `Ts*` model.
- No new NuGet/Python dependencies.
- No release, tag, push or publish.

**Coupling to rivet-ts (`../rivet-ts`).** rivet-ts produces contract JSON and invokes this tool as `rivet --from <contract.json> --output <dir>` (and `--openapi openapi.json` in its interop test). It relies only on exit code 0 vs non-zero plus stderr text, and on `<dir>/openapi.json` existing on success. It vendors `rivet-contract-schema.json` byte-for-byte, and reads constraint keywords as siblings on `components.schemas.<Name>.properties.<prop>`. Keep all of this working. A2 must keep the contract JSON wire shape; if a WP changes any of the above, record it as a rivet-ts follow-up in the ledger. See `../rivet-ts/docs/plans/slop-cleanup.md` §1.1.

**Breaking changes are allowed and preferred over shims.** Rivet has no consumers other than its owner. Never add or keep a compatibility layer: no `[Obsolete]` deprecation period, no "retained for callers compiled against…" overloads, no dual-read of old attribute or JSON shapes, no forwarding aliases. Remove the old thing in the same WP, update tests/docs/samples, and add a line to `CHANGELOG.md` (create it if absent) under "Unreleased — breaking".

## 2. Doctrine (inline summary — Meridian)

Apply these when making judgment calls inside a WP:

- **Libraries before machinery.** Don't hand-roll what the BCL, Roslyn, System.Text.Json, ASP.NET Core or an existing referenced package (Microsoft.OpenApi 2.7.5) already provides. Custom machinery needs a recorded disqualifying reason.
- **Seams must be earned.** No single-implementation interfaces, forwarding wrappers, or "strategy/registry" for two cases.
- **Let patterns prove themselves.** Only extract a shared helper when there are ≥3 real copies or the copies have already drifted. Two short identical lines are fine.
- **Strict types.** No `object` / `Dictionary<string, object>` as a value carrier where a type is known. `sealed` concrete types, `record` for data.
- **No self-narrating comments.** Comments explain *why*. Delete tracker/plan tags (`WP-1.1`, `P2 wave 5`, `FABLE_*`, `GAP-2`, `planner-constraint:`) and comments that restate code.
- **Fix root causes before shims.** Don't add a guard at every caller when one owner can enforce the rule.
- **Tests test behaviour.** Don't reach into private fields by reflection; don't keep test-only overloads or hooks in production code.

## 3. Ground rules for every WP

1. **GitNexus (mandatory, from `CLAUDE.md`).** Before editing any function/class/method: `impact({target, direction: "upstream"})`; report direct callers, affected processes and risk. If HIGH/CRITICAL, stop and report before editing. Before committing: `detect_changes({scope: "compare", base_ref: "main"})`. Never rename with find-and-replace — use `rename`. If the index is stale: `node .gitnexus/run.cjs analyze` (or `task analyze`).
2. **Red first for bugs.** Every item in §5 gets a failing regression test before the fix.
3. **Behaviour-preserving for cleanups.** For non-bug WPs, existing tests must pass unchanged, except tests that only exist to pin removed test-only surface (delete those, listing them in the ledger).
4. **Byte-stable output.** Emitted OpenAPI and imported C# must not change unless the WP explicitly says so. Use the golden-baseline check (§3.1).
5. **Stay in your lane.** Only edit files your WP owns (§6). If you need a change in another lane's file, record it in the ledger as a follow-up; don't make it.
6. **Grep to prove dead.** Before deleting a symbol, grep the whole repo (`Rivet.*`, `samples/`, `tools/`, `docs/`) including string references (the Tool reads builder method names as strings). Record the grep result in the ledger.
7. **Gate.** Before marking a WP done: `task build`, `task test`, `task format:check`. Run `task samples:build` if the WP touches `Rivet.Attributes` or emission. Run `task test:functions` only for WPs touching coverage/Functions routing.
8. **Meridian check.** After changes: `~/Sites/plumb/plumb . --json`. Fix errors; fix warns or record the exception. There are two known pre-existing MER-RV-003 warnings in `samples/ImportDemo/Endpoints/MembersEndpoints.cs`; the rule is obsolete.
9. **No commits unless asked.** Leave the work in the tree and update the ledger (§8).

### 3.1 Golden baseline (run once before any WP starts)

```bash
dotnet build Rivet.Tool -c Release
BASE=/tmp/rivet-golden/before; mkdir -p $BASE/emit $BASE/import $BASE/from
TOOL="dotnet Rivet.Tool/bin/Release/net9.0/Rivet.Tool.dll"
for p in samples/*/*.csproj; do n=$(basename $(dirname $p)); $TOOL --project $p --output $BASE/emit/$n -q; done
for f in openapi/*.json; do n=$(basename $f .json); $TOOL --from-openapi $f --namespace G --output $BASE/import/$n -q; done
$TOOL --from Rivet.Tests/Fixtures/contract-sample.json --output $BASE/from/sample -q
# --security variants (added for WP-04)
for n in ContractApi AnnotationApi; do
  $TOOL --project samples/$n/$n.csproj --output $BASE/sec/$n-bearer -q --security bearer
  $TOOL --project samples/$n/$n.csproj --output $BASE/sec/$n-multi -q --security a=bearer --security b=apikey:header:X-Key
  $TOOL --project samples/$n/$n.csproj --output $BASE/sec/$n-multi2 -q --security bearer=bearer --security b=apikey:header:X-Key
done
```

A runnable version that also logs stderr and exit codes lives at `/tmp/rivet-golden.sh <outdir>` (Wave 0); diff the logs as well as the trees. Baseline failures (both expected, both RIV2002): plain `--project samples/ContractApi` (its `.Secure("bearer")` needs `--security`), and `ContractApi-multi` (`a=bearer` does not define `bearer`). `task format` runs the `dotnet format` analyzer fixes first and CSharpier last, so `task format && task format:check` is stable (Lane E; `format:verbose` was renamed `format:check`).

After a WP, re-run it into `/tmp/rivet-golden/after` and `diff -r before after`. Any difference must be explained in the ledger and either be intended by the WP or be a bug. Some corpus specs may already fail import; record failures in the baseline too, so "fails the same way" counts as stable.

---

## 4. Decisions (made 2026-09-29, all final)

| ID | Decision | Affects |
|---|---|---|
| G1 | **Do** replace the emitter's `Dictionary<string, object>` trees with `JsonNode`, as the last Emit WP, gated on a byte-identical golden diff. | A5 |
| G2 | **Remove now** the 18 five-parameter `File` overloads and their reflection pins. No "remove later". | D1 |
| G3 | **Remove now** the `Define` implicit conversions that return `default!`, plus the Tool's acceptance of `Define`-typed fields. No `[Obsolete]` step. | W2-5 |
| G4 | **Delete** `tools/roundtrip-audit.py` and its test. | E3 |
| G5 | **Do** move exclusive min/max and item counts to DataAnnotations: delete the old `RivetConstraintsAttribute` properties outright, with no dual-read. | W2-1 |
| G6 | **No** System.CommandLine. Use a raw-string usage block, a data-driven option table, and split `ProjectPath` by mode. | W2-3 |
| G7 | Import of `security: []` should emit `.Anonymous()`. Treat the unreachable `IsAnonymous` path as a bug and fix it (red-first); don't delete it. | WP-03 |
| G8 | Delete the test-only public getters on the route builders now. | D2 |
| G9 | Delete the 22-argument compatibility constructor on `RivetGeneratedSchemaMetadataAttribute`, and the `TypeWalker` `Optional*At` tolerance for short argument lists. | W2-5 |

---

## 5. Bugs found by the hunt (fix first, red-first)

| ID | Bug | Evidence | Owner WP |
|---|---|---|---|
| BUG-1 | **Verified.** A bad form content type crashes the CLI with a stack trace. | `Emit/OpenApiEmitter.cs:1016` throws `ContractAnalysisException`. `Emit/EmitPipeline.cs:78` only catches `OpenApiEmissionException`/`SecurityConfigurationException`, and `Program.cs:206-218` calls the pipeline outside any `try`. | WP-01 |
| BUG-2 | User-facing refusals throw `InvalidOperationException`, which nothing catches. | `Analysis/TypeWalker.cs:216,244,257,273,363,1202`, `Analysis/ContractWalker.cs:863` | WP-01 |
| BUG-3 | The import inline-schema fingerprint ignores `oneOf`/`anyOf`/`allOf`/`const`/`not`, doesn't escape keys, and truncates past depth 10, so distinct inline schemas can share a synthetic record. From code reading; not yet reproduced. | `Import/SchemaClassifier.cs:682-819`, consumed at `Import/SchemaMapper.cs:2069` | C3 |
| BUG-4 | Type walkers have drifted: `CollectGenericsFromType` and `AssignComponentNames.Walk` have no `Union` case. `CollectGenericInstances` skips `ReturnType`, response `Contents`/`Headers` and `RequestContents`. | `Emit/OpenApiEmitter.cs:2762,3423,3475` | A1 |
| BUG-5 | `InlineTypeExtractor.ReplaceInType` rebuilds a `Brand` without its `Description`. | `Emit/InlineTypeExtractor.cs:636` | A1 |
| BUG-6 | `RoslynExtensions.GetAllTypes` never visits nested types, so a nested `[RivetContract]`/`[RivetEndpoint]` is silently skipped. | `Analysis/RoslynExtensions.cs` | B5 |
| BUG-7 | Field optionality ignores C# 11 `required` fields; the doc comment wrongly says `required` is property-only. | `Analysis/TypeWalker.cs:2511` vs `:2570` | B5 |
| BUG-8 | The contract chain walker accepts chains that don't start at `Rivet.Define` and interprets any same-named method. | `Analysis/ContractWalker.cs:190-1000` | B2 |
| BUG-9 | `BuildMonomorphisedSchema` skips `EnrichScalarSchema` and `x-rivet-empty-record`, unlike `BuildObjectSchema`. | `Emit/OpenApiEmitter.cs:3374-3418` vs `3241-3291` | A4 |
| BUG-10 | JSON-pointer handling differs across copies: `SchemaMapper.DecodeComponentId` doesn't percent-decode; `ResolvePointer` percent-decodes before splitting (so `%2F` becomes a separator); the provenance resolver doesn't handle array indices. The example `$ref` at `OpenApiEmitter.cs:2225` is built unescaped. | see WP-02 | WP-02 |
| BUG-11 | `ValidatePayload` reads `[JsonPolymorphic]`/`[JsonDerivedType]` by reflection, so it misses polymorphism configured through a resolver. | `Rivet.Attributes/EndpointRuntime.cs:815` | D4 |
| BUG-13 | Importing an operation with `security: []` never emits `.Anonymous()`. `ContractBuilder.ResolveSecurity` (`:2019-2034`) always returns `IsAnonymous = false`, so the `CSharpWriter.cs:1121` branch can't run, and the round trip loses "explicitly anonymous" (G7). | `Import/ContractBuilder.cs`, `Import/CSharpWriter.cs` | WP-03 |
| BUG-12 | Python pointer handling drifts: `roundtrip-diff.py:182` percent-decodes and `roundtrip-inventory.py:253` doesn't. `component_counts` counts different namespaces in audit vs inventory. | `tools/` | E4 |

---

## 6. Work packages

### Execution waves and lanes

- **Wave 0 (serial, one worker):** WP-01 → WP-02 → WP-03 → WP-04. These touch files across lanes, so they must land before the lanes fork.
- **Wave 1 (parallel, one worker per lane; lanes own disjoint files):**
  - **Lane A — Emit:** owns `Rivet.Tool/Emit/**`, `Rivet.Tool/Model/**`. Order: A3 → A1 → A4 → A2 → A5.
  - **Lane B — Analysis:** owns `Rivet.Tool/Analysis/**`. Order: B1 → B5 → B3 → B2 → B4 → B6.
  - **Lane C — Import:** owns `Rivet.Tool/Import/**`. Order: C1 → C3 → C2 → C4 → C5 → C6.
  - **Lane D — Runtime:** owns `Rivet.Attributes/**`, `Rivet.RuntimeTests/**`. Order: D3 → D1 → D2 → D4.
  - **Lane E — Test infra and tools:** owns `tools/**`, `Taskfile.yml`, plus the test-infrastructure files listed in E1/E2. Order: E1 → E2 → E4 → E3.
- Lanes may add *new* tests to the behaviour test file for their area (e.g. Lane C → `OpenApiImporterTests.cs`). Lane E must not restructure those files in Wave 1.
- **Wave 2 (serial, on `slop/integration` after all lanes merged):** W2-1 (G5), W2-2 (test-file folding), W2-3 (CLI, G6), W2-5 (cross-lane API removals, G3/G9), W2-6 (Wave 1 follow-ups), W2-4 (final sweep, last).

Each WP lists: **Tag · Est. LOC saved · Depends · Owns**, then Problem, Change, Acceptance.

---

### Wave 0

#### WP-01 — One user-error exception and one CLI boundary (fixes BUG-1, BUG-2)
NEW · ~55 LOC · no dependencies · Owns: `Program.cs`, `Emit/EmitPipeline.cs`, the four exception type files, the listed throw sites.

**Problem.** There are four exception types that differ only by name: `ContractAnalysisException`, `OpenApiEmissionException`, `SecurityConfigurationException` and `EndpointMerger.TransportConflictException`. Each is caught separately in 7 identical `catch { Console.Error.WriteLine(msg); return 1; }` blocks (`Program.cs:60,85,95,110,178,200`; `EmitPipeline.cs:78`). This split is why BUG-1 and BUG-2 escape.

**Change.**
- Introduce one `sealed class RivetUserException : Exception` (or keep `ContractAnalysisException` as that one type and delete the other three).
- Throw it at every user-facing refusal, including the BUG-2 sites.
- Catch it once around the top-level `Run` (print the message, exit 1).
- Delete the per-mode catch blocks.

**Acceptance.**
- A red test for BUG-1: a form endpoint with a non-form `Consumes` exits 1 with a message and no stack trace.
- Red tests for one BUG-2 site in `TypeWalker` and for `ContractWalker:863`.
- `grep -rn "catch (" Rivet.Tool/Program.cs` shows one user-error catch.
- The existing CLI tests (`CliPipelineTests`) pass.

#### WP-02 — Shared JSON-pointer helper (fixes BUG-10)
NEW · ~60 LOC · Depends: WP-01 · Owns: a new `Rivet.Tool/JsonPointer.cs`, plus the call sites listed below.

**Problem.** RFC 6901 escape/unescape is hand-written about 11 times, and the copies behave differently.
- Unescape: `Emit/OpenApiEmitter.cs:433,456,516`; `Import/OpenApiImporter.cs:261,1102,1127`; `Import/OpenApiProvenanceReader.cs:530,803,998`; `Import/SchemaMapper.cs:1607`.
- Escape: `OpenApiEmitter.cs:2370`, `OpenApiProvenanceReader.cs:711-713`.

**Change.**
- Add one `internal static class JsonPointer` with `Escape(string)`, `Unescape(string)`, `FromUriFragment(string)` (percent-decode the fragment *before* splitting, per RFC 6901 §6; a literal `/` in a name must be `~1`), `TryResolve(JsonNode, string, out JsonNode?)`, `TryResolve(JsonElement, string, out JsonElement)` (array indices supported) and `TryGetComponentName(string ref, string kind, out string)`.
- Replace every copy with it.
- Escape the example `$ref` at `OpenApiEmitter.cs:2225`.
- The BCL has no JSON Pointer API, so hand-rolling is justified here. JsonPointer.Net is the library option, but not worth a dependency for ~40 lines.

**Acceptance.**
- Unit tests for `~0`/`~1`, a percent-encoded `/` in a component name, and array-index resolution.
- A round-trip test with a component whose name contains `/` and `~`.
- Golden diff clean, except any spec where the old behaviour was wrong (record it).

#### WP-03 — Dead code sweep (fixes BUG-13)
NEW · ~200 LOC · Depends: WP-01 · Owns: only the listed symbols.

Every item was grep-verified at `3fd003a`; re-verify before deleting.

| Symbol | Evidence |
|---|---|
| `TypeWalker.HasErrors` and the `Program.cs:65` branch | Never assigned; only 2 references. |
| `SchemaMapper.HasMappedSchema` (`:83`), `TryAugmentComponentRecord` (`:178-199`), `FindRecordByName` (`:258-266`) | 1 reference each (the definition). |
| The no-op ternary at `ContractBuilder.cs:2025-2027` | Both branches return the same value. |
| `ContractBuilder.ResolveSingleType:1938-1941` | Second `schema.Enum` check can't be reached. |
| `TsType.CollectTypeRefs` (`Model/TsType.cs:217-274`) | Only used by 4 test files. Delete it and its tests, or move it to test helpers if the tests are valuable. |
| `TypeWalker._typeNamespaces` / `TypeNamespaces`, `EmitInput.TypeNamespaces`, `EmitInput.Brands`, `ExtractionResult.TypeNamespaces` | No production reader (asserted only in `InlineTypeExtractorTests.cs:620,1168`). `EmitInput.Definitions` duplicates `DefinitionsByName.Values`. |
| `InlineTypeExtractor.GenerateName` overloads at `:129-138,154-165` | Only called from tests (19 calls). Point the tests at the 6-argument overload. |
| `CoverageChecker.Check` default-`"api"` overload (`:39`) | Only used by `Rivet.Tests/CompilationHelper.cs:135`. |
| `OpenApiProvenanceWalker.Walk(..., TypeWalker? typeWalker = null)` default and runtime throw (`:11,150`) | Only needed by `ReusableComponentIdentityRoundTripTests.cs:332`. Make the parameter required. |
| `WellKnownTypes.CancellationToken`, `ProblemHttpResult`, `JsonHttpResultOfT`, `StatusCodeHttpResult` | Never read. |
| `ContractWalker.IsRivetEndpointField` `defineType` branch (`:1995`) | `Define` is never a field type. |
| `Rivet.Attributes` `IsFileUpload` getter (`EndpointBuilder.cs:82-101`) | Zero references anywhere. |
| `ContractWalker.cs:880` `"ProducesFile"` alternative | Can never match. |
| `EndpointBuilder.cs:266-269` ternary | `isSuccess && _responseContentType is not null` is always false here, because line 257 already handled it. |
| `SchemaMapper.ResolveAliasTargets` cycle branch (`:1280-1289`, RIV3007) | `BreakAliasCycles` already removed the cycles. Verify no path reaches the mapper without it, then delete (medium confidence). |

**Acceptance.** Each deletion has its grep evidence in the ledger. BUG-13 has a red test: importing an operation with `security: []` compiles to `.Anonymous()`, and re-emitting it gives `security: []`. Build, tests and golden diff are clean, except for the BUG-13 change, which is recorded.

#### WP-04 — Security pipeline simplification
NEW · ~80 LOC · Depends: WP-01 · Owns: `Emit/EmitPipeline.cs`, `SecurityConfig.cs`/`SecurityParser`, `OpenApiEmitter.ToSecurityMetadata` (`:536-564`), and `RivetOptions.DefaultSecurity` together with its `CliParser.cs:161,192,217` writers.

**Problem.**
- `EmitPipeline.cs:47-76` picks between three emit calls, but `Emit(..., null)` is the same as `EmitWithSecurityMetadata(null)`.
- `SecurityParser.Parse(options.DefaultSecurity)` is dead, because `DefaultSecurity` is always `SecuritySchemes.FirstOrDefault()`.
- RIV2011 (duplicate scheme names) is checked 3 times (`SecurityConfig.cs:37`, `OpenApiEmitter.cs:551`, `:277`). The third can't fire because its keys already come from a dictionary; its only test (`OpenApiEmitterTests.cs:780`) builds an impossible `SecurityConfig` by hand.

**Change.**
- `SecurityParser.ParseMany` returns `ContractSecurityMetadata` directly.
- The pipeline makes one emit call.
- Delete `SecurityConfig`, the extra `Emit` overload, `ToSecurityMetadata` and `DefaultSecurity`.
- Keep RIV2011 at the parser only, and delete the impossible-state test.

**Acceptance.** Security CLI tests pass. Golden diff is clean for `--security` variants: add baseline runs with `--security bearer` and `--security a=bearer --security b=apikey:header:X-Key` to §3.1 before starting.

---

### Lane A — Emit (`Rivet.Tool/Emit/**`, `Rivet.Tool/Model/**`)

#### A3 — Replace the thread-static emit context with an instance
ALREADY-COVERED (review A4) · ~30 LOC · Depends: Wave 0

**Problem.** `[ThreadStatic] private static EmitContext? _ctx` (`OpenApiEmitter.cs:54-55`) is consulted with `_ctx?.`/`_ctx is null` at `157,1859,2302,2306,2326,2337,2529,2549,2735`; the check at 157 is always true. Definitions, brands and enums are passed as parameters *and* stored in `_ctx`.

**Change.**
- Make `OpenApiEmitter` a `sealed class` with the context as instance state. Keep a static `Emit(...)` entry point that creates an instance.
- Remove the duplicated parameters and the null checks.

**Acceptance.** Golden diff clean. `grep ThreadStatic Rivet.Tool` returns nothing.

#### A1 — One traversal over `TsType` and one over endpoint types (fixes BUG-4, BUG-5)
NEW · ~250 LOC · Depends: A3

**Problem.** There are nine hand-written recursive walks over `TsType`:
- `TsType.ResolveTypeParams`
- `InlineTypeExtractor.CollectFromType`, `CollectArrayElements`, `ReplaceInType`
- `OpenApiEmitter.AssignComponentNames.Walk` (`:2762`), `CollectGenericsFromType` (`:3475`)
- `JsonContractReader.WalkForBrands` (`:143`)

There are also six "every type on an endpoint" loops: `InlineTypeExtractor.cs:72,528,659`, `OpenApiEmitter.cs:2809,3423`, `JsonContractReader.cs:73`. They have drifted apart (BUG-4, BUG-5).

**Change.**
- Add `TsType.Children()` and `TsType.Descendants()` (pre-order, covering every case including `Union`, `Brand`, generics and dictionaries).
- Add one rewriter `TsType.Rewrite(Func<TsType, TsType?>)` built on `with`, so it preserves all record members, including `Description`.
- Add `TsEndpointDefinition.AllTypes()` covering params, request body, `RequestContents`, every response (`DataType`, `Contents`, `Headers`) and `ReturnType`.
- Port each walker to these.

**Acceptance.**
- Red tests for BUG-4 (a generic used only inside a union, and a generic used only in a response header or content map, each gets its component) and BUG-5 (a brand description survives inline extraction).
- Golden diff clean apart from the intended fixes.

#### A4 — De-duplicate `BuildOperation` and schema builders (fixes BUG-9)
NEW · ~165 LOC · Depends: A1

**Problem.**
- Inside `BuildOperation` (`OpenApiEmitter.cs:818-1535`):
  - The `requestBody = {required, content: WithExamples({ct: {schema}})}` literal appears 7 times (`1050,1061,1156,1196,1216,1237,1255`).
  - The form-field properties + `required` list is built twice (`1104-1135`, `1173-1194`).
  - The Query/Header/Cookie parameter cases are identical apart from the location string (`896-940`).
  - The media-content loop appears 3 times (`1027-1047,1409-1434,2023-2049`).
  - The response header object (`1339-1401`) re-implements `BuildParameter` (`1678-1739`); in OpenAPI a Header Object is a Parameter without `name`/`in`.
- `BuildMonomorphisedSchema` (`3374-3418`) copies `BuildObjectSchema` (`3241-3291`) and has drifted (BUG-9).
- The type-parameter map is built three ways (`1972`, `2996-3004`, `3051-3059`).

**Change.**
- Add local helpers `RequestBody(ct, schema, required)`, `FormSchema(fields)` and `MediaContent(...)`.
- Collapse the three parameter cases into one keyed on location, and build the header object from `BuildParameter` minus `name`/`in`.
- Replace `BuildMonomorphisedSchema` with `BuildObjectSchema(props.Select(p => p with { Type = resolved }), typeName)`, and build the type-parameter map once with `TypeParameters.Zip(TypeArguments).ToDictionary()`.
- Also remove the forwarding shells:
  - `UpperFirst` (`:3580`) just calls `Naming.ToPascalCase`.
  - The private `ResolveTypeParams` (`:3420`) just calls `TsType.ResolveTypeParams`.
  - The caller at `:1153` re-applies `ep.InputTypeName ??`, which `SynthesizedInputTypeName` already applies.
  - `TryBuildRouteFilteredBodySchema`'s `out inputTypeName` is discarded, and the body properties are resolved twice (`1806`, `1848`/`1853→1900`).

**Acceptance.** Red test for BUG-9 (a generic instance with a scalar-metadata property carries its enrichment). Golden diff clean otherwise.

#### A2 — Drop the contract-JSON mirror records and the hand-written `TsType` converter
NEW (related to review A3) · ~400–500 LOC · Depends: A1

**Problem.**
- In `Emit/ContractEmitter.cs` (333 LOC), `ContractEndpoint`, `ContractResponseType`, `ContractEndpointExample`, `ContractTypeDefinition` and `ContractQueryAuth` copy `TsEndpointDefinition`, `TsResponseType`, `TsEndpointExample`, `TsTypeDefinition` and `QueryAuthMetadata` field for field, including `[JsonIgnore]`s.
- `ContractEmitter.Emit` and the `ToContract*` mappers are only called from 14 test files.
- `JsonContractReader.cs:209-306` maps back with 4 more mappers.
- `Model/TsTypeJsonConverter.cs` (433 LOC) hand-writes polymorphic read/write, with `GetRawText()` → `Deserialize` round trips at `13,52,100,120,149,161,190,199`.
- Integer-enum literal parsing is written 3 times: `TsTypeJsonConverter.cs:172-186,307-322`, `ContractEnumIntValuesConverter` (`ContractEmitter.cs:254-332`) and `OpenApiEmitter.ParseEnumLiteral` (`2406-2433`).

**Constraint.** Contract JSON is the `--from` input format (`rivet-contract-schema.json`), and rivet-ts produces it (§1). **Keep the wire shape exactly.** This WP is an internal refactor; verify with the fixture IR snapshot and the golden diff.

**Change.**
- Deserialize `RivetContract(IReadOnlyList<TsTypeDefinition>, IReadOnlyList<ContractEnum>, IReadOnlyList<TsEndpointDefinition>)` directly.
- On `TsType`, use `[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]`, `[JsonDerivedType(..., "primitive")]` for each case, `[property: JsonPropertyName]` for the renamed members, and `JsonSerializerOptions.AllowOutOfOrderMetadataProperties = true` (.NET 9; the Tool targets net9.0).
- Keep a small property-level converter for the "`optional` defaults to `type is Nullable`" rule (`TsTypeJsonConverter.cs:124`), and a post-deserialize fix-up for status code 0 vs null.
- Keep one integer-enum literal parser.
- Move `ContractEmitter.Emit` (the writer) into `Rivet.Tests` test helpers.

**Acceptance.**
- Every file under `Rivet.Tests/Fixtures/*.json` that is contract JSON reads to an identical IR (snapshot the IR before and after).
- `rivet-contract-schema.json` still validates the fixtures.
- `JsonContractReaderTests` pass.
- The `--from` golden diff is clean.

#### A5 — `JsonNode` for the emitted document (G1)
ALREADY-COVERED (deliberately deferred in post-v0.42 M1) · ~300–450 LOC · Depends: A4, A2

**Problem.** `Dictionary<string, object>` appears 186 times in `OpenApiEmitter.cs` and twice in `SchemaEnricher.cs`. This forces casts (`710,1178,1284,1549,2175`), dual `JsonElement` branches in `CollectSchemaReferences` (`371-417`), `Deserialize<object>` (`2243`) and string-compare equality in `SchemasEqual` (`2938`).

**Change.**
- Use `JsonObject`/`JsonArray`/`JsonValue`, `JsonNode.Parse`, `JsonNode.DeepEquals` and `DeepClone`.
- Rewrite `SchemaEnricher.EnrichConstraints` (`76-138`, 60 lines of `if (x.HasValue)`) as `JsonSerializer.SerializeToNode(constraints, camelCase + WhenWritingNull)` merged into the schema. Handle `UniqueItems`-only-when-true explicitly.
- Unify the invalid-JSON behaviour for `default`/`example`: `SchemaEnricher.cs:29-37` falls back to a raw string, while `EnrichScalarSchema:3179` and `BuildParameter:1694` throw. Pick "throw a user error" and record it.
- Do **not** switch to Microsoft.OpenApi's writer; its key ordering would break `--verify` byte stability.

**Acceptance.** Golden diff byte-identical across all samples and corpus.

---

### Lane B — Analysis (`Rivet.Tool/Analysis/**`)

#### B1 — Identify attributes by symbol, and a leaner `WellKnownTypes`
NEW · ~190 LOC · Depends: Wave 0

**Problem.**
- About 35 sites identify attributes by `ToDisplayString()` or bare `.Name`:
  - `EndpointWalker.cs:135,143`
  - `SecurityMetadataWalker.cs:19`
  - `OpenApiProvenanceWalker.cs:565`
  - `TypeWalker.cs:204,650,966-1024,1156,1172,1193,1307,1707,1767,1882,1916,1954,2155,2168,2357,2477,2497,2515,2520,2574,2579`
  - `ContractWalker.cs:880`
- `"RequiredAttribute"` by name matches any type with that name. `TypeWalker.cs:650` looks up `JsonIgnore` by name although `_jsonIgnoreType` is already resolved.
- In `WellKnownTypes.cs` (365 LOC), 7 `Http*` fields and 16 typed-result fields are only read in its constructor to build `HttpMethodAttributes`/`TypedResultStatusCodes`.

**Change.**
- Resolve every Rivet, System.Text.Json and DataAnnotations attribute once with `Compilation.GetTypeByMetadataName` and compare with `SymbolEqualityComparer.Default`.
- Replace the table-only fields with static `(string metadataName, T value)` arrays folded into the dictionaries.
- Also replace `_collectionTypes` (`TypeWalker.cs:155-163`) with `SpecialType` checks for the five interfaces that have one. Do **not** widen to "anything implementing `IEnumerable<T>`"; that would change behaviour.

**Acceptance.** A test where a user type named `RequiredAttribute` in another namespace is *not* treated as required. Golden diff clean.

#### B5 — `TypeWalker` helper consolidation (fixes BUG-6, BUG-7)
NEW · ~180 LOC · Depends: B1

**Change.**
- One `GetJsonMemberName(ISymbol)`, replacing `GetJsonPropertyName:759`, `GetJsonFieldName:795` and the inline copy at `915-935`.
- Delete the `IsJsonIgnored` forwarder (`:379`).
- Merge `HasJsonInclude` (`:517`) with the field include check (`:535-543`).
- One `IsOptional(ISymbol)` that switches on `IPropertySymbol.IsRequired`/`IFieldSymbol.IsRequired` (BUG-7).
- One `TsType.WithLeaf(schemaType, format)`, replacing `ContractWalker.ApplyParameterMetadata:1727`, `OpenApiProvenanceWalker.ApplySchemaLeafMetadata:495` (byte-identical), `TypeWalker.WalkType:1046-1100`, `MapPropertyType:1300` and `ApplyTypeFormat:1988`.
- One `GetRequestProperties(type)` for the filter `!IsJsonIgnored && surface != ResponseOnly && header is null`, which is repeated at `ContractWalker.cs:1430-1455,1540,1600-1620,1880-1910`.
- `GetAllTypes` descends into nested types (BUG-6).
- Ceremony to remove:
  - the stacked orphan `<summary>` at `:1322-1331`
  - the duplicate enum `[RivetFormat]` read at `:1762-1769`
  - the `ulong` special case in `EnumLiteral`
  - the type test at `:558`
  - the re-check in `IsRecordPrimaryConstructor`
  - the `EqualityContract` name guard
  - the dead positional branch in `ReadJsonIgnoreCondition`

**Acceptance.** Red tests for BUG-6 (a nested contract class is discovered) and BUG-7 (a `required` field is required in the schema). Golden diff clean.

#### B3 — One controller-endpoint builder
NEW · ~150 LOC · Depends: B5

**Problem.**
- `EndpointWalker.BuildEndpoint` (`:65-178`) and `ContractWalker.BuildEndpointFromMethod` (`:103-188`) are about 85% identical. `CoverageChecker.TryResolveController` (`:549-590`) is a third copy of the route composition.
- Both builders contain `successResponse?.DataType ?? (responses.Count == 0 ? ExtractReturnType(...) : null)` right after adding a 200 response, so the count can never be 0.
- `ExtractReturnType` (`EndpointWalker.cs:1180-1235`) reduces to `typeWalker.MapType(unwrapped)`.
- Also:
  - The example helpers are duplicated: `DefaultRequestExampleMediaType` (`EndpointWalker.cs:367` / `ContractWalker.cs:1135`), `ToEndpointExample` (`:437` / `:1162`), `ApplyResponseExamples` (`:455` / `:1177`).
  - The "Controller" suffix strip appears twice (`EndpointWalker.cs:529,612`).
  - `ExtractParams` re-checks FromServices/CancellationToken after `ClassifyParam` has already returned null for them.

**Change.** One `ResolveActionRoute(wkt, method)` and one builder parameterised by controller name and mode. Delete the dead fallback.

**Acceptance.** `ControllerEndpointTests`, `ContractEndpointTests` and `CoverageCheckerTests` pass. Golden diff clean.

#### B2 — Interpret the contract chain through `IOperation` (fixes BUG-8)
NEW · ~250–300 LOC · Depends: B3

**Problem.**
- `ContractWalker.cs:190-1000` has 41 `if (call.MethodName == "...")` branches.
- `ChainedCall`/`CollectInvocationsRecursive`/`ResolveParameter` (`:2032-2198`) map arguments to parameters by hand from syntax, missing default-valued and `params` arguments, and carry them in an `IReadOnlyDictionary<string, object>`. `StringArg` takes the first string value in dictionary order.
- The HTTP verb comes from `root.MethodName.ToUpperInvariant()`.

**Change.**
- Use `semanticModel.GetOperation(initializer)`, walk `IInvocationOperation.Instance` (unwrapping `IConversionOperation`), and require the root to be a `Rivet.Define` member.
- Dispatch on `TargetMethod.OriginalDefinition` compared by symbol against the resolved `RouteDefinitionBase<TSelf>` members.
- Read values from `Arguments[i].Parameter` and `.Value.ConstantValue`.
- Collapse the paired statusCode/statusKey branches (Returns×4, ResponseContent×4, ResponseExample×4, RequestExample×2) into one status-key normaliser, as `WithResponseHeader` already does.
- Also fold `GetRequestBodyType` (`:1796`) and `GetRequestBodyRequired` (`:1840`) into one attribute read, resolved once.

**Acceptance.**
- Red test for BUG-8: a user extension method named `Returns` in the chain, and a chain not rooted at `Define`, are rejected or ignored as designed.
- Named-argument and default-argument cases are covered.
- `ContractEndpointTests` pass. Golden diff clean.

#### B4 — Coverage uses `IOperation` and reuses discovery
NEW · ~190 LOC · Depends: B2

**Problem.**
- `CoverageChecker.BuildContractFieldMap` (`:126-168`) re-walks all types for `[RivetContract]`, although `SymbolDiscovery.ContractTypes` already has them. It then re-matches on `(ControllerName, Name)`.
- It hand-rolls data flow over syntax (`:305-395,850-1110`): `TryGetProvenanceValue`, `Unwrap`, `IsSameExpression`, `IsReturnedExpression`, `GetContainingFunction`, and a positional `Arguments[1]` read at `:650`.
- `NormalizeRoute` (`:1128`) is identical to `TransportIdentity.NormalizeRoute` (whose doc comment says so). Routes are stripped at `:588` and again in `RoutesMatch`.
- `TypedConstant` array flattening appears 4 times: `CoverageChecker.ExtractStrings:1060`, `EndpointWalker.cs:147-153`, `:290-310` and `SecurityMetadataWalker.ReadStrings`.

**Change.**
- `ContractWalker` returns each endpoint together with its `IFieldSymbol`.
- Use `IReturnOperation.ReturnedValue`, `ILocalReferenceOperation`, `ISwitchExpressionOperation.Arms`, `IConditionalOperation` and `IArgumentOperation.Parameter`.
- Keep one `NormalizeRoute` and one `TypedConstant` flatten extension.

**Acceptance.** `CoverageCheckerTests`, `FunctionsPrefixTests` and `task test:functions` pass.

#### B6 — `EndpointMerger` and small Analysis WET
NEW · ~100 LOC · Depends: B4

**Change.**
- One `ListEquivalentBy<T>(left, right, key)` replacing `ContentEquivalent` (`:291`), `RequestContentsEquivalent` and `HeadersEquivalent`.
- Inline `FileContentTypeEquivalent`.
- `ParamKey` compares a tuple directly.
- One security-requirement accumulator shared by `SecurityMetadataWalker.cs:13-100` and `ContractWalker.cs:281-283,540-570,1034-1046`.
- Use one `DefaultSuccessCode` (`ContractWalker.cs:1111`) from `ResponseStatusValidation.cs:89-93`, and call it once per method.
- Delete the forwarding `NormalizeIrAndEnsureResponse` overload.
- `BuildParams` (`:1287`) takes required parameters, not 4 optional ones.
- Drop the redundant `explicitSources.Count > 1 &&` condition.

**Acceptance.** Tests and golden diff clean.

---

### Lane C — Import (`Rivet.Tool/Import/**`)

#### C1 — Parse the spec once
ALREADY-COVERED (review "import allocation pressure" hypothesis) · ~40 LOC · Depends: Wave 0

**Problem.** `OpenApiImporter.Import` (`:41-74`) parses and re-serialises the whole spec separately for each of these passes, then `OpenApiDocument.Parse` parses it again:
- `BreakAliasCycles`
- `NormalizeMappedVendorExtensions`
- `OpenApiProvenanceReader.Read`
- `NormalizeLocalPathReferences`
- `NormalizeSchemaReferenceMetadataSiblings`
- `ReadSwaggerSchemaLessResponses`
- `EscapeLiteralSentinels`
- `ReadSecurityMetadata`

**Change.**
- Parse once into a `JsonNode`, run each pass in place (or as a read-only view) and serialise once.
- `NormalizeLocalPathReferences`' read-only predicate tree (`RequiresNormalization` and friends, `:712-793`) mirrors the mutating walk (`:795-990`). Drop it and use a `changed` flag or `JsonNode.DeepEquals`.
- `IsOpaqueOpenApiValue` is duplicated verbatim (`OpenApiImporter.cs:388`, `OpenApiProvenanceReader.cs:634`); keep one.
- Also replace `JsonNode.Parse(x.ToJsonString())` with `x.DeepClone()` (`ContractBuilder.cs:1101`) and cache the `JsonSerializerOptions` at `:1109` (CA1869).

**Acceptance.** Golden import diff byte-identical.

#### C3 — Fingerprint from canonical serialisation (fixes BUG-3)
NEW · ~120 LOC · Depends: C1

**Change.** Replace `SchemaClassifier.ComputeSchemaFingerprint` with the schema's canonical serialisation: `SerializeAsV31` into a string writer, with `$ref`s kept as references. Note: `SchemaClassifier.AllOfYieldsProperties` (`:225-268`) is a self-described "static mirror" of `ResolveAllOfRecord`, and the depth limits differ (25/50/10). Fold it into C4 if it becomes simpler.

**Acceptance.** A red test with two inline objects that differ only in a nested `oneOf` gets two distinct records. Golden diff: record every synthetic-name change (expected only where collisions existed).

#### C2 — Import security from the typed document
NEW · ~110 LOC · Depends: C1

**Problem.** `OpenApiImporter.cs:457-605` re-reads `securitySchemes`/`securityDefinitions`/`security` from raw JSON, while `ContractBuilder.ResolveSecurity` (`:2018-2057`) builds the same `SecurityRequirements` from the typed `operation.Security`.

**Change.** Map `doc.Components.SecuritySchemes` and `doc.Security` (typed `OpenApiSecurityScheme`; Microsoft.OpenApi already converts Swagger 2 flows), and reuse the operation mapper. **First verify** that Microsoft.OpenApi 2.7.5 preserves `mutualTLS` and any other scheme types the raw reader handles. If not, keep a raw read for exactly those types and record why.

**Acceptance.** Security import tests pass. Golden diff clean for the corpus specs that have security.

#### C4 — Import WET, dead code and value-carrier cleanup
NEW · ~250 LOC · Depends: C2, C3

**Change.**
- **One `UniqueName(string base, ISet<string> used)`** for the 6 suffix loops:
  - `SchemaMapper.cs:341-350,371-379` (`Foo_2`)
  - `ResolutionContext.ResolveSyntheticName` (`Foo2`)
  - `SchemaClassifier.DeduplicateProperties` and `MapIntEnum:615-625` (`_n`)
  - `MapEnum:542` and `ContractBuilder.DeduplicateFields:2059` (a count dictionary with no used-set, so an authored `X_2` can collide)
  - Pick one scheme. If this changes generated names, record it in the golden diff; prefer the scheme that most corpus output already uses.
- **Remaining duplication:**
  - `ResolveFormat` and `ResolveSchemaType` (`SchemaMapper.cs:1094-1204`) → one `Unwrap(IOpenApiSchema)` plus `TryResolveComponent(id, out schema)`.
  - The `JsonSchemaType` → keyword switch (`SchemaMapper.cs:747,1146`, `ContractBuilder.cs:541`) → keep one.
  - The HTTP method list (`OpenApiImporter._operationNames:22`, `OpenApiProvenanceReader._methods:26`, `ContractBuilder._supportedMethods:18`) and the duplicated `x-rivet-imported-*-reference` constants (`OpenApiImporter.cs:17-20`, `ContractBuilder.cs:14-17`) → one set of constants.
  - `GetExtensionString` ×3 (`ContractBuilder.cs:2152,2167`, `SchemaClassifier.cs:390`) → keep one.
  - `SanitizeName` ×2 (`SchemaMapper.cs:1988`, `RecordSynthesizer.cs:432`) → keep one.
  - `SynthesizeInlineEnum`/`SynthesizeInlineIntEnum` (`SchemaMapper.cs:2036-2064`) → merge them.
- **Model cleanup:**
  - Derive `IsSynthetic` as `ComponentId is null` on `GeneratedRecord`/`GeneratedEnum`/`GeneratedBrand` (`ImportTypes.cs:42,103,116`), and fold the three stamping loops (`SchemaMapper.cs:668-682`).
  - `ResolutionContext._syntheticsByName: Dictionary<string, object>` (`:12,79-111`) → two typed dictionaries or a generic `ResolveSyntheticName<T>`.
  - `GeneratedResponseMediaTypeContent` composes `GeneratedMediaTypeContent`, and the media loops in `ResolveRequestContents`/`ResolveResponseContents` (`ContractBuilder.cs:387-521`) share one body.
  - Derive `StatusCode` from `StatusKey` on the 4 records that carry both.
- **Parallel walks:**
  - Alias-cycle detection runs twice. Keep `BreakAliasCycles` (it prevents the library stack overflow) and reduce `ResolveAliasTargets` to missing-target handling.
  - Example `$ref` walks run twice (`ContractBuilder.CollectReferencedExampleComponents:1745`, `InlineExampleRefNode:1863`) → one walk, using `OpenApiJsonNodeSerializer.Clone(node)` instead of string round-trips.
- **`RecordSynthesizer` seam** (4 `Func` delegates injected "to avoid circular dependency", `SchemaMapper.cs:52`): pass `SchemaMapper` directly or merge the two. There is one consumer.
- **Small items:**
  - The switch at `ContractBuilder.cs:1137-1143` duplicates `ParameterStyleName`.
  - `_binaryContentTypes:1229`: remove the entries already covered by the `image/`, `audio/` and `video/` prefix checks.
  - `IsIntEnum:76-85` has two branches returning the same expression.
  - `WriteEnum:576-633`: make it single-pass.
  - `ResolveConstType:1895` calls `double.TryParse` without `InvariantCulture`.
  - `ContractBuilder.cs:1492` carries a "Mirror of" copy of `ResponseStatusValidation.IsBodyForbiddenStatus`; call the original (same assembly).

**Acceptance.** Import tests pass. Every golden-diff change is a recorded, intended naming consequence.

#### C5 — Literal emission in `CSharpWriter`
NEW · <10 LOC, but a correctness fix · Depends: C4

**Problem.**
- `EscapeString:1451` calls `SymbolDisplay.FormatLiteral(quote: true)`, strips the quotes, and then 74 call sites add them back with `\"{EscapeString(x)}\"`.
- `NullableIntLiteral:571` uses current-culture `ToString()`, and bools are written with `.ToString().ToLowerInvariant()`.

**Change.** Use `SymbolDisplay.FormatLiteral(x, quote: true)` / the existing `StringLiteral`, and `SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false)` for numbers and bools.

**Acceptance.** Run the golden import under `DOTNET_CULTURE=de-DE` (or with `CultureInfo.DefaultThreadCurrentCulture` set in a test); it is byte-identical to invariant.

#### C6 — Comment ceremony in Import
ALREADY-COVERED in principle (post-v0.42 M1, only applied to binding/scalar paths) · 0 LOC net · Depends: C5

**Change.**
- Delete the ~66 tracker-tag comments (27 are in `SchemaMapper`), the orphaned stacked `<summary>` at `SchemaMapper.cs:71-77`, and restating comments (`// Parse schemas`, `// Emit contract files`).
- Remove the fully qualified `System.Text.Json.Nodes` in `BreakAliasCycles` (the namespace is already imported).
- Keep genuine *why* comments.

---

### Lane D — Runtime (`Rivet.Attributes/**`, `Rivet.RuntimeTests/**`)

**Lane D constraint.** `Rivet.Tool` recognises builder and terminal calls **by method name and signature** (`ContractWalker`, `CoverageChecker`). Don't rename any public builder or terminal method, and don't change parameter names used as named arguments. Run `CoverageCheckerTests` and `ContractEndpointTests` as well as the runtime tests. Build all three targets (net8.0/net9.0/net10.0).

#### D3 — Publication gate and route state
ALREADY-COVERED (review "per-response overhead" hypothesis) for the lock cost; the reflection test is NEW · ~45 LOC · Depends: Wave 0

**Problem.**
- The `Monitor.Enter` / `MutationLease` / `Interlocked.Exchange` lease (`EndpointBuilder.cs:69-74,312-340`) allocates on every builder call.
- `_published` duplicates `_publishedContract is not null`.
- `CopyStateTo` (`:114-135`) copies 17 fields by hand, so a new field is silently lost unless it is added there too.
- `TerminalAndAdapterTests.cs:383-420` reflects into the private `_publicationLock` and uses `Monitor.Enter` + `Thread.Sleep(100)`.

**Change.**
- A plain `lock (_gate) { ThrowIfPublished(); … }` helper (`System.Threading.Lock` isn't on net8).
- Hold the state in one private `sealed record RouteState` and copy it with a single assignment.
- Replace the reflection test with a behavioural race: a `Barrier` racing `Summary(...)` against `Success(...)`, asserting either "mutation applied before publication" or "mutation threw".

**Acceptance.** Runtime tests pass on all three TFMs. `grep _publicationLock Rivet.RuntimeTests` returns nothing.

#### D1 — Consolidate the terminal overloads and delete the compatibility overloads (G2)
NEW (the 09-14 fix plan deliberately preserved these overloads) · ~500 LOC · Depends: D3

**Problem.** Six classes each repeat Error×2 and File×6 (byte[]/Stream/path × 5- and 6-parameter forms): `RouteDefinition<T>`, `RouteDefinition`, `FileRouteDefinition` and the three `Bound*` classes (`EndpointBuilder.cs:1129-1241,1272-1382,1410-1518`; `EndpointRuntime.cs:39-392`). Nine "Retained for callers compiled against…" overloads sit in each file; `FileMediaTypeTests.cs:130-150` pins them by reflection.

**Change.**
- Delete the 5-parameter overloads and their reflection pins (`FileMediaTypeTests.cs:130-150`).
- Move the remaining bodies into one abstract bound-terminal base holding a `private protected EndpointContract`, and one `TerminalRouteDefinitionBase<TSelf>` for the three route variants.
- Add a `CHANGELOG.md` breaking-change line.

**Acceptance.** A public-API snapshot (reflect all public members of `Rivet.Attributes` before and after into a sorted text file and diff) shows only the removed overloads and the new base types. Runtime tests, coverage tests and `task samples:build` pass.

#### D2 — Spec-only builder methods and state read only by tests
NEW · ~260 LOC · Depends: D1

**Problem.**
- These methods exist only for the Tool to read by syntax, yet they discard their parameters with `_ = x;` and take the lock: `RequestExampleJson/Ref`, `ResponseExampleJson/Ref`×4, `SecurityRequirement(s)`×3, `RequestContent`×2, `RequestBinaryContent`, `RequestBodyRequired`, `RequestBody`, `Parameter<T>`, and the ignored `description` on `StatusKey` (`EndpointBuilder.cs:389-395,738-841,857-940`).
- The discards exist because `.editorconfig:114-115` makes IDE0060/CA1801 errors.
- These fields are never read at runtime: `_summary`, `_description`, `_anonymous`, `_securityScheme`, `_queryAuthParameterName`, `_requestContentType`, and `_responseHeaders` with its 17-field `RouteResponseHeader` record and duplicate check (`712-736`).
- Their public getters (`:82-101`) are read only by tests (`FileRouteDefinitionTests`, `HeaderSupportTests:246`, `ContractTerminalResultTests:62,165`, `ContractEndpointTests:2441`, `RuntimeTests:214`).
- `protected SuccessStatus` (`:101`) duplicates `SuccessStatusCode` (`:98`).

**Change.**
- Add a scoped `.editorconfig` section (or a targeted `[SuppressMessage]`) for the spec-only methods, and give each an `=> Marker()` body over one lock-and-return helper that still enforces "throws after publication".
- Delete the never-read fields and the test-only getters. Rewrite the affected tests to assert on emitted OpenAPI or on runtime behaviour instead.
- Delete them even if `samples/` or docs use them; update those usages (G8).

**Acceptance.** Public-API snapshot diff shows only the removed getters; record them. Tests and golden diff clean.

#### D4 — Runtime small items (fixes BUG-11)
NEW (BUG-11's performance aspect is ALREADY-COVERED as a hypothesis) · ~120 LOC · Depends: D2

**Change.**
- **BUG-11:** in `ValidatePayload` (`EndpointRuntime.cs:815-818`), use `options.GetTypeInfo(expected).PolymorphismOptions?.DerivedTypes`. This needs the serializer options, so move the check to the point where the adapter has them. The `suppliedType` parameter is always `typeof(T)`; drop it.
- **Media-type checks:**
  - `IsJson` is hand-parsed twice (`EndpointRuntime.cs:847`, `RivetResultExtensions.cs:189`). Use `MediaTypeHeaderValue.TryParse` + `SubTypeWithoutSuffix`/`Suffix` once, and carry the JSON/text decision on `RivetBodyResult`.
  - The media-type dictionaries keyed on the raw string (`:246,575`) should use the parsed media type (without parameters) as the key.
- **Entity tag:** `EndpointRuntime.cs:601-616` throws `FormatException` into its own catch. Use `EntityTagHeaderValue.TryParse(tag, out var t) && !t.Equals(EntityTagHeaderValue.Any)`.
- **Per-request re-derivation:** `CreateFile` (`555-599`) re-filters and re-parses on every call, and `EnsureNotFile` (`708`) and `EnsureErrorIsNotBinary` (`730`) share one predicate. Compute the selected representation / binary set / is-JSON once in `BuildResponse`.
- **Status keys** are classified three times (`GetExactStatus`/`IsRangeStatusKey:536-549`, `AddErrorResponse:487-497`, `Publish:204-208`). `Publish:217` and `Publish:146-151` can't be reached. `RouteErrorResponse` uses `StatusCode = 0` as a sentinel (`471,479`). Classify once in `AddErrorResponse`; make `RouteErrorResponse` internal (no external references) with a proper status-key type.
- **Adapters:** in `RivetResultExtensions.cs:41-187`, `RivetJsonResult` and `RivetMvcJsonResult` are near-copies, and the status code is set 3 times. Use `HttpResponse.WriteAsJsonAsync(value, type, options, contentType, ct)`. Keep separate option resolution for MVC vs Minimal API (they genuinely differ).
- **`RivetUnionJsonConverter<T>`:** make it `internal`; only the attribute constructs it (`RivetUnionAttribute.cs:32`). Cache the `ConstructorInfo` once rather than reflecting on every `Read` (`107-125`).
- **Small items:**
  - Remove the unused constructor defaults `method = "GET"`, `route = ""` (`EndpointBuilder.cs:1109,1126,1252,1269`).
  - Delete `FileRouteDefinition.ContentType`, a pure alias for `ProducesFile`. Update docs, samples and tests to `ProducesFile`.
  - Derive `RivetBodyResult.HasBody` as `ContentType is not null`.
- The G3 (`Define` conversions) and G9 (22-arg constructor) removals are cross-lane, so they live in W2-5, not here.

**Acceptance.**
- Red test for BUG-11: a resolver-configured polymorphic type validates.
- An `application/json; charset=utf-8` declared type matches `application/json` selection as intended.
- Runtime tests pass on all three TFMs.

---

### Lane E — Test infrastructure and tools

Owns: `tools/**`, `Taskfile.yml`, `Rivet.Tests/CliRunner.cs`, `Rivet.Tests/TestInfrastructure.cs`, `Rivet.Tests/CompilationHelper.cs`, `Rivet.Tests/RoundTripGateValidator.cs`, `Rivet.Tests/RoundTripCorpusGateTests.cs`, and the helper regions of the files named below. Don't move test methods between files in Wave 1.

#### E1 — Shared process, server and fixture helpers
NEW · ~700 LOC · Depends: Wave 0

**Problem.**

| Helper | Copies | Locations / notes |
|---|---|---|
| `FindRepoRoot` | 6 | incl. `OpenApiConformanceTests.cs:162`, `SelfContainedPublishTests` |
| `RunProcessAsync` | 5 | 4 byte-identical |
| `MakeBuildHermetic` | 3 | |
| `StartSampleServer`/`StartAnnotationApiServer` | 2 + 2 | Differ only by project path |
| `RunNode` | 2 | `OpenApiConformanceTests.cs:384`, `SampleProjectOpenApiFetchTests.cs:356` |
| `RunDiff` | 3 | `RoundTripDiffTests.cs:1468`, `VendorExtensionProvenanceTests.cs:495`, `RoundTripCorpusGateTests.cs:389` |
| `FindingCount` | 2 | |
| `CollectRefs` | 5 | 3 md5-identical |
| `ResolvesInDocument` | 2 | |
| `LoadFixture` | 5 | |
| Fixture builders | | `CreateFixtureSliceDocument`, `CopyComponentEntries`, `GetOrAddComponents`, `Build*FixtureSpec`: identical between `OpenApiImporterTests.cs:598-740` and `OpenApiRoundTripTests.cs:16-158` |

- The 145-line server block is duplicated: `MvcBindingHostTests.cs:262-406` ≈ `MvcResponseHostTests.cs:120-264`, and `SampleProjectTests.cs:282-433` ≈ `SampleProjectOpenApiFetchTests.cs:345-480`.
- Temp directories: 45 `Directory.Delete` try/finally blocks across 22 files, plus three look-alike disposables (`TemporaryDirectory` in `RoundTripGateHardeningTests.cs:377`, `OperationIdentityWork` in `OperationIdentityTests.cs:798`, `TemporaryJson` in `TestInfrastructure.cs:168`).
- `DeepReviewFixTests.EmitOpenApi` (`:15`) and `OpenApiEmitterTests.cs:11` copy `CompilationHelper.EmitOpenApi`.

**Change.**
- Extend `CliRunner.cs` with `RepoRoot`, `RunAsync(file, args)`, `StartServer(csproj)` (behind an xUnit `IClassFixture`), `MakeBuildHermetic` and `RoundTripDiff.Run(...)`.
- Move `CollectRefs`, `ResolvesInDocument`, `LoadFixture` and the fixture builders into `TestInfrastructure.cs`.
- One `TempDir : IDisposable` over `Directory.CreateTempSubdirectory()`.
- Give `CompilationHelper.EmitOpenApi` an optional `security` parameter and delete the copies.
- Preserve the `Repository builds` xUnit collection and ephemeral-port allocation.

**Acceptance.** The test count is unchanged and all tests pass. `grep -c "static string FindRepoRoot" Rivet.Tests` returns 1.

#### E2 — Slim `RoundTripGateValidator`
NEW · ~400 LOC · Depends: E1

**Problem.** `RoundTripGateValidator.cs` (526 LOC) re-checks what `tools/roundtrip-diff.py:1890 validate_integrity` checks: unresolved `$ref`s, undefined security schemes, structure, path-param/token mismatch and duplicate operationIds. Both run in the same gate (`RoundTripCorpusGateTests.cs:196,277` and `RunDiff:389`). It calls `OpenApiDocument.Parse` and then walks JSON by hand anyway.

**Change.** Use the Python integrity findings plus Microsoft.OpenApi's `readResult.Diagnostic.Errors` and `document.Validate(ValidationRuleSet.GetDefaultRuleSet())`. Keep only checks neither covers; `ValidateComponentIdentity` is likely one. Record what was kept and why.

**Acceptance.** Mutation check: for each deleted check, inject that defect into a copied emitted spec and confirm the remaining gate still fails. Record the results.

#### E4 — Shared Python module (fixes BUG-12)
NEW · ~150 LOC · Depends: E1

**Change.**
- Add `tools/openapi_common.py` with `canonical`, `pointer_token`/`pointer_parts` (percent-decode per token), `resolve_local_reference`, `component_counts` (one definition of which namespaces count), `operation_count`, `METHODS` and `load_json`.
- Rename the hyphenated scripts to underscores (`roundtrip_diff.py`, `roundtrip_inventory.py`) so plain `import` works, replacing the `importlib.util.spec_from_file_location` hack. Update `Taskfile.yml` and the C# callers that spawn them (`RunDiff`, in E1's helper).
- `roundtrip-inventory.py:84-127` holds 16 `*_PROOF` C#-test-name strings: add a C# test that asserts each named test method exists by reflection over the test assembly.
- Keep the scripts stdlib-only (no `requirements.txt` exists). Don't add `jsonpointer` or `openapi-spec-validator`.

**Acceptance.** Red test for BUG-12 (a `$ref` with `%2F` resolves the same way in both code paths). C# round-trip tests and the Python unit tests pass.

#### E3 — Delete `roundtrip-audit.py` (G4)
NEW · ~1,100 LOC · Depends: E4

**Problem.** `tools/roundtrip-audit.py` (800 LOC) and `tools/test_roundtrip_audit.py` (327 LOC) are never run as a gate; `Taskfile.yml:61` only runs the script's own unit tests. The script duplicates the C# corpus gate and `roundtrip-inventory.py`: its own structural differ (`:224`) and a hard-coded list of 21 Twilio JSON paths allowed to differ (`:63`).

**Change (default).** Delete both files. Remove the `Taskfile.yml:61` line. Update the V1 gate table in `docs/plans/post-v0.42.0-remediation.md` §8 to drop the "Round-trip audit" row.

**Acceptance.** `task test` is green. `grep -rn roundtrip.audit .` shows only historical review docs.

---

### Wave 2 (serial, after lanes merge)

#### W2-1 — `RivetConstraintsAttribute` → DataAnnotations (G5)
NEW · ~60 LOC

**Change.**
- `ExclusiveMinimum`/`ExclusiveMaximum` → `RangeAttribute { MinimumIsExclusive, MaximumIsExclusive }` (.NET 8+).
- `MinItems`/`MaxItems` → `[MinLength]`/`[MaxLength]`/`[Length]`.
- Keep `MultipleOf` and `UniqueItems` custom.
- Delete the old properties. `TypeWalker` reads only the DataAnnotations form. `CSharpWriter` (`:1352-1381` already emits DataAnnotations for the inclusive facets) emits only DataAnnotations. Update samples, docs and tests in the same change.
- Note: plain `IEnumerable` behaves differently with `[MinLength]`; test it.

**Acceptance.** Import → compile → emit round trip preserves every constraint. Runtime validation tests pass.

#### W2-2 — Fold review-shaped test files
NEW · 0–300 LOC

**Change.**
- Move the tests in `Wp12GapFillTests.cs` (1,224 LOC; header cites a nonexistent `FABLE_REWRITE_PLAN.md`; `A3_`/`E8_`/`I1_` names) into the behaviour suites:
  - A3 (inheritance) → `ContractEndpointTests`/`OpenApiEmitterTests`
  - A6/A7/A10 → `ControllerEndpointTests`
  - I1 → `OpenApiImporterTests`
- Rename the tests by behaviour.
- `DeepReviewFixTests.cs` (771 LOC): move the unique cases and delete the duplicates. `OpenApi_Guid_Emits_Format_Uuid` duplicates assertions made in 11 other files; `DateTime` duplicates those in 10.
- Rename `GapAnalysisTests` → `ExampleFidelityMetricTests`. The 09-14 plan already deletes two of its report tests; do that too if it hasn't been done.

**Acceptance.** Every moved test still runs (the test count is unchanged, minus the deleted duplicates, which are listed in the ledger).

#### W2-3 — CLI parsing (G6)
NEW · ~30 LOC (without System.CommandLine)

**Change.**
- Turn `PrintUsage` (`CliParser.cs:235-305`) into one raw string literal.
- Derive the value-required set (`:97-107`) from a single option table.
- Split `RivetOptions.ProjectPath`, which currently holds the project, contract or OpenAPI path depending on mode (`:156,187`), into mode-specific fields.

#### W2-5 — Cross-lane API removals (G3, G9)
NEW · ~120 LOC

**Change.**
- **G3:**
  - Delete the `Define` implicit conversions (`EndpointBuilder.cs:1118,1243,1261,1395,1526,1553`, each returning `default!`).
  - Remove `Define`-typed field acceptance from `ContractWalker.IsRivetEndpointField` (`:1995`).
  - Rewrite the ~24 test files that declare `Define`-typed fields to use typed `RouteDefinition<…>` fields (mechanical).
- **G9:**
  - Delete the 22-argument constructor on `RivetGeneratedSchemaMetadataAttribute`.
  - Delete the `TypeWalker.cs:~1197` `Optional*At` helpers that tolerate short argument lists.
  - `CSharpWriter.cs:567` already emits only the full form.
  - Regenerate any checked-in generated code (`samples/ImportDemo`, test fixtures) that uses the short form.
- Add `CHANGELOG.md` breaking-change lines.

**Acceptance.** `grep -rn "implicit operator Define" Rivet.Attributes` returns nothing. Build, tests, samples and golden diff are clean.

#### W2-6 — Follow-ups deferred by Wave 1 (cross-lane)
NEW · ~200 LOC

1. **Terminal base types (from D1).** `CoverageChecker.IsRivetTerminalInvocation` matches a terminal's declaring type against the six concrete classes, so inherited terminals would silently drop out of coverage.
   - First make it accept terminals declared on a Rivet base type (compare `ContainingType.OriginalDefinition` walking `BaseType`).
   - Then move `Error`/`File`/`Success` into one abstract bound-terminal base plus one `TerminalRouteDefinitionBase<TSelf>`.
   - Acceptance: coverage tests pass, and a public-API snapshot shows only the base-type move.
2. **Remove the `ContentType` alias (from D4).** Switch `Import/CSharpWriter.cs` (~1093) to emit `.ProducesFile(...)`. Delete `FileRouteDefinition.ContentType` and its reading in `ContractWalker` (~773). Update the samples, `docs/guides/file-uploads*`, the ~7 test files and the Meridian reference if it's quoted anywhere in the repo docs.
3. **Duplicate response headers (from D4).** `.WithResponseHeader("ETag").WithResponseHeader("etag")` is no longer rejected anywhere, and the emitter's header map is case-sensitive, so both are emitted. Add a generation-time diagnostic in `ContractWalker` (new RIV id, registered in `Diagnostics.cs` and `docs/reference/diagnostics.md`) for a case-insensitively duplicate header name on the same status. Red-first.
4. **`.SecurityRequirements()` with no arguments (from Wave 0)** duplicates `.Anonymous()`. Delete the no-argument form from the runtime builder and the walker, and update any callers.
5. **Emit synthetic naming (from Lane C).** `InlineTypeExtractor` still produces `Foo2`, while import now uses `Foo_2`. Make emit use the same `Name_2` scheme through one shared naming helper, and record the golden-diff renames.
6. **Header dedupe in import (from Lane C).** `ContractBuilder` de-duplicates response headers on numeric status, so a `4XX` header and a `default` header with the same name collide. Key on the status string; red-first. Record the import golden changes.
7. **Shared `WellKnownTypes` (from Lane B).** `Program.cs` and `TypeWalker` each build one; build it once and pass it.

#### W2-4 — Final sweep
Run the §3 gate, the full golden diff, Plumb and `detect_changes` across the whole change. Update the ledger with final LOC deltas (`git diff --stat main`).

Also: delete the remaining tracker-tag comments (E6, E11, W4, GAP-1, FABLE_GAPS, P2 wave N, planner-constraint, …) and the stale TypeScript-era doc comments across `Rivet.Tool/Emit/**`, `Rivet.Tool/Model/**` and `Rivet.Tool/Analysis/**` (Lane A/B only removed them from code they rewrote).

Also pick up the remaining small items if still present:
- The `Naming.ToCamelCase` empty guard is redundant; `JsonNamingPolicy.CamelCase` already returns empty input unchanged.
- `ToPolicyToken`/`TryPolicyFromToken` are mirrored switches.
- `SecurityParser.IsValidSchemeName` → `char.IsAsciiLetterOrDigit`.
- `ImportedSourceFingerprint` → `Convert.ToHexStringLower`.
- `CompilationLoader` has duplicate error blocks (`32-46`, `273-287`).
- `OpenApiDocumentInfo` defines the "API"/"1.0.0" defaults twice.
- Stale TypeScript-era doc comments at `Model/TsType.cs:7` and `Model/TsEndpointDefinition.cs:7`.
- The 3 duplicate `(Name, Json)` provenance records in `Model/OpenApiProvenance.cs` → one record.
- The duplicated line in the comment at `OpenApiEmitter.cs:1304-1305`.

Explicitly **not** doing:
- `RoutePatternFactory` for `RouteParser`: it needs `Microsoft.AspNetCore.App` and would reject OpenAPI-style `{enterprise-team}` paths.
- `SyntaxFactory` for `CSharpWriter`: heavier than the fixed templates it would replace.
- `IProblemDetailsService` for `RivetErrorEnvelope`: the envelope deliberately matches the TS adapter's wire format.
- A lowercase `JsonNamingPolicy`: the BCL doesn't ship one.

---

## 7. Subagent brief template

Use this verbatim when delegating a WP, filling in the angle brackets:

> You are implementing work package **<WP-ID>** from `/Users/max/Sites/medway/rivet/docs/plans/slop-cleanup.md`. Read §2 (doctrine), §3 (ground rules) and your WP in full before starting. Also read `CLAUDE.md` for the GitNexus rules.
>
> - You may only edit the files your WP/lane owns (§6). Record anything outside that as a follow-up in your report; don't do it.
> - Bugs listed for your WP are red-first: write the failing test, show it failing, then fix.
> - Run GitNexus `impact` upstream before editing each symbol. Report HIGH/CRITICAL and stop if you hit one.
> - Before finishing: run the §3 gate and the golden diff (§3.1; the baseline lives in `/tmp/rivet-golden/before`), and run Plumb.
> - Don't commit.
> - Report: the files changed, LOC delta (`git diff --stat`), tests added or removed (with reasons), grep evidence for each deletion, golden-diff result, impact risk levels, and any follow-ups. Append a row to the §8 ledger.

For Wave 1, run one agent per lane, in parallel, each in its own git worktree (`isolation: "worktree"`). Merge the lanes in the order D, E, C, B, A: Lane A last, because A1/A2 touch the model that others read. Re-run the golden diff after each merge.

## 8. Ledger

| WP | Status | LOC Δ | Tests (+/−) | Golden diff | Impact risk | Notes / follow-ups |
|---|---|---|---|---|---|---|
| Baseline | done | — | Rivet.Tests 1917, RuntimeTests 101–102/TFM, py 11 | Captured to `/tmp/rivet-golden/before`: 5 emit, 25 import, 1 `--from`, 6 `--security` runs. 35 succeed; the 2 expected RIV2002 failures are noted in §3.1. | GitNexus re-indexed at `3fd003a` (it was stale at `25320a9`). | The GitNexus CLI works with `--repo rivet-studio`. It does not index primary-constructor exception classes, so their callers were found by grep. It has no `rename` command, so type swaps were mechanical and checked by the compiler. |
| WP-01 | done | prod −79 (+131/−210), tests +40; 30 files | +3 new (BUG-1 CLI exit test; TypeWalker colliding generated schema; malformed `--from` JSON exits 1, added in WP-04 pass). 2 AcceptsBinary walker tests now expect `RivetUserException`. ~40 assertions retargeted from `InvalidOperationException`/old types to `RivetUserException`. `Invalid_Security_Returns_Controlled_Error` became `Invalid_Security_Refuses_As_User_Error` (throws; the CLI boundary owns printing). | Clean (0 lines, logs identical) | `ApplyGeneratedSchemaRef` and `ReadGeneratedSchemaMetadata` CRITICAL; `BuildEndpointFromField` and `EndpointMerger.Merge` HIGH; `EmitPipeline.RunAsync` MEDIUM; `LoadGeneratedSchemas` and `BuildOperation` LOW. Every edit only swaps the exception type, and golden output is unchanged. | New `Rivet.Tool/RivetUserException.cs` (sealed, `: Exception`). Deleted `ContractAnalysisException`, `OpenApiEmissionException`, `SecurityConfigurationException` and `EndpointMerger.TransportConflictException`. `Program.cs` now has one `catch (RivetUserException)` around `Run`; the 6 per-mode catches and the `EmitPipeline` catch are gone. BUG-2 sites converted: TypeWalker ×6, ContractWalker:863. TypeWalker `EnumLiteral` and `GetMemberType` stay `InvalidOperationException` because they are internal invariants. Also fixed: malformed `--from` contract JSON crashed with exit 134 and a stack trace; it now exits 1 via `RivetUserException` (rivet-ts coupling). **Follow-up (Lane C):** Import still throws `InvalidOperationException` for malformed specs (`OpenApiImporter` ~12 sites, `OpenApiProvenanceReader` ~6), which prints a stack trace; convert them to `RivetUserException`. |
| WP-02 | done | prod −1 (+182/−183), tests +166; 6 files | +12: 3 red-first BUG-10 regressions (escaped example `$ref`; `%20` schema ref; Swagger body-param `$ref` through an array index, which crashed before) and 9 `JsonPointer` unit cases. | Clean | `DecodeComponentId` CRITICAL; `EscapeJsonPointerToken`, `EscapePointerToken` and `ReadRequestBodyComponentId` HIGH; the rest LOW. | New `Rivet.Tool/JsonPointer.cs` provides `Escape`, `Unescape`, `DecodeFragmentToken`, `FromUriFragment`, `TryGetComponentName`, `TryResolve` (JsonNode and JsonElement) and `TryIndex`. All 11 copies were replaced; `DecodePointer`, `EscapeJsonPointerToken`, `EscapePointerToken` and `TryResolveLocalReference` were deleted. The example `$ref` is now escaped. Cycle keys use the raw reference string. **RFC note:** RFC 6901 §6 percent-decodes the whole fragment before splitting, so `%2F` is a separator. This WP follows the spec (split, then decode) and records the deviation. **Follow-up (Lane C):** Microsoft.OpenApi does not unescape `~1`/`~0` in `$ref`s, so components whose names contain `/` or `~` are dropped on import. That makes a full import round trip impossible; the regression test goes source → emit instead. The emitter's `ResolveObjectPointer` still walks the `Dictionary` tree (A5 will move it to `JsonPointer.TryResolve(JsonNode)`). |
| WP-03 | done | prod −232 (+15/−247), tests +98 (mostly CSharpier re-wrapping of `GenerateName` calls); 27 files | BUG-13 red-first: `Anonymous_Endpoint` now asserts `.Anonymous()` and re-emit `security: []`. Also updated: `KitchenSink.Security_Annotations_Correct`, `Importer_Security_And_Description_Preserved_In_Builder_Chain`, and 2 `OpenApiRoundTripTests` assertions. −4 tests: 3 `CollectTypeRefs` unit tests (the helper moved to test infra) and `GenerateName_WithExplicitBaseName` (it pinned a removed overload). 16 `GenerateName` calls now use the 5-arg overload. | Only the intended BUG-13 change: 11 `.SecurityRequirements()` → `.Anonymous()` lines across box, square and vercel, plus their `RivetImportedSourceFile` fingerprints (69 diff lines). Emit, `--from` and `--security` outputs are identical. | `ResolveSingleType` CRITICAL; `ResolveSecurity` HIGH; `OpenApiProvenanceWalker.Walk` HIGH; `InlineTypeExtractor.Extract` CRITICAL (signature unchanged; only the `TypeNamespaces` field was dropped from `ExtractionResult`); `GenerateName` and `CoverageChecker.Check` MEDIUM; the rest LOW. | Deleted after re-grep (each had 0 production readers): `TypeWalker.HasErrors` and its `Program` branch; `SchemaMapper.HasMappedSchema`/`TryAugmentComponentRecord`/`FindRecordByName` (definition only); the `ContractBuilder.ResolveSecurity` no-op ternary; the second `schema.Enum` check (it is in `SchemaMapper.ResolveSingleType`, not `ContractBuilder`); `TsType.CollectTypeRefs` (moved to `Rivet.Tests/TestInfrastructure.cs` `TsTypeRefs`, used by KitchenSink and RealWorld); `TypeWalker._typeNamespaces`/`TypeNamespaces`/`GetNamespaceGroup`, `EmitInput.TypeNamespaces`/`Definitions`/`Brands`, and `ExtractionResult.TypeNamespaces` (tests only); the 3 `GenerateName` overloads; the `CoverageChecker.Check` "api" overload; the `ProvenanceWalker` optional `typeWalker` and its throw; `WellKnownTypes.CancellationToken`/`ProblemHttpResult`/`JsonHttpResultOfT`/`StatusCodeHttpResult`; `EndpointBuilder.IsFileUpload` (CHANGELOG); the `"ProducesFile"` alternative; the `EndpointBuilder` response-media ternary; and the `CSharpWriter` `.SecurityRequirements()` emission, which BUG-13 made unreachable. **Not deleted:** (1) the `ContractWalker.IsRivetEndpointField` `defineType` branch is live, because `public static readonly Define X = …` fields exist across the tests (belongs to G3/W2-5). (2) The `SchemaMapper.ResolveAliasTargets` cycle branch failed verification: `BreakAliasCycles` only scans `#/components/schemas/`, so Swagger 2 `definitions` alias cycles bypass it. They currently crash the importer with a stack overflow in Microsoft.OpenApi `OpenApiSchemaReference.get_Type` (repro: `definitions: {A: {$ref: "#/definitions/B"}, B: {$ref: "#/definitions/A"}}`). **Follow-up (Lane C):** fix `BreakAliasCycles` for Swagger 2, then delete the branch. **Follow-up (Lane D/W2):** `Rivet.Attributes` `.SecurityRequirements()` with no arguments now duplicates `.Anonymous()`; consider deleting it. |
| WP-04 | done | prod −88 (+94/−182; includes +15 for the WP-01 `--from` JSON fix), tests −31; 16 files | Parser tests rewritten through `ParseMany` (26 cases: formats, names, 12 malformed). Deleted `Direct_Emission_Rejects_Duplicate_Primary_Security_Definition` (it built an impossible state). | `--security` variants identical to baseline (6 runs, including the 2 multi-scheme ones). Overall diff is BUG-13 only. | `ToSecurityMetadata`, `EmitWithSecurityMetadata`/`Emit` (85 direct callers, almost all tests) and `SecurityParser` CRITICAL; `EmitCore` HIGH; `ParseMany`/`Parse` MEDIUM. | `SecurityConfig.cs` → `SecurityParser.cs`. `ParseMany(IReadOnlyList<string>)` returns `ContractSecurityMetadata?`; `Parse` is private and returns a tuple. `SecurityConfig`, `ToSecurityMetadata` and `RivetOptions.DefaultSecurity` are deleted; import reads `SecuritySchemes?.FirstOrDefault()`. The single emitter entry point is `OpenApiEmitter.Emit(…, ContractSecurityMetadata? security, …)`, renamed from `EmitWithSecurityMetadata`. The pipeline makes one call: `ParseMany(options.SecuritySchemes ?? []) ?? input.Security`. RIV2011 now lives only in the parser, and its message is `error RIV2011: duplicate --security scheme name 'x'` (was `error: duplicate …`); `Diagnostics` and `docs/reference/diagnostics.md` are updated. rivet-ts coupling checked: `--from/--output/--openapi` still work, user errors go to stderr with exit 1, and the contract JSON and schema file are untouched. |
| Wave 0 total | done | `git diff --shortstat`: 59 files, +1014/−1135. Prod (`Rivet.Tool` + `Rivet.Attributes`): 28 files, +422/−822 (net −400) | Rivet.Tests 1917 → 1927; RuntimeTests unchanged; py 11 OK | BUG-13 only | `detect_changes --scope compare --base-ref main`: 57 files, 220 symbols, 101 flows, risk "critical" (expected: the exception type changed at every throw site). | Plumb: `[]` (no findings). `task build`, `task test`, `task format:verbose` and `task samples:build` all green. **Follow-up (Lane A/B):** new tests import `using Rivet.Tool;` for `RivetUserException`. **Follow-up (Lane E):** fix the `task format` pass order (see §3.1); add a `format:check` alias if wanted. |
| A1 | done | prod −295 (+262/−557), tests +178 (+179/−1); 8 files | +6, all red-first. BUG-4: `Generic_Used_Only_Inside_A_Union_Gets_Its_Component` (two `Wrapper_Enum` shapes, one reachable only through a `Union`, now get `Wrapper_Enum` and `Wrapper_Enum2`) and `Generic_Used_Only_In_Response_Content_Or_Header_Gets_Its_Component`. Before the fix both fell back to untyped `{}` components. BUG-5: `Brand_Description_Survives_Extraction`. Also `Missing_Required_Type_Is_Refused` ×3; see Notes. One fixture was fixed: `Duplicate_Response_Status_In_Contract_Json_Keeps_First_Metadata_And_Sorts` had a response header without the schema-required `type`. | Clean: tree and logs identical (`/tmp/rivet-golden/lane-a-A1`). No golden input hits BUG-4 or BUG-5. | `ResolveTypeParams`, `CollectInlineObjects` (17 test callers), `CollectGenericsFromType` and `CollectGenericInstances` HIGH; `CollectFromType`, `CollectArrayElements`, `ReplaceInType`, `ReplaceInEndpoint`, `CollectArrayElementHashes`, `CollectBrands` and `WalkForBrands` MEDIUM. `detect_changes`: 8 files, 33 symbols, 12 flows, risk high. | New on `TsType`: `Children()` (dictionary value before key), `SelfAndDescendants()` (pre-order; the name says it includes the node itself) and `Rewrite(Func<TsType, TsType?>)` (built on `with`, so descriptions and metadata survive). New on `TsEndpointDefinition`: `AllTypes()`, which yields `(Site, Type)` for the return type, each response's data type, contents and headers, then params, request type and request contents. The site labels are the extractor's context suffixes, so it can use them directly. Ported: `TsType.ResolveTypeParams` (now a `Rewrite`), `InlineTypeExtractor.CollectInlineObjects`/`CollectFromType`/`CollectArrayElementHashes`/`CollectArrayElements` (deleted)/`ReplaceInType`, `OpenApiEmitter.AssignComponentNames` (the local `Walk` is deleted), `CollectGenericInstances`/`CollectGenericsFromType`, and `JsonContractReader.CollectBrands` (`WalkForBrands` deleted). `ReplaceInEndpoint` still rebuilds the endpoint by hand, because `AllTypes` is read-only; it now goes through `Rewrite`. The emitter walkers now also see `ReturnType`, response contents/headers and request contents. **Behaviour change (contract JSON):** a param, response header or property without its schema-required `type` used to deserialize to null behind a non-nullable member, and the emitter silently rendered it as `{type: object}`. Now that every walker visits those positions it would crash, so `JsonContractReader` refuses it with `JsonException` ("param 'id' on endpoint 'getUser' has no type"), and `--from` exits 1 via the existing boundary. rivet-ts vendors the schema that requires these fields, so its output is unaffected. A2 should replace this check with `RespectNullableAnnotations` if that holds. |
| A2 | done | prod −717 (+278/−995), tests +88 (+123/−35); 20 files | +1: `Deserialize_Kind_Need_Not_Come_First`. 0 removed. Adjusted: `Deserialize_MissingKind_Throws` now asserts through `JsonContractReader.Read`, because raw `JsonSerializer` raises `NotSupportedException` for a missing discriminator and the reader turns that into `JsonException`. `DictionaryKeyTests.TsTypeJson_DictionaryKey_RoundTrips_AndAbsenceDeserializes` now passes `JsonContractReader.Options`; the old converter wrote camelCase under any options. The A1 `Missing_Required_Type_Is_Refused` cases now expect STJ's `JsonRequired` message. Tests that built `TsTypeJsonConverter` options use `JsonContractReader.Options`. | Clean: tree and logs identical (`/tmp/rivet-golden/lane-a-A2`). **IR snapshot:** a reflection dump of `JsonContractReader.Read` for all 5 contract fixtures plus rivet-ts `tests/fixtures/expressive-contract/golden-contract.json` is identical before and after, and the test writer's output for those 6 contracts is byte-identical. `ContractSchemaTests` (schema validation of written contracts) pass. | `JsonContractReader.Read` and `ContractEmitter.Emit` CRITICAL (the rating counts every flow; direct callers are `Program.RunFromContract`, `CompilationHelper` and tests); `TsTypeJsonConverter` CRITICAL (74 direct, all through the type attribute); the `ToContract*`/`To*` mappers LOW (0 callers outside the file). `detect_changes`: 16 files, 50 symbols, risk critical. | **Contract JSON wire shape unchanged.** `TsType` is now `[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]` with 14 `[JsonDerivedType]`s. Renames use `[property: JsonPropertyName]` (`type`, `csharpType`, `values`, `typeArgs`, `underlying`, `properties`), schema-required members are `[property: JsonRequired]`, and `JsonContractReader.Options` sets `AllowOutOfOrderMetadataProperties = true`. Kept on purpose: a private `InlineObjectFieldJsonConverter` (a missing `optional` means `type is Nullable`; `surface` stays PascalCase) and a post-read fix-up in `NormalizeResponses` (`statusCode` 0/absent takes a numeric `statusKey`, then the existing normalisation). One integer-enum parser, `Model/IntEnumValuesJsonConverter.cs` (`IntEnumLiteral.Read`/`ToJson`), serves `IntUnion.Members`, `ContractEnum.IntValues` and `OpenApiEmitter.MapIntUnion`. Deleted: `Model/TsTypeJsonConverter.cs` (433), `Emit/ContractEmitter.cs` (333: the mirror records `ContractEndpoint`/`ContractResponseType`/`ContractEndpointExample`/`ContractTypeDefinition`/`ContractQueryAuth`, `ContractEnumIntValuesConverter`, and the `ToContract*` mappers), the 4 reader mappers, `OpenApiEmitter.ParseEnumLiteral`, and A1's hand-written `RequireTypes` (now `JsonRequired`). `JsonContractReader` deserializes `RivetContract(IReadOnlyList<TsTypeDefinition>, IReadOnlyList<ContractEnum>, IReadOnlyList<TsEndpointDefinition>?)` directly. `ContractEnum` stays as the enum declaration shape; it is not a mirror. Model changes: `TsResponseType.StatusCode` is omitted when 0, `EffectiveStatusKey` is `[JsonIgnore]`, and `TsTypeDefinition`'s JSON constructor drops `properties` beside a `type`, as the old mapper did. The writer is now `Rivet.Tests/ContractEmitter.cs`, a test helper built on the same options plus a resolver modifier that omits alias `properties`, as the schema requires; its call sites are unchanged. `EndpointMerger` compares `JsonSerializer.Serialize(x)` under default options; that stays self-consistent. CHANGELOG: `--from` refuses schema-required members that are missing. **rivet-ts:** no change needed, because it vendors the same schema, whose required members are the only ones now enforced. |
| A3 | done | prod −79 (+121/−200, `OpenApiEmitter.cs`), tests −14 (+60/−74); 3 files | 0 added/removed. The 8 tests that called the public `OpenApiEmitter.MapTsTypeToJsonSchema` (7 in `OpenApiEmitterTests`, 1 in `InlineObjectOptionalityTests`) now assert the same schema through `Emit`, via a `ResponseSchemaOf(TsType)` test helper that reads it back as a 200 response body. | Clean: tree and logs identical to `after-rfc` (`/tmp/rivet-golden/lane-a-A3`). | `Emit` CRITICAL (90 direct callers, almost all tests; its signature is unchanged). `MapTsTypeToJsonSchema`, `MapTypeReference`, `BuildTaggedUnionSchema` and `MonomorphisedName` HIGH; `AssignComponentNames` CRITICAL (only caller is `Emit`); `BuildPaths` and `BuildSchemas` MEDIUM; the rest LOW. | `OpenApiEmitter` is now a `sealed class`. The static `Emit(...)` creates one instance per call, and that instance holds the model (`_definitions`, `_brands`, `_enums`) and the naming state (`_genericNames`, `_taggedUnionNames`, `_filteredBodyNames`, `_extraComponents`, `_inliningSyntheticTypes`). Deleted: `EmitContext`, the `[ThreadStatic] _ctx`, every `_ctx?.`/`_ctx is null` check, and the `definitions`/`brands`/`enums` parameters threaded through `EmitCore`, `BuildPaths`, `BuildOperation`, `BuildBodySchema`, `TryBuildRouteFilteredBodySchema`, `TryGetRouteFilteredBodyProperties`, `TryResolveBodyProperties`, `BuildSchemas`, `CollectGenericInstances` and `AssignComponentNames`. `MapTsTypeToJsonSchema` is now private: its only external callers were tests, and without a context it silently skipped definition, enum and brand resolution. `grep ThreadStatic Rivet.Tool` returns nothing. |
| A4 | done | prod −165 (+254/−419, `OpenApiEmitter.cs`), tests +61; 2 files | +1, red-first for BUG-9: `Generic_Instance_Carries_Property_Scalar_Metadata_Like_A_Plain_Record`. A property title on `Wrapper<T>` now reaches `Wrapper_String`, and an empty generic record gets `x-rivet-empty-record`. | Clean: tree and logs identical (`/tmp/rivet-golden/lane-a-A4`). | `BuildObjectSchema`, the `UpperFirst` call sites, `TryGetRouteFilteredBodyProperties`, `BuildComponentRequestBodies` and `BuildSchemas` CRITICAL: each has 1–3 direct callers, all inside the emitter, and the rating counts every emit flow. `BuildMonomorphisedSchema` and `ResolveTypeParams` HIGH; `BuildOperation`, `BuildParameter`, `TryBuildRouteFilteredBodySchema` and `TryResolveBodyProperties` LOW. `detect_changes`: 2 files, 21 symbols, risk critical, for the same reason. | New local helpers: `RequestBody(required, content, examples)` replaces the 7 request-body literals and the component request body; `MediaContent(mediaType, schema)` replaces the 6 single-media literals, including header `content`; `MediaContent(entries, context)` is the one media loop for request and response contents; `FormSchema(ep, files, fields)` serves both multipart and form-only bodies; `ParameterLocation(source)` collapses Route/Query/Header/Cookie into one case; `TypeParameterMap(template, instance)` (`Zip`) replaces the 3 hand-built maps. Deleted: `BuildMonomorphisedSchema` (now `BuildObjectSchema(props with resolved types, typeName)`), `UpperFirst`, the private `ResolveTypeParams`, `TryBuildRouteFilteredBodySchema` and its discarded `out inputTypeName` (now `BuildRouteFilteredBodySchema` returns the schema or null, and resolves body properties once), and the redundant `ep.InputTypeName ??`. The body-param and `RequestType` branches are merged. **Drift fixed:** a form-encoded `RequestType` body now honours a validated `RequestContentTypeOverride`, as the body-param branch already did; request contents now carry `SchemaDescription`, as response contents already did; no walker sets either today. **Not done:** building the response header object from `BuildParameter` minus `name`/`in`. The inputs are different model types (`TsResponseHeader` vs `TsEndpointParam`), headers emit `required` only on opt-in and may use `content`, parameters carry default/constraints/leaf provenance, and the key order differs, so reuse would reorder emitted keys. The only duplication left is the `content`-vs-`schema` choice, which now uses `MediaContent`. The component request-body loop stays separate because its entries are `OpenApiRequestBodyContentProvenance` with raw `SchemaJson`. |
| A5 | done | prod −147 (+343/−490, `OpenApiEmitter.cs` + `SchemaEnricher.cs`), tests −6; 5 files | 0 added/removed. `RivetDefault_Invalid_Json_Falls_Back_To_Raw_String` became `RivetDefault_Invalid_Json_Is_Refused`; this is the decided behaviour change. `GenericTemplateMissing` gained an assertion on its message text, because A3's parameter rename had leaked `_definitions` into that string; this WP fixes it. | **Byte-identical**: tree and logs identical across all 5 samples, 25 corpus imports, `--from` and 6 `--security` runs (`/tmp/rivet-golden/lane-a-A5`). `task samples:build` green. | `EmitCore`, `AttachVendorExtensions`, `SchemasEqual` and `ParseSchemaObject` CRITICAL (every emit flow passes through them; single internal callers); `ResolveObjectPointer`, `WithExamples`, `EnrichScalarSchema`, `EnrichPropertySchema` and `EnrichConstraints` HIGH; the rest LOW. `detect_changes`: 5 files, 47 symbols, risk critical. | The emitted document is now `JsonObject`/`JsonArray`/`JsonValue` throughout, with 0 `Dictionary<string, object>` left in `Emit/`. Small local builders: `ArrayOf`, `StringArray`, `ObjectOf` (throws on duplicate names, as `ToDictionary` did) and `Node(JsonElement)`. Deleted: the `JsonElement` branches of `CollectSchemaReferences`, `ResolveObjectPointer` (the Wave 0 follow-up: vendor-extension owners now resolve through `JsonPointer.TryResolve(JsonNode)`), the `Deserialize<object>` in `ParseJson` (now `JsonNode.Parse`), the serialize-and-compare `SchemasEqual` (now an inline `JsonNode.DeepEquals`), every `(Dictionary<string, object>)` cast, and the unused camelCase policy on the output options. `WithExamples` now `DeepClone`s the template schema it used to share by reference. `ApplyNullableMetadata` moves the old members with `DeepClone`/`Clear`. `SchemaEnricher.EnrichConstraints` is `SerializeToNode(constraints, camelCase + WhenWritingNull)` merged into the schema, with `uniqueItems` kept only when true. **Decided behaviour change:** invalid JSON in `default`/`example`/`examples` (property attributes, schema metadata, parameter defaults) now throws `RivetUserException` via `SchemaEnricher.ParseJsonLiteral`, with a message such as "default of property 'mode' must be a JSON literal, got …". Before, the property path fell back to a raw string and the others crashed with an unhandled `JsonException`. CHANGELOG updated. **Latent ordering note (not in the corpus):** removing a key and then adding one used to land in the freed `Dictionary` slot, while `JsonObject` appends. The only removals are `format` when metadata clears it (`IsFormatSpecified` with a null `Format`), so a key added after that now serializes after the sibling keys instead of in `format`'s old position. It is the same JSON, and byte-identical on every golden input. `DeepEquals` ignores property order, whereas the old string compare did not, so a route-filtered body whose properties match a definition in a different order may now reuse that definition's name. Not hit by the corpus. The Microsoft.OpenApi writer is not used, per the spec. |
| Lane A total | done | `git diff --shortstat slop/wave0`: 23 files, +1614/−2703. Prod (`Rivet.Tool`): 10 files, +1179/−2582 (net −1403). Tests: 11 files, +428/−121. | Rivet.Tests 1927 → 1935; RuntimeTests unchanged | Byte-identical against `after-rfc` after every WP | Each WP commit ran `detect_changes`; risk is critical because every emit flow passes through `OpenApiEmitter`. | One commit per WP on `slop/lane-a`: A3, A1, A4, A2, A5. Plumb `[]`, `task format:verbose` and `task samples:build` green. **Follow-ups:** (Lane B/W2) `ResponseStatusValidation.NormalizeIrAndEnsureResponse(responses, endpoint)`, the overload B6 deletes, no longer has any Lane A caller. (W2-4) Tracker-tag comments remain in `OpenApiEmitter.cs` (`E6`, `E11`, `W4`, `GAP-1`, `FABLE_GAPS`, `P2 wave 6`, `planner-constraint:`); A-lane WPs removed only those in code they rewrote. The stale TypeScript-era doc comments on `TsType`/`TsEndpointDefinition` are also still there. (Lane E) `Rivet.Tests/ContractEmitter.cs` is now the test-side contract writer; E1 may want it next to the other helpers. (Merge) A1/A2 change `Model/**` and `JsonContractReader`, which Lane B/C code reads; merge Lane A last, as §7 says. |
| B1 | done | prod −387 (+368/−755), tests +55; 10 files | +1: `MetadataAttributeTests.Attributes_Are_Identified_By_Symbol_Not_By_Name` (red-first: a custom `Other.RequiredAttribute` made a nullable property required; a custom `Other.RivetDescriptionAttribute` is also ignored; a real DataAnnotations `[Required]` still counts). | Clean (tree and logs identical to `after-rfc` / lane-b baseline) | `WellKnownTypes` CRITICAL (29 direct); `GetHeaderName`, `ReadDataAnnotationConstraints`, `GetTypeDescription`, `GetTypeMetadata`, `ReadGeneratedSchemaMetadata` CRITICAL; `TypeWalker`, `IsOptionalProperty`, `GetJsonPropertyName`, `MemberFieldIsOptional`, `ReadOperation` HIGH; the rest LOW. Every edit swaps name matching for symbol matching; golden unchanged. `detect_changes` (vs `slop/wave0`, lane-b index): 10 files, 139 symbols, 30 flows, critical. | `WellKnownTypes` is now a primary-constructor class: the 7 `Http*` and 20 typed-result fields became static `(metadataName, value)` tables folded into `HttpMethodAttributes`/`TypedResultStatusCodes`, and it now also resolves every Rivet, System.Text.Json and DataAnnotations attribute the walkers read (TypeWalker's own 10 attribute fields moved there; `TypeWalker` holds a `WellKnownTypes`). New `RoslynExtensions` helpers: `AttributeData.Is(type)`, `ISymbol.GetAttribute(type)`/`HasAttribute(type)`, `AttributeData.StringArgument()`. `TypeWalker.GetHeaderName`/`IsOptionalProperty` are now instance methods. `OpenApiProvenanceWalker` resolves each attribute once per read (`OfAttribute(compilation, name)`); `ReadOperation` takes the compilation. `SecurityMetadataWalker` resolves its 5 attributes once. `_collectionTypes` → `SpecialType` for the 5 interfaces plus a resolved `List<T>`. Enum `[RivetFormat]` now reuses `GetTypeFormat` (B5's duplicate-read item). Not changed: `RivetRequestBody` double resolution (B2 folds it); converter-type and namespace checks in `GetEnumWireShape`/`TryGetScalarInner` (type identity, not attributes). CHANGELOG line added. **Follow-up (Program.cs, unowned):** `Program` builds its own `WellKnownTypes` while `TypeWalker` builds another; share one if Program is touched. |
| B2 | done | prod −313 (+417/−730); 3 files incl. `WellKnownTypes`; tests +105 | +3 in `ContractEndpointTests`: `User_Extension_Method_In_Chain_Is_Refused` (red-first BUG-8: a user `Returns<TError>(int, bool)` extension was read as a 404 response) and `Chain_Not_Rooted_At_Define_Is_Not_Read` (red-first BUG-8: `Routes.Get<T>("/api/fake")` became a GET endpoint), plus `Chain_Reads_Named_And_Default_Arguments` (out-of-order named arguments; default `QueryAuth()`/`ProducesFile()` values). | Clean | `BuildEndpointFromField`, `CollectInvocationChain`, `GetRequestBodyRequired`, `BuildParams` HIGH; `CollectInvocationsRecursive`, `ResolveParameter`, `GetRequestBodyType`, `ChainedCall` LOW. `detect_changes`: 3 files, 15 symbols, 11 flows, high. | New `ReadBuilderChain` uses `semanticModel.GetOperation(initializer)` and walks `IInvocationOperation.Instance`, unwrapping `IConversionOperation` and following extension receivers. The root must be a `Define` member, otherwise the field is not read (as before for any unreadable initializer). Any later call whose declaring type is not `RouteDefinitionBase<TSelf>`, `RouteDefinition`, `FileRouteDefinition` or `FileRouteDefinition<T>` is refused with a `RivetUserException`. Dispatch is a `switch` on the method name after that declaring-type check, so only Rivet builder methods are interpreted. Arguments are read through `IArgumentOperation.Parameter` and the conversion-stripped `ConstantValue`, so defaults and named arguments come from Roslyn. Deleted: `ChainedCall` (and its `IReadOnlyDictionary<string, object>` carrier and dictionary-order `StringArg`), `CollectInvocationChain`, `CollectInvocationsRecursive`, `ResolveParameter`. The HTTP verb comes from the verified `Define` member. A `ResponseStatus(Code, DeclaredKey)` normaliser collapses the paired statusCode/statusKey branches: Returns ×4→1, ResponseContent ×4→1, ResponseBinaryContent ×2→1, ResponseExample ×4→1, RequestExample ×2→1. The `Parameter` body moved to `ToDeclaredParameter`, and one `SchemaContent` builds typed request and response contents. `GetRequestBodyType`/`GetRequestBodyRequired` now share one `ReadDeclaredRequestBody` read via `wkt.RivetRequestBody`; `BuildParams` takes it instead of the compilation. `WellKnownTypes` gains `Define` and `RouteDefinitionBase`. CHANGELOG line added. The spec said to dispatch on `OriginalDefinition` compared to member symbols; this WP uses the declaring-type check plus the name, which gives the same guarantee (no user method can be declared on those types) without resolving each overload. |
| B3 | done | prod −229 (+207/−436); 3 files | None added or removed. `ContractEndpointTests`, `CoverageCheckerTests` and the controller-endpoint tests pass unchanged. | Clean | `BuildEndpoint`, `BuildEndpointFromMethod`, `TryResolveController`, `ExtractReturnType`, `ExtractParams`, `SubstituteRouteTokens`, `DeriveControllerFileName`, `ExtractResponseExamples` HIGH; `ClassifyParam`, `ApplyResponseExamples`, `DefaultRequestExampleMediaType` LOW. `detect_changes`: 3 files, 20 symbols, 18 flows, critical. | One `EndpointWalker.BuildControllerEndpoint(method, controllerName, isContract, …)` replaces `EndpointWalker.BuildEndpoint` and `ContractWalker.BuildEndpointFromMethod`. Contract mode keeps its differences: no normalisation, `RejectContractDuplicates` plus numeric sort, its own refusal text, and no `[Consumes]`/`[FromForm]`/example reads. One `ResolveActionRoute(wkt, method)` returns the verb and the combined, token-substituted, constraint-stripped route. It serves both builders and `CoverageChecker.TryResolveController`; the four route helpers are now private. Deleted the dead `returnType ?? (responses.Count == 0 ? …)` fallback, `ExtractReturnType` and `ExtractProducesResponseType`: after the no-response guard, the fallback is exactly `typeWalker.MapType(unwrapped)`. One `ApplyResponseExamples(responses, (statusKey, example) pairs, diagnostic, label)` replaces both copies, and `DefaultRequestExampleMediaType` is shared. `ToEndpointExample` stays per walker, because the two build examples from different sources (attribute vs chain call). One `ControllerBaseName` replaces the two "Controller" suffix strips. `ClassifyParam` now returns null only for plumbing and throws for an unresolved source itself, so the `FromServices`/`CancellationToken` re-check and the `HasAttribute` helper are gone. |
| B4 | done | prod −361 (+375/−736), tests −11; 9 files incl. `Program.cs` | None added or removed. `CoverageCheckerTests` (via `RunCheck`) and `FunctionsPrefixTests` now go through a new `CompilationHelper.CheckCoverage(compilation, prefix)`, which discovers, walks and checks, because coverage needs the contract fields. `CoverageCheckerTests`, `FunctionsPrefixTests` and `task test:functions` pass. | Clean | `ContractWalker.Walk` CRITICAL (Program plus test helper); `Check`, `BuildContractFieldMap`, `TryGetProvenanceValue`, `ResolveContractReference`, `TryResolveMinimalApi` HIGH; `NormalizeRoute` MEDIUM; the rest LOW. `detect_changes`: 9 files, 31 symbols, 13 flows, high. | `ContractWalker.Walk` returns `ContractEndpoint(Endpoint, IFieldSymbol? Field)` (Field is null for abstract-method endpoints). `CoverageChecker.Check` takes those, so `BuildContractFieldMap`'s type re-walk and the `(ControllerName, Name)` re-match are gone. `CoverageChecker` now runs on `IOperation`. Terminals, `Bind`s and adapters are `IInvocationOperation`s checked by declaring type, with receivers read from `Instance`/`Arguments[0]`. Local provenance uses `ILocalReferenceOperation`, `IVariableDeclaratorOperation.GetVariableInitializer()`, `IAssignmentOperation` and ref/out `IArgumentOperation.Parameter.RefKind`; the declaring function scope is still found from syntax to keep top-level-statement behaviour. "Returned" means an own (non-nested) reachable `IReturnOperation` whose `ReturnedValue`, `ISwitchExpressionOperation` arm or `IConditionalOperation` branch is the adapter. Minimal-API arguments are read by `IArgumentOperation.Parameter` (`pattern`, `httpMethods`, `prefix`) instead of the positional `Arguments[1]`. Deleted: `BuildContractFieldMap`, `TryGetProvenanceValue`, `Unwrap`, `IsSameExpression` (now `IsSameSyntax` across operation trees), `IsReturnedExpression`/`FromMethod`/`FromAnonymousFunction`/`FromBlock`, `GetContainingFunction`, `ReceiverResolvesToTerminal`, `TryGetRivetAdapterReceiver`, `TryGetRivetBindReceiver`, `TryResolveBinding`, `IsRivetTerminalInvocation`, `ExtractConstantStrings`, `ExtractMinimalRoute`, `ExtractStrings` and the private `NormalizeRoute` (it now uses `TransportIdentity.NormalizeRoute`). One `TypedConstant.Strings()` extension replaces the flatten in `CoverageChecker` and the two in `EndpointWalker` (`[Consumes]`, `[Produces]`). `SecurityMetadataWalker.ReadStrings` stays: it maps null to "" to keep scope names and descriptions aligned by position. The duplicate route strip at the old `:588` was removed with B3. **Out of lane:** `Rivet.Tool/Program.cs` (3 lines, adapting to the new `Walk` return), plus `Rivet.Tests/CompilationHelper.cs`, `CoverageCheckerTests.cs` and `FunctionsPrefixTests.cs` (test infra; may conflict with E1). |
| B5 | done | prod −230 (+158/−388), tests +59; 6 files | +2 red-first: `ContractEndpointTests.Nested_Contract_Class_Is_Discovered` (BUG-6; was empty) and `Required_Field_Is_Required_In_Schema` (BUG-7; a `required string?` `[JsonInclude]` field was optional). | Clean | `GetEffectiveProperties`, `GetJsonMemberName`, `EnumLiteral`, `ApplyTypeFormat` CRITICAL; `GetAllTypes`, `IsOptionalProperty`, `GetJsonPropertyName`, `ApplyParameterMetadata`, `ApplySchemaLeafMetadata`, `BuildParams`, `IsRecordPrimaryConstructor`, `CanOmitOnWire` HIGH; the rest LOW. `detect_changes`: 6 files, 27 symbols, 24 flows, critical. | `RoslynExtensions.GetAllTypes` descends into nested types (BUG-6). One `TypeWalker.IsOptional(ISymbol)` replaces `IsOptionalProperty` and `MemberFieldIsOptional` and honours `IFieldSymbol.IsRequired` (BUG-7); `CanOmitOnWire(ISymbol)` replaces `MemberIsOptional`. One `GetJsonMemberName(ISymbol)` replaces `GetJsonPropertyName`, `GetJsonFieldName` and the inline copy. One `IsJsonIgnored(ISymbol)` (the `IPropertySymbol` forwarder and `IsJsonIgnoredMember` are gone); `HasJsonInclude` inlined. New `TypeWalker.GetRequestProperties(type)` replaces the 6 copies of the ignored/response-only/header filter in `ContractWalker`. `WithLeaf(schemaType, format)` replaces `ContractWalker.ApplyParameterMetadata`, `OpenApiProvenanceWalker.ApplySchemaLeafMetadata`, `TypeWalker.ApplyTypeFormat`, the `MapPropertyType` switch and the `WalkType` schema-type/format branches. It is an extension in `Analysis/TsTypeLeaf.cs`, not a member of `TsType`, because `Model/**` is Lane A's. Ceremony removed: the orphan `<summary>` (moved onto `TryBuildPolymorphicUnion`), the `ulong` case (`EnumLiteral` is now `Convert.ToString(…, InvariantCulture)`), the `ContainingType` type test, the `IsRecordPrimaryConstructor` re-check, the `EqualityContract` name guard, and the positional `ReadJsonIgnoreCondition` branch. The duplicate enum `[RivetFormat]` read was already folded in B1. Semantics note: an explicit `[RivetFormat("")]` on a property now clears the leaf format rather than storing `""`; emitted output is unchanged, because `SchemaEnricher` writes the property-level format either way. No corpus spec has an empty format. CHANGELOG lines added (nested discovery, required fields). **Follow-up (Lane A / W2):** move `WithLeaf` onto `TsType` if wanted. |
| B6 | done | prod −100 (+136/−236); 6 files (1 new) | None added or removed. | Clean | `ContentEquivalent`, `RequestContentsEquivalent`, `HeadersEquivalent`, `FileContentTypeEquivalent`, `ParamKey`, `ParamsEquivalent`, `DefaultSuccessCode` HIGH; `NormalizeIrAndEnsureResponse` CRITICAL (359 impacted; only its default-status expression changed); `SecurityMetadataWalker.Walk` MEDIUM; `ClassifyParam` LOW. `detect_changes`: 6 files, 18 symbols, 23 flows, critical. | One `EndpointMerger.ListEquivalentBy<T>(left, right, key)` replaces `ContentEquivalent`, `RequestContentsEquivalent` and `HeadersEquivalent`. `FileContentTypeEquivalent` is inlined (its doc moved onto `DeclaredMediaTypeEquivalent`). `ParamKey` is gone: params compare `(Name, Source, IsOptional)` as a tuple. New `Analysis/SecurityRequirementsBuilder.cs` (`AddRequirement`, `AddScheme`, `Build`) is shared by `SecurityMetadataWalker` and the contract chain; same-scheme scopes of one order accumulate, as the chain already did. One `ResponseStatusValidation.DefaultSuccessCode` (case-insensitive) serves `NormalizeIrAndEnsureResponse` and `ContractWalker`, which now computes it once per endpoint (it was 4 calls), and the two success-response inserts are one. `BuildParams`'s 5 optional parameters are required. `explicitSources.Count > 1 &&` dropped. **Not done (out of lane):** deleting the forwarding `NormalizeIrAndEnsureResponse(responses, endpoint)` overload, because its only caller is `Emit/ContractEmitter.cs:186`. **Follow-up (Lane A / W2):** switch that call to the 4-argument form, then delete the overload. |
| Lane B total | done | `git diff --shortstat slop/wave0..slop/lane-b`: 22 files, +1833/−3235. Prod (`Rivet.Tool`): 15 files, +1589/−3209 (net −1620). `Rivet.Tool/Analysis` 9326 → 7678 lines | Rivet.Tests 1927 → 1933 (+6: B1 ×1, B5 ×2, B2 ×3); RuntimeTests unchanged; `task test:functions` OK | Clean after every WP: tree and logs identical to `after-rfc` (outputs in `/tmp/rivet-golden/lane-b-<WP>`) | `detect_changes --scope compare --base-ref slop/wave0` (lane-b index): 22 files, 196 symbols, 56 flows, critical. Expected: every Analysis entry point sits on the `Run` flow. | Plumb `[]` and `task format:verbose` clean after each WP. Out-of-lane edits: `Rivet.Tool/Program.cs` (B4, 3 lines), `Rivet.Tool/RoslynExtensions.cs` (B1/B5/B4; the spec gives BUG-6 there to B5), and the test helpers `CompilationHelper.cs`, `CoverageCheckerTests.cs` and `FunctionsPrefixTests.cs` (B4). CHANGELOG has 4 new lines. The GitNexus index for this lane is registered as `rivet-lane-b` (the worktree). |
| C1 | done | prod −120 (+152/−272), tests +93; 8 files | +8 red-first: `Malformed_Spec_Is_A_User_Error` ×4 (invalid JSON, missing `info.title`, example with both `value`/`externalValue`, unknown security scheme type; all threw `JsonException`/`InvalidOperationException` before); `Malformed_OpenApi_Spec_Exits_1_Without_Stack_Trace` ×2 (CLI); `Component_Refs_With_Escaped_Slash_And_Tilde_Resolve`. 5 `OpenApiReferenceNormalizationTests` retargeted `InvalidOperationException` → `RivetUserException`. | Import only: 4 `RivetDocument.cs` files (cloudflare, docusign, github, twilio), 681 changed `RivetDocumentExample`/`RivetVendorExtension` lines. Every pair is the same JSON value (checked by parsing both). The old reader copied `GetRawText()` from the original source, keeping its whitespace and `'`; now every provenance string is written by the one serialiser (compact, default escaping: `'` → `\u0027`). The old output was already inconsistent: any spec with an `x-desc`/`x-is-deprecated`-style extension got re-serialised, so it came out escaped. No fingerprint or type changes. Emit, `--from` and `--security`: identical. Logs: identical. | `Import`, `BreakAliasCycles`, `NormalizeLocalPathReferences`, `ReadSwaggerSchemaLessResponses`, `ReadSecurityMetadata`, `EscapeLiteralSentinels`, `OpenApiProvenanceReader.Read` and `DecodeComponentId` CRITICAL (all sit on the single `Import` → `RunImport` flow); `RequiresNormalization`, `ResolvePathItem`, `BuildParameterMetadataJson` and the provenance `Read*String` helpers HIGH; the rest LOW. `detect_changes`: 8 files, 41 symbols, all in `Import/` and its tests. | The spec is parsed once into a `JsonNode`. The passes mutate it in place, and it goes to `OpenApiJsonReader.Read(JsonNode, …)`, so there is no final string round trip. The JsonElement readers (provenance, security) share one `SerializeToElement` view. Deleted the read-only `RequiresNormalization` mirror (6 predicates); the path walk now always runs. Deleted the duplicated `IsOpaqueOpenApiValue` and the `changed` flags. `ContractBuilder`: `DeepClone` replaces the parse round trip, and the `JsonSerializerOptions` are cached. **Wave 0 follow-up 1:** 15 malformed-spec throws in `OpenApiImporter`/`OpenApiProvenanceReader`, plus the parse failure, now throw `RivetUserException` via `OpenApiImporter.InvalidSpec` (`error: invalid OpenAPI document: …`); the CLI exits 1 without a stack trace. `CSharpWriter`'s unsupported-scheme-model throw stays, because it is an internal invariant. **Wave 0 follow-up 3 (`~1`/`~0`):** Microsoft.OpenApi registers components under their raw names but looks up the still-escaped token (`OpenApiV31Deserializer.GetReferenceIdAndExternalResource` takes the last `/` segment verbatim). `RegisterEscapedComponentIds` now registers each such component under its escaped token as an alias, so `$ref`s to `a/b`/`a~b` responses, examples, request bodies and so on resolve. Example component ids are decoded through `SchemaMapper.DecodeComponentId`. `JsonPointer.cs` is unchanged. |
| C2 | done | prod −72 (+86/−158); 2 files | +1 (red on C3, green now): `Swagger2_Basic_Security_Definition_Imports_As_Http_Basic`. The raw reader refused Swagger 2 `type: basic` with "unsupported type 'basic'"; Microsoft.OpenApi converts it to `http`/`basic`. `Malformed_Spec_Is_A_User_Error` (unknown scheme type) still passes. | Byte-identical to C3: tree and logs, all 37 runs (the corpus specs with security include asana, box, github, slack, square, stripe, twilio and petstore-v2's Swagger 2 `oauth2`/`apiKey`). | `ReadSecurityMetadata` CRITICAL (single caller `Import`); `ReadSecurityScheme`, `ReadSecurityRequirements` and `ResolveSecurity` HIGH; the flow, location and string helpers LOW. The `rivet-lane-c` index gave noisy callers after re-analysis, so impact used the `rivet-studio` index; these symbols were unchanged since it was built. | Security is read from the typed document. `doc.Components.SecuritySchemes` goes through `MapSecurityScheme`, and `doc.Security`/`operation.Security` go through one `ContractBuilder.MapSecurityRequirements` (the operation mapper's loop, extracted). Deleted the raw `securitySchemes`/`securityDefinitions`/`security` reader and its 8 helpers. **Verified Microsoft.OpenApi 2.7.5:** `SecuritySchemeType.MutualTLS` exists and V3.1 parses `mutualTLS`; Swagger 2 `flow` (`implicit`/`password`/`application`/`accessCode`) is converted to `OpenApiOAuthFlows`; undeclared requirement names still become references (a diagnostic, not dropped). So no raw read is kept. URLs use `Uri.OriginalString`, so there is no trailing-slash normalisation. **Behaviour changes:** (1) Swagger 2 `basic` now imports (it was refused). (2) An unknown OAuth flow key is now ignored by the library; before, it was refused. (3) OAuth flows are listed in the fixed order implicit, password, clientCredentials, authorizationCode, not source order (no corpus spec is affected). (4) Missing required fields are still user errors: `security scheme 'x' has a missing or unsupported '<field>'`. |
| C3 | done | prod −86 (+50/−136); 1 file | +1 red-first BUG-3: `Inline_Objects_Differing_Only_In_A_Nested_OneOf_Get_Distinct_Records`. Before the fix, `Holder.Second` was typed `HolderFirst`. | Intended synthetic-identity changes only, all under `import/`. Compared with C1: 110 new synthetic files, 5 removed, 112 modified (their contracts, parent records and `RivetDocument.cs` fingerprints). Instrumenting old and new fingerprints across the corpus showed every split is a real old collision. The schemas differed only in composition keywords: `/allOf` ×80 (discord: every `type` property with the same `enum` but a different `allOf` `$ref`. Before, `ApplicationCommandRoleOption.Type` was typed as `ApplicationCommandAutocompleteCallbackRequestType`; now it gets its own `…RoleOptionType`), and nested `oneOf`/`anyOf` ×18 (digitalocean 2, github 1, stripe 1, vercel 13). New names follow the existing context scheme (`<Parent><Property>`, `…OptionN`; vercel's `Synthetic21..27` counters shift by one). The 5 removed stripe files (`PostPaymentIntentsRequestPaymentMethodOptionsCardOption0Installments*`) are renames. The first-seen owner of that identical shape is now the separated `PostInvoicesRequestPaymentSettingsPaymentMethodOptions…` tree, so the type is reused under that name. Emit, `--from`, `--security` and exit codes: unchanged. | `ComputeSchemaFingerprint` and `AppendSchemaFingerprint` CRITICAL (3 callers: `SynthesizeInlineEnum`, `SynthesizeInlineIntEnum`, `ResolveObjectType`; they reach every response/input resolution flow); `AppendSemanticFacets` LOW. | `ComputeSchemaFingerprint` is now `SerializeAsV31` (terse, `$ref` kept), canonicalised in one small walk. Object keys are sorted and `required` is sorted, which keeps the old deliberate key-order and required-order insensitivity; without it, 146 more types split, for example two cloudflare responses whose `required` lists differ only in order. `x-*` keys are dropped at schema level, but not in property-name maps or data keywords. Keeping them would have split 455+ identical shapes on `x-auditable`/`x-stripeBypassValidation`, which do not change the generated type. BUG-3 is fixed: `oneOf`/`anyOf`/`allOf`/`const`/`not` are included, keys are escaped by the serialiser, and depth is no longer truncated. Deleted the 150-line hand-written fingerprint (`AppendSchemaFingerprint`/`AppendSemanticFacets`). **Known limit:** an inline schema that differs only in an importer-read extension (`x-enum-varnames`, `x-rivet-*`) still shares a type, as before. **`AllOfYieldsProperties`** stays for C4. |
| C4 | done | prod −395 (+490/−885); 9 files | +3 red-first: `Swagger2_Definition_Alias_Cycle_Is_Broken_With_A_Warning` (the test host crashed with a stack overflow before), `String_Enum_Dedup_Suffix_Does_Not_Collide_With_An_Authored_Suffix` (enum `["a","A","A_2"]` generated two `A_2` members, which does not compile; now `A`, `A_2`, `A_2_2`). Retargeted to the new naming scheme: `Param_Input_Record_Reuses_Identically_Shaped_Numbered_Variant` and `Param_Input_Record_Disambiguates…`/`Two_Tags_Synthesizing_Same_Input_Name…` (`ListItemsInput2` → `ListItemsInput_2`, `GetByIdInput2` → `GetByIdInput_2`). | **Intended naming change only.** Compared with C2: 356 synthetic type files renamed from `Foo2` to `Foo_2` (asana 16, box 6, cloudflare 56, digitalocean 127, github 3, jira 1, slack 102, stripe 25, zoom 20). 135 files changed only by those renames, their `RivetImportedSourceFile` fingerprints, and 6 union files whose `As<Type>` accessor names follow the type name. A script mapped each old name to its new name and rewrote the old tree with that map; nothing else differs. The digit-ending cases show why a separator is needed: `InfoResponseDefaultContent1` + `2` used to be `…Content12`. Emit, `--from`, `--security` and exit codes: unchanged. | `MapSchemas`, `DeduplicateProperties`, `MapEnum`, `MapIntEnum`, `ResolveSyntheticName`, `AddOrReuseExtraRecord`/`Enum`, `DecodeComponentId`, `GetExtensionString`, `SanitizeName`, `WriteEnum` and `IsIntEnum` CRITICAL (all on the single `Import` flow); `ResolveAliasTargets`, `DeduplicateFields`, `ResolveFormat`/`ResolveSchemaType`, `ResolveRequest/ResponseContents`, `IsBodyForbiddenStatusCode`, `GeneratedTypeAttribute` and `GetOperationExtensionString` HIGH; the rest LOW. | **Names:** `SchemaClassifier.NameCandidates`/`UniqueName` is the one scheme, replacing 6 suffix loops (`SchemaMapper` ×2, `ResolutionContext`, `DeduplicateProperties`, `MapEnum`, `MapIntEnum`, `DeduplicateFields`). `_N` was chosen: it already covered slightly more of the corpus (342 vs 339), `Naming` already preserves a trailing `_N`, and it cannot fuse with a trailing digit. `FindNumberedSchemaWithShape` now reuses `ComponentCandidates`. **Wave 0 follow-up 2:** `BreakAliasCycles` also scans Swagger 2 `definitions` and decodes targets with `JsonPointer.FromUriFragment`. `ResolveAliasTargets` is reduced to missing-target handling; the cycle branch is gone. **Duplication:** `Unwrap` + `TryResolveComponent` (decodes `~1`/`%xx`) replace the copies in `ResolveFormat`/`ResolveSchemaType`. There is one `TypeKeyword`/`ScalarTypeKeyword`, one `OperationMethods` list, one pair of `x-rivet-imported-*` constants, one `GetExtensionString(extensions, key)` for schemas and operations, and one `ResolutionContext.TypeName` (was `SanitizeName` ×2; ref call sites now decode). One `SynthesizeInlineEnum(…, map)`. **Model:** `IsSynthetic` deleted (it is `ComponentId is null`), and the 3 stamping loops are `Select`s. `_syntheticsByName: Dictionary<string, object>` became typed record/enum registries. `GeneratedResponseMediaTypeContent` composes `GeneratedMediaTypeContent` through one `ResolveMediaContent`. `StatusCode` is derived from `StatusKey` (`ResponseStatus.Code`, invariant culture) on the 4 records. **Walks:** one example-ref walk, `InlineExampleRefs` (replaces `CollectReferencedExampleComponents` + `InlineExampleRefNode`, and decodes component names). **Seam:** `RecordSynthesizer(ctx, SchemaMapper)`; the 4 `Func`s are gone. **Small:** the style switch uses `ParameterStyleName`; `_binaryContentTypes` is gone (only octet-stream and pdf were not already covered by a prefix); `IsIntEnum` is one expression; `WriteEnum` makes one pass; `ResolveConstType` and `MultipleOf` are culture-invariant (`MultipleOf` was `decimal.ToString()` in the current culture); `ContractBuilder` calls `ResponseStatusValidation.IsBodyForbiddenStatus`. **Kept:** `AllOfYieldsProperties` is side-effect free by design, and folding it into `ResolveAllOfRecord` would mutate the context during classification. `ContractBuilder._supportedMethods` is the subset Rivet can express, not the OpenAPI key list. Grep evidence: `IsSynthetic` (only an unrelated `RealWorldImportTests` helper), `_binaryContentTypes`, `GetOperationExtensionString`, `SanitizeName`, `InlineEmbeddedExampleRefs`, `CollectReferencedExampleComponents`, `InlineExampleRefNode`, `IsBodyForbiddenStatusCode`, `SynthesizeInlineIntEnum`, `_syntheticsByName` and `_operationNames` each have 0 hits across `Rivet.*`, `samples`, `tools` and `docs`. **Follow-up (cross-lane):** `Diagnostics.ImportAliasRefCycle` (RIV3007) no longer has a producer. Delete it from `Diagnostics.cs`, `docs/reference/diagnostics.md`, `docs/reference/import-profile.md` and `ImportMetricTests.cs:64`. |
| C5 | done | prod −12 (+100/−112); 1 file | +1: `Import_Output_Does_Not_Depend_On_The_Current_Culture` imports a spec with `multipleOf 0.5`, negative/decimal bounds, a >Int64 int enum and item counts under sv-SE, de-DE, ar-SA and fa-IR, and asserts the output is byte-identical to invariant. **Red** against the pre-C4 constraint reader: `multipleOf` became `double.NaN` under a comma-decimal culture, because `decimal.ToString()` used the current culture and the result was then parsed as invariant. C4 fixed it, and this test pins it. | Byte-identical to C4 (tree and logs, all 37 runs). The whole corpus import was also rerun under `LC_ALL=de_DE.UTF-8` and `sv_SE.UTF-8`: byte-identical to invariant. | `EscapeString` CRITICAL (14 direct callers, all in `CSharpWriter`, on the import write flow); `StringLiteral` CRITICAL; `NullableIntLiteral` LOW. | `StringLiteral` is now `SymbolDisplay.FormatLiteral(value, quote: true)`. `EscapeString` (format, strip the quotes, re-add them) is deleted, and its 83 `\"{EscapeString(x)}\"` sites are `{StringLiteral(x)}`. The 20 `bool.ToString().ToLowerInvariant()` sites and the int literals (`NullableIntLiteral`, `.Status(…)`, length and item-count constraints) go through `Literal(bool/int)` = `SymbolDisplay.FormatPrimitive(…)`. The 4 copies of the status-argument ternary are one `StatusArgument(statusKey)`. Doubles were already invariant (`ToString(CultureInfo.InvariantCulture)`), so they are unchanged. Loop indexes are left as plain interpolation (never negative). |
| C6 | done | prod −15 (+68/−83); 8 files | none | Byte-identical to C5 (tree and logs). | Comment-only edits, plus `using System.Globalization` in `RecordSynthesizer`: LOW. | Stripped 59 tracker tags from `Import/**` and kept the *why* text: `P2 wave 4/5:`, `WP-1.1:`, `I1`–`I14` (including "(I3 guard)" and "(I3 residual)"), `GAP-2`, `FABLE_ROUNDTRIP #n`, `I.A-15/17`, `planner-constraint:` and `acceptance:`. Merged the stacked `<summary>` on `SchemaMapper.AddExtraRecord`. Removed restating comments in `OpenApiImporter.Import` (`// Parse schemas`, `// Parse paths → contracts`, `// Emit contract files`, `// Emit synthetic …`). Replaced the fully qualified `System.Globalization` in `RecordSynthesizer`. (C1 had already removed the fully qualified `System.Text.Json.Nodes` in `BreakAliasCycles`.) Fixed comments made stale by C4: aliases can no longer be cyclic in the mapper. Plumb: `[]`. |
| Lane C total | done | `git diff --shortstat slop/wave0`: prod `Rivet.Tool/Import/**` (10 files) +924/−1624 = −700 net; tests +238/−15 (8 new test methods, 12 cases, in `OpenApiImporterTests` and `CliExitCodeTests`, plus retargets) | Rivet.Tests 1927 → 1939; RuntimeTests unchanged | Import only. C1: 4 `RivetDocument.cs` files change JSON escaping and whitespace only (the same JSON values). C3: 110 new synthetic types, all real old collisions. C4: 356 renames from `Foo2` to `Foo_2`. Emit, `--from`, `--security` and CLI exit codes: identical throughout. | CRITICAL on the import entry flow in every WP. All changes stay in `Import/**`, the new tests and the ledger/CHANGELOG. | **Follow-ups (cross-lane):** (1) delete the now-unproduced `Diagnostics.ImportAliasRefCycle`/RIV3007 (`Diagnostics.cs`, `docs/reference/diagnostics.md`, `docs/reference/import-profile.md`, `ImportMetricTests.cs:64`). (2) Emit's `InlineTypeExtractor` still disambiguates with `Foo2`; consider moving it to the import's `Foo_2` scheme (Lane A/W2). (3) `ContractBuilder.ResolveResponseHeaders` de-duplicates headers on `StatusCode`, so `4XX` and `default` (both 0) collide; key it on `StatusKey` (import behaviour change). (4) An inline schema that differs only in an importer-read extension (`x-enum-varnames`, `x-rivet-*`) still shares a synthetic type with its twin. (5) `GetRawText`-era provenance: rivet-ts does not read `RivetDocument.cs`, so there is no coupling impact. |
| D1 | done (partial: overloads deleted; base-type consolidation blocked) | prod −306 (+4/−310), tests −32; 4 files | −1: `FileMediaTypeTests.Existing_five_argument_signatures_remain_available` (reflection pin for the removed overloads). Rivet.Tests 1927; RuntimeTests 100/101/101. | Clean | The 18 five-parameter `File` overloads LOW (0 upstream; source callers bind to the six-parameter form). `RivetTerminal.File`/`PhysicalFile` HIGH (8 direct, all the forwarders); only the `contentType = null` default was dropped. `detect_changes`: 5 symbols, 0 flows, risk low. | Grep: `Retained for callers` / `five-parameter` now appear only in this spec. API diff: exactly the 18 `File(…, string entityTag)` overloads removed (CHANGELOG). **Not done, by design:** moving `Error`/`File` into a shared `TerminalRouteDefinitionBase<TSelf>` and a bound-terminal base. `CoverageChecker.IsRivetTerminalInvocation` (`Rivet.Tool/Analysis/CoverageChecker.cs:~184`) matches `method.ContainingType.OriginalDefinition` against the six concrete types, so inherited terminals would disappear from coverage. That breaks the Lane D constraint and needs a Lane B file. **Follow-up (Lane B / W2):** have `IsRivetTerminalInvocation` accept the new base types (or walk `BaseType`), then move the 5 per-class terminal forwarders (~150 LOC) into the two bases. |
| D2 | done | prod −251 (+242/−493; 3 files incl. new `RouteDefinitionBase.SpecOnly.cs` and `Rivet.Attributes/.editorconfig`), tests −71 (+95/−166; 7 files) | −12, +2 (Rivet.Tests 1927 → 1917; RuntimeTests unchanged at 100/101/101). Deleted, all getter-only and covered by walker/emitter tests: `FileRouteDefinitionTests.QueryAuth_*` ×8 (the walker pins default, custom, `Get`, `File<T>`), `AcceptsBinaryTests.AcceptsBinary_Defaults_To_OctetStream`/`_Sets_Custom_ContentType` (the walker pins both), `HeaderSupportTests.Builder_Exposes_DeclaredResponseHeaders` (`WithResponseHeader_AttachesToExplicitAndSuccessStatuses` / `Emitter_Writes_Response_Headers_Shape` pin emission) and `Builder_Rejects_DuplicateHeaderPerStatus` (the guard is deleted). Rewritten to wire behaviour: 7 `FileRouteDefinitionTests` now execute `File(...)` and assert the response `Content-Type`; `AcceptsBinary_Survives_Accepts_Conversion` asserts `AcceptsFile()` still conflicts after `.Accepts<T>()`; `Returns_DefaultSuccessStatus_Can_Precede_Status_Override` executes both terminals; the runtime half of `Delete_Default_Status_Runtime_And_Walker_Agree` moved to `ContractTerminalResultTests` (new `Success_DeleteWithOutput_WithoutExplicitStatus_DefaultsTo200`). Added walker test `QueryAuth_OnInputAndConvertedDefinitions_SetsQueryAuth` (`Get<TIn,TOut>` and `.QueryAuth().Accepts<T>()`). | Clean | `AddResponseHeader` CRITICAL (its 6 direct callers are the `WithResponseHeader*` overloads, now markers); `RouteResponseHeader` MEDIUM; every getter, `QueryAuth`, `Anonymous`, `Secure`, `Parameter`, `RequestExampleJson`, `AcceptsContentType` and `StatusKey` LOW (0 upstream). `detect_changes`: 22 symbols, 0 flows, risk low. | Spec-only methods live in the partial `RouteDefinitionBase.SpecOnly.cs` as `=> Marker()` (`Mutate(static s => s)`, so they still throw after publication). The new nested `Rivet.Attributes/.editorconfig` turns IDE0060/CA1801 off for that file only; without it there are 236 IDE0060 errors. `RouteState` lost `Summary`, `Description`, `Anonymous`, `SecurityScheme`, `RequestContentType`, `QueryAuthParameterName` and `ResponseHeaders`. API diff: exactly the 15 getters and `RouteResponseHeader` removed (CHANGELOG). Grep: no getter use in `samples/`, `docs/`, `tools/` or `Rivet.Tool`. Kept `Method`/`Route`: the runtime reads them for error messages. **Follow-up (Lane A/B):** with the runtime duplicate-header guard gone, nothing rejects `.WithResponseHeader("ETag").WithResponseHeader("etag")`. `ContractWalker.ApplyResponseHeaders` concatenates, and `OpenApiEmitter` (~1252) builds a case-sensitive `headerObjs` map, so both spellings are emitted. Add a generation-time RIV error for case-insensitive duplicates per status. **Follow-up (W2-5 / Lane D):** `RouteErrorResponse` no longer has a public reader; D4 makes it internal. |
| D3 | done | prod −63 (+406/−469 in `EndpointBuilder.cs`), tests −9; 3 files | Replaced `Publication_and_mutation_synchronize_on_the_same_gate` (reflected into `_publicationLock`, `Monitor.Enter` + `Thread.Sleep`) with `Racing_mutation_either_reaches_the_published_contract_or_throws`: a `Barrier` races `.Returns(404)` against `Success(...)` 200 times and asserts that either `Error(404)` works (mutation landed before publication) or the mutation threw and `Error(404)` is a contract violation. Counts unchanged: Rivet.Tests 1927, RuntimeTests 101/102/102. | Clean (0 tree lines; exit codes identical) | `BeginMutation` CRITICAL (39 direct, all builder methods in the same file), `MutationLease` CRITICAL, `Publish` CRITICAL (21 direct: the terminals and `Bind`), `CopyStateTo` LOW. Behaviour is unchanged; all callers are inside `EndpointBuilder.cs`. | Builder state is one immutable `internal sealed record RouteState` (with `ImmutableList` collections), so `.Accepts<T>()` hands it over in one assignment (`CurrentState()`) and a new field can't be lost. It has to be top-level `internal`, not a private nested record, because `RouteDefinitionBase<A>` and `<B>` would get different nested types. Mutation goes through `Mutate(state => …)`, a plain `lock (_gate) { ThrowIfPublished(); … }`. `_published` is gone (it was `_publishedContract is not null`). `Publish` takes a lock-free fast path on a `volatile` published contract, so terminals no longer lock per response. API diff: removed protected `BeginMutation()` and `CopyStateTo()` (CHANGELOG). `grep _publicationLock Rivet.RuntimeTests` → nothing. |
| D4 | done (except the `FileRouteDefinition.ContentType` alias, blocked on Lane C) | prod −82 (+399/−481; 6 files), tests +81 (+97/−16; 2 files) | Red first: `Resolver_configured_polymorphic_subtype_is_accepted` ×2 (BUG-11; failed with "undeclared members could reach the wire"), `Media_type_parameters_do_not_split_a_declared_representation` ×2 and `File_content_type_selection_ignores_media_type_parameters` ×2 (failed with "multiple non-JSON representations" / "does not declare … 'text/csv'"). `Unregistered_polymorphic_subtype_is_rejected_at_terminal_construction` became `…_before_the_response_is_written` (Theory ×2). 4 `EnforcementHonestyTests` now expect the derived-type violation from execution, not from `Success(...)`. RuntimeTests 100/101/101 → 107/108/108; Rivet.Tests 1917. | Clean | `CreateFile`, `BuildResponse`, `GetExactStatus` and `RivetBodyResult` CRITICAL; `EnsureNotFile` HIGH; `ValidatePayload`, adapters, `RivetUnionJsonConverter`, `AddErrorResponse` LOW. All callers are inside `Rivet.Attributes`; no Tool or sample references. `detect_changes` (worktree vs HEAD): 76 symbols, 65 flows, risk critical, all within `Rivet.Attributes`. Plumb `[]`. | **BUG-11:** the derived-type check moved to `RivetTerminal.EnsureDeclaredRuntimeType`, which the JSON adapter path calls with the MVC or Minimal API options (still resolved separately) through `options.GetTypeInfo(expected).PolymorphismOptions`; `suppliedType` dropped. **Media types:** `ResponseRepresentation.Create` parses once with `MediaTypeHeaderValue` (`Suffix`/`SubTypeWithoutSuffix`, charset). Representations are keyed by the media type without parameters. `ResponseContract` carries the selected body representation and the binary set, so `SelectRepresentation`, both hand-rolled `IsJson`s and the per-call re-filtering are gone, and `RivetBodyResult` carries `IsJson`. `File(contentType: …)` matches parameter-insensitively and puts the caller's value on the wire. **Entity tag:** `TryParse && !Equals(Any)`. **Status keys:** one `ResponseStatusKey` (exact / nXX / default) parsed once in `AddErrorResponse`. The `StatusCode = 0` sentinel, `GetExactStatus`, `IsRangeStatusKey` and the two unreachable `Publish` throws are deleted. The `Returns(…, description)` overloads moved to the spec-only file (the description is Tool-only). **Adapters:** one `BeginResponse` sets the status once. `RivetJsonResult`/`RivetMvcJsonResult` became one `WriteJsonAsync` over `HttpResponse.WriteAsJsonAsync(value, type, options, contentType, ct)`. `RivetUnionJsonConverter<T>` is internal and caches its constructor and parameters. The route constructors lost their `"GET"`/`""` defaults. `HasBody => ContentType is not null`. New: a body representation with a malformed media type is a violation (it used to fall through to text). API diff: `RouteErrorResponse` and `RivetUnionJsonConverter<T>` removed from the public surface (CHANGELOG). **Not done:** deleting `FileRouteDefinition(.<T>).ContentType`. `Rivet.Tool/Import/CSharpWriter.cs:~1093` (Lane C) scaffolds `.ContentType("…")` for imported file endpoints, and imported code is compiled against `Rivet.Attributes` in tests, so the golden import output would change. **Follow-up (W2-5):** switch `CSharpWriter` to `.ProducesFile(…)`, drop the `"ContentType"` arm in `ContractWalker.cs:~773` (Lane B), then delete both aliases and update `samples/FunctionsApi`, `samples/ContractApi`, `docs/guides/file-uploads.md` and the ~7 test files that call `.ContentType(`. |
| E1 | done | tests −1,507 net (+2,266/−3,773; ignoring whitespace +1,090/−2,597), `Taskfile.yml` +4/−3; 40 files | None added or removed: Rivet.Tests 1927 → 1927, RuntimeTests 101/102/102, py 11 OK. | n/a (test-only) | `impact`: `CliRunner` LOW; `CompilationHelper.EmitOpenApi` CRITICAL (73 test callers), `RunDiff` CRITICAL (50 test callers), `FindRepoRoot` HIGH (15). All callers are test methods; `detect-changes`: 299 symbols, 0 processes, risk low. | `CliRunner` now owns `RepoRoot` (the one `FindRepoRoot`), `RunAsync`, `RunNode`, `StartServerAsync` → `ServerProcess`, and private `MakeBuildHermetic`; `RoundTripDiff.Run` + `RoundTripDiffReport` replace the three `RunDiff`s and two `FindingCount`s. `TestInfrastructure` gains `TempDir` (replaces 45 try/finally blocks, 5 class-level temp fields, `TemporaryDirectory`, `OperationIdentityWork`, `PublishFixture`'s dir and `CliExitCodeTests.DeleteWork`), `Fixture` (5 `LoadFixture`s), `FixtureSlices` (the duplicated GitHub/Twilio slice builders; the importer copy now also carries Twilio's `securitySchemes`, which the round-trip copy already had) and `JsonRefs` (5 `CollectRefs`, 2 `ResolvesInDocument`, `CliPipelineTests.CollectDanglingRefs`). `CompilationHelper.EmitOpenApi` takes optional `security`; the `OpenApiEmitterTests`/`DeepReviewFixTests` copies and 2 forwarding wrappers are gone (those callers now walk contracts and annotations merged, as the shared helper always did). `grep -c "static string FindRepoRoot"` → 1. `TemporaryJson` kept: it owns a file, not a directory. **Deviation:** `StartServerAsync` is a per-test helper, not an `IClassFixture`; a class fixture would boot a server for the build-only tests in the same class and share one host across tests that each expect a fresh one. **Taskfile (Wave 0 follow-up):** `format` runs analyzer fixes then CSharpier; `format:verbose` renamed `format:check` (no other references). **Merge note (Lane C):** `OpenApiImporterTests`/`RealWorldImportTests` lost their local `LoadFixture` — new tests use `Fixture.Text(name)`. |
| E2 | done | tests −497 (+18/−515); 2 files | None added or removed: Rivet.Tests 1927 → 1927. | n/a (test-only) | `RoundTripGateValidator` LOW (only the corpus gate calls it). | `RoundTripGateValidator.Validate(emittedPath)` is now 30 lines: Microsoft.OpenApi `Parse` diagnostics (its reader already applies the default `ValidationRuleSet`, so a separate `Validate` call added nothing) plus the 3.1 dialect check. Deleted: root/info/paths structure, unresolved local `$ref`, undefined and non-object security requirements, route-token/path-parameter mismatch (both directions), duplicate operationId (and its authored/emitted label), operation-not-an-object, and component-identity leak. **Kept and why:** the parser diagnostics (the library's own check) and the 3.1 dialect (the comparator accepts any 3.x/2.x). **Mutation check** (petstore-v3 `first-openapi.json`, each defect injected into a copy and run through both the first diff (source → mutant) and the fixed-point diff (first → mutant), plus the slim validator): 3.0.3 dialect → validator (plus 14 parser errors); missing `info` → comparator `document-structure` + parser; `paths` as array → comparator exit 1 + parser; dangling request-body `$ref` → `unresolved-reference`; undefined scheme in an operation → `undefined-security-scheme`; undefined root security → `undefined-security-scheme` ×11; non-object security requirement → `operation-security`; extra route token → `path-parameter-mismatch` + missing/invented operations; path parameter with no token → `path-parameter-mismatch` + parser; duplicate operationId → `duplicate-operation-id`; operation as a string → missing operation + parser; leaked schema → `unmatchedReemittedSchemas` + `component-invented`. Every mutation fails the remaining gate; the unmutated document has 0 findings on both. |
| E3 | done | −1,118 (+1/−1,119): `tools/roundtrip-audit.py` (790) and `tools/test_roundtrip_audit.py` (327) deleted; 4 files | −11 py (the audit's own tests). Rivet.Tests 1928, RuntimeTests 101/102/102, py 3 OK. | n/a | Tool-only; nothing in the solution referenced the script. | `Taskfile.yml` `test` now runs only `tools/test_openapi_common.py` after `dotnet test`. Dropped the "Round-trip audit" row from the V1 gate table in `docs/plans/post-v0.42.0-remediation.md` §8 (its "Completed evidence" line is historical and stays). `grep -rn roundtrip.audit .` now matches only this spec. `task test` green; Plumb `[]`. |
| E4 | done | +61 net (+218/−157); tools scripts −26 (`roundtrip_diff` −47, `roundtrip_inventory` −36, audit −10, new `openapi_common.py` +67), new tests +87; 11 files | +1 C#: `RoundTripProfileInventoryTests.Inventory_Proof_Names_Are_Existing_Tests` (parses the 17 `*_PROOF` names from `roundtrip_inventory.py` and asserts each is an existing `[Fact]`/`[Theory]` by reflection; red-checked by renaming one proof). +3 py in `tools/test_openapi_common.py`: BUG-12 red first (`%2F`, `%7E` and an array index resolved differently by the two scripts: 4 failing sub-cases), RFC 6901 cases, and component namespaces. Rivet.Tests 1927 → 1928; py 11 → 14. | n/a | Test/tool-only. | `tools/openapi_common.py` holds `METHODS`, `COMPONENT_NAMESPACES`, `load_json`, `canonical`, `pointer_token`, `pointer_parts` (split, then unescape and percent-decode per token, matching `Rivet.Tool/JsonPointer`) and `resolve_local_reference` (with array indices). `roundtrip-diff.py` → `roundtrip_diff.py`, `roundtrip-inventory.py` → `roundtrip_inventory.py`; callers updated (`RoundTripDiff`, the gate and inventory tests, `Taskfile.yml`, `docs/reference/import-profile.md`); the audit's `importlib` loader became a plain import (the audit goes in E3). `component_counts` now counts only the ten OpenAPI component namespaces (unknown ones are already reported by the inventory walker); inventory output and the verified profile are unchanged. **Deviation:** `component_counts` and `operation_count` stay in `roundtrip_inventory.py`, their only consumer once G4 deletes the audit. `Taskfile.yml` `test` runs `test_openapi_common.py` too. |
| Lane E total | done | `git diff --shortstat slop/wave0`: 51 files, +2,499/−5,555 (net −3,056) | Rivet.Tests 1927 → 1928; py 11 → 3 | n/a | `detect-changes` vs `slop/wave0`: 49 files, 314 symbols, 0 processes, risk low | **Follow-ups:** (1) Lanes that add tests to `OpenApiImporterTests`/`RealWorldImportTests`/`RoundTripDiffTests` must use `Fixture.Text`/`Fixture.Json`, `RoundTripDiff.Run` and `TempDir` (the local `LoadFixture`/`RunDiff` copies are gone). (2) `ServerProcess.DisposeAsync` kills only the `dotnet run` process, not the app it spawned (behaviour kept from the old helpers); consider `Kill(entireProcessTree: true)`. |
| W2-1 | done | prod −82 (+144/−226; 5 files), tests +145 (+221/−76; 6 files) | Rivet.Tests 1944 → 1949. Red first (3 failed before the change): `Import_Compile_Emit_Preserves_Every_Constraint_Facet` (spec → import → compile → walk → emit keeps all 11 keywords), `Exclusive_Range_Emits_Numeric_Exclusive_Bounds`, `Length_Attributes_On_A_Collection_Emit_Item_Counts`. New: `MinLength_On_IEnumerable_Counts_A_Materialized_Sequence`/`…_Throws_For_A_Lazy_Sequence` (the spec's plain-`IEnumerable` note: `[MinLength]` counts via `ICollection`/`Count`, so a deserialized list validates and a lazy sequence throws `InvalidCastException`; documented). The three `…_Survives_Numerically` source tests combined `[Range]` with `RivetConstraints(Exclusive…)`, which C# can no longer express; they became one model-level Theory (`Inclusive_And_Exclusive_Bounds_Survive_Numerically`, ×3), since `--from` JSON and generated-schema metadata can still carry both. Validation, metadata, conformance and importer fixtures moved to the DataAnnotations form. | Emit, `--from` and `--security`: identical (the ContractApi `Tags` sample moved to `[MaxLength(5), RivetConstraints(UniqueItems = true)]` and still emits `maxItems: 5, uniqueItems: true`). Import: 12 specs change, all intended: `RivetConstraints(MinItems/MaxItems…)` → `[MinLength]`/`[MaxLength]`/`[Length]` (+ `RivetConstraints(UniqueItems = true)` where set), 11 `RivetConstraints(ExclusiveMinimum = N)` → `[RangeAttribute(N, double.MaxValue, MinimumIsExclusive = true)]`, 264 added `using System.ComponentModel.DataAnnotations;`, and the matching `RivetImportedSourceFile` fingerprints. Logs identical. | `TypeWalker.ReadConstraints`/`ReadDataAnnotationConstraints` CRITICAL (1 direct caller, `WalkType`, on every emit path; proceeded under decision G5, golden emit unchanged); `CSharpWriter.EmitConstraintAttributes` LOW; `RivetConstraintsAttribute` LOW (0 Tool callers). `detect_changes`: 15 files, 49 symbols, 8 flows, high. | `RivetConstraintsAttribute` keeps `MultipleOf`/`UniqueItems`. `TypeWalker` reads one attribute set in one `ReadConstraints(attributes, isArray)` (the DA/Rivet merge block is gone); `[MinLength]`/`[MaxLength]`/`[Length]` are items on an array property and characters otherwise (so `[MaxLength]` on a list now emits `maxItems`, not `maxLength`); `[Range]` reads `MinimumIsExclusive`/`MaximumIsExclusive`; `[Length]` is new in `WellKnownTypes`. New `AttributeData.NamedArgument(name)` extension. `CSharpWriter` scaffolds DataAnnotations only (`[Length(min, max)]` for both item bounds, `StringLength` for both string bounds unchanged); when a side has both an inclusive and an exclusive bound it keeps the tighter one (not present in the corpus). Docs: `attributes.md`, `runtime-validation.md`. rivet-ts: unaffected (it reads emitted siblings only). Plumb `[]`. |
| W2-2 | done | tests −168 (+1,831/−1,999; 12 files); prod 0 | Rivet.Tests 1949 → 1947. Deleted as duplicates: `DeepReviewFixTests.OpenApi_Guid_Emits_Format_Uuid` and `OpenApi_DateTime_Emits_Format_DateTime` (their `uuid`/`date-time`/`date` assertions are also in `FormatRoundTripTests`, `ContractEmitterTests`, `EndpointMetadataSurfaceTests`, `ScalarMetadataRoundTripTests` and `OpenApiRoundTripTests`). Every other test moved unchanged apart from its name and comments. | Identical to W2-1 (no production change). | None: test-only moves. | `Wp12GapFillTests.cs` (32 tests) is gone: inheritance, type-name collisions and route-bound naming → `ContractEndpointTests`; the inheritance schema test and optional parameter/body tests → `OpenApiEmitterTests`; the inheritance import round trip → `OpenApiRoundTripTests`; route tokens, generic `[ProducesResponseType<T>]`, `[FromHeader]`/`[FromServices]` and default-valued action params → `ControllerEndpointTests`; `[Range]` bound parsing → `MetadataAttributeTests`; nullable type parameters and template walking → `GenericTypeTests`; `$ref` aliases → `OpenApiImporterTests`. The `A3_`/`E8_`/`I1_`… prefixes were dropped. `DeepReviewFixTests.cs` (16 kept) is gone: formats → `FormatRoundTripTests`; nullability/required → `OpenApiEmitterTests`; multipart → `FormFileTests`; input naming/types → `ContractEndpointTests`; import fidelity → `OpenApiRoundTripTests`; the generic-argument Theory → `GenericTypeTests` as `Monomorphised_Generic_Records_The_CSharp_Type_Argument`; the attribute-identity test → `MetadataAttributeTests`. The local `WalkEndpoints` helper was inlined. `GapAnalysisTests` → `ExampleFidelityMetricTests`; its two corpus report tests were already gone, and the dead `CollectAllRefs` helper went with them. `P2 wave 5`/`BUG-n` comments in the moved tests were reworded. |
