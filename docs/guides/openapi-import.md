# OpenAPI Import

This page is being rewritten.

Current command:

```bash
dotnet rivet --from-openapi spec.json --namespace MyApp.Contracts --output ./src/
```

After import, point the normal forward pipeline at the imported project to generate TypeScript output.
