# Post-v0.42.0 repair and Meridian cleanup plan

Status: complete; implemented and reviewed. Prepared for release v0.44.1.
Created: 2026-09-21.
Review baseline: `v0.42.0..570e6e1` (five commits, 65 changed files).
Purpose: persistent implementation brief and completion ledger for this review. Stored outside gitignored `reviews/` so it can be versioned with the fixes.

## 1. Objective and scope

Complete the useful explicit-contract work introduced after v0.42.0, repair its correctness gaps, and simplify the affected code according to Meridian. Preserve existing valid behaviour; make unsupported declarations fail clearly instead of generating a successful but incomplete or false contract.

This file authorizes no release, publishing, or unrelated redesign. Implementation was requested on 2026-09-21. Work and evidence are recorded below. No release or publishing is included.

Primary doctrine:

- `/Users/max/.config/opencode/skills/meridian/SKILL.md`
- Its `references/coding-philosophy.md`, `references/testing-philosophy.md`, `references/rivet.md`, and relevant invariant-ownership guidance.
- Repository `AGENTS.md` for GitNexus requirements; `CONTRIBUTING.md` and `Taskfile.yml` for repository checks. Reconcile stale contributor guidance with current Meridian and actual product behaviour rather than mechanically following contradictory examples.

### Preserve these improvements

- Explicit parameter bindings instead of scalar-to-query, complex-to-body, interface-to-service, or neighbour-of-file inference.
- The narrow retained conventions: route-name matching, host cancellation tokens, and coherent file transport declarations.
- Explicit response contracts; no invented success response from `ActionResult<T>`, void, non-generic Task, or variable-status results. Retain the documented concrete-payload convenience and genuinely fixed-status typed results.
- Interface-based detection of runtime-selected `IResult`/`IActionResult` containers.
- `[RivetScalar]` as explicit scalar opt-in with actual runtime conversion; unannotated single-Value records stay ordinary objects.
- Numeric ordinary enums, exact names for bare string converters, and framework-backed named enum converters.
- Exact legal integral enum constants, including signed and unsigned 64-bit extremes.
- Explicit file contracts, ordinary byte[] JSON/base64 behaviour, body-forbidden response checks, and existing operation identity/coverage fixes.
- Dependency security updates, inclusion of Local tests in the release gate, and OS-assigned test-server ports.

### Governing invariants

1. A successfully emitted operation accounts for every declared transport input and supported response branch.
2. An unsupported or contradictory declaration fails with an actionable diagnostic before new output is published; it never silently changes transport or wire type.
3. Supported runtime scalar/enum representations agree with generated schemas under the documented serializer configuration.
4. Imported generated code compiles in the advertised consumer context without accidental ambient imports.
5. Compatibility is removed only for a real requirement, not an incorrect API-version assumption.
6. Each fact has a clear owner: declarations and validation in analysis, valid representation in the IR, rendering in emission, and runtime conversion at the attribute/converter boundary. Share semantic rules where warranted without coupling Roslyn into the runtime package.

## 2. Historical review evidence and initial limitations

The original seven review findings comprise six reproduced behavioural/compiler failures plus one framework-compatibility finding. The pasted earlier review exposed two additional source-confirmed binding gaps.

Focused probes reproduced:

- Policy-enum import produces CS0246 for an unqualified Rivet converter.
- A complex `[FromForm]` DTO plus a scalar `[FromForm]` parameter emits only the scalar field.
- `Results<Ok<Dto>, Ok<string>>` throws `InvalidOperationException: Sequence contains no matching element`.
- A string-backed scalar dictionary key throws `NotSupportedException`.
- Colliding string enum names serialize as strings despite the numeric schema fallback.
- A scalar struct with nullable Value writes JSON null and rejects that same JSON when read.
- An additional bounded probe showed a Value-property string-enum converter being ignored by the scalar converter (numeric 0 was written). Treat the supported member-metadata policy as an explicit design/audit item below.

The serializer probes compiled current converter source. Analysis/import probes compiled current tool source directly against available references after normal restore/build was blocked. These establish focused behaviour; they are not a successful production-target build or full-suite run. Temporary `/tmp` harnesses are disposable and are not durable regression tests.

`dotnet test Rivet.Tests --no-restore` stopped on NETSDK1064: missing `System.Security.Cryptography.Pkcs` 9.0.20. Restore did not complete during review. Do not downgrade the security update to make the local cache work.

Plumb reported zero errors and two MER-RV-003 warnings in unchanged `samples/ImportDemo/Endpoints/MembersEndpoints.cs`, asking for `.Invoke`. Those handlers use the current `.Success`/`.Bind(...).Success(...).ToResult()` API. Verify the checker rule against current doctrine before changing correct sample code.

GitNexus MCP was unavailable. The CLI index was stale, and a query resolved an older sibling checkout at `/Users/max/Sites/medway/rivet` rather than this `/Volumes/max/Sites/medway/rivet` checkout. An attempted refresh did not finish. Do not use those query results as authoritative impact evidence.

