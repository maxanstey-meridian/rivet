# Changelog

## Unreleased — breaking

- `Rivet.Attributes`: removed the unused `RouteDefinitionBase.IsFileUpload` getter.
- Import: an operation with `security: []` now scaffolds `.Anonymous()` instead of `.SecurityRequirements()`. Both re-emit `security: []`.
- `Rivet.Attributes`: removed the protected `RouteDefinitionBase.BeginMutation()` and `CopyStateTo()` helpers. Builder state is now one immutable record behind a private gate.
- `Rivet.Attributes`: removed the 18 five-parameter `File(content, downloadName, enableRangeProcessing, lastModified, entityTag)` overloads on `RouteDefinition`, `RouteDefinition<T>`, `FileRouteDefinition` and the three `Bound*` types. Source calls still bind to the six-parameter overload (`contentType` defaults to `null`); assemblies compiled against the old overloads must be rebuilt.
