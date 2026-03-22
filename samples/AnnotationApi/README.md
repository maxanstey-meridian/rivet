# Annotation-style sample

Demonstrates Rivet's attribute-based endpoint discovery using `[RivetEndpoint]` on controller methods.

Features shown: `[RivetType]`, `[RivetEndpoint]`, `[ProducesResponseType]`, generic types (`PagedResult<T>`),
domain enums, branded value objects, file uploads, and typed error responses.

## Run

```bash
# Preview to stdout
dotnet run --project Rivet.Tool -- --project samples/AnnotationApi/AnnotationApi.csproj

# Write to disk
dotnet run --project Rivet.Tool -- --project samples/AnnotationApi/AnnotationApi.csproj --output /tmp/rivet-annotation

# Write to disk + Zod validators (requires zod in consumer project)
dotnet run --project Rivet.Tool -- --project samples/AnnotationApi/AnnotationApi.csproj --output /tmp/rivet-annotation --compile

# Preview OpenAPI spec to stdout
dotnet run --project Rivet.Tool -- --project samples/AnnotationApi/AnnotationApi.csproj --openapi
```

All commands should be run from the repository root.
