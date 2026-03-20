# OpenAPI Emission

Generate an OpenAPI 3.0 JSON spec alongside your TypeScript output. Your C# types and endpoints become a standards-compliant API spec — useful for documentation tools, API gateways, and external consumers.

## Command

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --openapi
```

This produces everything the normal forward pipeline does (types, client, validators) **plus** an `openapi.json` file.

## Security schemes

Add a security scheme to the spec with `--security`:

```bash
# Bearer token
dotnet rivet --project Api.csproj -o dir --openapi --security bearer

# Bearer JWT
dotnet rivet --project Api.csproj -o dir --openapi --security bearer:jwt

# Cookie-based
dotnet rivet --project Api.csproj -o dir --openapi --security cookie:sid

# API key in header
dotnet rivet --project Api.csproj -o dir --openapi --security apikey:header:X-Api-Key
```

### Per-endpoint security

On contract endpoints, use `.Anonymous()` and `.Secure(scheme)` to control per-endpoint security:

```csharp
public static readonly RouteDefinition<List<MemberDto>> List =
    Define.Get<List<MemberDto>>("/api/members")
        .Description("List all team members")
        .Anonymous();  // no auth required

public static readonly RouteDefinition<InviteMemberRequest, InviteMemberResponse> Invite =
    Define.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
        .Description("Invite a new team member")
        .Secure("admin");  // requires admin scheme
```

These are emitted as `security: []` (anonymous) or `security: [{ admin: [] }]` on the corresponding OpenAPI operation.

## What it produces

The generated `openapi.json` includes:

- **Paths** — one operation per endpoint, with parameters, request bodies, and response schemas
- **Schemas** — all C# types referenced by endpoints, under `#/components/schemas`
- **Security schemes** — if `--security` is specified
- **Descriptions** — from `.Description()` on contract endpoints

### Type representation

- **Generic types** are monomorphised: `PagedResult<TaskDto>` becomes `PagedResult_TaskDto` in the schema, with an `x-rivet-generic` extension that allows the [importer](/guides/openapi-import) to reconstruct the generic template
- **Branded value objects** (single-property records) are emitted as component schemas with an `x-rivet-brand` extension (e.g., `Email` becomes `{ "type": "string", "x-rivet-brand": "Email" }`), and references use `$ref`
- **File upload records** with `IFormFile` properties produce `multipart/form-data` schemas with `x-rivet-input-type` (preserving the record name) and `x-rivet-file` markers on file properties
- **Enums** are `type: string` with `enum: [...]`
- **Nullable types** use `nullable: true`

The `x-rivet-*` extensions are ignored by non-Rivet consumers (valid OpenAPI 3.0). They enable [lossless round-trips](/guides/openapi-round-trips) when the spec is imported back into Rivet.

## Viewing the spec

The generated `openapi.json` works with any OpenAPI 3.0 tool:

- [Swagger Editor](https://editor.swagger.io) — paste or upload the JSON
- [Redocly](https://redocly.com) — `npx @redocly/cli preview-docs openapi.json`
- API gateways (Kong, AWS API Gateway, Azure APIM) — import directly

## Consistency with import

The OpenAPI emitter and the [OpenAPI importer](/guides/openapi-import) use consistent type mappings. What the emitter outputs, the importer can consume — enabling a round-trip workflow where you emit a spec, hand it to another team, and they import it back.

The emitter annotates the spec with `x-rivet-*` vendor extensions so that brands, generic types, and file upload record names survive the round-trip without loss. See [OpenAPI Round-Trips](/guides/openapi-round-trips) for details.
