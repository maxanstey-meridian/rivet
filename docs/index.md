---
layout: home
hero:
  name: Rivet
  text: C# in, OpenAPI 3.1 out
  tagline: Your C# types and endpoints are the source of truth. Rivet emits a faithful OpenAPI 3.1 spec; the OpenAPI ecosystem generates the types, clients, and validators.
  image:
    src: /logo.png
    alt: Rivet
  actions:
    - theme: brand
      text: Get Started
      link: /getting-started
    - theme: alt
      text: CLI Reference
      link: /reference/cli
features:
  - title: "C# Types -> OpenAPI Schemas"
    details: Roslyn reads your compiled C# — records, enums, brands, generics, nullability, validation attributes — and maps it to component schemas with full fidelity.
  - title: "ASP.NET -> OpenAPI Operations"
    details: Controllers, minimal APIs, and compiler-enforced [RivetContract] definitions become operations derived from the real transport shape.
  - title: One Spec, Any Consumer
    details: TypeScript types via openapi-typescript, a typed client via openapi-fetch, Zod via openapi-zod-client, docs via any OpenAPI renderer.
---

Rivet is a meta-framework over OpenAPI: it owns the C# → spec mapping and delegates code
generation to the OpenAPI ecosystem.

Start with [Getting Started](/getting-started), then use the [CLI Reference](/reference/cli) for the current command surface.
