# OpenAPI Import

Start from an OpenAPI 3.x JSON spec, generate C# contracts and DTOs, then feed them into the normal Rivet pipeline to produce TypeScript output.

```
OpenAPI spec (source of truth)
  → C# contracts + DTOs (generated, checked in)
  → Roslyn walker (existing)
  → TS types + client (existing)
```

This is useful when another team owns the API — import their spec, get typed contracts, and the compiler tells you what broke when the upstream spec changes. Re-run the import, rebuild, fix what the compiler flags.

## Walkthrough

Say another team publishes an OpenAPI spec for their Members API. You can build a .NET implementation that's guaranteed to match their spec, and get a typed TypeScript client for free. Start with:

```json
{
  "openapi": "3.0.3",
  "info": { "title": "Members API", "version": "1.0.0" },
  "paths": {
    "/api/members": {
      "get": {
        "operationId": "members_list",
        "tags": ["Members"],
        "summary": "List all team members",
        "responses": {
          "200": {
            "description": "Success",
            "content": {
              "application/json": {
                "schema": {
                  "type": "array",
                  "items": { "$ref": "#/components/schemas/MemberDto" }
                }
              }
            }
          }
        }
      }
    },
    "/api/members/{id}": {
      "get": {
        "operationId": "members_getById",
        "tags": ["Members"],
        "summary": "Get a member by ID",
        "parameters": [
          { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
        ],
        "responses": {
          "200": {
            "description": "Success",
            "content": {
              "application/json": {
                "schema": { "$ref": "#/components/schemas/MemberDto" }
              }
            }
          },
          "404": {
            "description": "Member not found",
            "content": {
              "application/json": {
                "schema": { "$ref": "#/components/schemas/ErrorDto" }
              }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "MemberDto": {
        "type": "object",
        "properties": {
          "id": { "type": "string" },
          "name": { "type": "string" },
          "email": { "type": "string" },
          "role": { "type": "string" }
        },
        "required": ["id", "name", "email", "role"]
      },
      "ErrorDto": {
        "type": "object",
        "properties": {
          "code": { "type": "string" },
          "message": { "type": "string" }
        },
        "required": ["code", "message"]
      }
    }
  }
}
```

### Step 1: Import

```bash
dotnet rivet --from-openapi openapi.json --namespace ImportDemo --output ./Generated
```

This produces:

```
Generated/
├── Types/
│   ├── MemberDto.cs
│   ├── ErrorDto.cs
│   └── GetByIdInput.cs
└── Contracts/
    └── MembersContract.cs
```

The generated contract is a plain C# class — each field declares an endpoint's method, route, input/output types, and error responses:

```csharp
[RivetContract]
public static class MembersContract
{
    public static readonly RouteDefinition<List<MemberDto>> List =
        Define.Get<List<MemberDto>>("/api/members")
            .Summary("List all team members");

    public static readonly RouteDefinition<GetByIdInput, MemberDto> GetById =
        Define.Get<GetByIdInput, MemberDto>("/api/members/{id}")
            .Summary("Get a member by ID")
            .Returns<ErrorDto>(404, "Member not found");
}
```

### Step 2: Implement

Now implement the API. Each `MapGet` uses the contract's `.Route` (so you never duplicate the route string) and `.Invoke()` to execute your handler with type-safe input/output — the compiler rejects it if your handler returns the wrong type:

```csharp
using ImportDemo;
using Rivet;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet(MembersContract.List.Route, async () =>
    (await MembersContract.List.Invoke(async () =>
    {
        var members = GetMembers();
        return members;
    })).ToResult());

app.MapGet(MembersContract.GetById.Route, async (string id) =>
    (await MembersContract.GetById.Invoke(new GetByIdInput(id), async input =>
    {
        var members = GetMembers();
        return members.First(m => m.Id == input.Id);
    })).ToResult());

app.Run();

// Imagine this is a database call
List<MemberDto> GetMembers() =>
[
    new("1", "Alice", "alice@example.com", "admin"),
    new("2", "Bob", "bob@example.com", "member"),
    new("3", "Charlie", "charlie@example.com", "viewer"),
];

static class RivetExtensions
{
    public static IResult ToResult<T>(this RivetResult<T> r)
        => Results.Json(r.Data, statusCode: r.StatusCode);
}
```