Compatibility reference: Microsoft documents `JsonStringEnumConverter<TEnum>` and snake/kebab naming support in [.NET 8 System.Text.Json](https://devblogs.microsoft.com/dotnet/system-text-json-in-dotnet-8/). Member-name attribute support is a separate version question.

## 3. Work ledger

Completion requires code, relevant regression evidence, and docs where behaviour is public. Priorities indicate review urgency, not permission to skip lower-priority items.

| ID | Priority | Work | Status |
| --- | --- | --- | --- |
| P0 | Prerequisite | Establish clean baseline, working restore, correct GitNexus index | [x] |
| F1 | P1 | Generated policy enums compile | [x] |
| F2 | P1 | Mixed form input cannot disappear | [x] |
| F3 | P1 | Restore justified .NET 8 compatibility | [x] |
| F4 | P2 | Enum collisions never invent a numeric wire contract | [x] |
| F5 | P2 | String-backed scalar dictionary keys work | [x] |
| F6 | P2 | Nullable scalar structs round-trip null | [x] |
| F7 | P2 | Repeated response statuses fail deterministically | [x] |
| F8 | P2 | Service bindings participate in contradiction validation | [x] |
| F9 | P2 | JSON-body and form-body declarations remain distinct | [x] |
| A1 | Audit | Scalar shape, construction, member metadata and failure boundary | [x] |
| A2 | Audit | Enum/numeric/metadata completeness across all consumers | [x] |
| A3 | Audit | Response and binding interaction matrix | [x] |
| M1 | Cleanup | Meridian ownership, typing, local simplicity, comments | [x] |
| M2 | Cleanup | Behavioural tests and test infrastructure | [x] |
| D1 | Docs | Migration, supported surfaces, diagnostics and contributor guidance | [x] |
| V1 | Gate | Full verification, package inspection, impact scope and final review | [x] |

### P0 — Establish a trustworthy working baseline

- Record current HEAD, branch, dirty files, installed SDKs/runtimes and any baseline failures. Rebase this plan's evidence if HEAD has changed; preserve user changes.
- Restore the existing pinned packages and required vendored JS tooling. Inspect `Taskfile.yml`, local tool manifests, and test tooling README before running commands.
- Resolve GitNexus repository identity/path and refresh the correct index. Read its overview/freshness and relevant skill before using query/context/impact.
- Before editing any function, class, or method, run upstream impact for that symbol and report direct callers, affected processes and risk. Warn before HIGH/CRITICAL edits. If the tool cannot provide the required analysis, report that concrete prerequisite failure; do not silently pretend textual search is equivalent.
- Capture a reproducible baseline. A missing package, absent runtime, or stalled tool is an environment limitation, not a passing test or a product defect by itself.

### F1 — Generated policy enums compile

Owner: `Rivet.Tool/Import/CSharpWriter.cs`, `WriteEnum`; enum import/round-trip tests.

Problem: generated files import only `System.Text.Json.Serialization` but refer to `RivetCamelCaseEnumConverter<T>` and siblings without qualification. Namespace `Test` reproduces CS0246.

Desired outcome: each supported policy imports into compiling C# in an arbitrary consumer namespace and reproduces the original wire strings.

Approach: emit an unambiguous Rivet-qualified type reference (prefer `global::Rivet...` if that is the local generator convention) or a deliberate import. Do not depend on a consuming project's global using. Preserve member pins only where needed to retain exact values.

Acceptance:

- [x] All four policies compile after import, with no ambient Rivet global using.
- [x] Import → compile → forward emit retains the wire value set, including explicit pins and sanitised/colliding C# member names.
- [x] Unknown naming-policy tokens still use an honest exact-value path.
- [x] A runtime serialization check covers imported generated enums; substring assertions alone are insufficient.

### F2 — Mixed form input cannot disappear

Owners: `EndpointWalker.ExtractParams` / `ClassifyParam`, `OpenApiEmitter` request-body lowering, binding/form tests.

Reproducer: `Post([FromForm] Metadata metadata, [FromForm] string title)` emits only title because form-field emission takes precedence over bodyParam. File-plus-complex-body is already refused, but the no-file sibling is not.

Desired outcome: every explicit input is represented or the whole operation is refused. Parameter order must not change the result.

Default repair: reject unsupported complex-form-plus-scalar-form combinations at analysis with an actionable diagnostic. Extend the same invariant to JSON body plus form fields. Do not recreate recursive MVC form binding merely to accept these shapes. If an existing representation already supports a combination faithfully, use it and prove the actual wire result.

Acceptance:

- [x] DTO+scalar form, DTO+file, JSON body+form fields, and JSON body+file are represented faithfully or rejected; no body silently loses a branch.
- [x] Reordering parameters gives equivalent output/diagnostics.
- [x] Scalar-only forms, file+scalar forms, and supported standalone form DTOs remain valid.
- [x] Audit media-type overrides/Consumes against form emission: honour supported explicit choices or reject unsupported ones; do not silently discard them.
- [x] Supported examples are checked at the host boundary; unsupported examples fail without successful partial output.

### F3 — Restore .NET 8 compatibility

Owners: Attributes and RuntimeTests project targets, version-sensitive tests, framework documentation and release workflow.

Problem: net8.0 was removed because comments incorrectly say the generic string enum converter requires .NET 9. This blocks all existing .NET 8 package consumers.

Desired outcome: retain net8.0/net9.0/net10.0 runtime-library support where the actual API surface permits it. Do not retarget the tool itself merely because the runtime library supports older consumers.

Acceptance:

- [x] Build and test the runtime library on all three intended targets.
- [x] Basic enum policies and scalar conversion work on .NET 8.
- [x] Tests/features using newer member-name metadata are appropriately target-scoped; documentation distinguishes that capability from basic converter support.
- [x] A packed package contains the expected framework assets; a minimal .NET 8 consumer restores/builds against that package.
- [x] CI installs or otherwise supplies the actual runtimes required to execute the multi-target tests. Do not hide missing runtimes by skipping targets.
- [x] Correct false API-version claims in code comments, tests and docs. Preserve the security dependency update.

### F4 — Enum collisions preserve wire truth

Owner: `TypeWalker` enum analysis, RIV1106 diagnostics/docs and collision tests.

Reproducer: a camelCase converter over `FooBar` and `fooBar` writes `"fooBar"` for both; the walker instead emits an integer union after warning.

Desired outcome: a string converter never becomes a numeric schema because analysis encountered a collision.

Default repair: reject a colliding declaration with an actionable error naming enum, members and wire value. This avoids advertising a distinct reversible member mapping that is not present. Do not silently choose numeric output. If deliberate aliases are supported later, define that separately and test their actual serializer semantics.

Acceptance:

- [x] Policy collisions and duplicate explicit pins fail consistently with updated RIV1106 severity/docs.
- [x] Non-colliding explicit pins, exact names and all policies retain runtime parity.
- [x] Replace tests that canonise numeric fallback; compare actual serialized values with emitted schemas on successful cases.
- [x] CLI catches the analysis failure and returns the documented error status rather than publishing new misleading output or an unhandled stack trace.

### F5 — Scalar dictionary keys work

Owners: `RivetScalarJsonConverter`, scalar-key analysis and runtime/dictionary tests.

Reproducer: `[RivetScalar] record Key(string Value)` in `Dictionary<Key,int>` is accepted by analysis but throws NotSupportedException when serialized.

Desired outcome: the advertised string-backed scalar key surface reads and writes JSON object property names correctly.

Approach: implement the converter's property-name read/write surface using the underlying framework converter and the same wrapper construction rules as scalar values. Keep it bounded to supported key types; do not build a general key codec registry.

Acceptance:

- [x] Dictionary write and read round-trip string-backed scalar keys, including escaped/non-ASCII names.
- [x] Emitted propertyNames and actual object keys agree.
- [x] Non-string-backed scalar keys retain the intended supported/refused behaviour; the fix does not silently broaden the contract.
- [x] Null/invalid keys fail clearly at the appropriate boundary.

### F6 — Nullable scalar structs round-trip

Owner: `RivetScalarJsonConverter.Read`, null handling and scalar runtime tests.

Reproducer: `[RivetScalar] readonly record struct OptionalNumber(int? Value)` writes null but rejects it when deserialized, because the guard tests the wrapper rather than its scalar representation.

Desired outcome: a supported wrapper with nullable Value accepts the null representation it emits. Non-nullable inner values continue to reject invalid null input.

Acceptance:

- [x] Nullable-value structs round-trip both null and non-null values.
- [x] Nullable wrapper versus nullable Value semantics are explicit and tested.
- [x] Reference-wrapper null behaviour is documented: JSON null may not distinguish an absent wrapper from a wrapper containing null. Do not promise identity preservation where the wire has no distinction.
- [x] Framework HandleNull behaviour is used deliberately, with no recursion or accidental defaulting.
- [x] Generated nullability agrees with the supported runtime representation.

### F7 — Response merge owns status and body together

Owner: `EndpointWalker.ExtractAllResponseTypes`, typed-result mappings and response-fidelity tests.

Reproducer: `Results<Ok<Dto>, Ok<string>>` adds 200 to declaredStatuses on the first branch but not to declaredBodyTypes; the second branch executes First() over an absent entry and crashes.

Desired outcome: duplicate/conflicting response declarations produce the intended contract diagnostic. Attributes and typed branches cannot disagree silently or desynchronise parallel collections.

Approach: track each status with its body type and any diagnostic provenance needed in one local representation. Preserve current duplicate-status rules; do not invent union response support to avoid rejecting ambiguity. Redundant matching attribute/typed-result declarations may merge according to the existing documented policy.

Acceptance:

- [x] Same-status different payloads and body/bodyless conflicts produce actionable contract errors, not LINQ exceptions.
- [x] Matching attribute and typed-result declarations merge correctly.
- [x] Duplicate attributes and duplicate typed branches follow deliberate, tested rules.
- [x] Different-status branches remain complete; variable-status unresolved branches cannot hide behind attributes.
- [x] CLI surfaces a stable diagnostic and error exit without an unhandled exception.

### F8 and F9 — Validate declarations before lowering or exclusion

Owner: parameter classification/extraction in `EndpointWalker`.

F8: `[FromServices]` is skipped before source validation. `[FromServices][FromQuery]` therefore escapes the contradiction check.

F9: complex `[FromBody][FromForm]` declarations both lower to `ParamSource.Body`, so Distinct() cannot see that they are different transport authorities.

Desired outcome: collect and validate the original binding declarations first; only then exclude service plumbing or lower a coherent declaration to the emitted parameter category.

Approach: use a small local typed representation that distinguishes Services, Body, Form, Query, Route and Header. Keep this separate from ParamSource where the latter intentionally groups transport shapes. Do not create a general binding framework or strategy registry.

Acceptance:

- [x] Service+query/body/form/header/route contradictions fail instead of dropping input.
- [x] Body+form on a complex type fails even though both could lower to Body.
- [x] Existing query/route/header contradictions continue to fail, independent of attribute order.
- [x] Valid service-only parameters stay excluded; coherent FromForm file parameters remain supported.
- [x] Audit explicitly attributed cancellation tokens without reintroducing host inference: document/refuse contradictory declarations rather than letting an early plumbing skip erase them.

## 4. Bounded completeness audits

These are investigation requirements, not additional proven defects. Record the conclusion and evidence for each; do not present suspicions as reproduced failures.

### A1 — Scalar contract and runtime agreement

- Compare analyzer and runtime shape rules: public/readable Value, static/indexer exclusion, generic wrappers, extra properties, inheritance, constructor availability and accessibility. Currently Roslyn and reflection use different selection rules.
- Define supported shapes in terms of an observable scalar representation and reconstructible wrapper. Validate unsupported shapes predictably rather than emitting a schema for a runtime-only surprise.
- Check Value-property converters, number handling, nullability, format and supported constraints. A focused probe already showed a property-level enum converter ignored at runtime. Choose an explicit supported metadata policy: honour it consistently or reject/document unsupported declarations. Do not claim ordinary property metadata is preserved when serializing the raw property type bypasses it.
- Check constructor-domain validation failures: malformed request values should follow an appropriate serializer/host error path, not leak incidental reflection exceptions as avoidable server errors. Preserve unexpected exceptions as unexpected; do not catch everything and manufacture success.
- Examine conflicts with other type-level converters and avoid selecting an arbitrary winner.
- Remove the intermediate JsonDocument if direct framework deserialization from the reader is sufficient. Resolve/capture supported member/constructor information at converter creation rather than repeatedly discovering it for every value.
- Prefer a small typed inner-value converter if it simplifies delegation. Reflection at factory creation may be justified by the attribute boundary; reflection is not itself proof of a bug. Do not add runtime code generation, registries, DI services, or a serializer subsystem without a concrete requirement.
- Check trimming/AOT claims only if the project actually promises them; document limits honestly rather than silently expanding scope.

### A2 — Enum and numeric completeness

- Trace StringUnion/IntUnion and naming-policy metadata through TypeWalker, ContractEmitter, TsTypeJsonConverter, JsonContractReader, OpenApiEmitter, SchemaClassifier and CSharpWriter.
- ContractEmitter's top-level enum projection and JsonContractReader currently do not carry NamingPolicy. Determine whether the public contract-JSON path promises preservation; preserve it consistently if so, or document deliberate semantic loss without changing wire values. Keep the JSON schema in sync with any public field changes.
- Check every legal underlying enum type and boundary: Int32 extremes, UInt32 max, Int64 min/max and UInt64 max. Imported declarations must compile and re-emit exact numeric values.
- Audit integer-valued JSON forms such as exponent/decimal notation, mixed signed/oversized unsigned sets, nullable enum schemas and invalid literal carriers. Use the public import policy to decide support/refusal; do not silently truncate or quote invalid numeric values.
- Keep the wide literal representation if it remains the smallest honest choice, but centralise validation at its owner and remove misleading fallback-to-string paths. A new wrapper type earns its place only if it materially enforces validity across real consumers.
- Audit converter recognition by simple name, enum aliases and flags. Do not expand into arbitrary converter execution or flags-protocol emulation; clearly bound unsupported cases and record pre-existing versus newly introduced gaps.
- Audit newly added JSON converter read paths for correct document disposal and validated input. Do not use unchecked raw JSON output on externally supplied literals.

### A3 — Response and input interactions

- Trace direct fixed typed results, Results unions, ActionResult containers, explicit attributes, abstract contract methods and fluent contract fields through their actual owners.
- Confirm conflict policy is consistent where both signature and metadata provide facts. Check success/error-only declarations and avoid claiming that success is mandatory unless the product actually enforces that rule.
- Retain inherited/custom IResult and IActionResult detection and fixed-status exceptions.
- Check case/normalisation rules for route placeholders, explicit Name overrides and unrelated query/header parameters.
- Audit all request-body branches so source selection, content type and requestBody requiredness remain coherent. Do not fix only the one repro and leave a sibling branch with the same ownership error.
- Expected validation errors must be caught across eager type discovery as well as endpoint walking. Verify malformed scalar/enum declarations do not bypass the CLI exception boundary.

## 5. Meridian cleanup

### M1 — Production code

- Fix invariant ownership before adding guards at every caller. Keep declaration identity until validation is finished; keep response status and shape in one owned record/map.
- Retain framework-owned serialization/naming. The named enum wrappers are a useful small seam; do not replace them with a hand-written enum serializer or casing algorithm.
- Keep transformations explicit and local. Extract only repeated, stable concepts with real consumers; avoid generic validators, managers, registries, adapter hierarchies or broad walker rewrites.
- At framework/reflection boundaries, object-based APIs may be unavoidable. Narrow promptly into typed representations; do not spread untyped carriers into otherwise known internal models. Do not replace existing dynamic OpenAPI dictionaries wholesale as unrelated cleanup.
- Delete stale comments and planning artifacts such as acceptance/packet/planner labels when they merely narrate implementation. Retain genuine why-comments, framework constraints and stable diagnostic references. Keep planning traceability in this file and useful tests instead of production prose.
- Correct comments that make false claims about framework versions, null handling, shape parity, member-level options, or fallback semantics. Remove duplicate XML summaries and dead helpers in the touched area after impact analysis.
- Review new public helper/converter visibility: keep public only what the supported attribute activation/API surface requires; avoid growing an accidental public framework.
- Do not force application ports/adapters, CQRS or DDD furniture onto a compiler/generator library. The existing analysis/IR/emission/import/runtime boundaries are the relevant ownership structure.

### M2 — Tests and test infrastructure

- Test observable behaviour at the cheapest honest boundary: compilation for generated C#, real STJ for wire semantics, host integration for request binding and response output, pure tests for decision rules.
- Replace assertions that merely reproduce current code (numeric fallback, converter-name substrings) with the intended outcome. Keep useful structural assertions as supplements.
- Use a small number of shared runtime/compile fixtures only where repeated setup has proven a need. Reuse existing CompilationHelper and host helpers before introducing another harness.
- Avoid new sleeps, global mutable serializer state, or mocked framework behaviour. Preserve ephemeral-port allocation and correct server cleanup.
- Consolidate repetitive cases with readable theories where that improves clarity, without hiding materially different behaviour behind a giant matrix helper.
- Capture focused probes as repository regression tests; do not depend on temporary review artifacts.
- Do not delete existing regressions simply because the new policy makes them fail. Rewrite their expected declared contract or refusal, and preserve coverage of the original scenario.

## 6. Documentation and migration (D1)

- Document migration from v0.42.0: explicit From* bindings, explicit ambiguous-result responses, scalar opt-in, enum numeric default and type-level string policies. Give small before/after examples.
- Document the exact supported form combinations and remedies for refused ones.
- Explain supported scalar shape, construction, dictionary-key surface, null semantics and property metadata limits. Keep runtime and schema claims aligned.
- Correct RIV1106 severity/remedy and ensure all new/changed diagnostic IDs have registry entries, reference rows, CLI behaviour and accurate introductory error lists. Never reuse a retired ID.
- Preserve the already-corrected samples and upload guide; review tutorial, type mapping, limitations, attribute reference, sample READMEs and generated-code examples for remaining contradictions.
- Update `CONTRIBUTING.md`: its instruction to register JsonStringEnumConverter globally conflicts with source-level contract visibility. Explain type-level declarations and the limitation on arbitrary global serializer settings.
- Document .NET target support accurately, including capabilities available only on newer frameworks. Do not confuse tool runtime requirements with consumer package support.
- Keep documentation focused on user decisions, required declarations and resulting behaviour. Internal IR design and review packet history belong in contributor/review material, not routine user flows.

## 7. Execution order

1. **Baseline:** P0, current-code reconciliation and impact inventory.
2. **Compatibility/import:** F3 and F1. Establish supported targets and compiling round trips early.
3. **Binding invariants:** F8/F9 before F2, so mixed-body validation works from coherent declarations. Complete the relevant A3 checks.
4. **Response invariants:** F7 and remaining A3 checks.
5. **Scalar runtime:** F5/F6 with A1; settle the supported scalar contract once, then implement and prove it.
6. **Enum/numeric fidelity:** F4 with A2; preserve successful cases across both public input paths.
7. **Cleanup/docs:** M1/M2/D1 alongside each fix, then a final pass for cross-cutting leftovers.
8. **Integration gate:** V1 and final comparison with the original tag plus the implementation starting point.

Keep changes reviewable by concern. Before any eventual commit, run GitNexus detect_changes and inspect expected symbols/processes. Compare implementation changes against their starting commit; a comparison to main alone is insufficient if main is the working branch. Preserve the original v0.42.0 comparison for the overall review scope. Do not publish packages or create release tags as part of verification.

## 8. Verification gate (V1)

Use the repository commands after checking their current definitions. Do not repeat broad suites after they pass unless subsequent changes justify it.

| Layer | Required evidence |
| --- | --- |
| Focused regressions | Each F item has an outcome-based regression; each A item has a recorded conclusion and evidence |
| Build | `dotnet build ./Rivet.slnx` succeeds on restored dependencies |
| Full tests | `dotnet test ./Rivet.slnx` including Local tests and every supported runtime-test target |
| Round-trip audit | `python3 -m unittest tools/test_roundtrip_audit.py -v` plus importer compilation regressions |
| Samples | `task samples:build` or its current equivalent, including FunctionsApi |
| Real Functions gate | `task test:functions` with required Core Tools; missing prerequisites remain explicit blockers for this gate |
| Docs | `task docs:build` after docs changes |
| Formatting | Current repository CSharpier/analyzer verification on the affected change; avoid unrelated bulk formatting |
| Meridian | `~/Sites/plumb/plumb . --json`; fix error findings, fix warnings or record the exact applicable exception |
| Packages | Local Release pack, inspect framework assets, and build the .NET 8 consumer against the packed library; no publish |
| Impact | Correct-checkout GitNexus upstream analysis before edits, detect_changes before commits, final diff and affected-flow review |

The aggregate `task check` and `task ci` may satisfy overlapping rows; record which command covered which gate rather than running everything twice. Do not claim a full pass from the focused direct-compiler probes used during review.

For the two existing Plumb .Invoke warnings, inspect actual current terminal usage and the checker rule. If the rule is obsolete, record a narrowly evidenced exception or fix the owning checker in a separately scoped task; do not regress samples to an obsolete API to make the warning disappear.

## 9. Earlier-review reconciliation

These items from the pasted historical review are already addressed at 570e6e1. Preserve their regressions rather than reimplementing them:

| Historical issue | Current status / remaining relationship |
| --- | --- |
| Exact-symbol-only result-container guard | Fixed with interface checks |
| Bare string converter camelCases names | Fixed with exact names |
| Int32-only enum representation | Widened; completeness audit A2 remains |
| File convention beats explicit source | Fixed for coherent/noncoherent file sources |
| Header bypasses contradiction validation | Fixed; service and body/form gaps remain F8/F9 |
| FromRoute Name absent from template accepted | Fixed |
| File+complex form DTO disappears | Refused; no-file sibling remains F2 |
| Attributes hide unresolved Results branches | Fixed; merge crash remains F7 |
| Route token without input has no warning | Fixed |
| RivetScalar is marker-only | Runtime converter added; F5/F6/A1 finish its surface |
| Listed samples and upload docs retain old claims | Those examples fixed; D1 covers remaining guidance |

## 10. Completion ledger and handoff

For each work item, append:

| Item | Change/decision | Tests and result | Impact/risk | Commit or files | Remaining limitation |
| --- | --- | --- | --- | --- | --- |
| Plan creation | Consolidated current and historical reviews; no implementation changes | Plan structure/whitespace checked; Plumb rerun: 0 errors, the same 2 pre-existing MER-RV-003 warnings described above | No code symbols edited | This file | Historical entry; implementation and warning reconciliation completed below |

Definition of done:

- [x] F1–F9 resolved with durable regression evidence.
- [x] A1–A3 conclusions recorded, with new confirmed in-scope defects fixed or a specific supported-surface decision documented.
- [x] M1/M2/D1 completed without unrelated architecture churn.
- [x] V1 gates passed; any external blocker explicitly recorded and not represented as success.
- [x] Every supported successful path emits a truthful, complete contract; every unsupported path refuses predictably.
- [x] No unintended .NET 8 compatibility loss, security-package downgrade, ignored generated compilation failure, or hidden test exclusion.
- [x] Final report states what changed, why, evidence, intentional breaking changes and remaining limits. Update this document before handing work off.


## Implementation results — 2026-09-21

### Repairs and supported-surface decisions

- **F1:** qualified generated Rivet enum converter references; escaped explicit member wire names. Import tests compile generated enums in a separate namespace, execute their actual serializers, and forward-walk them. All four policies, unknown-policy exact pins, punctuation and escaped names are covered.
- **F2/F8/F9:** binding declaration identity survives until validation; service parameters and cancellation tokens cannot hide contradictions. Mixed bodies with scalar form fields or files fail RIV1104, independently of parameter order. Existing scalar forms, standalone DTO forms and file-plus-scalar forms remain supported. Controller/action Consumes metadata is carried to emission; one supported content type is honored, ambiguous or incompatible declarations refuse. Explicit route names match actual placeholders case-insensitively; punctuation is not erased.
- **F3:** restored net8/net9/net10 runtime targets and CI runtime installation. Only the member-pin test uses a NET9 guard. Basic converters and scalar tests execute on all three frameworks.
- **F4:** duplicate enum wire values fail RIV1106, never fall back to a numeric schema. String aliases/flags and unsupported or mistargeted converters fail RIV1108. Converter recognition checks namespace as well as name. Numeric aliases collapse to distinct declared values.
- **F5/F6/A1:** one public readable Value, matching public constructor, concrete/non-generic wrapper, no inherited wrapper shape. Runtime and Roslyn reject competing converters and property-level JSON settings. Reflection is resolved once at converter creation; inner values delegate directly to STJ without a JsonDocument. String-key conversion delegates to STJ's property-name converter; non-string scalar keys are unsupported. Nullable inner structs round-trip; reference null wrappers use standard STJ null semantics. Constructor ArgumentException becomes JsonException; unexpected constructor exceptions are rethrown without reflection wrapping. Type-level Rivet format/schema metadata is retained; collapsed Value-property metadata is not projected. No trimming/AOT claim is added.
- **F7/A3:** status/body facts use one dictionary; matching attributes/typed branches merge and conflicting shapes raise RIV1107. Duplicate authored attributes still pass through existing duplicate-status validation. Unresolved typed branches remain visible, inherited result-container detection remains intact, and error-only explicit declarations do not invent success responses. Eager type discovery now uses the CLI's contract-error boundary.
- **A2:** enum naming policy survives both nested IR and top-level contract JSON; both schema declarations include it. Numeric bounds cover signed Int64 and UInt64, including importer compilation. Invalid carriers refuse rather than becoming strings/raw unchecked JSON. Contract JSON accepts decimal integer syntax, not exponent/fractional forms. The OpenAPI importer retains its existing warning/degradation policy for nonrepresentable C# enums (including negative plus above-Int64 mixtures and unsupported numeric forms). Arbitrary global converters/options are not inferable. Enum schemas describe the declared constant set, not every arbitrary runtime numeric value or undeclared flags combination.
- **M1/M2/D1:** no dependencies, registries, code generation, binding framework or serializer subsystem added. Removed stale planning narration in repaired binding/scalar paths, the duplicate scalar XML summary, false framework claims, and the public scalar-converter implementation type. Regressions exercise compilation, actual serializers, CLI errors and existing host tests. Updated migration, diagnostics, schema and contributor guidance. Existing historical fixes were retained.

### Verification and environment

The workspace is an SMB mount. Restore succeeded there, but a parallel build encountered file-sharing errors and GitNexus refresh ultimately failed because LadybugDB locking is unsupported on SMB. Verification uses a local copy made from HEAD's tracked files plus every changed/new source file; sources are compared before handoff. No product build settings were weakened. Temporary SDK/runtime/dependency setup lives outside the repository.

Upstream impact ran before repaired symbols were changed. WriteEnum and type/enum mapping reported CRITICAL aggregate impact; endpoint extraction/response merging and BuildEndpoint reported HIGH; runtime converter methods and serialization helpers reported LOW. Warnings and callers were reported during work. The local matching-source GitNexus index refresh succeeded; the original mount's refresh limitation remains environmental. No commit is being made.

Completed evidence:

- Solution build: zero warnings/errors.
- Final full run: 1,904 tool tests and 302 runtime tests passed (2,206 total), no failures or skips. The subsequent MIME casing/parameter refinement passed all 165 focused explicit-contract, OpenAPI-emitter and MVC-host tests, including its two new cases.
- Runtime framework matrix: 100 tests on net8; 101 each on net9/net10.
- Real Functions host: default, empty and custom prefixes passed (81.6 seconds).
- Python round-trip audit: 11 tests passed.
- Samples: all five compile, including TypeShowcase and ImportDemo outside the solution's sample set.
- Docs: VitePress build passed. Invoked the installed VitePress entrypoint because the machine's pnpm version migration tried to reinstall esbuild; no tracked package policy changed.
- Formatting and analyzer verification passed. The final two changed C# files also passed CSharpier after the MIME refinement.
- Both local Release packages built. Attributes package contains net8.0/net9.0/net10.0 assets. An isolated net8 consumer restored the packed package and executed scalar, dictionary-key and enum-policy checks successfully.
- Plumb on the source workspace: **zero errors, two pre-existing MER-RV-003 warnings**. Exact exception: `/Users/max/Sites/plumb/lib/in-process-rules/rv-csharp.mjs:99` only recognizes `.Invoke`, whereas Meridian's current `references/rivet.md` explicitly prescribes `.Bind/.Success/.ToResult` and rejects the legacy Invoke API. The sample uses the current API and compiles. No sample regression or checker suppression added.

One intermediate final run was disturbed by moving its generated TestResults while it was still writing. This caused a DirectoryNotFoundException in a corpus test, not a product failure. Artifacts were restored; the clean final full run passed without artifact manipulation. Generated test output is not source and is excluded from the source-tree Plumb result above.

Final scope check: `gitnexus detect-changes --scope compare --base-ref main` completed against the workspace and reported 21 tracked files, 63 indexed symbols and 27 flows, aggregate CRITICAL risk. The changed production paths are the expected scalar runtime, binding/response walkers, enum import/emission/JSON and CLI boundaries. Existing index labels can name pre-rename tests because the network-mounted index cannot refresh; new regression files and the migration/plan files are reviewed separately as untracked files. No commit or release was created.

### Final handoff

All ledger items are complete. The 24 changed/new implementation, test and guidance files were compared byte-for-byte with the local verification copy; this plan is maintained in the original workspace. Final whitespace verification passed. No dependencies or unrelated production changes were introduced.

Intentional compatibility changes: contradictory bindings, ambiguous form media declarations, conflicting response shapes, colliding/unsupported string-enum contracts, invalid scalar shapes and invalid integer carriers now refuse predictably. Valid supported paths retain their contracts, and the runtime package again supports .NET 8. See `docs/guides/migrating-from-0.42.md` for migration and supported-surface details. The only verification environment limitation is the SMB checkout's GitNexus refresh; a matching-source local index refreshed successfully. The two existing Plumb warnings have the narrowly documented obsolete-rule exception above.

## Pre-commit Meridian review — 2026-09-21

Reviewed every dirty tracked file and all four untracked files against F1–F9, A1–A3 and M1/M2/D1. The review found and repaired these remaining gaps:

| Finding | Outcome | Regression evidence |
| --- | --- | --- |
| P1 — Single fixed typed results bypassed the response/attribute merge | Both single results and Results unions now use the existing status/body dictionary. Conflicting shapes refuse; matching declarations merge; a fixed success is retained beside error metadata. Removed the redundant fallback branch. | Three conflicting single-result cases and two successful merge cases in ExplicitContractTests. |
| P2 — File uploads discarded supported multipart media parameters | The file branch now uses the same validated content type as other form branches. | Parameterized multipart declaration retains its content key. |
| P2 — Scalar analysis/runtime constructor and converter rules disagreed | Require the exact Value type by value, reject competing derived converter attributes and property-level custom converters. Runtime reflection no longer accepts a broader object constructor. | Four analyzer cases and a real serializer test on net8/net9/net10. |
| P2 — A custom JsonConverterAttribute subclass became a numeric enum contract | Recognize converter-attribute inheritance and refuse unsupported custom attributes instead of ignoring them. | Custom string-enum attribute refuses with RIV1108. |

Eight new cases failed against the pre-review code before the repairs. The remaining additions cover successful merging and runtime parity. No new dependencies, protocols, registries or generic validation framework were introduced. A small local converter-attribute ancestry predicate is shared by the existing scalar and enum checks. Serialization and media-type parsing remain framework-owned.

Pre-edit GitNexus analysis: scalar/enum analysis has HIGH aggregate impact through type mapping, dictionary keys, endpoint extraction and contract generation. The emitter change reaches BuildPaths/EmitCore (LOW); the response merge reaches BuildEndpoint and contract-method extraction (LOW at direct-call depth); scalar reflection validation reaches CreateConverter (LOW). These risks were reported before editing. Source verification continues in the matching local copy because of the previously recorded SMB limitation.

Final verification after review fixes:

- Full solution tests: **2,222 passed**, zero failed/skipped (1,917 tool tests; 101 net8 runtime tests; 102 each on net9/net10).
- Focused review tests: 188 passed before the full gate.
- All 14 changed C# files pass CSharpier; solution analyzer verification passes.
- VitePress docs build and both Release packages pass; a fresh package cache and isolated .NET 8 consumer verify the rebuilt runtime package.
- Plumb: zero errors and the same two documented obsolete `.Invoke` warnings.
- GitNexus final comparison: 21 tracked files, 65 indexed symbols, 27 affected flows, CRITICAL aggregate risk from the shared analysis/emission owners. Scope is expected; untracked tests/docs reviewed separately. The index limitation is unchanged.
- All 24 implementation/test/guidance files match the local verification copy byte-for-byte. Final whitespace check passes.

No remaining blocking review findings. The plan's supported-surface decisions still apply; the review does not broaden support to arbitrary custom converters, recursive mixed form binding or AOT. At review completion, no commit, tag, push or publication had been performed.

## Release preparation

User authorized commit, tag and push after the pre-commit review. Release version: **v0.44.1**, following the existing v0.44.0 tag. Both NuGet project versions are aligned with the release tag. Pushing the tag triggers the existing publish workflow.
