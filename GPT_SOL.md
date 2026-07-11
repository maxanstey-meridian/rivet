# Rivet Critical Review

## Verdict

Rivet is not pretentious merely for avoiding NSwag. Its contract-first mode provides a real capability that NSwag does not provide cleanly: an explicit C# endpoint contract which participates in implementation and emits ordinary OpenAPI.

But Rivet as it exists now is not KISS. Its public happy path is fairly simple; internally it has grown into a bespoke compiler platform. Some of that complexity protects a valuable promise, while a significant portion comes from trying to support too many authoring directions.

Recommendation:

> Keep Rivet, but make it a narrow contract-first compiler. Stop trying to be a universal ASP.NET extractor, OpenAPI importer and round-trip meta-framework simultaneously.

## The Legitimate Core

This is worthwhile:

```csharp
public static readonly RouteDefinition<Request, Response> Invite =
    Define.Post<Request, Response>("/api/members")
        .Returns<ErrorResponse>(422);
```

Followed by:

```csharp
await Contract.Invite.Invoke(request, handler);
```

The genuine benefits are:

- The contract is readable application-owned C#, not generator configuration.
- Request, success and declared failure types are colocated.
- Roslyn reads actual symbols and nullability.
- OpenAPI is the only public output, so Rivet delegates clients and validators to the ecosystem.
- `--verify` gives deterministic drift detection.
- Typed-result validation catches undeclared statuses and payload types.
- Stable diagnostics make degradation visible rather than silently fabricating fidelity.

The v2 pivot was correct. Removing Rivet's own TypeScript and Zod generators and emitting only OpenAPI significantly improved the architectural story. `CLAUDE.md:3-30` describes a defensible scope.

I would choose this over NSwag for a greenfield API where contract-first authoring is intentional.

## Where It Stops Being KISS

### 1. The contract does not fully bind the implementation

The sample exposes the main hole.

`MembersContract.Remove` contains `"/api/members/{id}"` but has no typed input:

`samples/ContractApi/Contracts/MembersContract.cs:30-35`

The controller receives `Guid id`, but `Invoke` receives nothing:

`samples/ContractApi/Controllers/MembersController.cs:30-35`

Likewise `UpdateRole` receives `id` separately, while only the body is passed through `Invoke`:

`samples/ContractApi/Controllers/MembersController.cs:37-43`

Consequences:

- The generated contract can advertise `id` as a string while ASP.NET binds a `Guid`.
- The handler can ignore the route ID entirely.
- `Invoke` proves only part of the input contract.
- Coverage checking verifies route and method, but not binding equivalence or that route/query values reach the application operation.

That weakens the central claim that implementation matches declaration.

A contract-first endpoint should model the entire wire input:

```csharp
public sealed record UpdateRoleInput(
    Guid Id,
    UpdateRoleRequest Body);
```

Better still, Rivet should register or map the endpoint directly from the contract wherever the host model permits it. Then route, method and handler binding are not separately declared.

### 2. There are two implementations of the DSL

Every contract operation has parallel semantics:

- Runtime behaviour in `Rivet.Attributes/EndpointBuilder.cs`.
- Static interpretation in `Rivet.Tool/Analysis/ContractWalker.cs`.

The duplication is explicit. For example, default status behaviour in `ContractWalker.DefaultSuccessCode` must manually agree with `Define`:

`Rivet.Tool/Analysis/ContractWalker.cs:444-456`

Adding one builder operation requires coordinating:

- builder state
- fluent method
- syntax extraction
- internal IR
- OpenAPI emission
- importer
- diagnostics
- runtime validation
- documentation
- tests

`ContractWalker.cs` is 1,055 lines, `EndpointBuilder.cs` is 790 lines, and `OpenApiEmitter.cs` exceeds 1,300 lines. That is compiler complexity, not library-wrapper complexity.

This is not automatically bad, but "plain C# as data" is slightly misleading. It is a restricted embedded DSL interpreted once by C# at runtime and separately by a Roslyn syntax walker at generation time.