### Step 3: Generate the TypeScript client

```bash
dotnet rivet --project ImportDemo.csproj --output ./generated
```

```typescript
// members.list() → MemberDto[]
// members.getById(id) → MemberDto (throws on 404)
// members.getById(id, { unwrap: false }) → GetByIdResult (discriminated union)
```

Full circle: OpenAPI spec → C# contracts → implement handlers → typed TS client.

The full example is in [`samples/ImportDemo`](https://github.com/maxanstey-meridian/rivet/tree/main/samples/ImportDemo).

## Command

```bash
# Preview to stdout
dotnet rivet --from-openapi openapi.json --namespace TaskBoard.Contracts

# Write to disk
dotnet rivet --from-openapi openapi.json --namespace TaskBoard.Contracts --output ./src/

# With default security scheme
dotnet rivet --from-openapi openapi.json --namespace TaskBoard.Contracts --output ./src/ --security bearer
```

## Output structure

```
output/
├── Types/
│   ├── TaskDto.cs              # sealed record
│   ├── CreateTaskRequest.cs
│   └── Priority.cs             # enum
├── Domain/
│   └── Email.cs                # branded value object
└── Contracts/
    ├── TasksContract.cs        # [RivetContract] with RouteDefinition<T> fields
    └── MembersContract.cs
```

### What it generates

- **Sealed records** for object schemas, with metadata attributes (`[RivetDescription]`, `[RivetConstraints]`, `[RivetDefault]`, `[RivetExample]`, `[RivetReadOnly]`, `[RivetWriteOnly]`, `[RivetFormat]`, `[RivetOptional]`) preserving schema annotations
- **Input records** for path/query/header/cookie parameters (preserving types — `int`, `Guid`, etc.)
- **Enums** for string enums, with `[JsonStringEnumMemberName]` attributes to preserve original member names that differ from PascalCase
- **Branded value objects** for string types with semantic formats (`email`, `uri`, etc.)
- **Static contract classes** with `RouteDefinition<T>` builder chains — `.Summary()` for OpenAPI `summary`, `.Description()` for `description`, `.FormEncoded()` for `application/x-www-form-urlencoded` bodies
- Operations grouped by tag into separate contract classes

## Supported schema subset

See the [Type Mapping reference](/reference/type-mapping#openapi--c-import-direction) for the full mapping table.

## Unsupported features

The following produce a warning and are skipped:

- `discriminator` mappings
- XML-specific attributes
- Callbacks, webhooks, links

## Supported content types

The importer resolves request bodies and responses for these content types, in priority order:

**Request bodies:** `application/json`, `application/x-www-form-urlencoded`, `multipart/form-data`, `*/*`, then any binary (`application/octet-stream`, `image/*`, etc.), `text/*`, or `application/x-*` content type that has a schema.

**Responses:** `application/json`, `*/*`, then any binary content type (→ `.ProducesFile()`), then any `text/*` content type with a schema.

For binary bodies with `format: binary`, the schema resolves to `IFormFile`. For `text/*` bodies and responses, the schema typically resolves to `string`.

## Unsupported content markers

When an endpoint has a request body or response with a content type the importer can't resolve — either because no schema is defined, or the content type doesn't match any supported pattern — it emits a structured comment:

```csharp
// [rivet:unsupported body content-type=application/vnd.custom+xml]
public static readonly RouteDefinition Create =
    Define.Post("/api/things")
        .Description("Create a thing");
```

The endpoint is still generated — routes, parameters, and any resolvable types are preserved. Only the specific body or response that couldn't be mapped is annotated.

### What triggers a marker

A marker appears when:

- The content type doesn't match any supported pattern (e.g. a vendor-specific XML type)
- The content type is supported but the schema is missing (e.g. `application/json` with examples but no `schema` key — this is a spec authoring error)

### Working with markers

1. **Search for them** — `grep -r "rivet:unsupported" ./Generated/` shows every gap at a glance.
2. **Handle them manually** — add a hand-written endpoint alongside the generated contract.
3. **Track upstream changes** — re-importing updates the markers if the spec changes.

## Security scheme handling

If the source spec defines `securityDefinitions` / `components/securitySchemes`, the importer maps them to `.Secure()` / `.Anonymous()` calls on the generated contract endpoints.

You can also override with `--security` on the command line.
