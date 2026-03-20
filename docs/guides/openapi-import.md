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
│   └── ErrorDto.cs
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
            .Description("List all team members");

    public static readonly RouteDefinition<MemberDto> GetById =
        Define.Get<MemberDto>("/api/members/{id}")
            .Description("Get a member by ID")
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

var members = new List<MemberDto>
{
    new("1", "Alice", "alice@example.com", "admin"),
    new("2", "Bob", "bob@example.com", "member"),
    new("3", "Charlie", "charlie@example.com", "viewer"),
};

app.MapGet(MembersContract.List.Route, async () =>
    (await MembersContract.List.Invoke(async () => members)).ToResult());

app.MapGet(MembersContract.GetById.Route, async (string id) =>
    (await MembersContract.GetById.Invoke(async () =>
        members.FirstOrDefault(m => m.Id == id)!)).ToResult());

app.Run();

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

- **Sealed records** for object schemas
- **Enums** for string enums
- **Branded value objects** for string types with semantic formats (`email`, `uri`, etc.)
- **Static contract classes** with `RouteDefinition<T>` builder chains
- Operations grouped by tag into separate contract classes

## Supported schema subset

See the [Type Mapping reference](/reference/type-mapping#openapi--c-import-direction) for the full mapping table.

## Unsupported features

The following produce a warning and are skipped:

- `discriminator` mappings
- XML-specific attributes
- Callbacks, webhooks, links

## Security scheme handling

If the source spec defines `securityDefinitions` / `components/securitySchemes`, the importer maps them to `.Secure()` / `.Anonymous()` calls on the generated contract endpoints.

You can also override with `--security` on the command line.