### 3. The mutable builder undermines the single-source claim

`RouteDefinitionBase` is mutable until its first `Invoke`:

`Rivet.Attributes/EndpointBuilder.cs:22-117`

A `static readonly` field protects the reference, not the object. Application startup code can mutate the contract after its initializer:

```csharp
MembersContract.Invite.Status(202);
```

Roslyn only reads the initializer chain, so runtime state can differ from the generated specification. Freezing after the first request is too late.

The builder should be immutable, with every operation returning a new definition. That would remove:

- publication state
- mutation timing
- thread-safety questions
- the possibility of post-initializer drift
- much of `CopyStateTo`

This is the most obvious local design defect in the contract API.

### 4. Annotation mode mostly competes with built-in ASP.NET OpenAPI

The contract-first DSL is Rivet's differentiation. The annotation mode in `EndpointWalker.cs` is much less compelling.

For ordinary controllers and minimal APIs, ASP.NET already knows about:

- endpoint metadata
- model binding
- typed results
- authorization metadata
- filters and conventions
- serializer configuration
- runtime route composition

Rivet statically reconstructs a subset of that behaviour. `EndpointWalker.cs:381-517`, for example, contains its own parameter-classification rules. Interface parameters without binding attributes are assumed to be DI services. That is an approximation of ASP.NET, not ASP.NET's actual behaviour.

For "annotate an existing API and emit OpenAPI," I would default to `Microsoft.AspNetCore.OpenApi` and the normal OpenAPI ecosystem. Rivet should retain annotation support only as an explicitly limited migration path if it has real users.

### 5. The importer is scope expansion, not core value

`--from-openapi` is described honestly as one-shot onboarding, but it introduces:

- OpenAPI-to-C# schema classification
- record synthesis
- contract synthesis
- unsupported-shape markers
- Rivet-specific vendor extensions
- fixed-point round-trip tests
- substantial reverse-mapping policy

The handover records that only 32% of GitHub operations initially survived semantic round-trip before the v0.38 fix wave (`HANDOVER.md:44-50`). That is not an indictment of the work; OpenAPI reverse mapping genuinely is difficult. It demonstrates that this is another product.

Split it into `dotnet-rivet-import` or a separate package. Then the core generator does not carry reverse-engineering complexity merely because onboarding is occasionally useful.

### 6. The internal model still exposes its v1 ancestry

The central representation is named `TsType`, `TsTypeDefinition` and `TsEndpointDefinition`, despite TypeScript generation having been removed:

- `Rivet.Tool/Model/TsType.cs`
- `Rivet.Tool/Model/TsTypeDefinition.cs`
- `Rivet.Tool/Model/TsEndpointDefinition.cs`

That is more than cosmetic. The public architecture says:

```text
C# contracts -> neutral contract model -> OpenAPI
```

The implementation still says:

```text
C# contracts -> TypeScript-oriented model -> OpenAPI
```

Rename it to neutral contract/schema language. If the IR is genuinely shared with PHP and TypeScript producers, neutrality is an architectural requirement.

### 7. "No drift" remains too strong

The README claims "no drift between the code and the spec" at `README.md:10-13`.

Rivet cannot see all wire behaviour:

- custom JSON converters
- serializer configuration
- middleware-mutated responses
- headers emitted by handlers
- FluentValidation rules
- runtime authorization policy
- unregistered polymorphism
- separate route parameters not passed through `Invoke`

The limitations document acknowledges most of these at `docs/misc/limitations.md:5-39`.

A defensible claim is:

> The spec is deterministically generated from declared C# contracts, with diagnostics for known fidelity loss.

That is still valuable. "No drift" promises more than static extraction can guarantee.

### 8. Some helpful fallbacks should fail instead

`OpenApiEmitter` emits a default bearer security scheme when `.Secure("name")` references an undefined scheme:

`Rivet.Tool/Emit/OpenApiEmitter.cs:143-168`

