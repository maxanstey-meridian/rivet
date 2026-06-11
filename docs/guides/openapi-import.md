# OpenAPI Import

`--from-openapi` is an **onboarding scaffold**: a one-shot import that turns an
existing OpenAPI 3.x spec into C# contracts + DTOs so you can adopt Rivet on an
existing API. After the import, the C# is the source of truth — review the
`// [rivet:unsupported …]` markers and warnings, fix what matters, and use the
forward pipeline from then on. It is not an incremental sync; re-running against a
drifted spec is a re-onboarding, not a merge.

```bash
dotnet rivet --from-openapi spec.json --namespace MyApp.Contracts --output ./src/
```

Omit `--output` to preview the generated C# on stdout. `--namespace` defaults to
`Generated`.

What imports cleanly, every diagnostic category, and what is out of scope are
documented in the [Import Profile](/reference/import-profile). The quality gate:
the scaffolded C# must compile, and the suite pins importer stability metrics
against a corpus of real-world specs.

After import, run the normal forward pipeline against the scaffolded project —
`dotnet rivet --project … --output …` — to regenerate the OpenAPI spec from the C#.
