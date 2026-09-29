# Changelog

## Unreleased — breaking

- `Rivet.Attributes`: removed the unused `RouteDefinitionBase.IsFileUpload` getter.
- Import: an operation with `security: []` now scaffolds `.Anonymous()` instead of `.SecurityRequirements()`. Both re-emit `security: []`.
- `Rivet.Attributes`: removed the protected `RouteDefinitionBase.BeginMutation()` and `CopyStateTo()` helpers. Builder state is now one immutable record behind a private gate.
