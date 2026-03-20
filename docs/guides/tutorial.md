# Tutorial: Zero to Typed Client

Build a minimal API from `dotnet new webapi`, define contracts, and generate a fully typed TypeScript client — in under 5 minutes.

## What you'll build

A user management API with five endpoints showcasing lists, single-item gets, updates, file uploads, and branded value objects:

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/users` | List all users |
| GET | `/api/users/{id}` | Get a single user |
| POST | `/api/users` | Create a user |
| PUT | `/api/users/{id}` | Update a user |
| POST | `/api/users/{id}/avatar` | Upload a profile picture |

## 1. Create the project

```bash
dotnet new webapi -n UserApi --no-openapi
cd UserApi
dotnet add package Rivet.Attributes --version "*"
```

## 2. Define your domain

```csharp
// Domain/ValueObjects.cs
namespace UserApi.Domain;

/// Branded VO → TS: string & { readonly __brand: "Email" }
public sealed record Email(string Value);

/// Branded VO → TS: string & { readonly __brand: "UserId" }
public sealed record UserId(string Value);
```

```csharp
// Domain/Role.cs
namespace UserApi.Domain;

/// String enum → TS: "Admin" | "Member" | "Viewer"
public enum Role { Admin, Member, Viewer }
```

These types are discovered transitively from the contract — no `[RivetType]` needed.

## 3. Define the contract

The contract is the single source of truth for routes, HTTP methods, types, and status codes. It has zero dependency on ASP.NET.

```csharp
// Contracts/UsersContract.cs
using Rivet;
using UserApi.Domain;

namespace UserApi.Contracts;

[RivetContract]
public static class UsersContract
{
    public static readonly RouteDefinition<UserDto[]> List =
        Define.Get<UserDto[]>("/api/users")
            .Description("List all users");

    public static readonly RouteDefinition<UserDto> Get =
        Define.Get<UserDto>("/api/users/{id}")
            .Description("Get a user by ID")
            .Returns<ErrorDto>(404, "User not found");

    public static readonly RouteDefinition<CreateUserRequest, CreateUserResponse> Create =
        Define.Post<CreateUserRequest, CreateUserResponse>("/api/users")
            .Description("Create a new user")
            .Returns<ErrorDto>(422, "Validation failed");

    public static readonly InputRouteDefinition<UpdateUserRequest> Update =
        Define.Put("/api/users/{id}")
            .Accepts<UpdateUserRequest>()
            .Status(204)
            .Description("Update user details")
            .Returns<ErrorDto>(404, "User not found");

    public static readonly RouteDefinition<FileUploadResult> UploadAvatar =
        Define.Post<FileUploadResult>("/api/users/{id}/avatar")
            .AcceptsFile()
            .Description("Upload a profile picture");
}

// --- DTOs (colocated with the contract, discovered transitively) ---

public sealed record UserDto(
    UserId Id,
    string Name,
    Email Email,
    Role Role,
    string? AvatarUrl);

public sealed record CreateUserRequest(string Name, Email Email, Role Role);

public sealed record CreateUserResponse(UserId Id);

public sealed record UpdateUserRequest(string Name, Role Role);

public sealed record FileUploadResult(string Id, string FileName, string ContentType, long SizeBytes);

public sealed record ErrorDto(string Code, string Message);
```

Note what the contract captures:
- **Route + HTTP method** — `Define.Get<T>("/api/users/{id}")`
- **Input/output types** — compiler-enforced via generics
- **Status codes** — `Status(204)`, default 200/201
- **Error responses** — `.Returns<ErrorDto>(404)` becomes a discriminated union in TS
- **Branded VOs** — `UserId`, `Email` flow through to the TypeScript client as branded types

## 4. Wire up the endpoints

Use `.Route` from the contract — no route strings to keep in sync:

```csharp
// Program.cs
using Rivet;
using UserApi.Contracts;
using UserApi.Domain;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// List all users
app.MapGet(UsersContract.List.Route, async () =>
    (await UsersContract.List.Invoke(async () =>
    {
        // Return UserDto[] — compiler enforced
        return Array.Empty<UserDto>();
    })).ToResult());

// Get user by ID
app.MapGet(UsersContract.Get.Route, async (string id) =>
    (await UsersContract.Get.Invoke(async () =>
    {
        // Return UserDto — compiler enforced
        return new UserDto(
            new(id), "Alice", new("alice@example.com"),
            Role.Admin, null);
    })).ToResult());

// Create user
app.MapPost(UsersContract.Create.Route, async (CreateUserRequest req) =>
    (await UsersContract.Create.Invoke(req, async r =>
    {
        // r is CreateUserRequest, must return CreateUserResponse
        return new CreateUserResponse(new(Guid.NewGuid().ToString()));
    })).ToResult());

// Update user (void — 204)
app.MapPut(UsersContract.Update.Route, async (string id, UpdateUserRequest req) =>
    (await UsersContract.Update.Invoke(req, async r =>
    {
        // void — input only
    })).ToResult());

// Upload avatar (file)
app.MapPost(UsersContract.UploadAvatar.Route, async (string id, IFormFile file) =>
    (await UsersContract.UploadAvatar.Invoke(async () =>
    {
        return new FileUploadResult(
            Guid.NewGuid().ToString(),
            file.FileName,
            file.ContentType,
            file.Length);
    })).ToResult());

app.Run();

// --- Framework bridge (write once per project) ---

