# OpenAPI Emission

Generate an OpenAPI 3.1 JSON spec alongside your TypeScript output. Your C# types and endpoints become a standards-compliant API spec — useful for documentation tools, API gateways, and external consumers.

## Command

```bash
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet --openapi
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
public static readonly EndpointBuilder<List<MemberDto>> List =
    Endpoint.Get<List<MemberDto>>("/api/members")
        .Description("List all team members")
        .Anonymous();  // no auth required

public static readonly EndpointBuilder<InviteMemberRequest, InviteMemberResponse> Invite =
    Endpoint.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
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

- Generic types are monomorphised: `PagedResult<TaskDto>` becomes `PagedResultOfTaskDto` in the schema
- Branded value objects (single-property records) are unwrapped to their inner primitive type (e.g., `Email` becomes `type: string`)
- Enums are `type: string` with `enum: [...]`
- Nullable types use `type: ["string", "null"]`

## Viewing the spec

The generated `openapi.json` works with any OpenAPI 3.1 tool:

- [Swagger Editor](https://editor.swagger.io) — paste or upload the JSON
- [Redocly](https://redocly.com) — `npx @redocly/cli preview-docs openapi.json`
- API gateways (Kong, AWS API Gateway, Azure APIM) — import directly

## Consistency with import

The OpenAPI emitter and the [OpenAPI importer](/guides/openapi-import) use consistent type mappings. What the emitter outputs, the importer can consume — enabling a round-trip workflow where you emit a spec, hand it to another team, and they import it back.
