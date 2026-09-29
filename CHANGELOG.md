# Changelog

## Unreleased — breaking

- `Rivet.Attributes`: removed the unused `RouteDefinitionBase.IsFileUpload` getter.
- Import: an operation with `security: []` now scaffolds `.Anonymous()` instead of `.SecurityRequirements()`. Both re-emit `security: []`.
- `--from` contract JSON: the reader now enforces the members `rivet-contract-schema.json` requires on type nodes (`kind`, a primitive's `type`, `inner`, `element`, `values`, and so on) and the `type` of every param, response header and property. A missing one now fails with exit 1 and a JSON error. Before, it was either silently emitted as an untyped `{type: object}` or crashed with a stack trace. Schema-valid contracts, including rivet-ts output, are unaffected.
