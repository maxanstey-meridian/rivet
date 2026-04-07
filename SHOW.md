# Rivet — Show and Tell Notes

Demo project: `../rivet-demo/`
Task runner: `cd ../rivet-demo && task <name>`

---

## Setup (before the demo)

```bash
cd ../rivet-demo
dotnet build
npx @stoplight/prism-cli --version   # confirm prism is cached
task generate                         # pre-generate so nothing downloads live
```

---

## 1. The Problem (2 min)

APIs are contracts. Traditional approach: write code, document after, hope nobody breaks anything.
Docs drift, breaking changes go unnoticed, no safety net.

Rivet flips it: **contract first, everything else flows from it.**

---

## 2. Define the Contract (5 min)

Open `Contracts/UsersContract.cs`. Two endpoints, dead simple:

```csharp
[RivetContract]
public static class UsersContract
{
    public static readonly RouteDefinition<UserListResult> List =
        Define.Get<UserListResult>("/api/users")
            .Description("List all users");

    public static readonly RouteDefinition<CreateUserRequest, CreateUserResponse> Create =
        Define.Post<CreateUserRequest, CreateUserResponse>("/api/users")
            .Description("Create a new user")
            .Status(201);
}
```

Open `Models/UserModels.cs` — show the types: `UserDto`, `CreateUserRequest`, `CreateUserResponse`, `UserListResult`.

**Key point:** The contract is a plain static class. No ASP.NET dependency, no framework coupling. The
types _are_ the spec — the compiler enforces them.

Show the route list:

```bash
task routes
```

---

## 3. Generate Everything (3 min)

```bash
task generate
```

Walk through what appeared in `ts-client/`:

| File | What it is |
|------|------------|
| `types/models.ts` | TypeScript interfaces matching the C# records |
| `types/common.ts` | Shared types (Email branded primitive) |
| `client/users.ts` | Typed fetch functions: `list()`, `create()` — with Zod validation wired in |
| `rivet.ts` | Shared fetch wrapper + config |
| `openapi.json` | Full OpenAPI 3.0 spec |
| `schemas.ts` | JSON Schema definitions for all types |
| `validators.ts` | Zod runtime validators generated from schemas |

Open `client/users.ts` — show the typed functions. Open `openapi.json` briefly.

**Talking point:** "One contract definition → typed client, typed docs, typed mock server, runtime validators. For free."

---

## 4. Swagger UI (2 min)

Load the OpenAPI spec into Swagger UI. Either:
- Paste `openapi.json` into https://editor.swagger.io
- Or `task swagger` (runs Redocly preview)

Show interactive docs — endpoints, schemas, try-it-out.

**Talking point:** "This is your documentation, API explorer, and integration reference — automatically
kept in sync with the contract."

---

## 5. Prism Mock Server (5 min)

```bash
task prism
```

In another terminal:

```bash
npm run dev
```

Open the Vite dev server in the browser. Show `src/main.ts` — this is the generated client in action:

```ts
import { configureRivet } from "../ts-client/rivet.js";
import { list, create } from "../ts-client/client/users.js";
```

Click the buttons:

1. **GET /api/users** → valid mocked response matching the contract shape
2. **POST /api/users** → 201 with `{ id: "..." }`

Show the response shape matches what we defined.

**Talking point:** "Frontend can build against a live mock before a single line of business logic exists.
Backend and frontend work in parallel, both constrained by the same contract."

---

## 6. Break the Contract — Zod Fail (3 min)

Validators are already generated from step 3. Show `ts-client/validators.ts` — runtime validation
functions generated from the contract. Show `ts-client/client/users.ts` — validation is wired in.

Now break something:
- Edit the Prism response (or the OpenAPI spec) to return the wrong shape
- e.g. remove a required field, wrong type
- Re-run the fetch → show the Zod validation error

**Talking point:** "The contract is validated at runtime on the client too. Not just dev-time — a live
safety net. One source of truth, end to end."

---

## 7. Wrap-Up (1 min)

```
Rivet Contract (C#)
  → OpenAPI spec
      → Swagger UI (docs / explorer)
      → Prism (mock server)
  → TypeScript client (typed fetch functions)
  → Zod validators (runtime safety)
```

Everything flows from one definition. Change the contract, regenerate, everything updates. No drift.

---

## Task Commands Reference

| Command | What it does |
|---------|-------------|
| `task build` | Build the demo .csproj |
| `task generate` | TS client + OpenAPI spec + Zod validators |
| `task routes` | List discovered routes |
| `task check` | Verify contract coverage |
| `task swagger` | Open Redocly preview of the spec |
| `task prism` | Start Prism mock on :4010 |
| `task clean` | Wipe generated output |
| `npm run dev` | Vite dev server for the demo frontend |
