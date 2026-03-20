# Limitations

Known limitations of the current Rivet implementation.

## Type system

- **No inheritance hierarchies or interface-typed properties** — Rivet works with sealed records, sealed classes, structs, and enums. Class inheritance and interface-typed properties are not supported.
- **`delete` → `remove` rename** — `delete` is a reserved word in TypeScript/JavaScript. When the generated function name would be literally `delete` (e.g., a method named `Delete()`), it is renamed to `remove`. Other `[HttpDelete]` endpoints (e.g., `RemoveItem()`) keep their original name.

## File uploads

- **Single file only** — `IFormFile` is supported, but `IFormFileCollection` and `List<IFormFile>` are not.

## Real-time

- **No SignalR or WebSocket support** — Rivet generates HTTP clients only.

## Developer experience

- **No watch mode** — you must re-run the CLI manually when your C# code changes. A `--watch` flag may be added in the future.
- **No `dotnet rivet init`** — there's no scaffolding command to set up a project. Follow the [Getting Started](/getting-started) guide manually.

## OpenAPI import

- **OpenAPI 3.x JSON only** — YAML specs and OpenAPI 2.0 (Swagger) are not supported.
- **No `discriminator` mappings** — `discriminator` is not supported. These produce a warning and are skipped.
