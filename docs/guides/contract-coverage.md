# Contract Coverage

`--check` verifies that every contract endpoint has a matching implementation — correct HTTP method, correct route. Catches drift between contracts and controllers before your code ships.

## Command

```bash
# Standalone — exits with code 1 if any warnings
dotnet rivet --project Api.csproj --check --quiet

# Standalone with codegen preview
dotnet rivet --project Api.csproj --check

# Combined with generation — check + generate in one pass
dotnet rivet --project Api.csproj --output ./generated --check
```

## What it detects

`--check` finds every `[RivetContract]` static class, then looks for `.Invoke()` call sites on each field. For each call site, it resolves the HTTP method and route from the surrounding context and compares against the contract.

### Missing implementation

A contract field with no `.Invoke()` call anywhere in the project:

```
⚠ MembersContract.UpdateRole: no .Invoke() call found
```

### HTTP method mismatch

The contract says GET, but the controller or minimal API endpoint uses POST:

```csharp
// Contract: Define.Get<List<MemberDto>>("/api/members")
// Controller:
[HttpPost]  // ← wrong
public async Task<IActionResult> List(CancellationToken ct)
    => (await MembersContract.List.Invoke(async () => ...)).ToActionResult();
```

```
⚠ MembersContract.List: HTTP method mismatch — expected GET, found POST
```

### Route mismatch

The contract says `/api/members` but the controller routes to `/api/users`:

```
⚠ MembersContract.List: route mismatch — expected /api/members, found /api/users
```

## Works with MVC controllers and minimal APIs

`--check` resolves the implementation context by walking up the syntax tree from each `.Invoke()` call:

**MVC controllers** — reads `[HttpGet]`, `[Route]` etc. from the containing method and class:

```csharp
[Route("api/members")]
public sealed class MembersController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => (await MembersContract.List.Invoke(async () => ...)).ToActionResult();
}
```

**Minimal APIs** — walks up through the lambda to the `MapGet`/`MapPost`/etc. call to extract the HTTP method and route. Use `.Route` to avoid duplicating the route string:

```csharp
app.MapGet(MembersContract.List.Route, async (AppDb db, CancellationToken ct) =>
    (await MembersContract.List.Invoke(async () =>
    {
        return await db.Members.ToListAsync(ct);
    })).ToResult());
```

Both are checked automatically — no extra configuration needed.

## CI usage

`--check` without `--output` exits with code 1 if any warnings are found, making it easy to add to CI. Use `--quiet` (`-q`) to suppress codegen preview output:

```bash
dotnet rivet --project Api.csproj --check --quiet
```

Prints a coverage summary to stderr:

```
Coverage: 79/79 endpoints covered. All OK.
```

Or with warnings:

```
warning: [MissingImplementation] MembersContract.UpdateRole: expected PUT /api/members/{id}/role, got (none)
Coverage: 78/79 endpoints covered, 0 mismatch(es), 1 missing.
```

Combined with `--output`, warnings are reported but the exit code reflects generation success — the check is advisory.

## Scope

`--check` only applies to v1 static class contracts with `RouteDefinition` fields and `.Invoke()` calls. It does not check:

- v2 abstract class contracts (the compiler already enforces signatures via `override`)
- `[RivetClient]` / `[RivetEndpoint]` annotations (the controller *is* the source of truth)
