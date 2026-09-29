# Changelog

## Unreleased — breaking

- `Rivet.Attributes`: removed the unused `RouteDefinitionBase.IsFileUpload` getter.
- Import: an operation with `security: []` now scaffolds `.Anonymous()` instead of `.SecurityRequirements()`. Both re-emit `security: []`.
