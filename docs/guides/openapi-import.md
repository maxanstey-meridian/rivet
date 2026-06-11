# OpenAPI Import

This page is being rewritten.

`--from-openapi` is an **onboarding scaffold**: a one-shot import that turns an
existing OpenAPI 3.x spec into C# contracts + DTOs so you can adopt Rivet on an
existing API. After the import, the C# is the source of truth — review the
`// [rivet:unsupported …]` markers and warnings, fix what matters, and use the
forward pipeline from then on. It is not an incremental sync.

Current command:

```bash
dotnet rivet --from-openapi spec.json --namespace MyApp.Contracts --output ./src/
```

What imports cleanly, every diagnostic category, and what is out of scope are
documented in the [Import Profile](/reference/import-profile).

After import, point the normal forward pipeline at the imported project to generate TypeScript output.