That invents security semantics. A contract tool should fail generation rather than guess how an endpoint is secured.

The same principle should apply throughout:

- Never invent a stronger contract.
- Never silently weaken a contract.
- Fail when the declaration is contradictory.
- Warn and emit untyped only where degradation is explicitly allowed.

Rivet generally follows that philosophy, but undefined security should be an error.

### 9. The runtime package is misnamed

`Rivet.Attributes` contains much more than attributes:

- contract DSL
- mutable definitions
- result wrappers
- typed-result runtime validation
- contract-violation exceptions
- ASP.NET exception handling

It also takes a `Microsoft.AspNetCore.App` framework reference at `Rivet.Attributes.csproj:24-26`.

The package should probably be `Rivet.Contracts` or `Rivet.AspNetCore`. If attributes need to remain usable independently, split marker contracts from ASP.NET runtime enforcement.

### 10. There is a current dependency vulnerability

The test run reports `NU1903` for `Microsoft.OpenApi` 2.7.0 with high-severity advisory `GHSA-v5pm-xwqc-g5wc`.

All 1,320 tests pass, but that package needs upgrading or an explicit documented assessment.

## What The Tests Say

The test engineering is serious:

- 1,320 tests pass on the current worktree.
- Spectral checks validate emitted OpenAPI.
- Real corpus imports compile.
- Round trips are tested to fixed points.
- Stable diagnostic IDs are cross-checked against documentation.
- Disk-level CLI tests were added after in-memory tests failed to expose real pipeline failures.

This is not a toy abstraction built on vibes. The test suite proves substantial engineering effort and awareness of fidelity risk.

It also confirms that Rivet is a compiler product. A tool requiring this testing strategy should not be marketed or mentally maintained as a tiny convenience wrapper.

## Rivet Versus NSwag

For a normal existing ASP.NET API:

```text
ASP.NET endpoint metadata
    -> built-in OpenAPI
    -> openapi-typescript / Kiota / another client generator
```

That is simpler and more standards-aligned than Rivet annotation mode.

For an explicitly contract-first API:

```text
Rivet contract
    -> directly mapped implementation
    -> OpenAPI
    -> ecosystem generators
```

Rivet has a legitimate advantage. NSwag does not give you that C# contract object and handler linkage cleanly.

The comparison therefore is not "Rivet or NSwag everywhere." It is:

- Use standard ASP.NET OpenAPI when implementation metadata is the source of truth.
- Use Rivet when a statically authored contract is intentionally the source of truth.
- Use ecosystem tools after OpenAPI in both cases.

## What I Would Keep

- `[RivetContract]`
- `Define.Get/Post/...`
- request, response and declared errors
- Roslyn extraction of contract definitions
- OpenAPI 3.1 emission
- stable diagnostics
- `--verify`
- contract-to-implementation coverage
- typed-result status/payload enforcement
- delegation of TypeScript, Zod and client generation

## What I Would Remove Or Split

- OpenAPI importer and reverse round-trip machinery
- broad controller annotation extraction as a flagship feature
- internal JSON IR as a user-visible workflow
- Rivet-specific preservation extensions not required for forward emission
- abstract-class contract variants if they have no material consumers
- overlapping authoring styles that exist primarily because they can

## Bottom Line

Rivet's premise is clean. Its current breadth is not.

It is not pretentious to say:

> I want C# contract definitions to be the source of truth, compile against their handlers, and emit standard OpenAPI without owning downstream codegen.

That is a coherent tool.

It becomes pretentious when framed as a general meta-framework capable of replacing the whole ASP.NET/OpenAPI toolchain in both directions. The implementation cost and fidelity caveats show that this ambition is not free.

I would use a narrowed Rivet contract-first path in Meridian applications. I would not use its annotation path instead of standard ASP.NET OpenAPI, and I would move the importer out of the core product. With those boundaries and an immutable DSL, Rivet would be a focused piece of engineering rather than an alternative ecosystem that must be personally maintained.
