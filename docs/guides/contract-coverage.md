# Contract Coverage

`--check` verifies that every `[RivetContract]` field is actually implemented, and
that the implementation matches the declaration:

```bash
dotnet rivet --project path/to/Api.csproj --check
```

The checker finds `.Success(...)`, `.Error(...)`, and `.File(...)` terminal calls for
each contract field, including terminals reached through `.Bind(input)`, in
controller actions and minimal API handlers (`MapGet`/`MapPost`/`MapPut`/
`MapDelete`/`MapPatch` lambdas. It reports four kinds of warning on stderr:

| Warning | Meaning |
|---|---|
| `MissingImplementation` | No contract response terminal found for the contract field. |
| `OrphanedBinding` | The contract was bound in a route handler, but no terminal result from that binding is returned. |
| `HttpMethodMismatch` | The implementing endpoint uses a different HTTP method than the contract declares. |
| `RouteMismatch` | The implementing endpoint's route does not match the contract's route template. |

A summary line is always printed:

```
Coverage: 6/6 endpoints covered. All OK.
```

## Exit code

When `--check` is used **without** `--output`, any warning makes the command exit
with code `1` — suitable for CI. With `--output`, warnings are printed but the spec
is still emitted and the exit code reflects emission. Combine with `-q`/`--quiet` to
suppress generation output in CI logs.
