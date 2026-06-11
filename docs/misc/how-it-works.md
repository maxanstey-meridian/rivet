# How It Works

Rivet is a Roslyn-based extractor, not a runtime reflector. The pipeline, as
implemented in `Rivet.Tool`:

1. **Load** — `--project x.csproj` loads the project through MSBuild and gets the
   full Roslyn `Compilation`; bare `.cs` arguments are compiled directly.
   Compilation errors abort the run.
2. **Discover** — a single pass over the source assembly finds `[RivetContract]`
   classes, `[RivetClient]` classes, `[RivetEndpoint]` methods, and `[RivetType]`
   types.
3. **Walk** — `TypeWalker` lowers C# types into an internal type model (primitives
   with formats, brands, monomorphised generics, enums, nullability);
   `ContractWalker` reads `Define.*` builder chains from contract fields;
   `EndpointWalker` reads controller/minimal-API endpoints (routes, parameter
   binding, `[ProducesResponseType]`, typed results). Contract endpoints win when
   both describe the same endpoint.
4. **Emit** — `EmitPipeline` extracts repeated inline object types into named
   components, then `OpenApiEmitter` writes the OpenAPI 3.1 document with the
   `x-rivet-*` [vendor extensions](/reference/vendor-extensions).

Two side doors feed the same emit pipeline:

- `--from contract.json` — a contract JSON document produced by the sibling
  runtimes (rivet-ts, rivet-php). This is an **internal IR**, not a public format.
- `--from-openapi spec.json` — the one-shot [importer](/guides/openapi-import)
  generates C# contracts/DTOs which then go through the normal Roslyn path.

There is no runtime spec generation and no middleware: the `Rivet.Attributes`
package contains only the attributes, the `Define`/`RouteDefinition` builder, and
the `Invoke` helpers whose enforcement scope is described in
[Runtime Validation](/guides/runtime-validation).