public static class RivetExtensions
{
    public static IResult ToResult<T>(this RivetResult<T> result)
        => Results.Json(result.Data, statusCode: result.StatusCode);

    public static IResult ToResult(this RivetResult result)
        => Results.StatusCode(result.StatusCode);
}
```

Every handler uses the contract's `.Invoke()` — the compiler enforces the return type matches the contract definition. Try returning a `string` from `UsersContract.Get.Invoke` — it won't compile.

## 5. Generate the TypeScript client

```bash
dotnet rivet --project UserApi.csproj --output ./generated
```

## 6. What you get

::: code-group
```typescript [types/common.ts]
// Generated by Rivet — do not edit

export type Role = "Admin" | "Member" | "Viewer";

export type Email = string & { readonly __brand: "Email" };

export type UserId = string & { readonly __brand: "UserId" };

export type UserDto = {
  id: UserId;
  name: string;
  email: Email;
  role: Role;
  avatarUrl: string | null;
};

export type CreateUserRequest = {
  name: string;
  email: Email;
  role: Role;
};

export type CreateUserResponse = {
  id: UserId;
};

export type UpdateUserRequest = {
  name: string;
  role: Role;
};

export type FileUploadResult = {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
};

export type ErrorDto = {
  code: string;
  message: string;
};
```

```typescript [client/users.ts]
// Generated by Rivet — do not edit

export function list(): Promise<UserDto[]>;

export type GetResult =
  | { status: 200; data: UserDto; response: Response }
  | { status: 404; data: ErrorDto; response: Response }
  | { status: Exclude<number, 200 | 404>; data: unknown; response: Response };

export function get(id: string): Promise<UserDto>;
export function get(id: string, opts: { unwrap: false }): Promise<GetResult>;

export type CreateResult =
  | { status: 201; data: CreateUserResponse; response: Response }
  | { status: 422; data: ErrorDto; response: Response }
  | { status: Exclude<number, 201 | 422>; data: unknown; response: Response };

export function create(body: CreateUserRequest): Promise<CreateUserResponse>;
export function create(body: CreateUserRequest, opts: { unwrap: false }): Promise<CreateResult>;

export function update(id: string, body: UpdateUserRequest): Promise<void>;

export function uploadAvatar(id: string, file: File): Promise<FileUploadResult>;  // .AcceptsFile() → File param
```
:::

Notice:
- **`Email` and `UserId`** are branded types — you can't pass a plain `string` where a `UserId` is expected
- **`Role`** is a string union, not a numeric enum
- **`GetResult`** is a discriminated union — `{ status: 404; data: ErrorDto }` is a real type you can narrow on
- **`avatarUrl`** is `string | null` — C# nullability flows through
- **`uploadAvatar`** accepts `File` — `.AcceptsFile()` on the contract generates a `FormData` upload

## 7. Use in your frontend

```typescript
import { configureRivet } from "~/generated/rivet";
import { users } from "~/generated/client";

configureRivet({ baseUrl: "http://localhost:5000" });

// List — returns UserDto[]
const allUsers = await users.list();

// Get — unwrapped (throws on non-2xx)
const user = await users.get("abc-123");
console.log(user.email); // Email (branded string)

// Get — with status code narrowing
const result = await users.get("abc-123", { unwrap: false });
if (result.status === 404) {
  console.log(result.data.message); // ErrorDto — fully typed
}

// Create — branded types enforced
const created = await users.create({
  name: "Bob",
  email: "bob@example.com" as Email,  // branded
  role: "Member",                       // union — no typo possible
});
console.log(created.id); // UserId (branded)

// Update — void, 204
await users.update("abc-123", { name: "Robert", role: "Admin" });

// Upload — accepts File, sends as FormData
const input = document.querySelector<HTMLInputElement>("#avatar")!;
const uploaded = await users.uploadAvatar("abc-123", input.files![0]);
console.log(uploaded.fileName);
```

## 8. Verify coverage

```bash
dotnet rivet --project UserApi.csproj --check --quiet
```

```
Coverage: 5/5 endpoints covered. All OK.
```

## 9. List your routes

```bash
dotnet rivet --project UserApi.csproj --routes
```

```
  Method  Route                      Handler
  ──────  ─────────────────────────  ───────
  GET     /api/users                 users.list
  GET     /api/users/{id}            users.get
  POST    /api/users                 users.create
  PUT     /api/users/{id}            users.update
  POST    /api/users/{id}/avatar     users.uploadAvatar
5 route(s).
```

## What just happened

You defined your API shape **once** in a C# contract. From that single source:

- The **C# compiler** enforces that every endpoint handler returns the correct type via `.Invoke()`
- The **route strings** live in the contract and are used at runtime — no duplication
- The **TypeScript client** has full type safety: branded VOs, nullable fields, discriminated union error responses
- The **coverage checker** verifies every contract endpoint has a matching implementation
- The **route list** gives you `artisan route:list`-style visibility

No YAML, no codegen config files, no manual DTO sync. One command, end-to-end type safety.

## Next steps

- Add [runtime validation](/guides/runtime-validation) with `--compile` for type assertions at the network boundary
- Generate an [OpenAPI spec](/guides/openapi-emission) alongside your TypeScript output
- Read the [contracts guide](/guides/contracts) for advanced patterns (`.Secure()`, `.Anonymous()`, abstract class contracts)
- See [error handling](/guides/error-handling) for the full `RivetError` API
