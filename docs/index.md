---
layout: home
hero:
  name: Rivet
  text: End-to-end type safety between .NET and TypeScript
  tagline: No drift, no schema files, no codegen config.
  image:
    src: /logo.png
    alt: Rivet
  actions:
    - theme: brand
      text: Get Started
      link: /getting-started
    - theme: alt
      text: View on GitHub
      link: https://github.com/maxanstey-meridian/rivet
---

<div class="vp-doc" style="max-width: 688px; margin: 0 auto; padding: 2rem 1.5rem;">

## Three modes — use whichever fits your team

| Source of truth | Command | What it produces |
|---|---|---|
| **C# contracts** | `dotnet rivet --project Api.csproj` | TS types, typed client, validators, OpenAPI spec |
| **C# controllers** | `dotnet rivet --project Api.csproj` | Same — contracts and controllers are interchangeable |
| **OpenAPI spec** | `dotnet rivet --from-openapi spec.json` | C# contracts + DTOs (feed back into row 1) |

## Your C# types become TypeScript types

<div style="display: flex; gap: 1rem; flex-wrap: wrap;">
  <img src="/images/hero-cs-types.png" alt="C# types" style="max-width: 48%; min-width: 280px;" />
  <img src="/images/hero-ts-types.png" alt="Generated TypeScript types" style="max-width: 48%; min-width: 280px;" />
</div>

## Your controllers become a typed client

<div style="display: flex; gap: 1rem; flex-wrap: wrap;">
  <img src="/images/hero-controller.png" alt="Controller" style="max-width: 48%; min-width: 280px;" />
  <img src="/images/hero-client.png" alt="Generated client" style="max-width: 48%; min-width: 280px;" />
</div>

## Why Rivet?

[tRPC](https://trpc.io) and [oRPC](https://orpc.unnoq.com) give you end-to-end type safety when your server is TypeScript. Rivet gives you the same DX when your server is .NET.

Unlike OpenAPI-based generators (NSwag, Kiota, Kubb), Rivet reads Roslyn's full type graph — nullable annotations, sealed records, string enum unions, generic type parameters — and produces richer TypeScript types than any JSON schema intermediary can represent.

Rivet is not just a client generator. Any C# type marked with `[RivetType]` becomes a TypeScript type — whether or not it appears in an endpoint. Commands, results, value objects, DTOs — if your frontend and backend need to agree on a shape, mark it once in C# and it appears in your generated types. The types are the primary output; the client is a bonus.

</div>
