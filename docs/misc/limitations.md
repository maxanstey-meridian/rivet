# Limitations

Known limitations of the current Rivet implementation.

## Type system

- **Records only** — no inheritance, no polymorphism. Rivet works with sealed records and enums. Class hierarchies and interface-typed properties are not supported.
- **`delete` → `remove` rename** — `delete` is a reserved word in TypeScript/JavaScript. Generated client functions for `[HttpDelete]` endpoints are renamed to `remove`.

## File uploads

- **Single file only** — `IFormFile` is supported, but `IFormFileCollection` and `List<IFormFile>` are not.

## Real-time

- **No SignalR or WebSocket support** — Rivet generates HTTP clients only.

## Developer experience

- **No watch mode** — you must re-run the CLI manually when your C# code changes. A `--watch` flag may be added in the future.
- **No `dotnet rivet init`** — there's no scaffolding command to set up a project. Follow the [Getting Started](/getting-started) guide manually.

## OpenAPI import

- **OpenAPI 3.1 JSON only** — YAML specs and OpenAPI 2.0 (Swagger) are not supported.
- **No composition schemas** — `allOf`, `oneOf`, `anyOf`, and `discriminator` are not supported. These produce a warning and are skipped.
- **No inline anonymous objects** — all types must be named via `$ref`.
