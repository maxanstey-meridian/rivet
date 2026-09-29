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

A runnable version that also logs stderr and exit codes lives at `/tmp/rivet-golden.sh <outdir>` (Wave 0); diff the logs as well as the trees. Baseline failures (both expected, both RIV2002): plain `--project samples/ContractApi` (its `.Secure("bearer")` needs `--security`), and `ContractApi-multi` (`a=bearer` does not define `bearer`). The Taskfile has no `format:check`; use `task format:verbose`. `task format` runs CSharpier then `dotnet format`, and the second pass can add blank lines that CSharpier rejects, so run `dotnet csharpier format .` again after it.

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
- **Wave 2 (serial, after all lanes merge):** W2-1 (G5), W2-2 (test-file folding), W2-3 (CLI, G6), W2-5 (cross-lane API removals, G3/G9), W2-4 (final sweep, last).

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

#### W2-4 — Final sweep
Run the §3 gate, the full golden diff, Plumb and `detect_changes` across the whole change. Update the ledger with final LOC deltas (`git diff --stat main`).

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
| D3 | done | prod −63 (+406/−469 in `EndpointBuilder.cs`), tests −9; 3 files | Replaced `Publication_and_mutation_synchronize_on_the_same_gate` (reflected into `_publicationLock`, `Monitor.Enter` + `Thread.Sleep`) with `Racing_mutation_either_reaches_the_published_contract_or_throws`: a `Barrier` races `.Returns(404)` against `Success(...)` 200 times and asserts that either `Error(404)` works (mutation landed before publication) or the mutation threw and `Error(404)` is a contract violation. Counts unchanged: Rivet.Tests 1927, RuntimeTests 101/102/102. | Clean (0 tree lines; exit codes identical) | `BeginMutation` CRITICAL (39 direct, all builder methods in the same file), `MutationLease` CRITICAL, `Publish` CRITICAL (21 direct: the terminals and `Bind`), `CopyStateTo` LOW. Behaviour is unchanged; all callers are inside `EndpointBuilder.cs`. | Builder state is one immutable `internal sealed record RouteState` (with `ImmutableList` collections), so `.Accepts<T>()` hands it over in one assignment (`CurrentState()`) and a new field can't be lost. It has to be top-level `internal`, not a private nested record, because `RouteDefinitionBase<A>` and `<B>` would get different nested types. Mutation goes through `Mutate(state => …)`, a plain `lock (_gate) { ThrowIfPublished(); … }`. `_published` is gone (it was `_publishedContract is not null`). `Publish` takes a lock-free fast path on a `volatile` published contract, so terminals no longer lock per response. API diff: removed protected `BeginMutation()` and `CopyStateTo()` (CHANGELOG). `grep _publicationLock Rivet.RuntimeTests` → nothing. |
| D1 | done (partial: overloads deleted; base-type consolidation blocked) | prod −306 (+4/−310), tests −32; 4 files | −1: `FileMediaTypeTests.Existing_five_argument_signatures_remain_available` (reflection pin for the removed overloads). Rivet.Tests 1927; RuntimeTests 100/101/101. | Clean | The 18 five-parameter `File` overloads LOW (0 upstream; source callers bind to the six-parameter form). `RivetTerminal.File`/`PhysicalFile` HIGH (8 direct, all the forwarders); only the `contentType = null` default was dropped. `detect_changes`: 5 symbols, 0 flows, risk low. | Grep: `Retained for callers` / `five-parameter` now appear only in this spec. API diff: exactly the 18 `File(…, string entityTag)` overloads removed (CHANGELOG). **Not done, by design:** moving `Error`/`File` into a shared `TerminalRouteDefinitionBase<TSelf>` and a bound-terminal base. `CoverageChecker.IsRivetTerminalInvocation` (`Rivet.Tool/Analysis/CoverageChecker.cs:~184`) matches `method.ContainingType.OriginalDefinition` against the six concrete types, so inherited terminals would disappear from coverage. That breaks the Lane D constraint and needs a Lane B file. **Follow-up (Lane B / W2):** have `IsRivetTerminalInvocation` accept the new base types (or walk `BaseType`), then move the 5 per-class terminal forwarders (~150 LOC) into the two bases. |
